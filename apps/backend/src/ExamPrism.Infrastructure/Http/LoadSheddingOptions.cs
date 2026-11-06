namespace ExamPrism.Infrastructure.Http;

public sealed class LoadSheddingOptions
{
    public const string SectionName = "LoadShedding";

    public bool Enabled { get; set; } = true;
    public double InitialConcurrencyLimit { get; set; } = 100;
    public double MinConcurrencyLimit { get; set; } = 10;
    public double MaxConcurrencyLimit { get; set; } = 1000;
    public double LowPriorityShedFraction { get; set; } = 0.75;
    public double NormalPriorityShedFraction { get; set; } = 0.90;
    public double HighPriorityShedFraction { get; set; } = 1.00;
    public double CriticalReserveMultiplier { get; set; } = 1.25;
    public double BaselineLatencyFloorMs { get; set; } = 5.0;
    public double LatencyGradientThreshold { get; set; } = 0.8;
    public double SmoothingFactor { get; set; } = 0.2;
    public int MinLatencyResetIntervalSamples { get; set; } = 1000;
    public int MaxPendingThreadPoolWorkItems { get; set; } = 500;
    public int RetryAfterSeconds { get; set; } = 2;
}
