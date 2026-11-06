namespace ExamPrism.Infrastructure.MultiTenancy;

using Common.MultiTenancy;
using Microsoft.Extensions.Options;
using Npgsql;

public sealed class TenantConnectionStringNormalizer(IOptions<MultiTenancyOptions> options) : ITenantConnectionStringNormalizer
{
    private readonly MultiTenancyOptions _options = options?.Value ?? new MultiTenancyOptions();

    public string Normalize(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var pool = _options.DatabasePool ?? new TenantDatabasePoolOptions();

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MinPoolSize = pool.MinPoolSize,
            MaxPoolSize = pool.MaxPoolSize,
            ConnectionIdleLifetime = pool.ConnectionIdleLifetimeSeconds,
            ConnectionPruningInterval = pool.ConnectionPruningIntervalSeconds,
            NoResetOnClose = pool.NoResetOnClose,
            KeepAlive = pool.KeepAliveSeconds
        };

        if (pool.MaxAutoPrepare > 0)
        {
            builder.MaxAutoPrepare = pool.MaxAutoPrepare;
            builder.AutoPrepareMinUsages = Math.Max(1, pool.AutoPrepareMinUsages);
        }

        return builder.ConnectionString;
    }

    public string NormalizeForReadReplica(string connectionString, string? replicaHostOverride = null)
    {
        var normalized = Normalize(connectionString);
        if (string.IsNullOrWhiteSpace(replicaHostOverride))
        {
            return normalized;
        }

        var builder = new NpgsqlConnectionStringBuilder(normalized)
        {
            Host = replicaHostOverride.Trim()
        };
        builder["Target Session Attributes"] = "prefer-standby";

        return builder.ConnectionString;
    }
}
