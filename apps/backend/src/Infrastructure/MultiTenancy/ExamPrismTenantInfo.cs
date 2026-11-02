namespace ExamPrism.Infrastructure.MultiTenancy;

using System.Diagnostics.CodeAnalysis;
using Common.MultiTenancy;
using Finbuckle.MultiTenant.Abstractions;

public sealed class ExamPrismTenantInfo : TenantInfo
{
    public ExamPrismTenantInfo()
    {
    }

    [SetsRequiredMembers]
    public ExamPrismTenantInfo(string id, string identifier)
    {
        Id = id;
        Identifier = identifier;
    }

    public Guid TenantGuid { get; init; }
    public TenantStatus Status { get; init; }
    public string? ConnectionString { get; init; }
}
