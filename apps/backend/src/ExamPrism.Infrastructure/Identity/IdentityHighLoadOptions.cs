namespace ExamPrism.Infrastructure.Identity;

using System;

public sealed class IdentityHighLoadOptions
{
    public const string SectionName = "IdentityHighLoad";

    public int MaxConcurrentPasswordHashes { get; set; } = Math.Max(2, Environment.ProcessorCount);

    public int PasswordHashQueueTimeoutMs { get; set; } = 2000;

    public bool EnableTokenRevocationCheck { get; set; } = true;

    public TimeSpan TokenRevocationCacheTtl { get; set; } = TimeSpan.FromHours(24);

    public int TokenRevocationCacheTtlMinutes
    {
        get => (int)TokenRevocationCacheTtl.TotalMinutes;
        set => TokenRevocationCacheTtl = TimeSpan.FromMinutes(value);
    }
}
