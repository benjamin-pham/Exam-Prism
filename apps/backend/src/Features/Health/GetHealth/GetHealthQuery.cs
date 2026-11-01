namespace ExamPrism.Features.Health.GetHealth;

using System.Text.Json.Serialization;

public record GetHealthQuery;

public record GetHealthResult(
    string Status,
    DateTime Timestamp,
    [property: JsonIgnore] bool IsHealthy = true);
