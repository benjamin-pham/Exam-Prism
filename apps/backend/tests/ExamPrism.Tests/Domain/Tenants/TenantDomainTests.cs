namespace ExamPrism.Tests.Domain.Tenants;

using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Domain.Tenants;
using ExamPrism.Domain.Tenants.Events;
using ExamPrism.Domain.Tenants.Specifications;
using FluentAssertions;
using Xunit;

public class TenantDomainTests
{
    [Fact]
    public void TenantSlug_Create_TrimsAndLowercasesValidSlug_AndReturnsValidationErrorForInvalidSlug()
    {
        var validResult = TenantSlug.Create("  Acme-Org  ");
        validResult.IsSuccess.Should().BeTrue();
        validResult.Value.Value.Should().Be("acme-org");
        string str = validResult.Value;
        str.Should().Be("acme-org");

        var invalidResult = TenantSlug.Create("-invalid-slug");
        invalidResult.IsFailure.Should().BeTrue();
        invalidResult.Error.Type.Should().Be(ErrorType.Validation);
        invalidResult.Error.ValidationErrors.Should().Contain(e => e.Code == "INVALID_SLUG");

        var emptyResult = TenantSlug.Create("");
        emptyResult.IsFailure.Should().BeTrue();
        emptyResult.Error.Type.Should().Be(ErrorType.Validation);
        emptyResult.Error.ValidationErrors.Should().Contain(e => e.Code == "INVALID_SLUG");
    }

    [Fact]
    public void Tenant_Create_ValidatesInvariants_InitializesProvisioningStatus_AndRaisesTenantCreatedDomainEvent()
    {
        var now = DateTimeOffset.UtcNow;
        var result = Tenant.Create("my-org", "My Organization", "owner@example.com", "enc-pass", now);

        result.IsSuccess.Should().BeTrue();
        var tenant = result.Value;

        tenant.Id.Should().NotBeEmpty();
        tenant.Slug.Should().Be("my-org");
        tenant.Name.Should().Be("My Organization");
        tenant.OwnerEmail.Should().Be("owner@example.com");
        tenant.EncryptedOwnerPassword.Should().Be("enc-pass");
        tenant.Status.Should().Be(TenantStatus.Provisioning);
        tenant.CreatedAt.Should().Be(now);
        tenant.UpdatedAt.Should().Be(now);

        var events = tenant.DequeueDomainEvents();
        events.Should().ContainSingle();
        var createdEvent = events[0].Should().BeOfType<TenantCreatedDomainEvent>().Subject;
        createdEvent.TenantId.Should().Be(tenant.Id);
        createdEvent.Slug.Should().Be("my-org");

        // Invariant: empty name rejected
        var badNameRes = Tenant.Create("my-org", "   ", "owner@example.com", "enc-pass", now);
        badNameRes.IsFailure.Should().BeTrue();
        badNameRes.Error.Type.Should().Be(ErrorType.Validation);
        badNameRes.Error.ValidationErrors.Should().Contain(e => e.Code == "REQUIRED");

        // Invariant: bad slug rejected
        var badSlugRes = Tenant.Create("_bad", "Name", "owner@example.com", "enc-pass", now);
        badSlugRes.IsFailure.Should().BeTrue();
        badSlugRes.Error.Type.Should().Be(ErrorType.Validation);
        badSlugRes.Error.ValidationErrors.Should().Contain(e => e.Code == "INVALID_SLUG");
    }

    [Fact]
    public void Tenant_Rename_TrimsName_ValidatesLength_UpdatesTimestamp_AndRaisesTenantRenamedDomainEvent()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = Tenant.Create("test-org", "Original Name", "owner@test.com", "pass", now).Value;
        tenant.ClearDomainEvents();

        var updatedNow = now.AddMinutes(10);
        var renameResult = tenant.Rename("   Updated Name   ", updatedNow);

        renameResult.IsSuccess.Should().BeTrue();
        tenant.Name.Should().Be("Updated Name");
        tenant.UpdatedAt.Should().Be(updatedNow);

        var events = tenant.DequeueDomainEvents();
        events.Should().ContainSingle();
        var renamedEvent = events[0].Should().BeOfType<TenantRenamedDomainEvent>().Subject;
        renamedEvent.TenantId.Should().Be(tenant.Id);
        renamedEvent.OldName.Should().Be("Original Name");
        renamedEvent.NewName.Should().Be("Updated Name");

        // Validation: empty name
        tenant.Rename("  ", updatedNow).IsFailure.Should().BeTrue();
        // Validation: >200 chars
        tenant.Rename(new string('a', 201), updatedNow).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Tenant_Suspend_And_Reactivate_EnforceStateTransitionsAndConnectionStringInvariants_AndRaiseDomainEvents()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = Tenant.Create("lifecycle-org", "Lifecycle Org", "owner@test.com", "pass", now).Value;
        tenant.ClearDomainEvents();

