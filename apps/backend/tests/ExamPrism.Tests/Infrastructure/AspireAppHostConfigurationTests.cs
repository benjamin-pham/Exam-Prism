namespace Tests.Infrastructure;

using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

public class AspireAppHostConfigurationTests
{
    private static string GetRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, "apps", "backend")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    [Fact]
    public void AppHostCsproj_UsesAspireAppHostSdk_AndHasNoProjectReferences()
    {
        var csprojPath = Path.Combine(GetRepoRoot(), "aspire-app", "ExamPrism.AppHost.csproj");
        File.Exists(csprojPath).Should().BeTrue("aspire-app/ExamPrism.AppHost.csproj must exist at repo root");

        var doc = XDocument.Load(csprojPath);
        doc.Root.Should().NotBeNull();
        doc.Root!.Attribute("Sdk")?.Value.Should().StartWith("Aspire.AppHost.Sdk/");
        doc.Descendants("ProjectReference").Should().BeEmpty("ExamPrism.csproj must be registered by path, not ProjectReference");
        doc.Descendants("PackageReference").Should().Contain(e => e.Attribute("Include") != null && e.Attribute("Include")!.Value == "Aspire.Hosting.Seq");
    }

    [Fact]
    public void AppHostProgram_RegistersExamPrismByRelativePath_WithPostgresRedisAndAllEnvironmentVariables()
    {
        var appHostPath = Path.Combine(GetRepoRoot(), "aspire-app", "AppHost.cs");
        File.Exists(appHostPath).Should().BeTrue("aspire-app/AppHost.cs must exist");

        var content = File.ReadAllText(appHostPath);
        content.Should().Contain("AddPostgres(\"postgres\")");
        content.Should().Contain("WithPgAdmin");
        content.Should().Contain("AddDatabase(\"CatalogConnection\", \"examprism_catalog_dev\")");
        content.Should().Contain("AddRedis(\"Redis\")");
        content.Should().Contain("WithRedisCommander");
        content.Should().Contain("AddSeq(\"seq\")");
        content.Should().Contain("WaitFor(seq)");
        content.Should().Contain("WithContainerRuntimeArgs(\"--cpus=2\", \"--memory=1024m\")");
        content.Should().Contain("WithContainerRuntimeArgs(\"--cpus=1\", \"--memory=512m\")");
        content.Should().Contain("WithContainerRuntimeArgs(\"--cpus=0.5\", \"--memory=256m\")");
        content.Should().Contain("AddProject(\"examprism-api\", \"../apps/backend/src/ExamPrism/ExamPrism.csproj\")");

        string[] requiredEnvPrefixes =
        [
            "\"ConnectionStrings__CatalogConnection\"",
            "\"ConnectionStrings__Redis\"",
            "\"AllowedHosts\"",
            "\"Serilog__WriteTo__1__Name\"",
            "\"Serilog__WriteTo__1__Args__serverUrl\"",
            "\"Persistence__CommandTimeoutSeconds\"",
            "\"MultiTenancy__BaseDomains__0\"",
            "\"MultiTenancy__FleetMigration__MigrateOnStartup\"",
            "\"Caching__KeyPrefix\"",
            "\"Locking__KeyPrefix\"",
            "\"RateLimiting__Global__PermitLimit\"",
            "\"Jwt__SecretKey\"",
            "\"PlatformAdmin__SeedOnStartup\"",
            "\"Orleans__ClusterId\"",
            "\"Resilience__MaxRetryAttempts\"",
            "\"LoadShedding__Enabled\"",
            "\"Batching__Capacity\"",
            "\"IdentityHighLoad__MaxConcurrentPasswordHashes\"",
            "\"DdosProtection__Enabled\"",
            "\"RuntimeTuning__MaxRequestHeaderCount\"",
            "\"Telemetry__EnablePrometheusEndpoint\""
        ];

        foreach (var prefix in requiredEnvPrefixes)
        {
            content.Should().Contain(prefix);
        }
    }
}

