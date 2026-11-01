namespace Tests.Infrastructure;

using ExamPrism.Infrastructure;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

public class DependencyInjectionTests
{
    private static Dictionary<string, string?> CreateValidSettings() => new()
    {
        { "ConnectionStrings:DefaultConnection", "Host=localhost;Database=test;Username=postgres;Password=postgres" },
        { "Jwt:Issuer", "ExamPrism" },
        { "Jwt:Audience", "ExamPrism" },
        { "Jwt:SecretKey", "SuperSecretKeyForTestingJwtTokensMin256BitsLong!" }
    };

    [Fact]
    public void AddInfrastructure_ThrowsInvalidOperationException_WhenConnectionStringMissing()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        Action act = () => services.AddInfrastructure(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Connection string 'DefaultConnection' not found*");
    }

    [Fact]
    public void AddInfrastructure_ThrowsInvalidOperationException_WhenJwtSectionMissing()
    {
        var services = new ServiceCollection();
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test;Username=postgres;Password=postgres"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();

        Action act = () => services.AddInfrastructure(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt configuration section is missing*");
    }

    [Fact]
    public void AddInfrastructure_RegistersAppDbContext_WhenConnectionStringProvided()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();

        services.AddInfrastructure(configuration);

        services.Should().ContainSingle(s => s.ServiceType == typeof(AppDbContext))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInfrastructure_RegistersTimeProvider_AsSingleton()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();

        services.AddInfrastructure(configuration);

        services.Should().ContainSingle(s => s.ServiceType == typeof(TimeProvider))
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);

        var serviceProvider = services.BuildServiceProvider();
        var timeProvider = serviceProvider.GetService<TimeProvider>();
        timeProvider.Should().NotBeNull();
        timeProvider.Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public void AddInfrastructure_RegistersIdentityAndTokenService()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();
        var services = new ServiceCollection();

        // Act
        services.AddInfrastructure(configuration);
        var provider = services.BuildServiceProvider();

        // Assert
        provider.GetService<ITokenService>().Should().NotBeNull();
        provider.GetService<UserManager<ApplicationUser>>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddInfrastructure_ConfiguresJwtBearer_WithCookieFallback()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        var provider = services.BuildServiceProvider();

        // Act
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();
        var jwtOptions = optionsMonitor.Get(JwtBearerDefaults.AuthenticationScheme);

        // Assert TokenValidationParameters
        jwtOptions.TokenValidationParameters.ValidIssuer.Should().Be("ExamPrism");
        jwtOptions.TokenValidationParameters.ValidAudience.Should().Be("ExamPrism");
        jwtOptions.TokenValidationParameters.ValidateLifetime.Should().BeTrue();
        jwtOptions.TokenValidationParameters.ClockSkew.Should().Be(TimeSpan.Zero);

        // Assert OnMessageReceived extracts cookie when token is not set
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Append("Cookie", "access_token=cookie-jwt-token");
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var context = new MessageReceivedContext(httpContext, scheme, jwtOptions);

        await jwtOptions.Events.OnMessageReceived(context);
        context.Token.Should().Be("cookie-jwt-token");

        // Assert OnMessageReceived leaves existing token intact if already set (e.g. from Authorization header)
        var contextWithExisting = new MessageReceivedContext(httpContext, scheme, jwtOptions)
        {
            Token = "existing-bearer-token"
        };
        await jwtOptions.Events.OnMessageReceived(contextWithExisting);
        contextWithExisting.Token.Should().Be("existing-bearer-token");
    }

    [Theory]
    [InlineData("")]
    [InlineData("short-key")]
    public void AddInfrastructure_WithShortOrEmptyJwtSecretKey_ThrowsInvalidOperationException(string secretKey)
    {
        var settings = CreateValidSettings();
        settings["Jwt:SecretKey"] = secretKey;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        var act = () => services.AddInfrastructure(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:SecretKey*");
    }
}
