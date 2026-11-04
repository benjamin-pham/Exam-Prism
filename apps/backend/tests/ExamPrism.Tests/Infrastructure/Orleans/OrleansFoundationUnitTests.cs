namespace Tests.Infrastructure.Orleans;

using Common.MultiTenancy;
using Common.Orleans;
using ExamPrism.Infrastructure.Orleans;
using FluentAssertions;
using global::Orleans;
using global::Orleans.Runtime;
using NSubstitute;
using Xunit;

public interface IDummyTenantGrain : ITenantScopedGrain
{
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
}
