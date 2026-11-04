namespace ExamPrism.Infrastructure.Logging;

using Microsoft.Extensions.Hosting;
using Serilog;

public static class LoggingSetup
{
    public static IHostBuilder UseExamPrismLogging(this IHostBuilder hostBuilder)
    {
        return hostBuilder.UseSerilog((context, services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext();
        });
    }
}

