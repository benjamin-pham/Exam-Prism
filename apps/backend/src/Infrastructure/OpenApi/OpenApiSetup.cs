namespace ExamPrism.Infrastructure.OpenApi;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

public static class OpenApiSetup
{
    public static IServiceCollection AddAppOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Components ??= new();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Enter your JWT access token"
                };
                document.Components.SecuritySchemes["X-Tenant"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "X-Tenant",
                    Description = "Tenant slug for multi-tenant requests in development (e.g. school-a)"
                };

                document.Servers ??= new List<OpenApiServer>();
                document.Servers.Add(new OpenApiServer
                {
                    Url = "/",
                    Description = "Current Origin"
                });
                document.Servers.Add(new OpenApiServer
                {
                    Url = "{scheme}://{tenant}.localhost:{port}",
                    Description = "Subdomain Tenant (Development)",
                    Variables = new Dictionary<string, OpenApiServerVariable>
                    {
                        ["scheme"] = new OpenApiServerVariable
                        {
                            Default = "http",
                            Description = "HTTP scheme",
                            Enum = ["http", "https"]
                        },
                        ["tenant"] = new OpenApiServerVariable
                        {
                            Default = "school-a",
                            Description = "Tenant subdomain slug"
                        },
                        ["port"] = new OpenApiServerVariable
                        {
                            Default = "5000",
                            Description = "Server port"
                        }
                    }
                });

                return Task.CompletedTask;
            });
        });

        return services;
    }

    public static IEndpointRouteBuilder MapAppOpenApi(this IEndpointRouteBuilder app)
    {
        app.MapOpenApi();
        app.MapScalarApiReference(options =>
        {
            options.WithTitle("ExamPrism API")
                   .AddPreferredSecuritySchemes(["Bearer", "X-Tenant"]);
        });

        return app;
    }
}

