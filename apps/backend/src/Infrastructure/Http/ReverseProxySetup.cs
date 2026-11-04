namespace ExamPrism.Infrastructure.Http;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class ReverseProxySetup
{
    public static IServiceCollection AddAppReverseProxy(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<ReverseProxyOptions>(configuration.GetSection(ReverseProxyOptions.SectionName));
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            ConfigureForwardedHeaders(options);
        });

        return services;
    }

    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedHost;

        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
    }

    public static IApplicationBuilder UseAppReverseProxy(this IApplicationBuilder app)
    {
        return app.UseForwardedHeaders();
    }

    public static string ResolveClientIp(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Headers.TryGetValue("CF-Connecting-IP", out var cfIp) &&
            !string.IsNullOrWhiteSpace(cfIp))
        {
            return cfIp.ToString().Trim();
        }

        if (context.Request.Headers.TryGetValue("X-Real-IP", out var realIp) &&
            !string.IsNullOrWhiteSpace(realIp))
        {
            return realIp.ToString().Trim();
        }

        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor) &&
            !string.IsNullOrWhiteSpace(forwardedFor))
        {
            var raw = forwardedFor.ToString();
            var commaIndex = raw.IndexOf(',');
            var ip = commaIndex >= 0 ? raw[..commaIndex] : raw;
            if (!string.IsNullOrWhiteSpace(ip))
            {
                return ip.Trim();
            }
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
