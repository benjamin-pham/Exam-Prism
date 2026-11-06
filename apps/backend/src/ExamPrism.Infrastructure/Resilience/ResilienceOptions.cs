namespace ExamPrism.Infrastructure.Resilience;

public sealed class ResilienceOptions
{
    public const string SectionName = "Resilience";

    public int MaxRetryAttempts { get; set; } = 3;
    public double BackoffBaseDelaySeconds { get; set; } = 0.5;
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;
    public int CircuitBreakerSamplingDurationSeconds { get; set; } = 30;
    public int CircuitBreakerBreakDurationSeconds { get; set; } = 15;
    public int CircuitBreakerMinimumThroughput { get; set; } = 20;
    public int TimeoutSeconds { get; set; } = 10;
}
