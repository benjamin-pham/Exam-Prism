namespace ExamPrism.Infrastructure.Identity;

using Common.Endpoints;
using Common.MultiTenancy;
using Common.Security;
using ExamPrism.Infrastructure.MultiTenancy;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;

public sealed class JwtTenantValidator
{
    public const string AuthFailureItemKey = "ExamPrism.AuthFailureCode";
    public const string TenantTokenMismatchErrorCode = "TENANT_TOKEN_MISMATCH";

    private readonly IMultiTenantContextAccessor<ExamPrismTenantInfo>? _tenantAccessor;

    public JwtTenantValidator(IMultiTenantContextAccessor<ExamPrismTenantInfo>? tenantAccessor = null)
    {
        _tenantAccessor = tenantAccessor;
    }

    public Task ValidateAsync(TokenValidatedContext context)
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
                return Task.CompletedTask;
            }
        }
        else if (tenantInfo is null)
        {
            if (!string.IsNullOrWhiteSpace(claimTenantId) ||
                !string.Equals(tokenScope, CustomClaimTypes.PlatformScopeValue, StringComparison.OrdinalIgnoreCase))
            {
                context.HttpContext.Items[AuthFailureItemKey] = TenantTokenMismatchErrorCode;
                context.Fail("Platform context requires a valid platform token.");
                return Task.CompletedTask;
            }
        }
        else
        {
            context.HttpContext.Items[AuthFailureItemKey] = TenantTokenMismatchErrorCode;
            context.Fail("Tenant is not active.");
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    public async Task HandleChallengeAsync(JwtBearerChallengeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.HttpContext.Items.TryGetValue(AuthFailureItemKey, out var codeObj) &&
            codeObj is string code &&
            string.Equals(code, TenantTokenMismatchErrorCode, StringComparison.Ordinal))
        {
            context.HandleResponse();
            await TenantProblemResults.WriteTenantTokenMismatchAsync(context.HttpContext);
        }
    }
}
