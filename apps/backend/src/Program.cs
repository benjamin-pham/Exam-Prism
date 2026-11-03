using Common.Endpoints;
using Common.Exceptions;
using Common.MultiTenancy;
using ExamPrism.Features.Tenants;
using ExamPrism.Infrastructure;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.FluentValidation;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.UseFluentValidation();
    opts.Policies.AddMiddleware<TenantWolverineMiddleware>();
});

builder.Services.AddOpenApi(options =>
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

        return Task.CompletedTask;
    });
});
builder.Services.AddHealthChecks();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddLocalization(options => options.ResourcesPath = "Common/Localization/Resources");
builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddValidatorsFromAssemblyContaining<Program>(includeInternalTypes: true);

var supportedCultures = new[] { "vi-VN", "vi", "en-US", "en" };
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture("vi-VN")
           .AddSupportedCultures(supportedCultures)
           .AddSupportedUICultures(supportedCultures);
});

var app = builder.Build();

app.UseRequestLocalization();
app.UseExceptionHandler();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});

app.UseRouting();
app.UseMultiTenant();
app.UseMiddleware<TenantAccessGuardMiddleware>();

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("ExamPrism API")
               .AddPreferredSecuritySchemes(["Bearer"]);
    });
}

app.MapEndpoints();

var platformAdminOptions = app.Services.GetRequiredService<IOptions<PlatformAdminOptions>>().Value;
if (platformAdminOptions.SeedOnStartup)
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<PlatformAdminSeeder>();
        await seeder.SeedAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "PlatformAdminSeeder skipped on startup because database is not available or not yet migrated.");
    }
}

var multiTenancyOptions = app.Services.GetRequiredService<IOptions<MultiTenancyOptions>>().Value;
if (multiTenancyOptions.Provisioning.RecoverStuckProvisioningOnStartup)
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var stuckTenants = await catalogDb.Tenants
            .Where(t => t.Status == TenantStatus.Provisioning)
            .Select(t => t.Id)
            .ToListAsync();

        foreach (var tenantId in stuckTenants)
        {
            await bus.SendAsync(new StartTenantProvisioningCommand(tenantId));
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Stuck provisioning recovery skipped on startup because database or bus is not available.");
    }
}

if (multiTenancyOptions.FleetMigration.MigrateOnStartup)
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var fleetMigrator = scope.ServiceProvider.GetRequiredService<ITenantFleetMigrator>();
        await fleetMigrator.MigrateAllActiveTenantsAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Fleet migration skipped on startup because database is not available.");
    }
}

app.Run();

public partial class Program { }
