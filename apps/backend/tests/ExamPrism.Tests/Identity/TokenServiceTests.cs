namespace ExamPrism.Tests.Identity;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Common.MultiTenancy;
using Common.Security;
using ExamPrism.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

public class TokenServiceTests
{
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly IOptions<JwtOptions> _options = Options.Create(new JwtOptions
    {
        Issuer = "ExamPrismTest",
        Audience = "ExamPrismTestAudience",
        SecretKey = "SuperSecretKeyForTestingJwtTokensMin256BitsLong!",
        AccessTokenExpirationMinutes = 15,
        RefreshTokenExpirationDays = 7
    });

    [Fact]
    public void GenerateAccessToken_ContainsExpectedClaimsAndExpiration()
    {
        var expectedTenantId = Guid.NewGuid();
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.HasTenant.Returns(true);
        tenantContext.TenantId.Returns(expectedTenantId);

        var service = new TokenService(_options, _timeProvider, tenantContext);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            UserName = "user@example.com"
        };

        var tokenString = service.GenerateAccessToken(user.Id, user.Email);
        tokenString.Should().NotBeNullOrWhiteSpace();

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenString);

        jwt.Issuer.Should().Be("ExamPrismTest");
        jwt.Audiences.Should().Contain("ExamPrismTestAudience");
        jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value.Should().Be(user.Id.ToString());
        jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Email).Value.Should().Be(user.Email);
        jwt.Claims.First(c => c.Type == CustomClaimTypes.TenantId).Value.Should().Be(expectedTenantId.ToString("D"));
        jwt.ValidTo.Should().BeCloseTo(_timeProvider.GetUtcNow().UtcDateTime.AddMinutes(15), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void GenerateAccessToken_WhenNoActiveTenant_ThrowsInvalidOperationException()
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.HasTenant.Returns(false);
        tenantContext.TenantId.Returns((Guid?)null);

        var service = new TokenService(_options, _timeProvider, tenantContext);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            UserName = "user@example.com"
        };

        var act = () => service.GenerateAccessToken(user.Id, user.Email);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*tenant*");
    }

    [Fact]
    public void GeneratePlatformAccessToken_WhenInPlatformContext_EmitsTokenScopeAndRoleClaims_WithoutTenantId()
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.HasTenant.Returns(false);
        tenantContext.TenantId.Returns((Guid?)null);

        var service = new TokenService(_options, _timeProvider, tenantContext);
        var user = new PlatformUser
        {
            Id = Guid.NewGuid(),
            Email = "admin@examprism.com",
            UserName = "admin@examprism.com"
        };

        var tokenString = service.GeneratePlatformAccessToken(user.Id, user.Email, ["SuperAdmin"]);
        tokenString.Should().NotBeNullOrWhiteSpace();

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenString);

        jwt.Issuer.Should().Be("ExamPrismTest");
        jwt.Audiences.Should().Contain("ExamPrismTestAudience");
        jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value.Should().Be(user.Id.ToString());
        jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Email).Value.Should().Be(user.Email);
        jwt.Claims.First(c => c.Type == CustomClaimTypes.TokenScope).Value.Should().Be(CustomClaimTypes.PlatformScopeValue);
        jwt.Claims.First(c => c.Type == ClaimTypes.Role).Value.Should().Be("SuperAdmin");
        jwt.Claims.Any(c => c.Type == CustomClaimTypes.TenantId).Should().BeFalse();
        jwt.ValidTo.Should().BeCloseTo(_timeProvider.GetUtcNow().UtcDateTime.AddMinutes(15), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void GeneratePlatformAccessToken_WhenActiveTenantPresent_ThrowsInvalidOperationException()
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.HasTenant.Returns(true);
        tenantContext.TenantId.Returns(Guid.NewGuid());

        var service = new TokenService(_options, _timeProvider, tenantContext);
        var user = new PlatformUser
        {
            Id = Guid.NewGuid(),
            Email = "admin@examprism.com",
            UserName = "admin@examprism.com"
        };

        var act = () => service.GeneratePlatformAccessToken(user.Id, user.Email, ["SuperAdmin"]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*tenant*");
    }

    [Fact]
    public void GenerateRefreshToken_ReturnsRawTokenAndMatchingDeterministicSha256Hash()
    {
        var tenantContext = Substitute.For<ITenantContext>();
        var service = new TokenService(_options, _timeProvider, tenantContext);

        var (rawToken, tokenHash) = service.GenerateRefreshToken();

        rawToken.Should().NotBeNullOrWhiteSpace();
        tokenHash.Should().NotBeNullOrWhiteSpace();
        service.HashToken(rawToken).Should().Be(tokenHash);
    }
}
