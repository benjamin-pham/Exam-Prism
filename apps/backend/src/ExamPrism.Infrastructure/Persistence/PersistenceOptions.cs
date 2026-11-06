namespace ExamPrism.Infrastructure.Persistence;

public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    public int CommandTimeoutSeconds { get; set; } = 30;
    public int MaxBatchSize { get; set; } = 100;
    public int MaxRetryCount { get; set; } = 3;
    public int MaxRetryDelaySeconds { get; set; } = 5;
    public int SlowQueryThresholdMs { get; set; } = 500;
    public string? CatalogReadConnection { get; set; }
    public string? TenantReadReplicaHost { get; set; }
}
