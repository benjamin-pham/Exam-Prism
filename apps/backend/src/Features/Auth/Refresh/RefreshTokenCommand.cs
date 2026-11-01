namespace ExamPrism.Features.Auth.Refresh;

using System.Text.Json.Serialization;

public record RefreshTokenCommand(string RefreshToken);

public record RefreshTokenResult(
    [property: JsonIgnore] string AccessToken = "",
    [property: JsonIgnore] string RefreshToken = "");
