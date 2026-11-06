namespace ExamPrism.Infrastructure.MultiTenancy;

using System.Net;
using Common.MultiTenancy;
using Microsoft.AspNetCore.Http;

public enum HostResolutionKind
{
    Platform,
    TenantCandidate,
    InvalidTenantHost
}

public sealed record HostResolutionResult(HostResolutionKind Kind, string? Identifier)
{
    public static HostResolutionResult Platform() => new(HostResolutionKind.Platform, null);
    public static HostResolutionResult TenantCandidate(string identifier) => new(HostResolutionKind.TenantCandidate, identifier);
    public static HostResolutionResult InvalidTenantHost() => new(HostResolutionKind.InvalidTenantHost, null);
}

public static class SubdomainParser
{
    public const string HttpContextItemKey = "ExamPrism.HostResolution";

    public static HostResolutionResult Parse(HttpContext httpContext, MultiTenancyOptions options, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(options);

        if (httpContext.Items.TryGetValue(HttpContextItemKey, out var cached) && cached is HostResolutionResult cachedResult)
        {
            return cachedResult;
        }

        var host = httpContext.Request.Host.Host;
        var rawHost = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

        if (rawHost.Contains(':') && !rawHost.Contains('['))
        {
            rawHost = rawHost.Split(':')[0];
        }
        else if (rawHost.StartsWith('[') && rawHost.Contains("]:"))
        {
            rawHost = rawHost[..(rawHost.IndexOf(']') + 1)];
        }

        var ipHost = rawHost.Trim('[', ']');
        if (string.IsNullOrEmpty(rawHost) || IPAddress.TryParse(ipHost, out _))
        {
            var ipResult = ResolvePlatformOrFallback(httpContext, options, isDevelopment);
            httpContext.Items[HttpContextItemKey] = ipResult;
            return ipResult;
        }

        var baseDomains = (options.BaseDomains ?? [])
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b.Trim().TrimEnd('.').ToLowerInvariant())
            .OrderByDescending(b => b.Length);

        foreach (var baseDomain in baseDomains)
        {
            if (string.Equals(rawHost, baseDomain, StringComparison.Ordinal))
            {
                var rootResult = ResolvePlatformOrFallback(httpContext, options, isDevelopment);
                httpContext.Items[HttpContextItemKey] = rootResult;
                return rootResult;
            }

            var suffix = "." + baseDomain;
            if (rawHost.EndsWith(suffix, StringComparison.Ordinal))
            {
                var prefix = rawHost[..^suffix.Length];
                HostResolutionResult candidateResult;

                if (options.ReservedSubdomains != null &&
                    options.ReservedSubdomains.Any(r => string.Equals(r?.Trim(), prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    candidateResult = HostResolutionResult.Platform();
                }
                else if (TenantSlugValidator.IsValid(prefix))
                {
                    candidateResult = HostResolutionResult.TenantCandidate(prefix);
                }
                else
                {
                    candidateResult = HostResolutionResult.InvalidTenantHost();
                }

                httpContext.Items[HttpContextItemKey] = candidateResult;
                return candidateResult;
            }
        }

        var fallbackResult = HostResolutionResult.Platform();
        httpContext.Items[HttpContextItemKey] = fallbackResult;
        return fallbackResult;
    }

    private static HostResolutionResult ResolvePlatformOrFallback(
        HttpContext httpContext,
        MultiTenancyOptions options,
        bool isDevelopment)
    {
        if (isDevelopment && options.AllowHeaderFallbackInDevelopment)
        {
            var headerName = string.IsNullOrWhiteSpace(options.HeaderName) ? "X-Tenant" : options.HeaderName;
            if (httpContext.Request.Headers.TryGetValue(headerName, out var values))
            {
                var headerValue = values.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(headerValue))
                {
                    var normalizedHeader = headerValue.ToLowerInvariant();
                    return TenantSlugValidator.IsValid(normalizedHeader)
                        ? HostResolutionResult.TenantCandidate(normalizedHeader)
                        : HostResolutionResult.InvalidTenantHost();
                }
            }
        }

        return HostResolutionResult.Platform();
    }
}
