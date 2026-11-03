# Scalar Multi-Tenant & Subdomain API Docs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable full multi-tenant capabilities in Scalar API Reference and OpenAPI:
1. Support subdomain server variable `{tenant}.localhost` in OpenAPI servers with dynamic variable selection.
2. Provide `X-Tenant` API Key security scheme in OpenAPI so Scalar's Auth dialog lets developers input the tenant slug directly.
3. Support direct Scalar UI access on active tenant subdomains (`http://school-a.localhost:{port}/scalar/v1`) without origin mismatch.
4. Add Development CORS for `*.localhost` origins so cross-subdomain API calls from Scalar UI succeed.

**Architecture:** ASP.NET Core 10 OpenAPI document transformers, Scalar.AspNetCore 2.18.0 options, and Microsoft.AspNetCore.Cors middleware.

**Tech Stack:** .NET 10 (`net10.0`), C# 13, ASP.NET Core Minimal APIs, Microsoft.AspNetCore.OpenApi, Scalar.AspNetCore, xUnit, FluentAssertions.

## Global Constraints

- Target Framework: .NET 10 (`net10.0`).
- Only modify `apps/backend/src/Program.cs` and `apps/backend/tests/ExamPrism.Tests/Features/ScalarApiDocsTests.cs`.
- All development-only docs and CORS must only run when `app.Environment.IsDevelopment()`.
- Production must continue returning 404 for `/scalar/v1` and must not expose development CORS.

## Review Focus

1. OpenAPI document must include both `Bearer` and `X-Tenant` security schemes.
2. OpenAPI document must define server templates including relative path and `{tenant}.localhost:{port}` with `tenant` variable.
3. Scalar UI must be accessible on both root host (`localhost`) and tenant subdomains (`school-a.localhost`).
4. Preflight CORS OPTIONS requests from `*.localhost` in Development must be permitted.

---

### Task 1: Multi-Tenant OpenAPI & Scalar Configuration with Development CORS

**Files:**
- Modify: `apps/backend/tests/ExamPrism.Tests/Features/ScalarApiDocsTests.cs`
- Modify: `apps/backend/src/Program.cs`

**Specification:**
1. In `ScalarApiDocsTests.cs`:
   - Add test `GetOpenApiDocument_ContainsXTenantSecurityScheme`: asserts `components.securitySchemes.X-Tenant` has `type = apiKey`, `name = X-Tenant`, `in = header`.
   - Add test `GetOpenApiDocument_ContainsMultiTenantServerVariables`: asserts `servers` contains a server url containing `{tenant}` with server variable `tenant`.
   - Add test `GetScalarApiReference_OnTenantSubdomain_InDevelopment_ReturnsOk`: requests `http://tenant-a.localhost/scalar/v1` and returns 200 OK.
   - Add test `OptionsRequest_FromLocalhostSubdomain_InDevelopment_ReturnsCorsHeaders`: sends preflight OPTIONS request with `Origin: http://school-a.localhost:5000`, `Access-Control-Request-Method: GET` and verifies `Access-Control-Allow-Origin` is present.
2. In `Program.cs`:
   - Add CORS policy `builder.Services.AddCors(...)` in development, or configure a development policy `DevSubdomainCors` with `SetIsOriginAllowed(origin => new Uri(origin).Host.EndsWith("localhost", StringComparison.OrdinalIgnoreCase))` allowing credentials, methods, and headers.
   - In `builder.Services.AddOpenApi(...)`:
     - Add `X-Tenant` to `document.Components.SecuritySchemes`:
       ```csharp
       document.Components.SecuritySchemes["X-Tenant"] = new OpenApiSecurityScheme
       {
           Type = SecuritySchemeType.ApiKey,
           In = ParameterLocation.Header,
           Name = "X-Tenant",
           Description = "Tenant slug for multi-tenant requests in development (e.g. school-a)"
       };
       ```
     - Add document-level `document.Servers`:
       - `new OpenApiServer { Url = "/", Description = "Current Origin" }`
       - `new OpenApiServer { Url = "{scheme}://{tenant}.localhost:{port}", Description = "Subdomain Tenant (Development)", Variables = ... }`
   - In Scalar configuration:
     - `options.WithTitle("ExamPrism API").AddPreferredSecuritySchemes(["Bearer", "X-Tenant"]);`
   - In middleware pipeline:
     - Apply CORS `app.UseCors("DevSubdomainCors")` before `app.UseRouting()`.

