namespace ExamPrism.Tests.Identity;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Common.Security;
using ExamPrism.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Xunit;

public class UserContextTests
{
    private readonly IHttpContextAccessor _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

    [Fact]
    public void Properties_WhenHttpContextIsNull_ReturnsNullAndNotAuthenticated()
    {
        _httpContextAccessor.HttpContext.Returns((HttpContext?)null);
        var userContext = new UserContext(_httpContextAccessor);

        userContext.IsAuthenticated.Should().BeFalse();
        userContext.UserId.Should().BeNull();
        userContext.Email.Should().BeNull();
    }

    [Fact]
    public void Properties_WhenUserNotAuthenticated_ReturnsNullAndNotAuthenticated()
    {
        var httpContext = new DefaultHttpContext();
        _httpContextAccessor.HttpContext.Returns(httpContext);
        var userContext = new UserContext(_httpContextAccessor);

        userContext.IsAuthenticated.Should().BeFalse();
        userContext.UserId.Should().BeNull();
        userContext.Email.Should().BeNull();
    }

    [Fact]
    public void Properties_WhenAuthenticatedWithValidClaims_ReturnsUserIdAndEmail()
    {
        var expectedGuid = Guid.NewGuid();
        var expectedEmail = "test@example.com";
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, expectedGuid.ToString()),
            new Claim(ClaimTypes.Email, expectedEmail)
        ], "TestAuth");

        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        _httpContextAccessor.HttpContext.Returns(httpContext);
        var userContext = new UserContext(_httpContextAccessor);

        userContext.IsAuthenticated.Should().BeTrue();
        userContext.UserId.Should().Be(expectedGuid);
        userContext.Email.Should().Be(expectedEmail);
    }

    [Fact]
    public void Properties_WhenAuthenticatedWithSubAndJwtEmailClaims_ReturnsUserIdAndEmail()
    {
        var expectedGuid = Guid.NewGuid();
        var expectedEmail = "sub@example.com";
        var identity = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, expectedGuid.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, expectedEmail)
        ], "TestAuth");

        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        _httpContextAccessor.HttpContext.Returns(httpContext);
        var userContext = new UserContext(_httpContextAccessor);

        userContext.IsAuthenticated.Should().BeTrue();
        userContext.UserId.Should().Be(expectedGuid);
        userContext.Email.Should().Be(expectedEmail);
    }

    [Fact]
    public void UserId_WhenClaimIsNotValidGuid_ReturnsNull()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "not-a-guid")
        ], "TestAuth");

        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        _httpContextAccessor.HttpContext.Returns(httpContext);
        var userContext = new UserContext(_httpContextAccessor);

        userContext.IsAuthenticated.Should().BeTrue();
        userContext.UserId.Should().BeNull();
    }
}
