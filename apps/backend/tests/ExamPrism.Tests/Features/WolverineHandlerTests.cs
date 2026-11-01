namespace ExamPrism.Tests.Features;

using Common.Security;
using ExamPrism.Features.Auth;
using ExamPrism.Features.Health;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;
using Wolverine;
using Xunit;

public class WolverineHandlerTests
{
    [Fact]
    public async Task LoginHandler_WhenCredentialsAreEmpty_ReturnsUnauthorized()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            userStore, null!, null!, null!, null!, null!, null!, null!, null!);
        var tokenService = Substitute.For<ITokenService>();
        var jwtOptions = Options.Create(new JwtOptions());
        var timeProvider = TimeProvider.System;
        var httpContext = new DefaultHttpContext();

        var request = new LoginRequest("", "");

        var result = await LoginHandler.Handle(
            request,
            userManager,
            null!,
            tokenService,
            jwtOptions,
            timeProvider,
            httpContext.Response);

        result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    [Fact]
    public async Task RegisterHandler_WhenCredentialsAreEmpty_ReturnsValidationProblem()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            userStore, null!, null!, null!, null!, null!, null!, null!, null!);
        var tokenService = Substitute.For<ITokenService>();
        var jwtOptions = Options.Create(new JwtOptions());
        var timeProvider = TimeProvider.System;
        var httpContext = new DefaultHttpContext();

        var request = new RegisterRequest("", "");

        var result = await RegisterHandler.Handle(
            request,
            userManager,
            null!,
            tokenService,
            jwtOptions,
            timeProvider,
            httpContext.Response);

        result.Should().BeOfType<ProblemHttpResult>();
    }

    [Fact]
    public async Task HealthHandler_WhenHealthy_ReturnsOkWithStatus()
    {
        var healthCheckService = Substitute.For<HealthCheckService>();
        var healthReport = new HealthReport(
            new Dictionary<string, HealthReportEntry>(),
            HealthStatus.Healthy,
            TimeSpan.FromMilliseconds(5));

        healthCheckService.CheckHealthAsync(Arg.Any<Func<HealthCheckRegistration, bool>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(healthReport));

        var result = await HealthHandler.Handle(healthCheckService);

        result.Should().BeOfType<Ok<HealthResponse>>();
        var okResult = (Ok<HealthResponse>)result;
        okResult.Value.Should().NotBeNull();
        okResult.Value!.Status.Should().Be("Healthy");
    }

    [Fact]
    public async Task GetMeHandler_WhenUserContextHasNoUserId_ReturnsUnauthorized()
    {
        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns((Guid?)null);

        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            userStore, null!, null!, null!, null!, null!, null!, null!, null!);

        var result = await GetMeHandler.Handle(userContext, userManager);

        result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    [Fact]
    public async Task RefreshHandler_WhenRefreshTokenCookieIsMissing_ReturnsUnauthorized()
    {
        var httpContext = new DefaultHttpContext();
        var tokenService = Substitute.For<ITokenService>();
        var jwtOptions = Options.Create(new JwtOptions());
        var timeProvider = TimeProvider.System;

        var result = await RefreshHandler.Handle(
            httpContext.Request,
            httpContext.Response,
            null!,
            tokenService,
            jwtOptions,
            timeProvider);

        result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    [Fact]
    public async Task LogoutHandler_WhenRefreshTokenCookieIsMissing_ReturnsNoContentAndClearsCookies()
    {
        var httpContext = new DefaultHttpContext();
        var tokenService = Substitute.For<ITokenService>();
        var timeProvider = TimeProvider.System;

        var result = await LogoutHandler.Handle(
            httpContext.Request,
            httpContext.Response,
            null!,
            tokenService,
            timeProvider);

        result.Should().BeOfType<NoContent>();
        httpContext.Response.Headers.SetCookie.Should().Contain(h => h != null && h.StartsWith("access_token="));
        httpContext.Response.Headers.SetCookie.Should().Contain(h => h != null && h.StartsWith("refresh_token="));
    }
}

