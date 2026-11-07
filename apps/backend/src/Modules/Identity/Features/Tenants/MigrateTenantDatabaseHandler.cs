namespace ExamPrism.Features.Tenants;

using Common.Handlers;
using Common.Messages.Tenants;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed class MigrateTenantDatabaseHandler(
    ITenantConnectionStringNormalizer normalizer,
    IOptions<PersistenceOptions> persistenceOptions)
    : IHandler<MigrateTenantDatabaseCommand, MigrateTenantDatabaseResult>
{
    public async Task<Result<MigrateTenantDatabaseResult>> Handle(
        MigrateTenantDatabaseCommand command,
        CancellationToken ct = default)
    {
        var normalizedConnStr = normalizer.Normalize(command.RawConnectionString);
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(normalizedConnStr, npgsql =>
            {
                if (persistenceOptions?.Value is { } opts)
                {
                    npgsql.ConfigureNpgsql(opts);
                }
            })
            .Options;

        await using var tenantDb = new AppDbContext(dbOptions);
        var pending = (await tenantDb.Database.GetPendingMigrationsAsync(ct)).Count();
        await tenantDb.Database.MigrateAsync(ct);

        return new MigrateTenantDatabaseResult(pending);
    }
}
