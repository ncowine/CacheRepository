namespace CacheRepository;

public class EmployeeDataCache : DataCache<Guid, Employee>
{
    internal override Task<Employee> FetchAsync(Guid key, CancellationToken ct)
    {
        //Returns value from db
        throw new NotImplementedException();
    }

    internal override Task<IReadOnlyDictionary<Guid, Employee>> FetchAsync(HashSet<Guid> keys, CancellationToken ct)
    {
        //Returns value from db
        throw new NotImplementedException();
    }
}
