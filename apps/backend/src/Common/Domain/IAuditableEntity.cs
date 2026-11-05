namespace Common.Domain;

public interface IAuditableEntity
{
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset UpdatedAt { get; }
    void SetCreated(DateTimeOffset now);
    void SetUpdated(DateTimeOffset now);
}

