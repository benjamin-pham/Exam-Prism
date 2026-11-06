namespace Common.MultiTenancy;

using System.Text.RegularExpressions;

public static partial class TenantSlugValidator
{
    public const int MaxLength = 63;

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    public static bool IsValid(string? slug)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length > MaxLength)
        {
            return false;
        }

        return SlugRegex().IsMatch(slug);
    }

    public static bool IsValidFormat(string? slug) => IsValid(slug);
}
