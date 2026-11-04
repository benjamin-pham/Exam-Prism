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
            ConnectionPruningInterval = pool.ConnectionPruningIntervalSeconds
        };

        return builder.ConnectionString;
    }
}
