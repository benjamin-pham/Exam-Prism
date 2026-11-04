namespace ExamPrism.Infrastructure.Telemetry;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    public string? OtlpEndpoint { get; set; }
    public bool EnablePrometheusEndpoint { get; set; } = true;
}
