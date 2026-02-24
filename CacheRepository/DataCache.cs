using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CacheRepository;

public abstract class DataCache<TKey, TValue> : IDataCache<TKey, TValue>
{
    private readonly ConcurrentDictionary<TKey, CacheEntry<TValue>> _store = new();

    // Maps a key to its in-flight fetch task. When multiple callers request the same key
    // concurrently, they all receive the SAME Lazy<Task> — so only one DB call is made.
    // Lazy<Task<T>> is used (instead of just Task<T>) because:
    //   - ConcurrentDictionary.GetOrAdd may invoke the factory on multiple threads,
    //     but Lazy guarantees the inner factory (the actual DB call) runs exactly once.
    //   - Without Lazy, two threads could both create separate Tasks before GetOrAdd
    //     picks a winner, resulting in duplicate DB calls.
    private readonly ConcurrentDictionary<TKey, Lazy<Task<TValue>>> _inflightFetches = new();

    private readonly CacheOptions _options;
    private readonly CacheMetrics _metrics;
    private readonly Channel<CacheChangeNotification<TKey>> _changeChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _changeProcessorTask;
    private readonly Task _purgeTask;

    public DataCache(CacheOptions options = null)
    {
        _options = options ?? new CacheOptions();
        _metrics = new CacheMetrics(GetType().Name, () => _store.Count);

        _changeChannel = Channel.CreateBounded<CacheChangeNotification<TKey>>(
            new BoundedChannelOptions(_options.ChangeQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

        _changeProcessorTask = ProcessChangesAsync(_cts.Token);
        _purgeTask = PurgeLoopAsync(_cts.Token);
    }

    public async Task<TValue> Get(TKey key)
    {
        if (TryGetFromStore(key, out var value))
            return value;

        _metrics.Misses.Add(1);
        return await FetchAndCacheAsync(key);
    }

    public async Task<IReadOnlyDictionary<TKey, TValue>> Get(HashSet<TKey> keys)
    {
        var result = new Dictionary<TKey, TValue>(keys.Count);
        var keysToFetch = new HashSet<TKey>();
        var alreadyInflight = new List<(TKey key, Lazy<Task<TValue>> lazy)>();

        // Partition: cached / already in-flight / need to fetch
        foreach (var key in keys)
        {
            if (TryGetFromStore(key, out var value))
                result[key] = value;
            else if (_inflightFetches.TryGetValue(key, out var existing))
                alreadyInflight.Add((key, existing));
            else
                keysToFetch.Add(key);
        }

        var missCount = alreadyInflight.Count + keysToFetch.Count;
        if (missCount > 0)
            _metrics.Misses.Add(missCount);

        // Batch-fetch all truly missing keys in ONE db call.
        //
        // The challenge: _inflightFetches stores Lazy<Task<TValue>> (per-key), but the
        // batch DB call returns all keys at once in a dictionary. We bridge this with
        // two layers of Lazy:
        //
        // 1. batchLazy — wraps the single FetchBatchCoreAsync call. No matter how many
        //    per-key lazys reference it, the DB call happens exactly once.
        //
        // 2. perKeyLazy (one per missing key) — awaits batchLazy.Value (the shared batch
        //    result) and extracts just its own key. These get registered in _inflightFetches
        //    so that a concurrent single-key Get(key) calling FetchAndCacheAsync will find
        //    them via GetOrAdd and piggyback on the batch — no extra DB call.
        //
        // Example: Bulk Get({A,B,C}) runs while a concurrent Get(B) arrives.
        //   - Bulk registers perKeyLazy for A, B, C in _inflightFetches
        //   - Concurrent Get(B) calls GetOrAdd(B) → finds the existing perKeyLazy → awaits it
        //   - All four callers (bulk + single) resolve from the same single batch DB call
        if (keysToFetch.Count > 0)
        {
            var batchLazy = new Lazy<Task<IReadOnlyDictionary<TKey, TValue>>>(
                () => FetchBatchCoreAsync(keysToFetch));

            foreach (var key in keysToFetch)
            {
                var k = key; // capture for closure — loop variable would be wrong
                var perKeyLazy = new Lazy<Task<TValue>>(async () =>
                {
                    var batchResult = await batchLazy.Value; // triggers the ONE batch DB call
                    return batchResult.TryGetValue(k, out var v) ? v : default;
                });

                // GetOrAdd: if another thread registered this key first, we get theirs instead
                var registered = _inflightFetches.GetOrAdd(key, perKeyLazy);
                alreadyInflight.Add((key, registered));
            }
        }

        // Await all pending keys (pre-existing in-flight from other callers + our batch-derived)
        // Then clean up _inflightFetches so future requests create fresh fetches.
        var pendingTasks = alreadyInflight.Select(async item =>
        {
            try
            {
                return (item.key, value: await item.lazy.Value);
            }
            finally
            {
                // Remove only our specific Lazy instance (KeyValuePair overload),
                // so we don't clobber a newer Lazy registered by a subsequent request
                _inflightFetches.TryRemove(
                    new KeyValuePair<TKey, Lazy<Task<TValue>>>(item.key, item.lazy));
            }
        });

        foreach (var (key, value) in await Task.WhenAll(pendingTasks))
        {
            if (value is not null)
                result[key] = value;
        }

        return result;
    }

    /// Single-key fetch with coalescing.
    ///
    /// GetOrAdd ensures that if two threads call this for the same key simultaneously:
    ///   Thread A: GetOrAdd(key) → creates Lazy, wins the race → stored in dictionary
    ///   Thread B: GetOrAdd(key) → sees Thread A's Lazy already there → gets the SAME instance
    /// Both threads then await the same lazy.Value → same Task → single DB call.
    ///
    /// The finally block uses TryRemove with the KeyValuePair overload to only remove
    /// OUR Lazy instance. If a new request already registered a fresh Lazy for a retry,
    /// we won't accidentally remove it.
    private async Task<TValue> FetchAndCacheAsync(TKey key)
    {
        var lazy = _inflightFetches.GetOrAdd(key, k =>
            new Lazy<Task<TValue>>(() => FetchSingleCoreAsync(k)));

        try
        {
            return await lazy.Value;
        }
        finally
        {
            _inflightFetches.TryRemove(new KeyValuePair<TKey, Lazy<Task<TValue>>>(key, lazy));
        }
    }

    private async Task<IReadOnlyDictionary<TKey, TValue>> FetchBatchCoreAsync(HashSet<TKey> keys)
    {
        // Double-check: some may have been populated while waiting
        var stillMissing = keys.Where(k => !_store.ContainsKey(k)).ToHashSet();

        if (stillMissing.Count == 0)
            return new Dictionary<TKey, TValue>();

        var freshData = await FetchAsync(stillMissing, _cts.Token);
        if (freshData != null)
        {
            foreach (var kvp in freshData)
                _store[kvp.Key] = new CacheEntry<TValue>(kvp.Value);
        }

        return freshData ?? (IReadOnlyDictionary<TKey, TValue>)new Dictionary<TKey, TValue>();
    }

    private async Task<TValue> FetchSingleCoreAsync(TKey key)
    {
        // Double-check: may have been populated while waiting
        if (TryGetFromStore(key, out var value))
            return value;

        var result = await FetchAsync([key], _cts.Token);

        if (result != null && result.TryGetValue(key, out var fetched))
        {
            _store[key] = new CacheEntry<TValue>(fetched);
            return fetched;
        }

        return default;
    }

    public Task<IReadOnlyDictionary<TKey, TValue>> GetAll()
    {
        throw new NotImplementedException();
    }

    internal abstract Task<TValue> FetchAsync(TKey key, CancellationToken ct);
    internal abstract Task<IReadOnlyDictionary<TKey, TValue>> FetchAsync(HashSet<TKey> keys, CancellationToken ct);

    private async Task ProcessChangesAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var notification in _changeChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    switch (notification.ChangeType)
                    {
                        case ChangeType.Deleted:
                            foreach (var key in notification.Keys)
                                _store.TryRemove(key, out _);
                            break;

                        case ChangeType.Updated:
                            // Only refetch keys that are currently cached
                            var keysToRefresh = notification.Keys
                                .Where(k => _store.ContainsKey(k))
                                .ToHashSet();

                            if (keysToRefresh.Count == 0)
                                break;

                            // Evict stale entries first
                            foreach (var key in keysToRefresh)
                                _store.TryRemove(key, out _);

                            // Batch fetch fresh data
                            var freshData = await FetchAsync(keysToRefresh, ct);
                            if (freshData != null)
                            {
                                foreach (var kvp in freshData)
                                    _store[kvp.Key] = new CacheEntry<TValue>(kvp.Value);
                            }
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Log and continue — don't kill the processor
                    // Replace with your logger
                    System.Diagnostics.Debug.WriteLine(
                        $"Cache change processing error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    // ──────────────────────────────────────────────
    // Background: Purge loop
    // ──────────────────────────────────────────────
    private async Task PurgeLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PurgeInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = DateTime.UtcNow;

                var keysToRemove = _store
                    .Where(kvp =>
                        (now - kvp.Value.LastAccessedUtc) > _options.UnusedThreshold ||
                        (now - kvp.Value.CreatedAtUtc) > _options.AbsoluteExpiration)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                    _store.TryRemove(key, out _);

                var removedCount = keysToRemove.Count;

                // LRU eviction
                if (_options.MaxItems.HasValue && _store.Count > _options.MaxItems.Value)
                {
                    var excess = _store
                        .OrderBy(x => x.Value.LastAccessedUtc)
                        .Take(_store.Count - _options.MaxItems.Value)
                        .Select(x => x.Key)
                        .ToList();

                    foreach (var key in excess)
                        _store.TryRemove(key, out _);

                    removedCount += excess.Count;
                }

                if (removedCount > 0)
                    _metrics.Evictions.Add(removedCount);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private bool TryGetFromStore(TKey key, out TValue value)
    {
        if (_store.TryGetValue(key, out var entry) &&
            (DateTime.UtcNow - entry.CreatedAtUtc) <= _options.AbsoluteExpiration)
        {
            entry.Touch();
            value = entry.Value;
            _metrics.Hits.Add(1);
            return true;
        }
        value = default;
        return false;
    }

    public int Count => _store.Count;

    public void Dispose()
    {
        _cts.Cancel();

        _changeChannel.Writer.TryComplete();

        try { _changeProcessorTask.GetAwaiter().GetResult(); } catch { }
        try { _purgeTask.GetAwaiter().GetResult(); } catch { }

        _cts.Dispose();
        _metrics.Dispose();
        _store.Clear();
        _inflightFetches.Clear();
    }
}
