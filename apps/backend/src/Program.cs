using Common.Endpoints;
using ExamPrism.Infrastructure;
using ExamPrism.Infrastructure.Cors;
using ExamPrism.Infrastructure.Http;
using ExamPrism.Infrastructure.Localization;
using ExamPrism.Infrastructure.Logging;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.OpenApi;
using ExamPrism.Infrastructure.Security;
using ExamPrism.Infrastructure.Telemetry;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddInfrastructure();

var app = builder.Build();

app.UseAppReverseProxy();
app.UseAppLocalization();
app.UseExceptionHandler();
app.UseIpPenaltyBox();
app.UseRouting();
app.UseAdaptiveLoadShedding();
app.UseRequestTimeouts();
app.UseRequestDecompression();
app.UseDecompressionBombGuard();
app.UseResponseCompression();
app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = LogEnrichmentMiddlewareExtensions.EnrichDiagnosticContext;
});
app.UseAppCors();
app.UseMultiTenant();
app.UseMiddleware<TenantAccessGuardMiddleware>();
app.UseAuthentication();
app.UseLogEnrichment();
app.UseRateLimiter();
app.UseOutputCache();
app.UseAuthorization();
app.UseAppTelemetry();

if (app.Environment.IsDevelopment())
{
    app.MapAppOpenApi();
}

app.MapEndpoints(typeof(Program).Assembly);

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
