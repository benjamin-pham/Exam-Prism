namespace ExamPrism.Infrastructure.MultiTenancy;

using Common.Endpoints;
using Common.MultiTenancy;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

public sealed class TenantAccessGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptions<MultiTenancyOptions> _options;
    private readonly IHostEnvironment _environment;

    public TenantAccessGuardMiddleware(
        RequestDelegate next,
        IOptions<MultiTenancyOptions> options,
        IHostEnvironment environment)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resolution = SubdomainParser.Parse(context, _options.Value, _environment.IsDevelopment());

        switch (resolution.Kind)
        {
            case HostResolutionKind.Platform:
                await _next(context);
                return;

            case HostResolutionKind.InvalidTenantHost:
                await RecordStrikeIfReputationServiceAvailableAsync(context);
                await TenantProblemResults.WriteTenantNotFoundAsync(context);
                return;

            case HostResolutionKind.TenantCandidate:
                var tenantInfo = context.GetMultiTenantContext<ExamPrismTenantInfo>()?.TenantInfo;
                if (tenantInfo is null)
                {
                    ClearTenantContext(context);
                    await RecordStrikeIfReputationServiceAvailableAsync(context);
                    await TenantProblemResults.WriteTenantNotFoundAsync(context);
                    return;
                }

                switch (tenantInfo.Status)
                {
                    case TenantStatus.Active:
                        var tenantEndpoint = context.GetEndpoint();
                        if (tenantEndpoint?.Metadata.GetMetadata<TenantBoundaryMetadata>()?.Mode == TenantBoundaryMode.PlatformOnly)
                        {
                            await TenantProblemResults.WritePlatformOnlyAsync(context);
                            return;
                        }
                        await _next(context);
                        return;

                    case TenantStatus.Provisioning:
                        ClearTenantContext(context);
                        var retryAfter = _options.Value.ProvisioningRetryAfterSeconds > 0
                            ? _options.Value.ProvisioningRetryAfterSeconds
                            : 5;
                        await TenantProblemResults.WriteTenantProvisioningAsync(context, retryAfter);
                        return;

                    case TenantStatus.Suspended:
                        ClearTenantContext(context);
                        await TenantProblemResults.WriteTenantSuspendedAsync(context);
                        return;

                    case TenantStatus.Failed:
                    default:
                        ClearTenantContext(context);
                        await TenantProblemResults.WriteTenantUnavailableAsync(context);
                        return;
                }
        }
    }

    private static void ClearTenantContext(HttpContext context)
    {
        var emptyContext = new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null);

        var setter = context.RequestServices.GetService<IMultiTenantContextSetter>();
        if (setter is not null)
        {
            setter.MultiTenantContext = emptyContext;
        }

        context.Items[typeof(IMultiTenantContext)] = emptyContext;
    }

    private static async ValueTask RecordStrikeIfReputationServiceAvailableAsync(HttpContext context)
    {
        var reputationService = context.RequestServices.GetService<Common.Security.IIpReputationService>();
        if (reputationService is not null)
        {
            var clientIp = Http.ReverseProxySetup.ResolveClientIp(context);
            await reputationService.RecordStrikeAsync(clientIp, Common.Security.IpStrikeReason.UnknownTenantProbe, cancellationToken: context.RequestAborted);
        }
    }
}

public static class TenantAccessGuardMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantAccessGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantAccessGuardMiddleware>();
    }
}
