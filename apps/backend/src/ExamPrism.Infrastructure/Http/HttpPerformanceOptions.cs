namespace ExamPrism.Infrastructure.Http;

public sealed class HttpPerformanceOptions
{
    public const string SectionName = "RequestTimeouts";

    public int DefaultTimeoutSeconds { get; set; } = 30;
    public int FastTimeoutSeconds { get; set; } = 5;
    public int ExamSubmitTimeoutSeconds { get; set; } = 15;
    public int LongRunningTimeoutSeconds { get; set; } = 120;
    public int OutputCacheExpirationSeconds { get; set; } = 30;
    public bool EnableCompression { get; set; } = true;
    public bool EnableRequestDecompression { get; set; } = true;
}
