namespace Tests.Identity;

using Common.Security;
using ExamPrism.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public class PlatformAdminSeederTests
{
    private readonly RoleManager<PlatformRole> _roleManager;
    private readonly UserManager<PlatformUser> _userManager;
    private readonly ILogger<PlatformAdminSeeder> _logger = Substitute.For<ILogger<PlatformAdminSeeder>>();

    public PlatformAdminSeederTests()
    {
        var roleStore = Substitute.For<IRoleStore<PlatformRole>>();
        _roleManager = Substitute.For<RoleManager<PlatformRole>>(
            roleStore,
            Array.Empty<IRoleValidator<PlatformRole>>(),
            Substitute.For<ILookupNormalizer>(),
            new IdentityErrorDescriber(),
            Substitute.For<ILogger<RoleManager<PlatformRole>>>());

        var userStore = Substitute.For<IUserStore<PlatformUser>>();
        _userManager = Substitute.For<UserManager<PlatformUser>>(
            userStore,
            Substitute.For<IOptions<IdentityOptions>>(),
            Substitute.For<IPasswordHasher<PlatformUser>>(),
            Array.Empty<IUserValidator<PlatformUser>>(),
            Array.Empty<IPasswordValidator<PlatformUser>>(),
            Substitute.For<ILookupNormalizer>(),
            new IdentityErrorDescriber(),
            Substitute.For<IServiceProvider>(),
            Substitute.For<ILogger<UserManager<PlatformUser>>>());
    }

    [Fact]
    public async Task SeedAsync_CreatesSuperAdminRoleAndUser_AndIsIdempotentOnRepeatedCalls()
    {
        var options = Options.Create(new PlatformAdminOptions
        {
            SeedOnStartup = true,
            Email = "admin@examprism.com",
            Password = "AdminPassword123!"
        });

        var roleExists = false;
        _roleManager.RoleExistsAsync(PlatformRoles.SuperAdmin)
            .Returns(_ => Task.FromResult(roleExists));
        _roleManager.CreateAsync(Arg.Any<PlatformRole>())
            .Returns(_ =>
            {
                roleExists = true;
                return Task.FromResult(IdentityResult.Success);
            });

        PlatformUser? existingUser = null;
        var userInRole = false;

        _userManager.FindByEmailAsync("admin@examprism.com")
            .Returns(_ => Task.FromResult(existingUser));

        _userManager.CreateAsync(Arg.Any<PlatformUser>(), "AdminPassword123!")
            .Returns(ci =>
            {
                existingUser = ci.Arg<PlatformUser>();
                return Task.FromResult(IdentityResult.Success);
            });

        _userManager.IsInRoleAsync(Arg.Any<PlatformUser>(), PlatformRoles.SuperAdmin)
            .Returns(_ => Task.FromResult(userInRole));

        _userManager.AddToRoleAsync(Arg.Any<PlatformUser>(), PlatformRoles.SuperAdmin)
            .Returns(_ =>
            {
                userInRole = true;
                return Task.FromResult(IdentityResult.Success);
            });

        var seeder = new PlatformAdminSeeder(_roleManager, _userManager, options, _logger);

        // Run 1
        await seeder.SeedAsync();

        // Run 2 (repeated call)
        await seeder.SeedAsync();

        // Verify role and user created exactly once
        await _roleManager.Received(1).CreateAsync(Arg.Is<PlatformRole>(r => r.Name == PlatformRoles.SuperAdmin));
        await _userManager.Received(1).CreateAsync(Arg.Is<PlatformUser>(u => u.Email == "admin@examprism.com" && u.EmailConfirmed), "AdminPassword123!");
        await _userManager.Received(1).AddToRoleAsync(Arg.Is<PlatformUser>(u => u.Email == "admin@examprism.com"), PlatformRoles.SuperAdmin);
    }

    [Theory]
    [InlineData("", "AdminPassword123!")]
    [InlineData("admin@examprism.com", "")]
    [InlineData(null, null)]
    public async Task SeedAsync_WhenEmailOrPasswordBlank_EnsuresRoleOnlyWithoutCreatingUser(string? email, string? password)
    {
        var options = Options.Create(new PlatformAdminOptions
        {
            SeedOnStartup = true,
            Email = email!,
            Password = password!
        });

        _roleManager.RoleExistsAsync(PlatformRoles.SuperAdmin).Returns(false);
        _roleManager.CreateAsync(Arg.Any<PlatformRole>()).Returns(IdentityResult.Success);

        var seeder = new PlatformAdminSeeder(_roleManager, _userManager, options, _logger);

        await seeder.SeedAsync();

        await _roleManager.Received(1).CreateAsync(Arg.Is<PlatformRole>(r => r.Name == PlatformRoles.SuperAdmin));
        await _userManager.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!);
        await _userManager.DidNotReceiveWithAnyArgs().FindByEmailAsync(default!);
    }
}
