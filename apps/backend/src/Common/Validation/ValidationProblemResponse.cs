namespace Common.Validation;

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Represents an RFC 7807 compliant problem details response for validation failures.
/// </summary>
public class ValidationProblemResponse : ProblemDetails
{
    public ValidationProblemResponse()
    {
        Status = StatusCodes.Status400BadRequest;
        Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1";
        Title = "One or more validation errors occurred.";
    }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("errors")]
    public IReadOnlyList<ValidationError> Errors { get; set; } = [];
}
