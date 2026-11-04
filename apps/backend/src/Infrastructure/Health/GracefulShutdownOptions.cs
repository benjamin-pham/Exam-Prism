namespace ExamPrism.Infrastructure.Health;

public sealed class GracefulShutdownOptions
{
    public const string SectionName = "GracefulShutdown";

    public int DrainDelaySeconds { get; set; } = 0;
}
