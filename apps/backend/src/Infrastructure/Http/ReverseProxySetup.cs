namespace ExamPrism.Infrastructure.Http;

using System.Net;
using Common.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public static class ReverseProxySetup
{
    public const string OriginalRemoteIpItemKey = "ExamPrism.OriginalRemoteIp";

    public static IServiceCollection AddAppReverseProxy(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<ReverseProxyOptions>(configuration.GetSection(ReverseProxyOptions.SectionName));
        }

        services.AddOptions<ForwardedHeadersOptions>().Configure<IServiceProvider>((options, sp) =>
        {
            var proxyOptions = sp.GetService<IOptions<ReverseProxyOptions>>()?.Value ?? new ReverseProxyOptions();
            ConfigureForwardedHeaders(options, proxyOptions);
        });

        return services;
    }

    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, ReverseProxyOptions? proxyOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        proxyOptions ??= new ReverseProxyOptions();

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedHost;

        options.ForwardLimit = proxyOptions.ForwardLimit;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        var networks = proxyOptions.KnownNetworks is { Count: > 0 }
            ? proxyOptions.KnownNetworks
            : ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

        foreach (var net in networks)
        {
            if (System.Net.IPNetwork.TryParse(net, out var parsedNet))
            {
                options.KnownIPNetworks.Add(parsedNet);
            }
        }

        if (proxyOptions.KnownProxies is { Count: > 0 })
        {
            foreach (var proxy in proxyOptions.KnownProxies)
            {
                if (IPAddress.TryParse(proxy, out var parsedProxy))
                {
                    options.KnownProxies.Add(parsedProxy);
                }
            }
        }
    }

    public static IApplicationBuilder UseAppReverseProxy(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress is not null)
            {
                context.Items[OriginalRemoteIpItemKey] = context.Connection.RemoteIpAddress;
            }

            await next();
        });

        return app.UseForwardedHeaders();
    }

    public static bool IsTrustedProxy(IPAddress? peerIp, ReverseProxyOptions? options = null)
    {
        if (peerIp is null || IPAddress.IsLoopback(peerIp))
        {
            return true;
        }

        var normalizedIp = peerIp.IsIPv4MappedToIPv6 ? peerIp.MapToIPv4() : peerIp;
        options ??= new ReverseProxyOptions();

        if (options.TrustAllPrivateNetworks && IsPrivateNetwork(normalizedIp))
        {
            return true;
        }

        if (options.KnownProxies is { Count: > 0 })
        {
            foreach (var proxyStr in options.KnownProxies)
            {
                if (IPAddress.TryParse(proxyStr, out var proxyIp))
                {
                    var normalizedProxy = proxyIp.IsIPv4MappedToIPv6 ? proxyIp.MapToIPv4() : proxyIp;
                    if (normalizedProxy.Equals(normalizedIp))
                    {
                        return true;
                    }
                }
            }
        }

        if (options.KnownNetworks is { Count: > 0 })
        {
            foreach (var netStr in options.KnownNetworks)
            {
                if (System.Net.IPNetwork.TryParse(netStr, out var network) && network.Contains(normalizedIp))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsPrivateNetwork(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 127) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            return false;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
            return false;
        }

        return false;
    }

    public static string ResolveClientIp(HttpContext context, ReverseProxyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        options ??= context.RequestServices?.GetService<IOptions<ReverseProxyOptions>>()?.Value
            ?? new ReverseProxyOptions();

        var peerIp = context.Items.TryGetValue(OriginalRemoteIpItemKey, out var origObj) && origObj is IPAddress origIp
            ? origIp
            : context.Connection.RemoteIpAddress;

        var hasForwardedHeader = context.Request.Headers.ContainsKey("CF-Connecting-IP") ||
                                context.Request.Headers.ContainsKey("X-Real-IP") ||
                                context.Request.Headers.ContainsKey("X-Forwarded-For");

        if (hasForwardedHeader)
        {
            if (!options.EnableForwardedHeaders || !IsTrustedProxy(peerIp, options))
            {
                ExamPrismDiagnostics.RecordSpoofedIpIgnored();
                return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            }

            if (context.Request.Headers.TryGetValue("CF-Connecting-IP", out var cfIp) &&
                !string.IsNullOrWhiteSpace(cfIp))
            {
                var candidate = cfIp.ToString().Trim();
                if (IPAddress.TryParse(candidate, out var parsedIp))
                {
                    return parsedIp.ToString();
                }
            }

            if (context.Request.Headers.TryGetValue("X-Real-IP", out var realIp) &&
                !string.IsNullOrWhiteSpace(realIp))
            {
                var candidate = realIp.ToString().Trim();
                if (IPAddress.TryParse(candidate, out var parsedIp))
                {
                    return parsedIp.ToString();
                }
            }

            if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor) &&
                !string.IsNullOrWhiteSpace(forwardedFor))
            {
                var raw = forwardedFor.ToString();
                var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var part in parts)
                {
                    if (IPAddress.TryParse(part, out var parsedIp))
                    {
                        return parsedIp.ToString();
                    }
                }
            }
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
