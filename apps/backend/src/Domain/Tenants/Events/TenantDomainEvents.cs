namespace ExamPrism.Domain.Tenants.Events;

using Common.Domain;

public sealed record TenantCreatedDomainEvent(
    Guid TenantId,
    string Slug,
    DateTime OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

public sealed record TenantRenamedDomainEvent(
    Guid TenantId,
    string OldName,
    string NewName,
    DateTime OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

public sealed record TenantSuspendedDomainEvent(
    Guid TenantId,
    string Slug,
    DateTime OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

public sealed record TenantReactivatedDomainEvent(
    Guid TenantId,
    string Slug,
    DateTime OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

public sealed record TenantProvisioningRetriedDomainEvent(
    Guid TenantId,
    string Slug,
    DateTime OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

public sealed record TenantProvisionedDomainEvent(
    Guid TenantId,
    string Slug,
    DateTime OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

public sealed record TenantProvisioningFailedDomainEvent(
    Guid TenantId,
    string Slug,
    string FailureReason,
    DateTime OccurredOnUtc) : DomainEventBase(OccurredOnUtc);

