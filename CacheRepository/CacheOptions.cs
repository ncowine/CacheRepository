namespace CacheRepository;

public sealed class CacheOptions
{
    public TimeSpan PurgeInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan UnusedThreshold { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan AbsoluteExpiration { get; init; } = TimeSpan.FromHours(2);
    public int? MaxItems { get; init; }
    public int ChangeQueueCapacity { get; init; } = 1_000;
}
