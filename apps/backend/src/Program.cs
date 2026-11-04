using Common.Endpoints;
using ExamPrism.Infrastructure;
using ExamPrism.Infrastructure.Cors;
using ExamPrism.Infrastructure.Localization;
using ExamPrism.Infrastructure.Logging;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.OpenApi;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddInfrastructure();

var app = builder.Build();

app.UseExamPrismLocalization();
app.UseExceptionHandler();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseRouting();

app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = LogEnrichmentMiddlewareExtensions.EnrichDiagnosticContext;
});

app.UseExamPrismCors(app.Environment);

app.UseMultiTenant();
app.UseMiddleware<TenantAccessGuardMiddleware>();

app.UseAuthentication();
app.UseLogEnrichment();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapExamPrismOpenApi();
}

app.MapEndpoints();

try
{
    await app.InitializeInfrastructureAsync();
    app.Run();
}
finally
{
    Serilog.Log.CloseAndFlush();
}

public partial class Program { }
