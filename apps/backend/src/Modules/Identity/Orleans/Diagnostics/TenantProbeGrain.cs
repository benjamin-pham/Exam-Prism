namespace ExamPrism.Infrastructure.Orleans.Diagnostics;

using Common.Caching;
using Common.Orleans;
using ExamPrism.Infrastructure.Orleans;
using ExamPrism.Infrastructure.Persistence;
using Finbuckle.MultiTenant.Abstractions;
using global::Orleans;
using global::Orleans.Runtime;
using global::Orleans.Timers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[GenerateSerializer]
public sealed record TenantProbeState(
    [property: Id(0)] string? Value = null,
    [property: Id(1)] int Counter = 0,
    [property: Id(2)] string? LastReminderName = null);

[GenerateSerializer]
public sealed record TenantProbeSnapshot(
    [property: Id(0)] Guid TenantId,
    [property: Id(1)] string EntityKey,
    [property: Id(2)] string? StateValue,
    [property: Id(3)] int Counter,
    [property: Id(4)] string? CachedValue,
    [property: Id(5)] int TenantUserCount,
    [property: Id(6)] string? LastReminderName);

public interface ITenantProbeGrain : ITenantScopedGrain
{
    Task<TenantProbeSnapshot> SetAndInspectAsync(string value, string cacheKey, string cacheValue);
    Task<TenantProbeSnapshot> GetSnapshotAsync(string cacheKey);
    Task<TenantProbeSnapshot> SimulateTimerScopeCallbackAsync(string cacheKey, string cacheValue);
    Task RegisterProbeReminderAsync(string reminderName);
    Task DeactivateImmediatelyAsync();
}

public sealed class TenantProbeGrain : TenantScopedGrain, ITenantProbeGrain, IRemindable
{
    private readonly IPersistentState<TenantProbeState> _state;
    private readonly ICacheService _cacheService;

    public TenantProbeGrain(
        [PersistentState("probe", OrleansTenantConstants.DefaultStateStore)] IPersistentState<TenantProbeState> state,
        ICacheService cacheService)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
    }

    public async Task<TenantProbeSnapshot> SetAndInspectAsync(string value, string cacheKey, string cacheValue)
    {
        var current = _state.State ?? new TenantProbeState();
        _state.State = current with
        {
            Value = value,
            Counter = current.Counter + 1
        };
        await _state.WriteStateAsync();

        var cached = await _cacheService.GetOrCreateAsync(cacheKey, _ => ValueTask.FromResult(cacheValue));

        var userCount = await ExecuteInTenantScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return await db.Users.CountAsync();
        });

        return new TenantProbeSnapshot(
            TenantId,
            EntityKey,
            _state.State.Value,
            _state.State.Counter,
            cached,
            userCount,
            _state.State.LastReminderName);
    }

    public async Task<TenantProbeSnapshot> GetSnapshotAsync(string cacheKey)
    {
        var current = _state.State ?? new TenantProbeState();
        var cached = await _cacheService.GetOrDefaultAsync<string>(cacheKey);

        var userCount = await ExecuteInTenantScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return await db.Users.CountAsync();
        });

        return new TenantProbeSnapshot(
            TenantId,
            EntityKey,
            current.Value,
            current.Counter,
            cached,
            userCount,
            current.LastReminderName);
    }

    public async Task<TenantProbeSnapshot> SimulateTimerScopeCallbackAsync(string cacheKey, string cacheValue)
    {
        RequestContext.Clear();
        var setter = ServiceProvider.GetService<IMultiTenantContextSetter>();
        if (setter != null)
        {
            setter.MultiTenantContext = null!;
        }

        return await ExecuteInTenantScopeAsync(async sp =>
        {
            var scopedCache = sp.GetRequiredService<ICacheService>();
            var cached = await scopedCache.GetOrCreateAsync(cacheKey, _ => ValueTask.FromResult(cacheValue));
            var db = sp.GetRequiredService<AppDbContext>();
            var userCount = await db.Users.CountAsync();

            var current = _state.State ?? new TenantProbeState();
            return new TenantProbeSnapshot(
                TenantId,
                EntityKey,
                current.Value,
                current.Counter,
                cached,
                userCount,
                current.LastReminderName);
        });
    }

    public async Task RegisterProbeReminderAsync(string reminderName)
    {
        var reminder = await this.RegisterOrUpdateReminder(
            reminderName,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));

        await this.UnregisterReminder(reminder);

        var current = _state.State ?? new TenantProbeState();
        _state.State = current with { LastReminderName = reminderName };
        await _state.WriteStateAsync();
    }

    public Task DeactivateImmediatelyAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    Task IRemindable.ReceiveReminder(string reminderName, TickStatus status)
    {
        return Task.CompletedTask;
    }
}
