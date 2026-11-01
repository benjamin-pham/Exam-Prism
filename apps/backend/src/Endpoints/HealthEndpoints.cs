namespace ExamPrism.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new
        {
            Status = "Healthy",
            Timestamp = DateTime.UtcNow
        }))
        .WithName("GetHealth")
        .WithDescription("Returns health status of the API");

        return app;
    }
}
