namespace Common.Domain;

public interface IAuditableEntity
{
    DateTime CreatedAt { get; }
    DateTime UpdatedAt { get; }
    void SetCreated(DateTime now);
    void SetUpdated(DateTime now);
}

