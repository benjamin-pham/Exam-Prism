namespace ExamPrism.Infrastructure.Identity;

using System.Text;
using Common.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

public static class IdentitySetup
{
    public static IServiceCollection AddIdentity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, UserContext>();

        // High-load identity: CPU password hash bulkhead & hybrid cache token revocation
        services.Configure<IdentityHighLoadOptions>(configuration.GetSection(IdentityHighLoadOptions.SectionName));
        services.AddSingleton<ITokenRevocationService, HybridCacheTokenRevocationService>();

        // JwtOptions & TokenService
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddScoped<ITokenService, TokenService>();
        services.AddSingleton<JwtTenantValidator>();

        var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? throw new InvalidOperationException("Jwt configuration section is missing.");

        if (string.IsNullOrWhiteSpace(jwtOptions.SecretKey) || Encoding.UTF8.GetByteCount(jwtOptions.SecretKey) < 32)
        {
            throw new InvalidOperationException("Jwt:SecretKey must be configured and be at least 256 bits (32 bytes).");
        }

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtOptions.Issuer,
                ValidAudience = jwtOptions.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SecretKey)),
                ClockSkew = TimeSpan.Zero
            };

            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (string.IsNullOrEmpty(context.Token) &&
                        context.Request.Cookies.TryGetValue("access_token", out var cookieToken))
                    {
                        context.Token = cookieToken;
                    }
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var validator = context.HttpContext.RequestServices.GetService<JwtTenantValidator>()
                        ?? new JwtTenantValidator();
                    return validator.ValidateAsync(context);
                },
                OnChallenge = context =>
                {
                    var validator = context.HttpContext.RequestServices.GetService<JwtTenantValidator>()
                        ?? new JwtTenantValidator();
                    return validator.HandleChallengeAsync(context);
                }
            };
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(PlatformRoles.PolicyName, policy =>
                policy.RequireAuthenticatedUser()
                      .RequireClaim(CustomClaimTypes.TokenScope, CustomClaimTypes.PlatformScopeValue)
                      .RequireRole(PlatformRoles.SuperAdmin));

        return services;
    }

    public static async Task InitializeIdentityAsync(
        this WebApplication app,
        CancellationToken cancellationToken = default)
    {
        var platformAdminOptions = app.Services.GetService<IOptions<PlatformAdminOptions>>()?.Value;
        if (platformAdminOptions is not null && platformAdminOptions.SeedOnStartup)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var scopedSeeder = scope.ServiceProvider.GetService<PlatformAdminSeeder>();
                if (scopedSeeder is not null)
                {
                    await scopedSeeder.SeedAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "PlatformAdminSeeder skipped on startup because database is not available or not yet migrated.");
            }
        }
    }
}
