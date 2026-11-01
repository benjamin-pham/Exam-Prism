namespace Tests.Infrastructure;

using ExamPrism.Infrastructure;
using ExamPrism.Infrastructure.Persistence;
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

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddInfrastructure(configuration));

        Assert.Contains("Connection string 'DefaultConnection' not found", exception.Message);
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

        var serviceDescriptor = services.FirstOrDefault(s => s.ServiceType == typeof(AppDbContext));
        Assert.NotNull(serviceDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, serviceDescriptor.Lifetime);
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

        var serviceDescriptor = services.FirstOrDefault(s => s.ServiceType == typeof(TimeProvider));
        Assert.NotNull(serviceDescriptor);
        Assert.Equal(ServiceLifetime.Singleton, serviceDescriptor.Lifetime);

        var serviceProvider = services.BuildServiceProvider();
        var timeProvider = serviceProvider.GetService<TimeProvider>();
        Assert.NotNull(timeProvider);
        Assert.Same(TimeProvider.System, timeProvider);
    }
}
