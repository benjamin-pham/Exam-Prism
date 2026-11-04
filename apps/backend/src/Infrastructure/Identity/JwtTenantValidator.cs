namespace ExamPrism.Infrastructure.Identity;

using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using Common.Endpoints;
using Common.MultiTenancy;
using Common.Security;
using Common.Telemetry;
using ExamPrism.Infrastructure.MultiTenancy;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public sealed class JwtTenantValidator
{
    public const string AuthFailureItemKey = "ExamPrism.AuthFailureCode";
    public const string TenantTokenMismatchErrorCode = "TENANT_TOKEN_MISMATCH";
    public const string TokenRevokedErrorCode = "TOKEN_REVOKED";

    private readonly IMultiTenantContextAccessor<ExamPrismTenantInfo>? _tenantAccessor;
    private readonly ITokenRevocationService? _tokenRevocationService;
    private readonly IdentityHighLoadOptions _options;

    public JwtTenantValidator(
        IMultiTenantContextAccessor<ExamPrismTenantInfo>? tenantAccessor = null,
        ITokenRevocationService? tokenRevocationService = null,
        IOptions<IdentityHighLoadOptions>? options = null)
    {
        _tenantAccessor = tenantAccessor;
        _tokenRevocationService = tokenRevocationService;
        _options = options?.Value ?? new IdentityHighLoadOptions();
    }

    public async Task ValidateAsync(TokenValidatedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tenantInfo = _tenantAccessor?.MultiTenantContext?.TenantInfo
            ?? context.HttpContext.GetMultiTenantContext<ExamPrismTenantInfo>()?.TenantInfo;

        var claimTenantId = context.Principal?.FindFirst(CustomClaimTypes.TenantId)?.Value;
        var tokenScope = context.Principal?.FindFirst(CustomClaimTypes.TokenScope)?.Value;

        if (tenantInfo is { Status: TenantStatus.Active })
        {
            if (string.IsNullOrWhiteSpace(claimTenantId) ||
                !string.Equals(claimTenantId, tenantInfo.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tokenScope, CustomClaimTypes.PlatformScopeValue, StringComparison.OrdinalIgnoreCase))
            {
                context.HttpContext.Items[AuthFailureItemKey] = TenantTokenMismatchErrorCode;
                context.Fail("Token tenant mismatch.");
                return;
            }
        }
        else if (tenantInfo is null)
        {
            if (!string.IsNullOrWhiteSpace(claimTenantId) ||
                !string.Equals(tokenScope, CustomClaimTypes.PlatformScopeValue, StringComparison.OrdinalIgnoreCase))
            {
                context.HttpContext.Items[AuthFailureItemKey] = TenantTokenMismatchErrorCode;
                context.Fail("Platform context requires a valid platform token.");
                return;
            }
        }
        else
        {
            context.HttpContext.Items[AuthFailureItemKey] = TenantTokenMismatchErrorCode;
            context.Fail("Tenant is not active.");
            return;
        }

        var revocationService = _tokenRevocationService
            ?? context.HttpContext.RequestServices?.GetService<ITokenRevocationService>();

        if (revocationService != null && _options.EnableTokenRevocationCheck)
        {
            var jti = context.Principal?.FindFirst(JwtRegisteredClaimNames.Jti)?.Value
                ?? context.Principal?.FindFirst("jti")?.Value;

            var userId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? context.Principal?.FindFirst("sub")?.Value;

            var iatClaim = context.Principal?.FindFirst(JwtRegisteredClaimNames.Iat)?.Value
                ?? context.Principal?.FindFirst("iat")?.Value;

            DateTimeOffset? issuedAt = null;
            if (!string.IsNullOrWhiteSpace(iatClaim) && long.TryParse(iatClaim, out var iatSeconds))
            {
                issuedAt = DateTimeOffset.FromUnixTimeSeconds(iatSeconds);
            }

            if (!string.IsNullOrWhiteSpace(jti) || !string.IsNullOrWhiteSpace(userId))
            {
                var isRevoked = await revocationService.IsTokenRevokedAsync(
                    jti ?? string.Empty,
                    userId,
                    claimTenantId,
                    issuedAt,
                    context.HttpContext.RequestAborted);

                if (isRevoked)
                {
                    context.HttpContext.Items[AuthFailureItemKey] = TokenRevokedErrorCode;
                    context.Fail("Token has been revoked.");
                    ExamPrismDiagnostics.RecordJwtRevokedRejection();
                }
            }
        }
    }

    public async Task HandleChallengeAsync(JwtBearerChallengeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.HttpContext.Items.TryGetValue(AuthFailureItemKey, out var codeObj) &&
            codeObj is string code)
        {
            if (string.Equals(code, TenantTokenMismatchErrorCode, StringComparison.Ordinal))
            {
                context.HandleResponse();
                await TenantProblemResults.WriteTenantTokenMismatchAsync(context.HttpContext);
            }
            else if (string.Equals(code, TokenRevokedErrorCode, StringComparison.Ordinal))
            {
                context.HandleResponse();
                await TenantProblemResults.WriteTokenRevokedAsync(context.HttpContext);
            }
        }
    }
}
