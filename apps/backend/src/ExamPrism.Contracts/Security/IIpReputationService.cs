namespace Common.Security;

using System;
using System.Threading;
using System.Threading.Tasks;

public enum IpStrikeReason
{
    RateLimitExceeded = 1,
    UnknownTenantProbe = 2,
    UnauthorizedAuthAttempt = 3,
    DecompressionBomb = 4
}

public interface IIpReputationService
{
    ValueTask<TimeSpan?> GetBanRemainingAsync(string ipAddress, CancellationToken cancellationToken = default);
    ValueTask RecordStrikeAsync(string ipAddress, IpStrikeReason reason, int customWeight = 0, CancellationToken cancellationToken = default);
    ValueTask BanIpAsync(string ipAddress, TimeSpan duration, CancellationToken cancellationToken = default);
    ValueTask UnbanIpAsync(string ipAddress, CancellationToken cancellationToken = default);
}

