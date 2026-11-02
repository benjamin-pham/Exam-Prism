namespace ExamPrism.Infrastructure.MultiTenancy;

public sealed class MultiTenancyOptions
{
    public const string SectionName = "MultiTenancy";

    public string[] BaseDomains { get; set; } = ["localhost"];
    public string[] ReservedSubdomains { get; set; } = ["www", "admin", "api"];
    public bool AllowHeaderFallbackInDevelopment { get; set; } = true;
    public string HeaderName { get; set; } = "X-Tenant";
    public int CatalogCacheExpirationMinutes { get; set; } = 30;
    public int CatalogLocalCacheExpirationMinutes { get; set; } = 5;
    public int NotFoundCacheExpirationSeconds { get; set; } = 30;
    public int ProvisioningRetryAfterSeconds { get; set; } = 5;
    public TenantDatabasePoolOptions DatabasePool { get; set; } = new();
}

public sealed class TenantDatabasePoolOptions
{
    public int MaxPoolSize { get; set; } = 15;
    public int MinPoolSize { get; set; } = 0;
    public int ConnectionIdleLifetimeSeconds { get; set; } = 30;
    public int ConnectionPruningIntervalSeconds { get; set; } = 10;
}
