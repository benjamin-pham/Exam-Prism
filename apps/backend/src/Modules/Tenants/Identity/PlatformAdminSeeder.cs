namespace ExamPrism.Infrastructure.Identity;

using Common.Security;
using ExamPrism.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class PlatformAdminSeeder(
    RoleManager<PlatformRole> roleManager,
    UserManager<PlatformUser> userManager,
    IOptions<PlatformAdminOptions> options,
    ILogger<PlatformAdminSeeder> logger)
{
    private readonly PlatformAdminOptions _options = options.Value;

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!await roleManager.RoleExistsAsync(PlatformRoles.SuperAdmin))
        {
            var roleResult = await roleManager.CreateAsync(new PlatformRole(PlatformRoles.SuperAdmin));
            if (!roleResult.Succeeded)
            {
                var errors = string.Join(", ", roleResult.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Failed to create role '{PlatformRoles.SuperAdmin}': {errors}");
            }
        }

        if (string.IsNullOrWhiteSpace(_options.Email) || string.IsNullOrWhiteSpace(_options.Password))
        {
            logger.LogWarning("PlatformAdmin email or password is not configured; skipping platform admin user creation.");
            return;
        }

        var user = await userManager.FindByEmailAsync(_options.Email);
        if (user is null)
        {
            user = new PlatformUser
            {
                Id = Guid.NewGuid(),
                UserName = _options.Email,
                Email = _options.Email,
                EmailConfirmed = true
            };

            var createResult = await userManager.CreateAsync(user, _options.Password);
            if (!createResult.Succeeded)
            {
                var errors = string.Join(", ", createResult.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Failed to create platform admin user '{_options.Email}': {errors}");
            }
        }

        if (!await userManager.IsInRoleAsync(user, PlatformRoles.SuperAdmin))
        {
            var addRoleResult = await userManager.AddToRoleAsync(user, PlatformRoles.SuperAdmin);
            if (!addRoleResult.Succeeded)
            {
                var errors = string.Join(", ", addRoleResult.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Failed to add platform admin user '{_options.Email}' to role '{PlatformRoles.SuperAdmin}': {errors}");
            }
        }
    }
}
