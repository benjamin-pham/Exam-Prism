namespace ExamPrism.Tests.Identity;

using ExamPrism.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class RefreshTokenTests
{
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void IsActive_WhenNotRevokedAndNotExpired_ReturnsTrue()
    {
        var token = new RefreshToken
        {
            ExpiresAt = _timeProvider.GetUtcNow().UtcDateTime.AddDays(7),
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            RevokedAt = null
        };

        token.IsActive(_timeProvider).Should().BeTrue();
        token.IsRevoked.Should().BeFalse();
        token.IsExpired(_timeProvider).Should().BeFalse();
    }

    [Fact]
    public void IsActive_WhenExpired_ReturnsFalse()
    {
        var token = new RefreshToken
        {
            ExpiresAt = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-1),
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-8),
            RevokedAt = null
        };

        token.IsActive(_timeProvider).Should().BeFalse();
        token.IsExpired(_timeProvider).Should().BeTrue();
    }

    [Fact]
    public void IsActive_WhenRevoked_ReturnsFalse()
    {
        var token = new RefreshToken
        {
            ExpiresAt = _timeProvider.GetUtcNow().UtcDateTime.AddDays(7),
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            RevokedAt = _timeProvider.GetUtcNow().UtcDateTime
        };

        token.IsActive(_timeProvider).Should().BeFalse();
        token.IsRevoked.Should().BeTrue();
    }
}
