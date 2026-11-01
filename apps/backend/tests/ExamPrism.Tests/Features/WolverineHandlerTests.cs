namespace ExamPrism.Tests.Features;

using System.Text.Json;
using Common.Results;
using Common.Security;
using ExamPrism.Features.Auth;
using ExamPrism.Features.Health;
using ExamPrism.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public class WolverineHandlerTests
{
    [Fact]
    public async Task LoginHandler_WhenCredentialsAreEmpty_ReturnsUnauthorizedFailure()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            userStore, null!, null!, null!, null!, null!, null!, null!, null!);
        var tokenService = Substitute.For<ITokenService>();
        var jwtOptions = Options.Create(new JwtOptions());
        var timeProvider = TimeProvider.System;

        var handler = new LoginHandler(userManager, null!, tokenService, jwtOptions, timeProvider);
        var result = await handler.Handle(new LoginCommand("", ""));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Unauthorized);
        result.Error.Code.Should().Be("INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task RegisterHandler_WhenCredentialsAreEmpty_ReturnsValidationFailure()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            userStore, null!, null!, null!, null!, null!, null!, null!, null!);
        var tokenService = Substitute.For<ITokenService>();
        var jwtOptions = Options.Create(new JwtOptions());
        var timeProvider = TimeProvider.System;

        var handler = new RegisterHandler(userManager, null!, tokenService, jwtOptions, timeProvider);
        var result = await handler.Handle(new RegisterCommand("", ""));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);
        result.Error.Code.Should().Be("VALIDATION_ERROR");
        result.Error.ValidationErrors.Should().ContainSingle(e => e.Code == "REQUIRED");
    }

    [Fact]
    public async Task RegisterHandler_WhenEmailAlreadyExists_ReturnsDuplicateEmailValidationFailure()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>, IUserEmailStore<ApplicationUser>>();
        ((IUserEmailStore<ApplicationUser>)userStore)
            .FindByEmailAsync("existing@example.com", Arg.Any<CancellationToken>())
            .Returns(new ApplicationUser { Email = "existing@example.com" });

        var userManager = new UserManager<ApplicationUser>(
            userStore, null!, null!, null!, null!, null!, null!, null!, null!);
        var tokenService = Substitute.For<ITokenService>();
        var jwtOptions = Options.Create(new JwtOptions());
        var timeProvider = TimeProvider.System;

        var handler = new RegisterHandler(userManager, null!, tokenService, jwtOptions, timeProvider);
        var result = await handler.Handle(new RegisterCommand("existing@example.com", "Password123!"));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);
        result.Error.ValidationErrors.Should().ContainSingle(e =>
            e.Field == "Email" && e.Code == "DUPLICATE_EMAIL");
    }

    [Fact]
    public void RegisterResult_And_LoginResult_JsonSerialization_OmitsTokensAndRoundTripsPublicFields()
    {
        var regOriginal = new RegisterResult(Guid.NewGuid(), "reg@example.com", "secret-access", "secret-refresh");
        var regJson = JsonSerializer.Serialize(regOriginal);
        regJson.Should().NotContain("secret-access").And.NotContain("secret-refresh");
        var regDeserialized = JsonSerializer.Deserialize<RegisterResult>(regJson);
        regDeserialized.Should().NotBeNull();
        regDeserialized!.Id.Should().Be(regOriginal.Id);
        regDeserialized.Email.Should().Be("reg@example.com");

        var loginOriginal = new LoginResult(Guid.NewGuid(), "login@example.com", "login-access", "login-refresh");
        var loginJson = JsonSerializer.Serialize(loginOriginal);
        loginJson.Should().NotContain("login-access").And.NotContain("login-refresh");
        var loginDeserialized = JsonSerializer.Deserialize<LoginResult>(loginJson);
        loginDeserialized.Should().NotBeNull();
        loginDeserialized!.Id.Should().Be(loginOriginal.Id);
        loginDeserialized.Email.Should().Be("login@example.com");
    }

    [Fact]
    public async Task GetHealthHandler_WhenHealthy_ReturnsIsHealthyTrue_AndOmitsIsHealthyFromJson()
    {
        var healthCheckService = Substitute.For<HealthCheckService>();
        var healthReport = new HealthReport(
            new Dictionary<string, HealthReportEntry>(),
            HealthStatus.Healthy,
            TimeSpan.FromMilliseconds(5));

        healthCheckService.CheckHealthAsync(Arg.Any<Func<HealthCheckRegistration, bool>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(healthReport));

        var handler = new GetHealthHandler(healthCheckService);
        var result = await handler.Handle(new GetHealthQuery());

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Healthy");
        result.Value.IsHealthy.Should().BeTrue();

        var json = JsonSerializer.Serialize(result.Value);
        json.Should().NotContain("IsHealthy").And.NotContain("isHealthy");
    }

    [Fact]
    public async Task GetHealthHandler_WhenUnhealthy_ReturnsIsHealthyFalse()
    {
        var healthCheckService = Substitute.For<HealthCheckService>();
        var healthReport = new HealthReport(
            new Dictionary<string, HealthReportEntry>(),
            HealthStatus.Unhealthy,
            TimeSpan.FromMilliseconds(5));

        healthCheckService.CheckHealthAsync(Arg.Any<Func<HealthCheckRegistration, bool>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(healthReport));

        var handler = new GetHealthHandler(healthCheckService);
        var result = await handler.Handle(new GetHealthQuery());

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Unhealthy");
        result.Value.IsHealthy.Should().BeFalse();
    }

    [Fact]
    public async Task GetMeHandler_WhenUserContextHasNoUserId_ReturnsUnauthorizedFailure()
    {
        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns((Guid?)null);

        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            userStore, null!, null!, null!, null!, null!, null!, null!, null!);

        var handler = new GetMeHandler(userContext, userManager);
        var result = await handler.Handle(new GetMeQuery());

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Unauthorized);
        result.Error.Code.Should().Be("UNAUTHORIZED");
    }

    [Fact]
    public async Task GetMeHandler_WhenUserNotFound_ReturnsNotFoundFailure()
    {
        var userId = Guid.NewGuid();
        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(userId);

        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            userStore, null!, null!, null!, null!, null!, null!, null!, null!);

        var handler = new GetMeHandler(userContext, userManager);
        var result = await handler.Handle(new GetMeQuery());

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.NotFound);
        result.Error.Code.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task RefreshTokenHandler_WhenTokenIsBlank_ReturnsUnauthorizedFailure()
    {
        var tokenService = Substitute.For<ITokenService>();
        var jwtOptions = Options.Create(new JwtOptions());
        var timeProvider = TimeProvider.System;

        var handler = new RefreshTokenHandler(null!, tokenService, jwtOptions, timeProvider);
        var result = await handler.Handle(new RefreshTokenCommand(""));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Unauthorized);
        result.Error.Code.Should().Be("INVALID_REFRESH_TOKEN");
    }

    [Fact]
    public async Task LogoutHandler_WhenTokenIsNullOrBlank_ReturnsSuccessResult()
    {
        var tokenService = Substitute.For<ITokenService>();
        var timeProvider = TimeProvider.System;

        var handler = new LogoutHandler(null!, tokenService, timeProvider);
        var result = await handler.Handle(new LogoutCommand(null));

        result.IsSuccess.Should().BeTrue();
    }
}
