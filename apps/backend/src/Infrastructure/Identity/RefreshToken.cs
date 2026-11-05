namespace ExamPrism.Infrastructure.Identity;

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }

    public bool IsExpired(TimeProvider timeProvider) => timeProvider.GetUtcNow().UtcDateTime >= ExpiresAt;
    public bool IsRevoked => RevokedAt != null;
    public bool IsActive(TimeProvider timeProvider) => !IsRevoked && !IsExpired(timeProvider);
}
