namespace Common.Validation;

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Represents an RFC 7807 compliant problem details response for validation failures.
/// </summary>
public class ValidationProblemResponse : ProblemDetails
{
    private IReadOnlyList<ValidationError> _errors = [];
    private string? _traceId;

    public ValidationProblemResponse()
    {
        Status = StatusCodes.Status400BadRequest;
        Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1";
        Title = "One or more validation errors occurred.";
    }

    [JsonPropertyName("traceId")]
    public string? TraceId
    {
        get => _traceId;
        set
        {
            _traceId = value;
            if (value is not null)
            {
                Extensions["traceId"] = value;
            }
            else
            {
                Extensions.Remove("traceId");
            }
        }
    }

    [JsonPropertyName("errors")]
    public IReadOnlyList<ValidationError> Errors
    {
        get => _errors;
        set
        {
            _errors = value ?? [];
            Extensions["errors"] = _errors;
        }
    }
}
