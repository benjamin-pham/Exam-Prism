namespace ExamPrism.Domain.Tenants.Specifications;

using Common.Domain.Specifications;

public sealed class TenantBySlugSpec : Specification<Tenant>
{
    public TenantBySlugSpec(string slug, bool track = false)
    {
        var normalized = (slug ?? string.Empty).Trim().ToLowerInvariant();
        Where(t => t.Slug == normalized);
        if (track)
        {
            WithTracking();
        }
    }
}

