namespace ExamPrism.Infrastructure.Persistence.Interceptors;

using System.Runtime.CompilerServices;
using Common.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

public sealed class DomainEventDispatchInterceptor(IServiceProvider serviceProvider) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, List<IDomainEvent>> _pendingEvents = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        CaptureDomainEvents(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CaptureDomainEvents(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null && _pendingEvents.TryGetValue(eventData.Context, out var events))
        {
            _pendingEvents.Remove(eventData.Context);

            if (events.Count > 0)
            {
                var bus = serviceProvider.GetService<IMessageBus>();
                if (bus is not null)
                {
                    foreach (var domainEvent in events)
                    {
                        try
                        {
                            await bus.InvokeAsync(domainEvent, cancellationToken);
                        }
                        catch (Wolverine.Runtime.Routing.IndeterminateRoutesException)
                        {
                            // No subscribers or handlers currently registered for this domain event; safe to ignore.
                        }
                    }
                }
            }
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is not null)
        {
            _pendingEvents.Remove(eventData.Context);
        }

        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            _pendingEvents.Remove(eventData.Context);
        }

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void CaptureDomainEvents(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var aggregates = context.ChangeTracker.Entries<IAggregateRoot>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToList();

        if (aggregates.Count == 0)
        {
            return;
        }

        if (!_pendingEvents.TryGetValue(context, out var list))
        {
            list = [];
            _pendingEvents.Add(context, list);
        }

        foreach (var aggregate in aggregates)
        {
            list.AddRange(aggregate.DequeueDomainEvents());
        }
    }
}

