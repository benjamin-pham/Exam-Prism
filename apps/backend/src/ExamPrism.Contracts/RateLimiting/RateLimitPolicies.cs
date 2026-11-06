namespace Common.RateLimiting;

public static class RateLimitPolicies
{
    public const string Auth = "auth";
    public const string TenantOnboarding = "TenantOnboarding";
    public const string ExamCritical = "ExamCritical";
    public const string HeavyOperationBulkhead = "HeavyOperationBulkhead";
}
