namespace ExamPrism.Tests.Infrastructure.Modules;

using Common.Modules;
using Common.MultiTenancy;
using Common.Persistence;
using ExamPrism.Domain.Identity;
using ExamPrism.Domain.Tenants;
using ExamPrism.Infrastructure;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using ExamPrism.Modules.Tenants;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class TenantsModuleTests
{
    [Fact]
    public void TenantsModule_ExposesExpectedNameAndSettingFiles_AndRegistersExpectedServices()
    {
        var module = new TenantsModule();
        module.Name.Should().Be("Tenants");
        module.ModuleSettingFiles.Should().ContainSingle().Which.Should().Be("appsettings.tenants.json");

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

        builder.Services.Should().Contain(s => s.ServiceType == typeof(CatalogDbContext));
        builder.Services.Should().Contain(s => s.ServiceType == typeof(ICatalogDbContext));
        builder.Services.Should().Contain(s => s.ServiceType == typeof(UserManager<PlatformUser>));
        builder.Services.Should().Contain(s => s.ServiceType == typeof(RoleManager<PlatformRole>));
        builder.Services.Should().Contain(s => s.ServiceType == typeof(PlatformAdminSeeder));
        builder.Services.Should().Contain(s => s.ServiceType == typeof(CatalogMultiTenantStore));
        builder.Services.Should().Contain(s => s.ServiceType == typeof(ITenantProvisioner));
        builder.Services.Should().Contain(s => s.ServiceType == typeof(ITenantFleetMigrator));
        builder.Services.Should().Contain(s => s.ServiceType == typeof(IStartupInitializer));
    }
}
