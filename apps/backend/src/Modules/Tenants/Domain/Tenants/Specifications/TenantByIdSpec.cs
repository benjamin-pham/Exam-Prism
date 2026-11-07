namespace ExamPrism.Domain.Tenants.Specifications;

using Common.Domain.Specifications;

public sealed class TenantByIdSpec : Specification<Tenant>
{
    public TenantByIdSpec(Guid id, bool track = false)
    {
        Where(t => t.Id == id);
        if (track)
        {
            WithTracking();
        }
    }
}

