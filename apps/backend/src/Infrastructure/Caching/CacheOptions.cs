namespace ExamPrism.Infrastructure.Caching;

public sealed class CacheOptions
{
    public const string SectionName = "Caching";

    public string KeyPrefix { get; set; } = "examprism:";
    public int DefaultExpirationMinutes { get; set; } = 30;
    public int DefaultLocalExpirationMinutes { get; set; } = 5;
    public bool EnableBackplane { get; set; } = true;
    public string BackplaneChannelName { get; set; } = "cache:backplane";
    public int OutputCacheExpirationSeconds { get; set; } = 30;
}

