namespace Common.Domain;

public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredOnUtc { get; }
}

public abstract record DomainEventBase(Guid EventId, DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    protected DomainEventBase()
        : this(Guid.NewGuid(), DateTimeOffset.UtcNow)
    {
    }

    protected DomainEventBase(DateTimeOffset occurredOnUtc)
        : this(Guid.NewGuid(), occurredOnUtc)
    {
    }
}

