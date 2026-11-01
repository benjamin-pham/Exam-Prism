namespace ExamPrism.Infrastructure.RateLimiting;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public FixedWindowPolicyOptions Global { get; set; } = new(PermitLimit: 100, WindowSeconds: 60);
    public FixedWindowPolicyOptions Auth { get; set; } = new(PermitLimit: 10, WindowSeconds: 60);
}

public sealed record FixedWindowPolicyOptions(int PermitLimit, int WindowSeconds);

