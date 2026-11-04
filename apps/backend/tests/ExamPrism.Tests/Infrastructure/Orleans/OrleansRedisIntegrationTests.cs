namespace Tests.Infrastructure.Orleans;

using System.Net;
using System.Net.Http.Json;
using Common.Orleans;
using ExamPrism.Features.Health;
using ExamPrism.Infrastructure.Orleans.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using Tests.Infrastructure;
using Xunit;

public class OrleansRedisIntegrationTests : IClassFixture<RedisWebApplicationFactory>
{
    private readonly RedisWebApplicationFactory _factory;

    public OrleansRedisIntegrationTests(RedisWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RedisMode_PersistsGrainStateAcrossDeactivation_RegistersReminder_AndReportsHealthyOnHealthEndpoint()
    {
        // 1. Resolve probe grain in tenant-a scope and set state
        using var scope = await _factory.CreateTenantScopeAsync("tenant-a");
        var grainFactory = scope.ServiceProvider.GetRequiredService<ITenantGrainFactory>();
        var grain = grainFactory.GetGrain<ITenantProbeGrain>("redis-attempt-1");

        var initialSnapshot = await grain.SetAndInspectAsync("persisted-in-redis", "exam:redis", "cached");
        initialSnapshot.StateValue.Should().Be("persisted-in-redis");
        initialSnapshot.CachedValue.Should().Be("cached");

        // 2. Register reminder via Redis reminder table and update state
        await grain.RegisterProbeReminderAsync("auto-submit-reminder");

        // 3. Deactivate grain from memory
        await grain.DeactivateImmediatelyAsync();
        await Task.Delay(500);

        // 4. Resolve grain reference and reactivate from Redis storage
        var reactivatedGrain = grainFactory.GetGrain<ITenantProbeGrain>("redis-attempt-1");
        var snapshot = await reactivatedGrain.GetSnapshotAsync("exam:redis");

        snapshot.StateValue.Should().Be("persisted-in-redis");
        snapshot.LastReminderName.Should().Be("auto-submit-reminder");

        // 5. Verify Redis contains keys created by Orleans
        var multiplexer = _factory.Services.GetRequiredService<IConnectionMultiplexer>();
        var server = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);
        var keys = server.Keys().Select(k => (string)k!).ToList();
        keys.Should().Contain(k => k.Contains("redis-attempt-1"));

        // 6. Verify /health returns 200 OK ("Healthy")
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var healthResult = await response.Content.ReadFromJsonAsync<GetHealthResult>();
        healthResult.Should().NotBeNull();
        healthResult!.Status.Should().Be("Healthy");

        // 7. Verify HealthCheckService details include catalog_db, redis, and orleans
        var healthCheckService = _factory.Services.GetRequiredService<HealthCheckService>();
        var report = await healthCheckService.CheckHealthAsync();
        report.Status.Should().Be(HealthStatus.Healthy);
        report.Entries.Should().ContainKeys("catalog_db", "redis", "orleans");
        report.Entries["catalog_db"].Status.Should().Be(HealthStatus.Healthy);
        report.Entries["redis"].Status.Should().Be(HealthStatus.Healthy);
        report.Entries["orleans"].Status.Should().Be(HealthStatus.Healthy);
    }
}
