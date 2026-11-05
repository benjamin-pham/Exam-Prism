namespace Tests.Infrastructure.Orleans;

using Common.MultiTenancy;
using Common.Orleans;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Orleans;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.Extensions;
using FluentAssertions;
using global::Orleans;
using global::Orleans.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Xunit;

public interface IDummyTenantGrain : ITenantScopedGrain
{
}

public sealed class TestTenantGrain : TenantScopedGrain
{
    public TestTenantGrain(IGrainContext? grainContext)
        : base(grainContext)
    {
    }

    public Task TestExecuteInTenantScopeAsync(Func<IServiceProvider, Task> action) =>
        ExecuteInTenantScopeAsync(action);

    public Task<T> TestExecuteInTenantScopeAsync<T>(Func<IServiceProvider, Task<T>> action) =>
        ExecuteInTenantScopeAsync(action);
}

public class OrleansFoundationUnitTests
{
    [Fact]
    public void TenantGrainKey_FormatAndParse_RoundTripsGuidAndCompositeEntityKeyWithColons()
    {
        var tenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var entityKey = "exam-10:attempt-20";

        var formatted = TenantGrainKey.Format(tenantId, entityKey);
        formatted.Should().Be("11111111-2222-3333-4444-555555555555:exam-10:attempt-20");

        var parsed = TenantGrainKey.Parse(formatted);
        parsed.TenantId.Should().Be(tenantId);
        parsed.EntityKey.Should().Be(entityKey);
        parsed.ToString().Should().Be(formatted);

        var success = TenantGrainKey.TryParse(formatted, out var tryParsed);
        success.Should().BeTrue();
        tryParsed.TenantId.Should().Be(tenantId);
        tryParsed.EntityKey.Should().Be(entityKey);
    }

    [Fact]
    public void TenantGrainKey_RejectsEmptyGuid_BlankEntityKey_AndMalformedCompoundKeys()
    {
        var validGuid = Guid.NewGuid();

        var actEmptyGuid = () => TenantGrainKey.Format(Guid.Empty, "k");
        actEmptyGuid.Should().Throw<ArgumentException>();

        var actBlankKey = () => TenantGrainKey.Format(validGuid, "  ");
        actBlankKey.Should().Throw<ArgumentException>();

        string?[] malformedKeys =
        [
            null,
            "",
            "not-a-guid:key",
            "00000000-0000-0000-0000-000000000000:key",
            "11111111-2222-3333-4444-555555555555:",
            "11111111-2222-3333-4444-555555555555"
        ];

        foreach (var badKey in malformedKeys)
        {
            TenantGrainKey.TryParse(badKey, out _).Should().BeFalse($"key '{badKey}' should be rejected");
        }
    }

    [Fact]
    public void TenantGrainFactory_GetGrain_UsesAmbientTenantContext_FormatsCompoundKey_AndSetsRequestContext()
    {
        var tenantId = Guid.NewGuid();
        var grainFactory = Substitute.For<IGrainFactory>();
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.HasTenant.Returns(true);
        tenantContext.TenantId.Returns(tenantId);

        var dummyGrain = Substitute.For<IDummyTenantGrain>();
        grainFactory.GetGrain<IDummyTenantGrain>($"{tenantId:D}:attempt-1").Returns(dummyGrain);

        RequestContext.Clear();

        var factory = new TenantGrainFactory(grainFactory, tenantContext);
        var grain = factory.GetGrain<IDummyTenantGrain>("attempt-1");

        grain.Should().BeSameAs(dummyGrain);
        grainFactory.Received(1).GetGrain<IDummyTenantGrain>($"{tenantId:D}:attempt-1");
        RequestContext.Get(OrleansTenantConstants.TenantIdRequestContextKey).Should().Be(tenantId.ToString("D"));
    }

    [Fact]
    public void TenantGrainFactory_GetGrain_WithoutActiveTenant_ThrowsInvalidOperationException()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.HasTenant.Returns(false);
        tenantContext.TenantId.Returns((Guid?)null);

