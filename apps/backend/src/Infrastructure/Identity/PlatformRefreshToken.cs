namespace ExamPrism.Infrastructure.Identity;

public class PlatformRefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public PlatformUser User { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }

    public bool IsExpired(TimeProvider timeProvider) => timeProvider.GetUtcNow() >= ExpiresAt;
    public bool IsRevoked => RevokedAt != null;
    public bool IsActive(TimeProvider timeProvider) => !IsRevoked && !IsExpired(timeProvider);
}
