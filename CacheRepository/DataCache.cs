using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CacheRepository;

public abstract class DataCache<TKey, TValue> : IDataCache<TKey, TValue>
{
    private readonly ConcurrentDictionary<TKey, CacheEntry<TValue>> _store = new();

    // Coalesces concurrent fetches for the same key into one task
    private readonly ConcurrentDictionary<TKey, Lazy<Task<TValue>>> _inflightFetches = new();

    private readonly CacheOptions _options;
    private readonly Channel<CacheChangeNotification<TKey>> _changeChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _changeProcessorTask;
    private readonly Task _purgeTask;

    public DataCache(CacheOptions options = null)
    {
        _options = options ?? new CacheOptions();

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

        return await FetchAndCacheAsync(key);
        throw new NotImplementedException();
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

        // Batch-fetch all truly missing keys in ONE db call,
        // but register per-key lazys so concurrent single-key Get() callers coalesce onto it
        if (keysToFetch.Count > 0)
        {
            var batchLazy = new Lazy<Task<IReadOnlyDictionary<TKey, TValue>>>(
                () => FetchBatchCoreAsync(keysToFetch));

            foreach (var key in keysToFetch)
            {
                var k = key;
                var perKeyLazy = new Lazy<Task<TValue>>(async () =>
                {
                    var batchResult = await batchLazy.Value;
                    return batchResult.TryGetValue(k, out var v) ? v : default;
                });

                var registered = _inflightFetches.GetOrAdd(key, perKeyLazy);
                alreadyInflight.Add((key, registered));
            }
        }

        // Await all pending keys (pre-existing in-flight + our batch-derived)
        var pendingTasks = alreadyInflight.Select(async item =>
        {
            try
            {
                return (item.key, value: await item.lazy.Value);
            }
            finally
            {
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
            // Remove only if it's the same Lazy instance (not a new retry)
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
                }
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
        _store.Clear();
        _inflightFetches.Clear();
    }
}
