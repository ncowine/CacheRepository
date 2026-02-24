namespace CacheRepository;

public enum ChangeType { Updated, Deleted }

public readonly record struct CacheChangeNotification<TKey>(
    IReadOnlyCollection<TKey> Keys,
    ChangeType ChangeType
);
