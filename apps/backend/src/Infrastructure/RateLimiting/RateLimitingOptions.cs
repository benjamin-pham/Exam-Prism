namespace ExamPrism.Infrastructure.RateLimiting;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public FixedWindowPolicyOptions Global { get; set; } = new(PermitLimit: 100, WindowSeconds: 60);
    public FixedWindowPolicyOptions Auth { get; set; } = new(PermitLimit: 10, WindowSeconds: 60);
    public SlidingWindowPolicyOptions TenantOnboarding { get; set; } = new(PermitLimit: 5, WindowSeconds: 60, SegmentsPerWindow: 6);
    public SlidingWindowPolicyOptions ExamCritical { get; set; } = new(PermitLimit: 300, WindowSeconds: 60, SegmentsPerWindow: 6);
    public ConcurrencyBulkheadPolicyOptions HeavyOperationBulkhead { get; set; } = new(PermitLimit: 10, QueueLimit: 5);
}

public sealed record FixedWindowPolicyOptions(int PermitLimit, int WindowSeconds);
public sealed record SlidingWindowPolicyOptions(int PermitLimit, int WindowSeconds, int SegmentsPerWindow = 6);
public sealed record ConcurrencyBulkheadPolicyOptions(int PermitLimit, int QueueLimit);
