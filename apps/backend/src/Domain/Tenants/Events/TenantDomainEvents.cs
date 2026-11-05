namespace ExamPrism.Domain.Tenants.Events;

using Common.Domain;

public sealed record TenantCreatedDomainEvent(
    Guid TenantId,
    string Slug,
    DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

public sealed record TenantRenamedDomainEvent(
    Guid TenantId,
    string OldName,
    string NewName,
    DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

public sealed record TenantSuspendedDomainEvent(
    Guid TenantId,
    string Slug,
    DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

public sealed record TenantReactivatedDomainEvent(
    Guid TenantId,
    string Slug,
    DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

public sealed record TenantProvisioningRetriedDomainEvent(
    Guid TenantId,
    string Slug,
    DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

public sealed record TenantProvisionedDomainEvent(
    Guid TenantId,
    string Slug,
    DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

public sealed record TenantProvisioningFailedDomainEvent(
    Guid TenantId,
    string Slug,
    string FailureReason,
    DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

