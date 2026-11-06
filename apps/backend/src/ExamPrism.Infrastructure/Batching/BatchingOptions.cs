namespace ExamPrism.Infrastructure.Batching;

public sealed class BatchingOptions
{
    public const string SectionName = "Batching";

    public int MaxBatchSize { get; set; } = 100;

    public int FlushIntervalMs { get; set; } = 100;

    public int Capacity { get; set; } = 10_000;

    public int ChannelCapacity
    {
        get => Capacity;
        set => Capacity = value;
    }

    public int QueueCapacity
    {
        get => Capacity;
        set => Capacity = value;
    }
}
