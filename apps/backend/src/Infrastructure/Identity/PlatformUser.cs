namespace ExamPrism.Infrastructure.Identity;

using Microsoft.AspNetCore.Identity;

public class PlatformUser : IdentityUser<Guid>
{
    public ICollection<PlatformRefreshToken> RefreshTokens { get; set; } = new List<PlatformRefreshToken>();
}
