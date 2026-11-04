namespace ExamPrism.Infrastructure.Cors;

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class CorsSetup
{
    public const string DevCorsPolicyName = "DevSubdomainCors";

    public static IServiceCollection AddExamPrismCors(this IServiceCollection services, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            services.AddCors(options =>
            {
                options.AddPolicy(DevCorsPolicyName, policy =>
                {
                    policy.SetIsOriginAllowed(origin =>
                    {
                        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        {
                            var host = uri.Host;
                            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                                   host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
                        }
                        return false;
                    })
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
                });
            });
        }

        return services;
    }

    public static IApplicationBuilder UseExamPrismCors(this IApplicationBuilder app, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            app.UseCors(DevCorsPolicyName);
        }

        return app;
    }
}

