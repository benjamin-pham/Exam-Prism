namespace ExamPrism.Tests.Features;

using System.Text.Json;
using Common.Exceptions;
using Common.Security;
using ExamPrism.Features.Auth.GetMe;
using ExamPrism.Features.Auth.Login;
using ExamPrism.Features.Auth.Logout;
using ExamPrism.Features.Auth.Refresh;
using ExamPrism.Features.Auth.Register;
using ExamPrism.Features.Health;
using ExamPrism.Infrastructure.Identity;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public class WolverineHandlerTests
{
    [Fact]
    public async Task LoginHandler_WhenCredentialsAreEmpty_ThrowsUnauthorizedAccessException()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            userStore, null!, null!, null!, null!, null!, null!, null!, null!);
        var tokenService = Substitute.For<ITokenService>();
        var jwtOptions = Options.Create(new JwtOptions());
        var timeProvider = TimeProvider.System;

        var command = new LoginCommand("", "");

        await FluentActions.Invoking(() => LoginHandler.Handle(
            command,
            userManager,
            null!,
            tokenService,
            jwtOptions,
            timeProvider))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task RegisterHandler_WhenCredentialsAreEmpty_ThrowsValidationException()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            userStore, null!, null!, null!, null!, null!, null!, null!, null!);
        var tokenService = Substitute.For<ITokenService>();
        var jwtOptions = Options.Create(new JwtOptions());
        var timeProvider = TimeProvider.System;

        var command = new RegisterCommand("", "");

        await FluentActions.Invoking(() => RegisterHandler.Handle(
            command,
            userManager,
            null!,
            tokenService,
            jwtOptions,
            timeProvider))
            .Should().ThrowAsync<ValidationException>();
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
    public async Task GetMeHandler_WhenUserContextHasNoUserId_ThrowsUnauthorizedAccessException()
    {
        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns((Guid?)null);

        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            userStore, null!, null!, null!, null!, null!, null!, null!, null!);

        await FluentActions.Invoking(() => GetMeHandler.Handle(new GetMeQuery(), userContext, userManager))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task GetMeHandler_WhenUserNotFound_ThrowsNotFoundException()
    {
        var userId = Guid.NewGuid();
        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(userId);

        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            userStore, null!, null!, null!, null!, null!, null!, null!, null!);

        await FluentActions.Invoking(() => GetMeHandler.Handle(new GetMeQuery(), userContext, userManager))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task RefreshTokenHandler_WhenTokenIsBlank_ReturnsNullWithoutThrowing()
    {
        var tokenService = Substitute.For<ITokenService>();
        var jwtOptions = Options.Create(new JwtOptions());
        var timeProvider = TimeProvider.System;

        var result = await RefreshTokenHandler.Handle(
            new RefreshTokenCommand(""),
            null!,
            tokenService,
            jwtOptions,
            timeProvider);

        result.Should().BeNull();
    }

    [Fact]
    public async Task LogoutHandler_WhenTokenIsNullOrBlank_CompletesWithoutTouchingDb()
    {
        var tokenService = Substitute.For<ITokenService>();
        var timeProvider = TimeProvider.System;

        var act = () => LogoutHandler.Handle(
            new LogoutCommand(null),
            null!,
            tokenService,
            timeProvider);

        await act.Should().NotThrowAsync();
    }
}
