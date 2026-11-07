namespace ExamPrism.Features.Tenants;

using Common.Handlers;
using Common.Messages.Tenants;
using Common.MultiTenancy;
using Common.Results;
using Common.Security;
using ExamPrism.Domain.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public sealed class InitializeTenantIdentityHandler(
    ITenantConnectionStringNormalizer normalizer,
    IConnectionStringProtector protector,
    IServiceProvider serviceProvider)
    : IHandler<InitializeTenantIdentityCommand, InitializeTenantIdentityResult>
{
    public async Task<Result<InitializeTenantIdentityResult>> Handle(
        InitializeTenantIdentityCommand command,
        CancellationToken ct = default)
    {
        var normalizedConnStr = normalizer.Normalize(command.RawConnectionString);
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(normalizedConnStr)
            .Options;

        await using (var tenantDb = new AppDbContext(dbOptions))
        {
            await tenantDb.Database.MigrateAsync(ct);
        }

        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var setter = scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>();
            setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(
                new ExamPrismTenantInfo(command.TenantId.ToString(), command.TenantSlug)
                {
                    Status = TenantStatus.Active,
                    ConnectionString = command.RawConnectionString
                });

            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (!await roleManager.RoleExistsAsync(TenantRoles.TenantOwner))
            {
                var roleResult = await roleManager.CreateAsync(new IdentityRole<Guid>(TenantRoles.TenantOwner));
                if (!roleResult.Succeeded)
                {
                    return Error.Failure(
                        "RoleCreationFailed",
                        $"Failed to create role '{TenantRoles.TenantOwner}': {string.Join(", ", roleResult.Errors.Select(e => e.Description))}");
                }
            }

            if (!string.IsNullOrWhiteSpace(command.OwnerEmail))
            {
                var ownerUser = await userManager.FindByEmailAsync(command.OwnerEmail);
                if (ownerUser is null)
                {
                    if (string.IsNullOrWhiteSpace(command.EncryptedOwnerPassword))
                    {
                        return Error.Validation(
                            nameof(command.EncryptedOwnerPassword),
                            "MissingOwnerPassword",
                            $"Owner password for tenant '{command.TenantSlug}' is missing.");
                    }

                    var ownerPassword = protector.Unprotect(command.EncryptedOwnerPassword);
                    ownerUser = new ApplicationUser
                    {
                        Id = Guid.NewGuid(),
                        UserName = command.OwnerEmail,
                        Email = command.OwnerEmail,
                        EmailConfirmed = true
                    };

                    var createResult = await userManager.CreateAsync(ownerUser, ownerPassword);
                    if (!createResult.Succeeded)
                    {
                        return Error.Failure(
                            "UserCreationFailed",
                            $"Failed to create tenant owner: {string.Join(", ", createResult.Errors.Select(e => e.Description))}");
                    }
                }

                if (!await userManager.IsInRoleAsync(ownerUser, TenantRoles.TenantOwner))
                {
                    var addRoleResult = await userManager.AddToRoleAsync(ownerUser, TenantRoles.TenantOwner);
                    if (!addRoleResult.Succeeded)
                    {
                        return Error.Failure(
                            "RoleAssignmentFailed",
                            $"Failed to add tenant owner to role '{TenantRoles.TenantOwner}': {string.Join(", ", addRoleResult.Errors.Select(e => e.Description))}");
                    }
                }
            }
        }

        return new InitializeTenantIdentityResult(true);
    }
}
