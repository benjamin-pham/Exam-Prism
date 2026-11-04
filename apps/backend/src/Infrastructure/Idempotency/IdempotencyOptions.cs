namespace ExamPrism.Infrastructure.Idempotency;

public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    public string HeaderName { get; set; } = "Idempotency-Key";
    public int ExpirationMinutes { get; set; } = 1440;
    public int LockTimeoutSeconds { get; set; } = 30;
}
