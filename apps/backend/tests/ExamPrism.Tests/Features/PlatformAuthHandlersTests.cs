namespace Tests.Features;

using Common.Results;
using Common.Security;
using ExamPrism.Features.Platform.Auth;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Tests.Infrastructure;
using Xunit;

public class PlatformAuthHandlersTests : IClassFixture<CatalogDbFixture>
{
    private readonly CatalogDbFixture _fixture;

    public PlatformAuthHandlersTests(CatalogDbFixture fixture)
    {
        _fixture = fixture;
    }

    private CatalogDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;
        return new CatalogDbContext(options);
    }

    private static UserManager<PlatformUser> CreateMockUserManager()
    {
        var store = Substitute.For<IUserStore<PlatformUser>>();
        return Substitute.For<UserManager<PlatformUser>>(
            store, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    [Fact]
    public async Task PlatformLoginHandler_WithValidCredentials_PersistsPlatformRefreshTokenAndReturnsRolesAndTokens()
    {
        await using var db = CreateDbContext();
        var user = new PlatformUser
        {
            Id = Guid.NewGuid(),
            UserName = "admin@examprism.com",
            NormalizedUserName = "ADMIN@EXAMPRISM.COM",
            Email = "admin@examprism.com",
            NormalizedEmail = "ADMIN@EXAMPRISM.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("D"),
            ConcurrencyStamp = Guid.NewGuid().ToString("D")
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var userManager = CreateMockUserManager();
        userManager.FindByEmailAsync("admin@examprism.com").Returns(user);
        userManager.CheckPasswordAsync(user, "AdminPassword123!").Returns(true);
        userManager.GetRolesAsync(user).Returns(new List<string> { PlatformRoles.SuperAdmin });

        var tokenService = Substitute.For<ITokenService>();
        tokenService.GeneratePlatformAccessToken(user, Arg.Any<IEnumerable<string>>())
            .Returns("platform-access-token-xyz");
        tokenService.GenerateRefreshToken()
            .Returns(("raw-refresh-123", "hash-refresh-123"));

        var jwtOptions = Options.Create(new JwtOptions { RefreshTokenExpirationDays = 14 });
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var handler = new PlatformLoginHandler(userManager, db, tokenService, jwtOptions, timeProvider);
        var result = await handler.Handle(new PlatformLoginCommand("admin@examprism.com", "AdminPassword123!"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.Id.Should().Be(user.Id);
        result.Value.Email.Should().Be("admin@examprism.com");
        result.Value.Roles.Should().ContainSingle(r => r == PlatformRoles.SuperAdmin);
        result.Value.AccessToken.Should().Be("platform-access-token-xyz");
        result.Value.RefreshToken.Should().Be("raw-refresh-123");

        var persistedToken = await db.PlatformRefreshTokens
            .FirstOrDefaultAsync(t => t.UserId == user.Id && t.TokenHash == "hash-refresh-123");
        persistedToken.Should().NotBeNull();
        persistedToken!.RevokedAt.Should().BeNull();
        persistedToken.ExpiresAt.Should().Be(timeProvider.GetUtcNow().AddDays(14));
    }

    [Fact]
    public async Task PlatformLoginHandler_WithInvalidEmailOrPassword_ReturnsUnauthorizedInvalidCredentials()
    {
        await using var db = CreateDbContext();
        var userManager = CreateMockUserManager();
        var tokenService = Substitute.For<ITokenService>();
        var jwtOptions = Options.Create(new JwtOptions());
        var timeProvider = TimeProvider.System;

        var handler = new PlatformLoginHandler(userManager, db, tokenService, jwtOptions, timeProvider);

        // Empty credentials
        var emptyResult = await handler.Handle(new PlatformLoginCommand("", ""));
        emptyResult.IsFailure.Should().BeTrue();
        emptyResult.Error.Code.Should().Be("INVALID_CREDENTIALS");

        // Non-existent email
        userManager.FindByEmailAsync("missing@examprism.com").Returns((PlatformUser?)null);
        var missingResult = await handler.Handle(new PlatformLoginCommand("missing@examprism.com", "Pass123!"));
        missingResult.IsFailure.Should().BeTrue();
        missingResult.Error.Code.Should().Be("INVALID_CREDENTIALS");

        // Wrong password
        var user = new PlatformUser
        {
            Id = Guid.NewGuid(),
            Email = "wrongpass@examprism.com",
            UserName = "wrongpass@examprism.com"
        };
        userManager.FindByEmailAsync("wrongpass@examprism.com").Returns(user);
        userManager.CheckPasswordAsync(user, "WrongPass!").Returns(false);

        var wrongPassResult = await handler.Handle(new PlatformLoginCommand("wrongpass@examprism.com", "WrongPass!"));
        wrongPassResult.IsFailure.Should().BeTrue();
        wrongPassResult.Error.Code.Should().Be("INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task PlatformRefreshTokenHandler_RotatesActiveToken_AndRevokesAllUserTokensOnCompromisedReuse()
    {
        await using var db = CreateDbContext();
        var user = new PlatformUser
        {
            Id = Guid.NewGuid(),
            UserName = $"user_{Guid.NewGuid():N}@examprism.com",
            Email = $"user_{Guid.NewGuid():N}@examprism.com",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("D"),
            ConcurrencyStamp = Guid.NewGuid().ToString("D")
        };
        db.Users.Add(user);

        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var initialHash = $"init-hash-{Guid.NewGuid():N}";
        var activeToken = new PlatformRefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = initialHash,
            CreatedAt = timeProvider.GetUtcNow().AddHours(-1),
            ExpiresAt = timeProvider.GetUtcNow().AddDays(7)
        };
        db.PlatformRefreshTokens.Add(activeToken);
        await db.SaveChangesAsync();

        var userManager = CreateMockUserManager();
        userManager.GetRolesAsync(Arg.Any<PlatformUser>()).Returns(new List<string> { PlatformRoles.SuperAdmin });

        var tokenService = Substitute.For<ITokenService>();
        tokenService.HashToken("raw-token-1").Returns(initialHash);
        var rotatedHash = $"rotated-hash-{Guid.NewGuid():N}";
        tokenService.GenerateRefreshToken().Returns(("raw-token-2", rotatedHash));
        tokenService.GeneratePlatformAccessToken(Arg.Any<PlatformUser>(), Arg.Any<IEnumerable<string>>())
            .Returns("new-access-token-456");

        var jwtOptions = Options.Create(new JwtOptions { RefreshTokenExpirationDays = 7 });
        var handler = new PlatformRefreshTokenHandler(db, userManager, tokenService, jwtOptions, timeProvider);

        // 1. Rotate active token
        var rotateResult = await handler.Handle(new PlatformRefreshTokenCommand("raw-token-1"));
        rotateResult.IsSuccess.Should().BeTrue();
        rotateResult.Value.AccessToken.Should().Be("new-access-token-456");
        rotateResult.Value.RefreshToken.Should().Be("raw-token-2");

        // Reload db state
        var oldToken = await db.PlatformRefreshTokens.AsNoTracking().FirstAsync(t => t.Id == activeToken.Id);
        oldToken.RevokedAt.Should().NotBeNull();
        oldToken.ReplacedByTokenHash.Should().Be(rotatedHash);

        var newToken = await db.PlatformRefreshTokens.AsNoTracking().FirstAsync(t => t.TokenHash == rotatedHash);
        newToken.RevokedAt.Should().BeNull();
        newToken.UserId.Should().Be(user.Id);

        // 2. Reuse the revoked initial token (compromised reuse)
        var replayResult = await handler.Handle(new PlatformRefreshTokenCommand("raw-token-1"));
        replayResult.IsFailure.Should().BeTrue();
        replayResult.Error.Code.Should().Be("COMPROMISED_REFRESH_TOKEN");

        // Verify all tokens for this user are now revoked
        var activeTokensCount = await db.PlatformRefreshTokens
            .CountAsync(t => t.UserId == user.Id && t.RevokedAt == null);
        activeTokensCount.Should().Be(0);
    }

    [Fact]
    public async Task PlatformLogoutHandler_RevokesActiveRefreshToken_AndSucceedsWhenTokenMissing()
    {
        await using var db = CreateDbContext();
        var user = new PlatformUser
        {
            Id = Guid.NewGuid(),
            UserName = $"logout_{Guid.NewGuid():N}@examprism.com",
            Email = $"logout_{Guid.NewGuid():N}@examprism.com",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("D"),
            ConcurrencyStamp = Guid.NewGuid().ToString("D")
        };
        db.Users.Add(user);

        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var tokenHash = $"logout-hash-{Guid.NewGuid():N}";
        var token = new PlatformRefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = tokenHash,
            CreatedAt = timeProvider.GetUtcNow().AddHours(-1),
            ExpiresAt = timeProvider.GetUtcNow().AddDays(7)
        };
        db.PlatformRefreshTokens.Add(token);
        await db.SaveChangesAsync();

        var tokenService = Substitute.For<ITokenService>();
        tokenService.HashToken("logout-raw-token").Returns(tokenHash);

        var handler = new PlatformLogoutHandler(db, tokenService, timeProvider);

        // Revoke active token
        var revokeResult = await handler.Handle(new PlatformLogoutCommand("logout-raw-token"));
        revokeResult.IsSuccess.Should().BeTrue();

        var reloaded = await db.PlatformRefreshTokens.AsNoTracking().FirstAsync(t => t.Id == token.Id);
        reloaded.RevokedAt.Should().NotBeNull();

        // Succeeds on null / whitespace token
        var nullResult = await handler.Handle(new PlatformLogoutCommand(null));
        nullResult.IsSuccess.Should().BeTrue();

        var blankResult = await handler.Handle(new PlatformLogoutCommand("   "));
        blankResult.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetPlatformMeHandler_ReturnsAuthenticatedUserWithRoles_OrUnauthorizedWhenUnauthenticated()
    {
        var userContext = Substitute.For<IUserContext>();
        var userManager = CreateMockUserManager();

        var handler = new GetPlatformMeHandler(userContext, userManager);

        // 1. Unauthenticated (no UserId)
        userContext.UserId.Returns((Guid?)null);
        var unauthResult = await handler.Handle(new GetPlatformMeQuery());
        unauthResult.IsFailure.Should().BeTrue();
        unauthResult.Error.Code.Should().Be("UNAUTHORIZED");

        // 2. User not found
        var missingUserId = Guid.NewGuid();
        userContext.UserId.Returns(missingUserId);
        userManager.FindByIdAsync(missingUserId.ToString()).Returns((PlatformUser?)null);

        var notFoundResult = await handler.Handle(new GetPlatformMeQuery());
        notFoundResult.IsFailure.Should().BeTrue();
        notFoundResult.Error.Code.Should().Be("USER_NOT_FOUND");

        // 3. Authenticated user with roles
        var userId = Guid.NewGuid();
        var user = new PlatformUser
        {
            Id = userId,
            Email = "superadmin@examprism.com",
            UserName = "superadmin@examprism.com"
        };
        userContext.UserId.Returns(userId);
        userManager.FindByIdAsync(userId.ToString()).Returns(user);
        userManager.GetRolesAsync(user).Returns(new List<string> { PlatformRoles.SuperAdmin });

        var successResult = await handler.Handle(new GetPlatformMeQuery());
        successResult.IsSuccess.Should().BeTrue();
        successResult.Value.Should().NotBeNull();
        successResult.Value.Id.Should().Be(userId);
        successResult.Value.Email.Should().Be("superadmin@examprism.com");
        successResult.Value.Roles.Should().ContainSingle(r => r == PlatformRoles.SuperAdmin);
    }
}
