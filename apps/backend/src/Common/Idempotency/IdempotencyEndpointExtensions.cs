namespace Common.Idempotency;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

public sealed record IdempotencyMetadata(bool Enabled = true);

public interface IIdempotencyFilter : IEndpointFilter
{
}

public static class IdempotencyEndpointExtensions
{
    public static TBuilder RequireIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithMetadata(new IdempotencyMetadata(true));
        builder.AddEndpointFilter(async (context, next) =>
        {
            var filter = context.HttpContext.RequestServices.GetService<IIdempotencyFilter>();
            if (filter is not null)
            {
                return await filter.InvokeAsync(context, next);
            }

            return await next(context);
        });

        return builder;
    }
}
