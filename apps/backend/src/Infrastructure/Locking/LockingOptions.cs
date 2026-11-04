namespace ExamPrism.Infrastructure.Locking;

public sealed class LockingOptions
{
    public const string SectionName = "Locking";

    public string KeyPrefix { get; set; } = "lock:";
    public int DefaultExpirySeconds { get; set; } = 30;
}
