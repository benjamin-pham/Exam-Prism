namespace ExamPrism.Infrastructure.Http;

public sealed class RuntimeTuningOptions
{
    public const string SectionName = "RuntimeTuning";

    public int MinWorkerThreads { get; set; } = 64;
    public int MinCompletionPortThreads { get; set; } = 64;
    public long? MaxConcurrentConnections { get; set; } = 50_000;
    public long? MaxRequestBodySize { get; set; } = 10 * 1024 * 1024;
    public long MaxDecompressedBodySizeBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxHttp2StreamsPerConnection { get; set; } = 250;
    public int KeepAliveTimeoutSeconds { get; set; } = 120;
    public int RequestHeadersTimeoutSeconds { get; set; } = 30;
    public double MinRequestBodyDataRateBytesPerSecond { get; set; } = 240;
    public double MinResponseDataRateBytesPerSecond { get; set; } = 240;
    public int DataRateGracePeriodSeconds { get; set; } = 5;
    public int MaxRequestHeaderCount { get; set; } = 100;
    public int MaxRequestHeadersTotalSizeBytes { get; set; } = 32 * 1024;
    public int MaxRequestLineSizeBytes { get; set; } = 8 * 1024;
}
