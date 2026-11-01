namespace ExamPrism.Tests.Identity;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ExamPrism.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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
        var service = new TokenService(_options, _timeProvider);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            UserName = "user@example.com"
        };

        var tokenString = service.GenerateAccessToken(user);
        tokenString.Should().NotBeNullOrWhiteSpace();

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenString);

        jwt.Issuer.Should().Be("ExamPrismTest");
        jwt.Audiences.Should().Contain("ExamPrismTestAudience");
        jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value.Should().Be(user.Id.ToString());
        jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Email).Value.Should().Be(user.Email);
        jwt.ValidTo.Should().BeCloseTo(_timeProvider.GetUtcNow().UtcDateTime.AddMinutes(15), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void GenerateRefreshToken_ReturnsRawTokenAndMatchingDeterministicSha256Hash()
    {
        var service = new TokenService(_options, _timeProvider);

        var (rawToken, tokenHash) = service.GenerateRefreshToken();

        rawToken.Should().NotBeNullOrWhiteSpace();
        tokenHash.Should().NotBeNullOrWhiteSpace();
        service.HashToken(rawToken).Should().Be(tokenHash);
    }
}