        var factory = new TenantGrainFactory(grainFactory, tenantContext);
        var act = () => factory.GetGrain<IDummyTenantGrain>("attempt-1");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*active tenant context*");
    }

    [Fact]
    public void TenantGrainFactory_GetGrainForTenant_ResolvesWithExplicitTenantIdEvenWhenAmbientTenantAbsent()
    {
        var tenantId = Guid.NewGuid();
        var entityKeyGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var grainFactory = Substitute.For<IGrainFactory>();
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.HasTenant.Returns(false);

        var dummyGrain = Substitute.For<IDummyTenantGrain>();
        var expectedCompoundKey = $"{tenantId:D}:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        grainFactory.GetGrain<IDummyTenantGrain>(expectedCompoundKey).Returns(dummyGrain);

        var factory = new TenantGrainFactory(grainFactory, tenantContext);
        var grain = factory.GetGrainForTenant<IDummyTenantGrain>(tenantId, entityKeyGuid);

        grain.Should().BeSameAs(dummyGrain);
        grainFactory.Received(1).GetGrain<IDummyTenantGrain>(expectedCompoundKey);
        RequestContext.Get(OrleansTenantConstants.TenantIdRequestContextKey).Should().Be(tenantId.ToString("D"));
    }

    [Fact]
    public async Task OutgoingFilter_WhenRequestContextEmptyAndTenantActive_PopulatesTenantIdInRequestContext()
    {
        var tenantId = Guid.NewGuid();
        var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        var multiTenantContext = Substitute.For<IMultiTenantContext<ExamPrismTenantInfo>>();
        var tenantInfo = new ExamPrismTenantInfo(tenantId.ToString("D"), "school-a")
        {
            TenantGuid = tenantId,
            Status = TenantStatus.Active
        };
        multiTenantContext.TenantInfo.Returns(tenantInfo);
        accessor.MultiTenantContext.Returns(multiTenantContext);

        RequestContext.Clear();

        var context = Substitute.For<IOutgoingGrainCallContext>();
        context.Invoke().Returns(Task.CompletedTask);

        var filter = new TenantOutgoingGrainCallFilter(accessor);
        await filter.Invoke(context);

        RequestContext.Get(OrleansTenantConstants.TenantIdRequestContextKey).Should().Be(tenantId.ToString("D"));
        await context.Received(1).Invoke();
    }

    [Fact]
    public async Task IncomingFilter_ForNonTenantScopedGrain_InvokesContextDirectlyWithoutKeyValidation()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var filter = new TenantIncomingGrainCallFilter(scopeFactory);

        var context = Substitute.For<IIncomingGrainCallContext>();
        var plainGrain = Substitute.For<IGrain>();
        context.Grain.Returns(plainGrain);
        context.Invoke().Returns(Task.CompletedTask);

        await filter.Invoke(context);

        await context.Received(1).Invoke();
        scopeFactory.DidNotReceiveWithAnyArgs().CreateScope();
    }

    [Fact]
    public async Task IncomingFilter_ForTenantScopedGrain_HydratesMultiTenantContext_AndRestoresPriorContextInFinallyEvenOnException()
    {
        var tenantA = Guid.NewGuid();
        var tenantAInfo = new ExamPrismTenantInfo(tenantA.ToString("D"), "school-a")
        {
            TenantGuid = tenantA,
            Status = TenantStatus.Active
        };

        var priorTenant = Guid.NewGuid();
        var priorTenantInfo = new ExamPrismTenantInfo(priorTenant.ToString("D"), "prior-tenant")
        {
            TenantGuid = priorTenant,
            Status = TenantStatus.Active
        };
        var priorContext = new MultiTenantContext<ExamPrismTenantInfo>(priorTenantInfo);

        var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();
        store.GetAsync(tenantA.ToString("D")).Returns(tenantAInfo);

        var services = new ServiceCollection();
        services.AddMultiTenant<ExamPrismTenantInfo>();
        services.AddSingleton(store);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var accessor = sp.GetRequiredService<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        var setter = sp.GetRequiredService<IMultiTenantContextSetter>();

        var filter = new TenantIncomingGrainCallFilter(scopeFactory);

        // Case 1: Normal execution - hydrates context during Invoke and restores priorContext in finally
        setter.MultiTenantContext = priorContext;
        RequestContext.Clear();

        var context1 = Substitute.For<IIncomingGrainCallContext>();
        var dummyGrain1 = Substitute.For<IDummyTenantGrain>();
        context1.Grain.Returns(dummyGrain1);
        context1.TargetId.Returns(GrainId.Create(GrainType.Create("dummy"), IdSpan.Create($"{tenantA:D}:attempt-1")));

        string? hydratedTenantIdDuringInvoke = null;
        context1.Invoke().Returns(_ =>
        {
            hydratedTenantIdDuringInvoke = accessor.MultiTenantContext?.TenantInfo?.Id;
            return Task.CompletedTask;
        });

        await filter.Invoke(context1);

        hydratedTenantIdDuringInvoke.Should().Be(tenantA.ToString("D"));
        accessor.MultiTenantContext.Should().BeSameAs(priorContext);

        // Case 2: Exception thrown inside Invoke - finally restores priorContext
        setter.MultiTenantContext = priorContext;
        RequestContext.Clear();

        var context2 = Substitute.For<IIncomingGrainCallContext>();
        var dummyGrain2 = Substitute.For<IDummyTenantGrain>();
        context2.Grain.Returns(dummyGrain2);
        context2.TargetId.Returns(GrainId.Create(GrainType.Create("dummy"), IdSpan.Create($"{tenantA:D}:attempt-1")));
        context2.Invoke().Returns<Task>(_ => throw new InvalidOperationException("boom"));

        var act = () => filter.Invoke(context2);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        accessor.MultiTenantContext.Should().BeSameAs(priorContext);
    }

    [Fact]
    public async Task IncomingFilter_RejectsMalformedKey_CrossTenantRequestContext_AndInactiveOrUnknownTenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();
        var services = new ServiceCollection();
        services.AddMultiTenant<ExamPrismTenantInfo>();
        services.AddSingleton(store);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var filter = new TenantIncomingGrainCallFilter(scopeFactory);

        // (a) Malformed key
        RequestContext.Clear();
        var contextBadKey = Substitute.For<IIncomingGrainCallContext>();
        var grainBadKey = Substitute.For<IDummyTenantGrain>();
        contextBadKey.Grain.Returns(grainBadKey);
        contextBadKey.TargetId.Returns(GrainId.Create(GrainType.Create("dummy"), IdSpan.Create("bad-key")));

        var actBadKey = () => filter.Invoke(contextBadKey);
        await actBadKey.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Invalid tenant grain key format 'bad-key'*");

        // (b) Cross-tenant RequestContext: RequestContext has tenantB while grain has tenantA
        RequestContext.Set(OrleansTenantConstants.TenantIdRequestContextKey, tenantB.ToString("D"));
        var contextCrossTenant = Substitute.For<IIncomingGrainCallContext>();
        var grainCrossTenant = Substitute.For<IDummyTenantGrain>();
        contextCrossTenant.Grain.Returns(grainCrossTenant);
        contextCrossTenant.TargetId.Returns(GrainId.Create(GrainType.Create("dummy"), IdSpan.Create($"{tenantA:D}:attempt-1")));

        var actCrossTenant = () => filter.Invoke(contextCrossTenant);
        await actCrossTenant.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cross-tenant grain call rejected*");

        // (c) Store returns null (unknown tenant)
        RequestContext.Clear();
        store.GetAsync(tenantA.ToString("D")).Returns((ExamPrismTenantInfo?)null);
        var contextUnknownTenant = Substitute.For<IIncomingGrainCallContext>();
        var grainUnknownTenant = Substitute.For<IDummyTenantGrain>();
        contextUnknownTenant.Grain.Returns(grainUnknownTenant);
        contextUnknownTenant.TargetId.Returns(GrainId.Create(GrainType.Create("dummy"), IdSpan.Create($"{tenantA:D}:attempt-1")));

        var actUnknownTenant = () => filter.Invoke(contextUnknownTenant);
        await actUnknownTenant.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*Cannot execute grain call for inactive or unknown tenant '{tenantA:D}'*");

        // (d) Store returns tenant with Status = TenantStatus.Suspended
        var suspendedTenant = new ExamPrismTenantInfo(tenantA.ToString("D"), "suspended-school")
        {
            TenantGuid = tenantA,
            Status = TenantStatus.Suspended
        };
        store.GetAsync(tenantA.ToString("D")).Returns(suspendedTenant);

        var actSuspendedTenant = () => filter.Invoke(contextUnknownTenant);
        await actSuspendedTenant.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*Cannot execute grain call for inactive or unknown tenant '{tenantA:D}'*");
    }

    [Fact]
    public async Task OrleansHealthCheck_ReturnsHealthyWhenSiloActive_AndUnhealthyWhenNotActiveOrNull()
    {
        // 1. SiloStatus.Active -> Healthy
        var oracle = Substitute.For<ISiloStatusOracle>();
        oracle.CurrentStatus.Returns(SiloStatus.Active);
        var healthCheckActive = new OrleansHealthCheck(oracle);

        var resultActive = await healthCheckActive.CheckHealthAsync(new HealthCheckContext());
        resultActive.Status.Should().Be(HealthStatus.Healthy);
        resultActive.Description.Should().Contain("active");

        // 2. SiloStatus.Dead -> Unhealthy
        oracle.CurrentStatus.Returns(SiloStatus.Dead);
        var healthCheckDead = new OrleansHealthCheck(oracle);

        var resultDead = await healthCheckDead.CheckHealthAsync(new HealthCheckContext());
        resultDead.Status.Should().Be(HealthStatus.Unhealthy);
        resultDead.Description.Should().Contain("Dead");

        // 3. SiloStatusOracle == null -> Unhealthy
        var healthCheckNull = new OrleansHealthCheck(null);
        var resultNull = await healthCheckNull.CheckHealthAsync(new HealthCheckContext());
        resultNull.Status.Should().Be(HealthStatus.Unhealthy);
        resultNull.Description.Should().Contain("unavailable");
    }

    [Fact]
    public async Task TenantScopedGrain_OnActivateAsync_SetsTenantKey_And_ThrowsOnMalformedKey()
    {
        var tenantId = Guid.NewGuid();
        var validKey = $"{tenantId:D}:attempt-1";

        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainId.Returns(GrainId.Create(GrainType.Create("test"), IdSpan.Create(validKey)));

        var grain = new TestTenantGrain(grainContext);
        await grain.OnActivateAsync(CancellationToken.None);

        grain.TenantKey.TenantId.Should().Be(tenantId);
        grain.TenantKey.EntityKey.Should().Be("attempt-1");

        // Malformed key throws InvalidOperationException
        var badContext = Substitute.For<IGrainContext>();
        badContext.GrainId.Returns(GrainId.Create(GrainType.Create("test"), IdSpan.Create("bad-key")));

        var badGrain = new TestTenantGrain(badContext);
        var act = () => badGrain.OnActivateAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Grain 'TestTenantGrain' cannot activate with invalid tenant key 'bad-key'*");
    }

    [Fact]
    public async Task TenantScopedGrain_ExecuteInTenantScopeAsync_HydratesContext_AndRestoresPriorContextInFinallyEvenOnException()
    {
        var tenantId = Guid.NewGuid();
        var validKey = $"{tenantId:D}:attempt-1";

        var tenantInfo = new ExamPrismTenantInfo(tenantId.ToString("D"), "school-a")
        {
            TenantGuid = tenantId,
            Status = TenantStatus.Active
        };

        var priorTenantId = Guid.NewGuid();
        var priorTenantInfo = new ExamPrismTenantInfo(priorTenantId.ToString("D"), "prior-tenant")
        {
            TenantGuid = priorTenantId,
            Status = TenantStatus.Active
        };
        var priorContext = new MultiTenantContext<ExamPrismTenantInfo>(priorTenantInfo);

        var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();
        store.GetAsync(tenantId.ToString("D")).Returns(tenantInfo);

        var services = new ServiceCollection();
        services.AddMultiTenant<ExamPrismTenantInfo>();
        services.AddSingleton(store);
        var sp = services.BuildServiceProvider();

        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainId.Returns(GrainId.Create(GrainType.Create("test"), IdSpan.Create(validKey)));
        grainContext.ActivationServices.Returns(sp);

        var grain = new TestTenantGrain(grainContext);
        await grain.OnActivateAsync(CancellationToken.None);

        var accessor = sp.GetRequiredService<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        var setter = sp.GetRequiredService<IMultiTenantContextSetter>();

        // Case 1: Normal execution
        setter.MultiTenantContext = priorContext;
        RequestContext.Clear();

        string? capturedTenantInScope = null;
        string? capturedRequestContextInScope = null;
        var result = await grain.TestExecuteInTenantScopeAsync(provider =>
        {
            capturedTenantInScope = accessor.MultiTenantContext?.TenantInfo?.Id;
            capturedRequestContextInScope = RequestContext.Get(OrleansTenantConstants.TenantIdRequestContextKey) as string;
            return Task.FromResult("ok");
        });

        result.Should().Be("ok");
        capturedTenantInScope.Should().Be(tenantId.ToString("D"));
        capturedRequestContextInScope.Should().Be(tenantId.ToString("D"));
        accessor.MultiTenantContext.Should().BeSameAs(priorContext);

        // Case 2: Action throws -> restores priorContext in finally
        setter.MultiTenantContext = priorContext;

        var act = () => grain.TestExecuteInTenantScopeAsync<string>(_ => throw new InvalidOperationException("boom-scope"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom-scope");

        accessor.MultiTenantContext.Should().BeSameAs(priorContext);
    }

    [Fact]
    public void OrleansOptions_ListenOnAnyHostAddress_DefaultsToFalse()
    {
        var options = new OrleansOptions();
        options.ListenOnAnyHostAddress.Should().BeFalse();
    }

    [Fact]
    public void OrleansOptions_BindsFromConfigurationCorrectly()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orleans:ListenOnAnyHostAddress"] = "true"
            })
            .Build();

        var options = new OrleansOptions();
        config.GetSection("Orleans").Bind(options);

        options.ListenOnAnyHostAddress.Should().BeTrue();
    }
}
