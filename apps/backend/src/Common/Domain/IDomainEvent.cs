namespace Common.Domain;

public interface IDomainEvent
{
    Guid EventId { get; }
    DateTime OccurredOnUtc { get; }
}

public abstract record DomainEventBase(Guid EventId, DateTime OccurredOnUtc) : IDomainEvent
{
    protected DomainEventBase()
        : this(Guid.NewGuid(), DateTime.UtcNow)
    {
    }

    protected DomainEventBase(DateTime occurredOnUtc)
        : this(Guid.NewGuid(), occurredOnUtc)
    {
    }
}

