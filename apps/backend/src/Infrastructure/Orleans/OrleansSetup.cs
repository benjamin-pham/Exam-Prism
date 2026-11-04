namespace ExamPrism.Infrastructure.Orleans;

using System.Net;
using System.Net.Sockets;
using Common.Orleans;
using global::Orleans;
using global::Orleans.Configuration;
using global::Orleans.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

public static class OrleansSetup
{
    public static int ResolveAvailableLoopbackPort(int configuredPort)
    {
        if (configuredPort > 0)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, configuredPort);
                listener.Start();
                listener.Stop();
                return configuredPort;
            }
            catch (SocketException)
            {
                // Port already in use or unavailable; fall back to ephemeral port
            }
        }

        var ephemeral = new TcpListener(IPAddress.Loopback, 0);
        ephemeral.Start();
        var port = ((IPEndPoint)ephemeral.LocalEndpoint).Port;
        ephemeral.Stop();
        return port;
    }

    public static (int SiloPort, int GatewayPort) ResolveSiloAndGatewayPorts(int configuredSiloPort, int configuredGatewayPort)
    {
        var siloPort = ResolveAvailableLoopbackPort(configuredSiloPort);
        var gatewayPort = configuredGatewayPort > 0 && configuredGatewayPort != siloPort
            ? ResolveAvailableLoopbackPort(configuredGatewayPort)
            : 0;

        if (gatewayPort <= 0 || gatewayPort == siloPort)
        {
            TcpListener? heldSiloListener = null;
            try
            {
                heldSiloListener = new TcpListener(IPAddress.Loopback, siloPort);
                heldSiloListener.Start();
            }
            catch
            {
                heldSiloListener = null;
            }

            try
            {
                var l2 = new TcpListener(IPAddress.Loopback, 0);
                l2.Start();
                gatewayPort = ((IPEndPoint)l2.LocalEndpoint).Port;
                l2.Stop();
            }
            finally
            {
                heldSiloListener?.Stop();
            }
        }

        return (siloPort, gatewayPort);
    }

    public static IHostBuilder UseExamPrismOrleans(this IHostBuilder hostBuilder)
    {
        return hostBuilder.UseOrleans((context, siloBuilder) =>
        {
            var configuration = context.Configuration;
            var orleansSection = configuration.GetSection(OrleansOptions.SectionName);
            var options = orleansSection.Get<OrleansOptions>() ?? new OrleansOptions();

            var (siloPort, gatewayPort) = ResolveSiloAndGatewayPorts(options.SiloPort, options.GatewayPort);

            siloBuilder.Configure<ClusterOptions>(clusterOpts =>
            {
                clusterOpts.ClusterId = options.ClusterId;
                clusterOpts.ServiceId = options.ServiceId;
            });

            siloBuilder.AddIncomingGrainCallFilter<TenantIncomingGrainCallFilter>();
            siloBuilder.AddOutgoingGrainCallFilter<TenantOutgoingGrainCallFilter>();

            var redisConnectionString = configuration.GetConnectionString("Redis");
            var isRedisReachable = false;
            ConfigurationOptions? redisOptions = null;

            if (!string.IsNullOrWhiteSpace(redisConnectionString))
            {
                try
                {
                    redisOptions = ConfigurationOptions.Parse(redisConnectionString);
                    redisOptions.AbortOnConnectFail = false;
                    redisOptions.ConnectTimeout = 1000;
                    redisOptions.SyncTimeout = 1000;

                    using var testMux = ConnectionMultiplexer.Connect(redisOptions);
                    isRedisReachable = testMux.IsConnected;
                    if (!isRedisReachable)
                    {
                        Console.WriteLine("[OrleansSetup] WARNING: ConnectionStrings:Redis was provided but unreachable at boot time. Falling back to localhost in-memory clustering/storage/reminders.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[OrleansSetup] WARNING: Redis connectivity probe failed ({ex.Message}). Falling back to localhost in-memory clustering/storage/reminders.");
                    isRedisReachable = false;
                }
            }

            if (isRedisReachable && redisOptions is not null)
            {
                siloBuilder.ConfigureEndpoints(
                    siloPort: siloPort,
                    gatewayPort: gatewayPort,
                    listenOnAnyHostAddress: true);

                siloBuilder.UseRedisClustering(opt => opt.ConfigurationOptions = redisOptions);
                siloBuilder.AddRedisGrainStorage(options.StateStoreName, opt => opt.ConfigurationOptions = redisOptions);
                siloBuilder.UseRedisReminderService(opt => opt.ConfigurationOptions = redisOptions);
            }
            else
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: siloPort,
                    gatewayPort: gatewayPort,
                    primarySiloEndpoint: null,
                    serviceId: options.ServiceId,
                    clusterId: options.ClusterId);

                siloBuilder.AddMemoryGrainStorage(options.StateStoreName);
                siloBuilder.UseInMemoryReminderService();
            }
        });
    }

    public static IServiceCollection AddExamPrismOrleans(
        this IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecksBuilder)
    {
        services.Configure<OrleansOptions>(configuration.GetSection(OrleansOptions.SectionName));
        services.AddScoped<ITenantGrainFactory, TenantGrainFactory>();
        services.AddSingleton<TenantIncomingGrainCallFilter>();
        services.AddSingleton<TenantOutgoingGrainCallFilter>();
        healthChecksBuilder.AddCheck<OrleansHealthCheck>("orleans");

        return services;
    }
}
