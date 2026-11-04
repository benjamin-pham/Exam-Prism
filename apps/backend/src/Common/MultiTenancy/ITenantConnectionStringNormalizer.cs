namespace Common.MultiTenancy;

public interface ITenantConnectionStringNormalizer
{
    string Normalize(string connectionString);
}