        // 1. Suspend non-active tenant -> fails
        var failSuspend = tenant.Suspend(now);
        failSuspend.IsFailure.Should().BeTrue();
        failSuspend.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");

        // 2. MarkProvisioned -> transitions to Active
        tenant.MarkProvisioned("enc-conn-str", now);
        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.ClearDomainEvents();

        // 3. Suspend Active tenant -> succeeds
        var suspendTime = now.AddHours(1);
        var suspendRes = tenant.Suspend(suspendTime);
        suspendRes.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.Suspended);
        tenant.UpdatedAt.Should().Be(suspendTime);

        var suspendEvent = tenant.DequeueDomainEvents().Should().ContainSingle()
            .Which.Should().BeOfType<TenantSuspendedDomainEvent>().Subject;
        suspendEvent.TenantId.Should().Be(tenant.Id);

        // 4. Reactivate without connection string -> fails
        tenant.EncryptedConnectionString = null;
        var failReact = tenant.Reactivate(now.AddHours(2));
        failReact.IsFailure.Should().BeTrue();
        failReact.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");

        // 5. Reactivate with valid connection string -> succeeds
        tenant.SetEncryptedConnectionString("enc-conn-str", now.AddHours(2));
        var reactTime = now.AddHours(3);
        var reactRes = tenant.Reactivate(reactTime);
        reactRes.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.UpdatedAt.Should().Be(reactTime);

        var reactEvent = tenant.DequeueDomainEvents().Should().ContainSingle()
            .Which.Should().BeOfType<TenantReactivatedDomainEvent>().Subject;
        reactEvent.TenantId.Should().Be(tenant.Id);
    }

    [Fact]
    public void Tenant_RetryProvisioning_MarkProvisioned_And_MarkProvisioningFailed_EnforceInvariantsTruncateFailureReasonAndRaiseDomainEvents()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = Tenant.Create("retry-org", "Retry Org", "owner@test.com", "pass", now).Value;
        tenant.ClearDomainEvents();

        // Mark failed
        var longReason = new string('E', 1500);
        tenant.MarkProvisioningFailed(longReason, now);
        tenant.Status.Should().Be(TenantStatus.Failed);
        tenant.FailureReason.Should().HaveLength(1000);

        var failEvent = tenant.DequeueDomainEvents().Should().ContainSingle()
            .Which.Should().BeOfType<TenantProvisioningFailedDomainEvent>().Subject;
        failEvent.FailureReason.Should().HaveLength(1000);

        // Retry provisioning
        var retryTime = now.AddMinutes(5);
        var retryRes = tenant.RetryProvisioning(retryTime);
        retryRes.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.Provisioning);
        tenant.FailureReason.Should().BeNull();
        tenant.UpdatedAt.Should().Be(retryTime);

        var retryEvent = tenant.DequeueDomainEvents().Should().ContainSingle()
            .Which.Should().BeOfType<TenantProvisioningRetriedDomainEvent>().Subject;
        retryEvent.TenantId.Should().Be(tenant.Id);

        // Mark provisioned
        var provTime = retryTime.AddMinutes(2);
        tenant.MarkProvisioned("enc-conn", provTime);
        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.EncryptedConnectionString.Should().Be("enc-conn");
        tenant.EncryptedOwnerPassword.Should().BeNull();
        tenant.ProvisionedAt.Should().Be(provTime);

        var provEvent = tenant.DequeueDomainEvents().Should().ContainSingle()
            .Which.Should().BeOfType<TenantProvisionedDomainEvent>().Subject;
        provEvent.TenantId.Should().Be(tenant.Id);

        // Retrying on Active tenant returns conflict
        var badRetry = tenant.RetryProvisioning(provTime.AddMinutes(1));
        badRetry.IsFailure.Should().BeTrue();
        badRetry.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");
    }

    [Fact]
    public void TenantSpecs_TenantByIdSpec_And_TenantBySlugSpec_EvaluateInMemoryCriteriaAndConfigureTracking()
    {
        var tenant1 = Tenant.Create("org-one", "Org One", "a@test.com", "p", DateTimeOffset.UtcNow).Value;
        var tenant2 = Tenant.Create("org-two", "Org Two", "b@test.com", "p", DateTimeOffset.UtcNow).Value;

        var idSpec = new TenantByIdSpec(tenant1.Id, track: true);
        idSpec.IsSatisfiedBy(tenant1).Should().BeTrue();
        idSpec.IsSatisfiedBy(tenant2).Should().BeFalse();
        idSpec.AsNoTracking.Should().BeFalse();

        var slugSpec = new TenantBySlugSpec("org-two", track: false);
        slugSpec.IsSatisfiedBy(tenant1).Should().BeFalse();
        slugSpec.IsSatisfiedBy(tenant2).Should().BeTrue();
        slugSpec.AsNoTracking.Should().BeTrue();
    }
}

