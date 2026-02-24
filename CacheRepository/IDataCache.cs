namespace CacheRepository;

public interface IDataCache<TKey, TValue>
{
    Task<TValue> Get(TKey key);
    Task<IReadOnlyDictionary<TKey, TValue>> Get(HashSet<TKey> keys);
    Task<IReadOnlyDictionary<TKey, TValue>> GetAll();
}
