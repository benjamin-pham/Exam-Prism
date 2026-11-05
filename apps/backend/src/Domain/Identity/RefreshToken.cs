namespace ExamPrism.Domain.Identity;

using Common.Domain;

public class RefreshToken : Entity<Guid>
{
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }

    public RefreshToken() { }

    public RefreshToken(Guid id) : base(id) { }

    public bool IsExpired(TimeProvider timeProvider) => timeProvider.GetUtcNow().UtcDateTime >= ExpiresAt;
    public bool IsRevoked => RevokedAt != null;
    public bool IsActive(TimeProvider timeProvider) => !IsRevoked && !IsExpired(timeProvider);
}

