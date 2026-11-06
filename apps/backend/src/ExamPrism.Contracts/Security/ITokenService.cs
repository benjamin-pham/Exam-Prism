namespace Common.Security;

public interface ITokenService
{
    string GenerateAccessToken(Guid userId, string? email);
    string GeneratePlatformAccessToken(Guid userId, string? email, IEnumerable<string> roles);
    (string RawToken, string TokenHash) GenerateRefreshToken();
    string HashToken(string rawToken);
}

