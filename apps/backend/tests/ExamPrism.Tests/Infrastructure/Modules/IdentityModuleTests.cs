namespace ExamPrism.Tests.Infrastructure.Modules;

using Common.Messages.Tenants;
using Common.MultiTenancy;
using Common.Security;
using ExamPrism.Domain.Identity;
using ExamPrism.Features.Tenants;
using ExamPrism.Infrastructure;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Modules.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

public class IdentityModuleTests
{
    [Fact]
    public void IdentityModule_ExposesExpectedNameAndSettingFiles_AndRegistersAppDbContextAndUserManager()
    {
        var module = new IdentityModule();
        module.Name.Should().Be("Identity");
        module.ModuleSettingFiles.Should().ContainSingle().Which.Should().Be("appsettings.identity.json");

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:CatalogConnection"] = "Host=localhost;Database=test;Username=postgres;Password=postgres",
            ["Jwt:Issuer"] = "ExamPrism",
            ["Jwt:Audience"] = "ExamPrism",
            ["Jwt:SecretKey"] = "SuperSecretKeyForTestingJwtTokensMin256BitsLong!"
        });
        builder.Services.AddInfrastructure(builder.Configuration);
        module.ConfigureServices(builder);

        builder.Services.Should().Contain(s => s.ServiceType == typeof(AppDbContext));
        builder.Services.Should().Contain(s => s.ServiceType == typeof(UserManager<ApplicationUser>));
    }

    [Fact]
    public async Task MigrateTenantDatabaseHandler_NormalizesConnectionString_AndAttemptsMigration()
    {
        var normalizer = Substitute.For<ITenantConnectionStringNormalizer>();
        normalizer.Normalize("Host=localhost;Database=tenant_test;Username=postgres;Password=postgres")
            .Returns("Host=localhost;Database=tenant_test;Username=postgres;Password=postgres");

        var handler = new MigrateTenantDatabaseHandler(normalizer);
        var command = new MigrateTenantDatabaseCommand(
            Guid.NewGuid(),
            "tenant-test",
            "Host=localhost;Database=tenant_test;Username=postgres;Password=postgres");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => handler.Handle(command, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        normalizer.Received(1).Normalize(command.RawConnectionString);
    }

    [Fact]
    public async Task InitializeTenantIdentityHandler_NormalizesConnectionString_AndAttemptsMigration()
    {
        var normalizer = Substitute.For<ITenantConnectionStringNormalizer>();
        normalizer.Normalize("Host=localhost;Database=tenant_init_test;Username=postgres;Password=postgres")
            .Returns("Host=localhost;Database=tenant_init_test;Username=postgres;Password=postgres");

        var protector = Substitute.For<IConnectionStringProtector>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        var handler = new InitializeTenantIdentityHandler(normalizer, protector, serviceProvider);
        var command = new InitializeTenantIdentityCommand(
            Guid.NewGuid(),
            "tenant-init-test",
            "Host=localhost;Database=tenant_init_test;Username=postgres;Password=postgres",
            "owner@test.com",
            "encrypted-secret");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => handler.Handle(command, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        normalizer.Received(1).Normalize(command.RawConnectionString);
    }

    [Fact]
    public void ConfigureNpgsql_ConfiguresTimeoutBatchSizeAndRetryOnFailure()
    {
        var optionsBuilder = new DbContextOptionsBuilder();
        var persistenceOptions = new PersistenceOptions
        {
            CommandTimeoutSeconds = 45,
            MaxBatchSize = 50,
            MaxRetryCount = 4,
            MaxRetryDelaySeconds = 7
        };

        optionsBuilder.UseNpgsql("Host=localhost;Database=test", npgsql =>
        {
            npgsql.ConfigureNpgsql(persistenceOptions);
        });

#pragma warning disable EF1001
        var extension = optionsBuilder.Options.FindExtension<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal.NpgsqlOptionsExtension>();
        extension.Should().NotBeNull();
        extension!.CommandTimeout.Should().Be(45);
        extension.MaxBatchSize.Should().Be(50);
        extension.ExecutionStrategyFactory.Should().NotBeNull();
#pragma warning restore EF1001
    }
}
