namespace ExamPrism.Infrastructure.Identity;

public interface ITokenService
{
    string GenerateAccessToken(ApplicationUser user);
    (string RawToken, string TokenHash) GenerateRefreshToken();
    string HashToken(string rawToken);
}
