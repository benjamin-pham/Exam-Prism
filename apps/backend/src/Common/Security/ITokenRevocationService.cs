namespace Common.Security;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface ITokenRevocationService
{
    ValueTask RevokeTokenAsync(string jti, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    ValueTask RevokeUserTokensAsync(string userId, string? tenantId = null, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    ValueTask<bool> IsTokenRevokedAsync(string jti, CancellationToken cancellationToken = default);

    ValueTask<bool> IsTokenRevokedAsync(
        string jti,
        string? userId,
        string? tenantId,
        DateTimeOffset? issuedAt,
        CancellationToken cancellationToken = default);
}
