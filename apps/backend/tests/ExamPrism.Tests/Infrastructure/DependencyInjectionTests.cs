namespace Tests.Infrastructure;

using ExamPrism.Infrastructure;
using ExamPrism.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class DependencyInjectionTests
{
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
    public void AddInfrastructure_RegistersAppDbContext_WhenConnectionStringProvided()
    {
        var services = new ServiceCollection();
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test;Username=postgres;Password=postgres"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        services.AddInfrastructure(configuration);

        services.Should().ContainSingle(s => s.ServiceType == typeof(AppDbContext))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInfrastructure_RegistersTimeProvider_AsSingleton()
    {
        var services = new ServiceCollection();
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test;Username=postgres;Password=postgres"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        services.AddInfrastructure(configuration);

        services.Should().ContainSingle(s => s.ServiceType == typeof(TimeProvider))
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);

        var serviceProvider = services.BuildServiceProvider();
        var timeProvider = serviceProvider.GetService<TimeProvider>();
        timeProvider.Should().NotBeNull();
        timeProvider.Should().BeSameAs(TimeProvider.System);
    }
}
