namespace Common.Http;

using Microsoft.AspNetCore.Builder;

public static class PriorityEndpointExtensions
{
    public static TBuilder WithRequestPriority<TBuilder>(this TBuilder builder, RequestPriority priority)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new RequestPriorityMetadata(priority));
        return builder;
    }
}
