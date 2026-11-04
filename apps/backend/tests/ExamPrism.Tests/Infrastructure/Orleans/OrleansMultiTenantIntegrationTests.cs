namespace Tests.Infrastructure.Orleans;

using Common.Caching;
using Common.MultiTenancy;
using Common.Orleans;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Orleans.Diagnostics;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentAssertions;
using global::Orleans.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tests.Infrastructure;
using Xunit;

public class OrleansMultiTenantIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OrleansMultiTenantIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GrainState_And_HybridCache_AreStrictlyIsolatedAcrossTenantsForIdenticalEntityKey()
    {
        using var scopeA = await _factory.CreateTenantScopeAsync("tenant-a");
        using var scopeB = await _factory.CreateTenantScopeAsync("tenant-b");

        // 1. Resolve and invoke grain on tenant A
        ((TenantServiceScope)scopeA).SetContext();
        var factoryA = scopeA.ServiceProvider.GetRequiredService<ITenantGrainFactory>();
        var grainA = factoryA.GetGrain<ITenantProbeGrain>("attempt-shared");
        var snapshotA = await grainA.SetAndInspectAsync("ans-A", "exam:1:rubric", "rubric-A");

        // 2. Resolve and invoke grain on tenant B
        ((TenantServiceScope)scopeB).SetContext();
        var factoryB = scopeB.ServiceProvider.GetRequiredService<ITenantGrainFactory>();
        var grainB = factoryB.GetGrain<ITenantProbeGrain>("attempt-shared");
        var snapshotB = await grainB.SetAndInspectAsync("ans-B", "exam:1:rubric", "rubric-B");

        // 3. Assert complete isolation between tenant A and tenant B
        snapshotA.TenantId.Should().NotBe(snapshotB.TenantId);
        snapshotA.TenantId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        snapshotB.TenantId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        snapshotA.EntityKey.Should().Be("attempt-shared");
        snapshotB.EntityKey.Should().Be("attempt-shared");

        snapshotA.StateValue.Should().Be("ans-A");
        snapshotB.StateValue.Should().Be("ans-B");

        snapshotA.CachedValue.Should().Be("rubric-A");
        snapshotB.CachedValue.Should().Be("rubric-B");

        // 4. Verify across second snapshot call to ensure grain A state was not mutated by grain B
        ((TenantServiceScope)scopeA).SetContext();
        RequestContext.Set(OrleansTenantConstants.TenantIdRequestContextKey, snapshotA.TenantId.ToString("D"));
        var readA = await grainA.GetSnapshotAsync("exam:1:rubric");

        ((TenantServiceScope)scopeB).SetContext();
        RequestContext.Set(OrleansTenantConstants.TenantIdRequestContextKey, snapshotB.TenantId.ToString("D"));
        var readB = await grainB.GetSnapshotAsync("exam:1:rubric");

        readA.StateValue.Should().Be("ans-A");
        readB.StateValue.Should().Be("ans-B");
        readA.CachedValue.Should().Be("rubric-A");
        readB.CachedValue.Should().Be("rubric-B");

        // 5. Verify direct ICacheService resolution in each tenant scope
        ((TenantServiceScope)scopeA).SetContext();
        var cacheA = scopeA.ServiceProvider.GetRequiredService<ICacheService>();
        var cachedValA = await cacheA.GetOrDefaultAsync<string>("exam:1:rubric");

        ((TenantServiceScope)scopeB).SetContext();
        var cacheB = scopeB.ServiceProvider.GetRequiredService<ICacheService>();
        var cachedValB = await cacheB.GetOrDefaultAsync<string>("exam:1:rubric");

        cachedValA.Should().Be("rubric-A");
        cachedValB.Should().Be("rubric-B");
    }

    [Fact]
    public async Task ExecuteInTenantScopeAsync_WhenRequestContextAndAsyncLocalCleared_RehydratesTenantForHybridCacheAndAppDbContext()
    {
        // 1. Register a user in tenant-a so tenant-a has 1 more user than tenant-b
        using (var seedScopeA = await _factory.CreateTenantScopeAsync("tenant-a"))
        {
            var dbA = seedScopeA.ServiceProvider.GetRequiredService<AppDbContext>();
            var newUser = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = $"student_{Guid.NewGuid():N}@tenant-a.com",
                Email = $"student_{Guid.NewGuid():N}@tenant-a.com",
                NormalizedUserName = $"STUDENT_{Guid.NewGuid():N}@TENANT-A.COM",
                NormalizedEmail = $"STUDENT_{Guid.NewGuid():N}@TENANT-A.COM",
                SecurityStamp = Guid.NewGuid().ToString()
            };
            dbA.Users.Add(newUser);
            await dbA.SaveChangesAsync();
        }

        using var scopeA = await _factory.CreateTenantScopeAsync("tenant-a");
        using var scopeB = await _factory.CreateTenantScopeAsync("tenant-b");

        ((TenantServiceScope)scopeA).SetContext();
        var factoryA = scopeA.ServiceProvider.GetRequiredService<ITenantGrainFactory>();
        var grainA = factoryA.GetGrain<ITenantProbeGrain>("timer-scope-test");

        ((TenantServiceScope)scopeB).SetContext();
        var factoryB = scopeB.ServiceProvider.GetRequiredService<ITenantGrainFactory>();
        var grainB = factoryB.GetGrain<ITenantProbeGrain>("timer-scope-test");

        ((TenantServiceScope)scopeA).SetContext();
        RequestContext.Set(OrleansTenantConstants.TenantIdRequestContextKey, "11111111-1111-1111-1111-111111111111");
        var snapshotA = await grainA.SimulateTimerScopeCallbackAsync("timer:key", "timer-val-A");

        ((TenantServiceScope)scopeB).SetContext();
        RequestContext.Set(OrleansTenantConstants.TenantIdRequestContextKey, "22222222-2222-2222-2222-222222222222");
        var snapshotB = await grainB.SimulateTimerScopeCallbackAsync("timer:key", "timer-val-B");

        snapshotA.TenantUserCount.Should().Be(snapshotB.TenantUserCount + 1);

        ((TenantServiceScope)scopeA).SetContext();
        var cacheA = scopeA.ServiceProvider.GetRequiredService<ICacheService>();
        var cachedValA = await cacheA.GetOrDefaultAsync<string>("timer:key");
        cachedValA.Should().Be("timer-val-A");

        ((TenantServiceScope)scopeB).SetContext();
        var cacheB = scopeB.ServiceProvider.GetRequiredService<ICacheService>();
        var cachedValB = await cacheB.GetOrDefaultAsync<string>("timer:key");
        cachedValB.Should().Be("timer-val-B");

        // Verify root/platform cache without tenant scope does NOT read "timer-val-A"
        var rootCache = _factory.Services.GetRequiredService<ICacheService>();
        var platformVal = await rootCache.GetOrDefaultAsync<string>("timer:key");
        platformVal.Should().NotBe("timer-val-A");
    }

    [Fact]
    public async Task GrainCall_ForSuspendedOrUnknownTenant_ThrowsInvalidOperationException()
    {
        using var scope = _factory.Services.CreateScope();
        var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var suspendedTenant = await catalogDb.Tenants.FirstAsync(t => t.Status == TenantStatus.Suspended);
        var suspendedTenantId = suspendedTenant.Id;

        var factory = scope.ServiceProvider.GetRequiredService<ITenantGrainFactory>();

        var suspendedGrain = factory.GetGrainForTenant<ITenantProbeGrain>(suspendedTenantId, "attempt-1");
        var actSuspended = () => suspendedGrain.GetSnapshotAsync("k");
        await actSuspended.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*inactive or unknown tenant '{suspendedTenantId:D}'*");

        var unknownTenantId = Guid.NewGuid();
        var unknownGrain = factory.GetGrainForTenant<ITenantProbeGrain>(unknownTenantId, "attempt-1");
        var actUnknown = () => unknownGrain.GetSnapshotAsync("k");
        await actUnknown.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*inactive or unknown tenant '{unknownTenantId:D}'*");
    }
}
