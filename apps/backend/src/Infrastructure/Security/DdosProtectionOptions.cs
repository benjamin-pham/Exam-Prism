namespace ExamPrism.Infrastructure.Security;

public sealed class DdosProtectionOptions
{
    public const string SectionName = "DdosProtection";

    public bool Enabled { get; set; } = true;
    public int BanThresholdScore { get; set; } = 100;
    public int StrikeWindowSeconds { get; set; } = 60;
    public int BanDurationSeconds { get; set; } = 300;
    public int MaxTrackedIps { get; set; } = 10_000;
    public bool BypassPenaltyBoxForAuthenticatedJwt { get; set; } = true;

    public int RateLimitExceededWeight { get; set; } = 15;
    public int UnknownTenantProbeWeight { get; set; } = 20;
    public int UnauthorizedAuthAttemptWeight { get; set; } = 15;
    public int DecompressionBombWeight { get; set; } = 100;
}

