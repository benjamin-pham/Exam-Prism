namespace ExamPrism.Infrastructure.Orleans;

public sealed class OrleansOptions
{
    public const string SectionName = "Orleans";
    public const string DefaultStateStoreName = "Default";

    public string ClusterId { get; set; } = "examprism-cluster";
    public string ServiceId { get; set; } = "examprism-service";
    public string StateStoreName { get; set; } = DefaultStateStoreName;
    public int SiloPort { get; set; } = 11111;
    public int GatewayPort { get; set; } = 30000;
    public bool ListenOnAnyHostAddress { get; set; } = false;
    public string? AdvertisedIpAddress { get; set; }
    public bool LoadSheddingEnabled { get; set; } = true;
    public int LoadSheddingLimitPercent { get; set; } = 95;
    public int CollectionAgeMinutes { get; set; } = 15;
    public int CollectionQuantumMinutes { get; set; } = 1;
    public int ResponseTimeoutSeconds { get; set; } = 15;
}
