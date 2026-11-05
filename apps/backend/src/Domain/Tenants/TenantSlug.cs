namespace ExamPrism.Domain.Tenants;

using Common.Domain;
using Common.MultiTenancy;
using Common.Results;

public sealed class TenantSlug : ValueObject
{
    public string Value { get; }

    private TenantSlug(string value)
    {
        Value = value;
    }

    public static Result<TenantSlug> Create(string? rawSlug)
    {
        var normalized = (rawSlug ?? string.Empty).Trim().ToLowerInvariant();
        if (!TenantSlugValidator.IsValid(normalized))
        {
            return TenantErrors.InvalidSlug();
        }

        return new TenantSlug(normalized);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public static implicit operator string(TenantSlug slug) => slug.Value;

    public override string ToString() => Value;
}

