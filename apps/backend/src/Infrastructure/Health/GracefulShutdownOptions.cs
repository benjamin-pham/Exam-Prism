namespace ExamPrism.Infrastructure.Health;

public sealed class GracefulShutdownOptions
{
    public const string SectionName = "GracefulShutdown";

    public int DrainDelaySeconds { get; set; } = 0;
    public int StartupWaitTimeoutSeconds { get; set; } = 30;
    public int StartupPollIntervalMs { get; set; } = 200;
}
