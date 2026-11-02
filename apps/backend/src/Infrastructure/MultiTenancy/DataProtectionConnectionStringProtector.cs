namespace ExamPrism.Infrastructure.MultiTenancy;

using Microsoft.AspNetCore.DataProtection;

public sealed class DataProtectionConnectionStringProtector(IDataProtectionProvider provider) : IConnectionStringProtector
{
    public const string Purpose = "ExamPrism.TenantConnectionStrings.v1";
    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        return _protector.Protect(plaintext);
    }

    public string Unprotect(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);
        return _protector.Unprotect(ciphertext);
    }
}
