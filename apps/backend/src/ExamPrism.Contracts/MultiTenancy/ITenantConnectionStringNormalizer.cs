namespace Common.MultiTenancy;

public interface ITenantConnectionStringNormalizer
{
    string Normalize(string connectionString);
    string NormalizeForReadReplica(string connectionString, string? replicaHostOverride = null);
}


