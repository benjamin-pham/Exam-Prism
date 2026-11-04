namespace Common.Orleans;

public readonly record struct TenantGrainKey
{
    public Guid TenantId { get; }
    public string EntityKey { get; }

    public TenantGrainKey(Guid tenantId, string entityKey)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId cannot be empty.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(entityKey))
        {
            throw new ArgumentException("EntityKey cannot be null or whitespace.", nameof(entityKey));
        }

        TenantId = tenantId;
        EntityKey = entityKey.Trim();
    }

    public static string Format(Guid tenantId, string entityKey)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId cannot be empty.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(entityKey))
        {
            throw new ArgumentException("EntityKey cannot be null or whitespace.", nameof(entityKey));
        }

        return $"{tenantId:D}:{entityKey.Trim()}";
    }

    public static TenantGrainKey Parse(string compoundKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compoundKey);

        if (!TryParse(compoundKey, out var result))
        {
            throw new FormatException($"Invalid tenant grain key format '{compoundKey}'. Expected '{{tenantId:D}}:{{entityKey}}'.");
        }

        return result;
    }

    public static bool TryParse(string? compoundKey, out TenantGrainKey result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(compoundKey))
        {
            return false;
        }

        var colonIndex = compoundKey.IndexOf(':');
        if (colonIndex != 36)
        {
            return false;
        }

        if (!Guid.TryParseExact(compoundKey.AsSpan(0, 36), "D", out var tenantId) || tenantId == Guid.Empty)
        {
            return false;
        }

        var entityKey = compoundKey[(colonIndex + 1)..].Trim();
        if (string.IsNullOrEmpty(entityKey))
        {
            return false;
        }

        result = new TenantGrainKey(tenantId, entityKey);
        return true;
    }

    public override string ToString()
    {
        return TenantId == Guid.Empty && string.IsNullOrEmpty(EntityKey)
            ? string.Empty
            : Format(TenantId, EntityKey);
    }
}
