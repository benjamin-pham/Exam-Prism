namespace ExamPrism.Api.Endpoints;

public record HealthResponse(string Status, DateTime Timestamp);

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new HealthResponse("Healthy", DateTime.UtcNow)))
            .WithName("GetHealth")
            .WithDescription("Returns health status of the API");

        return app;
    }
}
