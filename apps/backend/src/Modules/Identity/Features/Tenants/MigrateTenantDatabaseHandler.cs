namespace ExamPrism.Features.Tenants;

using Common.Handlers;
using Common.Messages.Tenants;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class MigrateTenantDatabaseHandler(ITenantConnectionStringNormalizer normalizer)
    : IHandler<MigrateTenantDatabaseCommand, MigrateTenantDatabaseResult>
{
    public async Task<Result<MigrateTenantDatabaseResult>> Handle(
        MigrateTenantDatabaseCommand command,
        CancellationToken ct = default)
    {
        var normalizedConnStr = normalizer.Normalize(command.RawConnectionString);
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(normalizedConnStr)
            .Options;

        await using var tenantDb = new AppDbContext(dbOptions);
        var pending = (await tenantDb.Database.GetPendingMigrationsAsync(ct)).Count();
        await tenantDb.Database.MigrateAsync(ct);

        return new MigrateTenantDatabaseResult(pending);
    }
}
