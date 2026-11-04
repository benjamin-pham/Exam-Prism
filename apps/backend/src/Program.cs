using Common.Endpoints;
using ExamPrism.Infrastructure;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.OpenApi;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.AddInfrastructure();

var app = builder.Build();

app.UseRequestLocalization();
app.UseExceptionHandler();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseRouting();

if (app.Environment.IsDevelopment())
{
    app.UseCors(DependencyInjection.DevCorsPolicyName);
}

app.UseMultiTenant();
app.UseMiddleware<TenantAccessGuardMiddleware>();

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapExamPrismOpenApi();
}

app.MapEndpoints();

await app.InitializeInfrastructureAsync();

app.Run();

public partial class Program { }
