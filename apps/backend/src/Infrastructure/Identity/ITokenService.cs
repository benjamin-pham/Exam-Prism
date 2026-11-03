namespace ExamPrism.Infrastructure.Identity;

public interface ITokenService
{
    string GenerateAccessToken(ApplicationUser user);
    string GeneratePlatformAccessToken(PlatformUser user, IEnumerable<string> roles);
    (string RawToken, string TokenHash) GenerateRefreshToken();
    string HashToken(string rawToken);
}
