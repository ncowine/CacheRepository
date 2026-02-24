using System.Diagnostics.Metrics;

namespace CacheRepository;

internal sealed class CacheMetrics : IDisposable
{
    private readonly Meter _meter;

    public Counter<long> Hits { get; }
    public Counter<long> Misses { get; }
    public Counter<long> Evictions { get; }

    public CacheMetrics(string cacheTypeName, Func<int> itemCountCallback)
    {
        _meter = new Meter("CacheRepository");

        Hits = _meter.CreateCounter<long>("cache.hits", description: "Cache hit (served from store)");
        Misses = _meter.CreateCounter<long>("cache.misses", description: "Cache miss (triggered a fetch)");
        Evictions = _meter.CreateCounter<long>("cache.evictions", description: "Entry removed by purge loop");

        _meter.CreateObservableGauge(
            "cache.item_count",
            itemCountCallback,
            description: "Current number of items in the cache");
    }

    public void Dispose() => _meter.Dispose();
}
