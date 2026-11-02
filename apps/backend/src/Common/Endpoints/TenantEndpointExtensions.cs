namespace Common.Endpoints;

using Common.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

public enum TenantBoundaryMode
{
    TenantRequired,
    PlatformOnly
}

public sealed record TenantBoundaryMetadata(TenantBoundaryMode Mode);

public static class TenantEndpointExtensions
{
    public static TBuilder RequireTenant<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithMetadata(new TenantBoundaryMetadata(TenantBoundaryMode.TenantRequired));
        builder.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.HasTenant)
            {
                return TenantProblemResults.TenantRequiredResult(context.HttpContext);
            }

            return await next(context);
        });

        return builder;
    }

    public static TBuilder RequirePlatform<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithMetadata(new TenantBoundaryMetadata(TenantBoundaryMode.PlatformOnly));
        builder.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (tenantContext.HasTenant)
            {
                return TenantProblemResults.PlatformOnlyResult(context.HttpContext);
            }

            return await next(context);
        });

        return builder;
    }
}
