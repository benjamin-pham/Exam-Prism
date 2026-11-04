namespace Tests.Infrastructure.Logging;

using System.IO;
using System.Linq;
using ExamPrism.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Xunit;

public class SerilogConfigurationTests
{
    private static Dictionary<string, string?> CreateValidSettings() => new()
    {
        { "ConnectionStrings:CatalogConnection", "Host=localhost;Database=test;Username=postgres;Password=postgres" },
        { "Jwt:Issuer", "ExamPrism" },
        { "Jwt:Audience", "ExamPrism" },
        { "Jwt:SecretKey", "SuperSecretKeyForTestingJwtTokensMin256BitsLong!" }
    };

    [Fact]
    public void AddInfrastructure_ConfiguresSerilogAsLoggingProviderAndResolvesILogger()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Configuration.AddInMemoryCollection(CreateValidSettings());

        builder.AddInfrastructure();

        using var app = builder.Build();
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var logger = app.Services.GetRequiredService<ILogger<SerilogConfigurationTests>>();

        logger.Should().NotBeNull();
        loggerFactory.GetType().FullName.Should().Contain("Serilog");

        var act = () => logger.LogInformation("Testing Serilog logging integration from ILogger");
        act.Should().NotThrow();
    }

    [Fact]
    public void AddInfrastructure_InProduction_ConfiguresSerilogAsLoggingProviderAndResolvesILogger()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.Configuration.AddInMemoryCollection(CreateValidSettings());

        builder.AddInfrastructure();

        using var app = builder.Build();
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var logger = app.Services.GetRequiredService<ILogger<SerilogConfigurationTests>>();

        logger.Should().NotBeNull();
        loggerFactory.GetType().FullName.Should().Contain("Serilog");

        var act = () => logger.LogInformation("Testing Serilog logging integration in Production");
        act.Should().NotThrow();
    }

    private static string GetSrcPath(string fileName)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.Combine(current, "apps", "backend", "src", fileName);
            if (File.Exists(candidate))
                return candidate;

            var candidate2 = Path.Combine(current, "src", fileName);
            if (File.Exists(candidate2))
                return candidate2;

            current = Directory.GetParent(current)?.FullName;
        }
        throw new FileNotFoundException($"Could not find {fileName}");
    }

    [Fact]
    public void AppsettingsJson_ContainsSerilogConfiguration_WithConsoleSinkAndRenderedCompactJsonFormatter()
    {
        var jsonPath = GetSrcPath("appsettings.json");
        File.Exists(jsonPath).Should().BeTrue();

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(jsonPath)
            .Build();

        var serilogSection = configuration.GetSection("Serilog");
        serilogSection.Exists().Should().BeTrue("appsettings.json must configure the Serilog section");

        configuration.GetSection("Logging").Exists().Should().BeFalse("Standard Logging section should be replaced by Serilog");

        var writeTo = serilogSection.GetSection("WriteTo").GetChildren().ToList();
        writeTo.Should().Contain(s => s["Name"] == "Console");
        var consoleSink = writeTo.First(s => s["Name"] == "Console");
        consoleSink["Args:formatter"].Should().Contain("RenderedCompactJsonFormatter");
    }

    [Fact]
    public void AppsettingsDevelopmentJson_ContainsSerilogConfiguration_WithDevelopmentConsoleSink()
    {
        var basePath = Path.GetDirectoryName(GetSrcPath("appsettings.json"))!;
        var jsonPath = GetSrcPath("appsettings.Development.json");
        File.Exists(jsonPath).Should().BeTrue();

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(basePath, "appsettings.json"))
            .AddJsonFile(jsonPath)
            .Build();

        var serilogSection = configuration.GetSection("Serilog");
        serilogSection.Exists().Should().BeTrue("appsettings.Development.json must configure the Serilog section");

        configuration.GetSection("Logging").Exists().Should().BeFalse("Standard Logging section should be replaced by Serilog in Development");

        var writeTo = serilogSection.GetSection("WriteTo").GetChildren().ToList();
        writeTo.Should().Contain(s => s["Name"] == "Console");
        var consoleSink = writeTo.First(s => s["Name"] == "Console");
        consoleSink["Args:outputTemplate"].Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Serilog_ConfiguredFromAppsettingsJson_CreatesWorkingLogger()
    {
        var jsonPath = GetSrcPath("appsettings.json");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(jsonPath)
            .Build();

        var logger = new Serilog.LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        logger.Should().NotBeNull();
        var act = () => logger.Information("Test log message from Serilog LoggerConfiguration with base appsettings");
        act.Should().NotThrow();
    }

    [Fact]
    public void Serilog_ConfiguredFromAppsettingsDevelopmentJson_CreatesWorkingLogger()
    {
        var basePath = Path.GetDirectoryName(GetSrcPath("appsettings.json"))!;
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(basePath, "appsettings.json"))
            .AddJsonFile(Path.Combine(basePath, "appsettings.Development.json"))
            .Build();

        var logger = new Serilog.LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        logger.Should().NotBeNull();
        var act = () => logger.Information("Test log message from Serilog LoggerConfiguration with dev appsettings");
        act.Should().NotThrow();
    }
}
