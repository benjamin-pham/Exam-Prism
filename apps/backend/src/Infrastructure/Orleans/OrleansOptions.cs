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
}
