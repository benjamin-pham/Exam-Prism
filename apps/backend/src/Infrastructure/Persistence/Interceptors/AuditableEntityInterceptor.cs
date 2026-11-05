namespace ExamPrism.Infrastructure.Persistence.Interceptors;

using Common.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

public sealed class AuditableEntityInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        UpdateAuditProperties(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        UpdateAuditProperties(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void UpdateAuditProperties(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        foreach (var entry in context.ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAt == default)
                {
                    entry.Entity.SetCreated(now);
                }

                if (entry.Entity.UpdatedAt == default)
                {
                    entry.Entity.SetUpdated(now);
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                var updatedProp = entry.Property(nameof(IAuditableEntity.UpdatedAt));
                if (!updatedProp.IsModified || entry.Entity.UpdatedAt == default)
                {
                    entry.Entity.SetUpdated(now);
                }
            }
        }
    }
}

