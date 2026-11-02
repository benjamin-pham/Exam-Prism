namespace Tests.Infrastructure;

using System.Security.Claims;
using System.Text.Json;
using Common.Caching;
using Common.MultiTenancy;
using Common.RateLimiting;
using Common.Security;
using ExamPrism.Infrastructure.Caching;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.RateLimiting;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Finbuckle.MultiTenant.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Wolverine;
using Xunit;

public class CrossCuttingTenantIsolationUnitTests
{
    [Fact]
    public async Task JwtTenantValidator_AllowsMatchingTenantClaim_RejectsCrossTenantOrMissingClaimOnTenantHost_AndRejectsTenantTokenOnPlatformHost()
    {
        var tenantGuid = Guid.NewGuid();
        var tenantA = new ExamPrismTenantInfo(tenantGuid.ToString("D"), "tenant-a")
        {
            TenantGuid = tenantGuid,
            Status = TenantStatus.Active
        };

        var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        var multiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantA);
        accessor.MultiTenantContext.Returns(multiTenantContext);

        var validator = new JwtTenantValidator(accessor);
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();

        // 1. Tenant host + matching claim -> Success
        {
            var httpContext = new DefaultHttpContext();
            var context = new TokenValidatedContext(httpContext, scheme, options)
            {
                Principal = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(CustomClaimTypes.TenantId, tenantA.Id)
                ], "Bearer"))
            };

            await validator.ValidateAsync(context);

            context.Result?.Failure.Should().BeNull();
            httpContext.Items.ContainsKey(JwtTenantValidator.AuthFailureItemKey).Should().BeFalse();
        }

        // 2. Tenant host + cross-tenant claim -> Fails with TENANT_TOKEN_MISMATCH
        {
            var httpContext = new DefaultHttpContext();
            var context = new TokenValidatedContext(httpContext, scheme, options)
            {
                Principal = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(CustomClaimTypes.TenantId, Guid.NewGuid().ToString("D"))
                ], "Bearer"))
            };

            await validator.ValidateAsync(context);

            context.Result?.Failure.Should().NotBeNull();
            httpContext.Items[JwtTenantValidator.AuthFailureItemKey].Should().Be(JwtTenantValidator.TenantTokenMismatchErrorCode);
        }

        // 3. Tenant host + missing claim -> Fails with TENANT_TOKEN_MISMATCH
        {
            var httpContext = new DefaultHttpContext();
            var context = new TokenValidatedContext(httpContext, scheme, options)
            {
                Principal = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "user-123")
                ], "Bearer"))
            };

            await validator.ValidateAsync(context);

            context.Result?.Failure.Should().NotBeNull();
            httpContext.Items[JwtTenantValidator.AuthFailureItemKey].Should().Be(JwtTenantValidator.TenantTokenMismatchErrorCode);
        }

        // 4. Platform host (TenantInfo == null) + token has tenant_id claim -> Fails with TENANT_TOKEN_MISMATCH
        {
            var platformAccessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
            platformAccessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null));
            var platformValidator = new JwtTenantValidator(platformAccessor);

            var httpContext = new DefaultHttpContext();
            var context = new TokenValidatedContext(httpContext, scheme, options)
            {
                Principal = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(CustomClaimTypes.TenantId, tenantA.Id)
                ], "Bearer"))
            };

            await platformValidator.ValidateAsync(context);

            context.Result?.Failure.Should().NotBeNull();
            httpContext.Items[JwtTenantValidator.AuthFailureItemKey].Should().Be(JwtTenantValidator.TenantTokenMismatchErrorCode);
        }

        // 5. Platform host (TenantInfo == null) + token has NO tenant_id claim -> Success
        {
            var platformAccessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
            platformAccessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null));
            var platformValidator = new JwtTenantValidator(platformAccessor);

            var httpContext = new DefaultHttpContext();
            var context = new TokenValidatedContext(httpContext, scheme, options)
            {
                Principal = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "platform-admin")
                ], "Bearer"))
            };

            await platformValidator.ValidateAsync(context);

            context.Result?.Failure.Should().BeNull();
            httpContext.Items.ContainsKey(JwtTenantValidator.AuthFailureItemKey).Should().BeFalse();
        }
    }

    [Fact]
    public async Task JwtTenantValidator_HandleChallengeAsync_WhenTenantMismatchMarkerPresent_Writes401ProblemDetails()
    {
        var validator = new JwtTenantValidator();
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();
        var authProperties = new AuthenticationProperties();

        // 1. With marker -> Handled and 401 ProblemDetails written
        {
            var httpContext = new DefaultHttpContext();
            httpContext.RequestServices = new ServiceCollection().BuildServiceProvider();
            httpContext.Response.Body = new MemoryStream();
            httpContext.Items[JwtTenantValidator.AuthFailureItemKey] = JwtTenantValidator.TenantTokenMismatchErrorCode;

            var challengeContext = new JwtBearerChallengeContext(httpContext, scheme, options, authProperties);

            await validator.HandleChallengeAsync(challengeContext);

            challengeContext.Handled.Should().BeTrue();
            httpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
            httpContext.Response.ContentType.Should().StartWith("application/problem+json");

            httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(httpContext.Response.Body);
            var bodyText = await reader.ReadToEndAsync();
            using var jsonDoc = JsonDocument.Parse(bodyText);
            var root = jsonDoc.RootElement;

            root.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status401Unauthorized);
            root.GetProperty("errorCode").GetString().Should().Be(JwtTenantValidator.TenantTokenMismatchErrorCode);
        }

        // 2. Without marker -> Not handled
        {
            var httpContext = new DefaultHttpContext();
            httpContext.RequestServices = new ServiceCollection().BuildServiceProvider();
            httpContext.Response.Body = new MemoryStream();

            var challengeContext = new JwtBearerChallengeContext(httpContext, scheme, options, authProperties);

            await validator.HandleChallengeAsync(challengeContext);

            challengeContext.Handled.Should().BeFalse();
            httpContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK); // default before auth handler mutates
        }
    }

    [Fact]
    public async Task HybridCacheService_IsolatesIdenticalKeysAndTagsAcrossTenantsAndPlatform_AndSupportsUmbrellaTenantEviction()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<CacheOptions>(_ => { });
        services.AddMultiTenant<ExamPrismTenantInfo>();
        services.AddHybridCache();
        services.AddSingleton<ICacheService, HybridCacheService>();

        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();
        var hybridCache = provider.GetRequiredService<HybridCache>();
        var setter = provider.GetRequiredService<IMultiTenantContextSetter>();

        var tenantAId = Guid.NewGuid();
        var tenantA = new ExamPrismTenantInfo(tenantAId.ToString("D"), "tenant-a")
        {
            TenantGuid = tenantAId,
            Status = TenantStatus.Active
        };

        var tenantBId = Guid.NewGuid();
        var tenantB = new ExamPrismTenantInfo(tenantBId.ToString("D"), "tenant-b")
        {
            TenantGuid = tenantBId,
            Status = TenantStatus.Active
        };

        // Write "quiz:1" with tag "quizzes" under Tenant A, Tenant B, and Platform
        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantA);
        await cache.SetAsync("quiz:1", "val-A", tags: ["quizzes"]);

        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantB);
        await cache.SetAsync("quiz:1", "val-B", tags: ["quizzes"]);

        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null);
        await cache.SetAsync("quiz:1", "val-P", tags: ["quizzes"]);

        // Verify each context reads its own isolated value
        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantA);
        (await cache.GetOrDefaultAsync<string>("quiz:1")).Should().Be("val-A");

        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantB);
        (await cache.GetOrDefaultAsync<string>("quiz:1")).Should().Be("val-B");

        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null);
        (await cache.GetOrDefaultAsync<string>("quiz:1")).Should().Be("val-P");

        // RemoveByTagAsync("quizzes") under Tenant A evicts ONLY Tenant A
        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantA);
        await cache.RemoveByTagAsync("quizzes");

        (await cache.GetOrDefaultAsync<string>("quiz:1")).Should().BeNull();

        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantB);
        (await cache.GetOrDefaultAsync<string>("quiz:1")).Should().Be("val-B");

        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null);
        (await cache.GetOrDefaultAsync<string>("quiz:1")).Should().Be("val-P");

        // hybridCache.RemoveByTagAsync($"t:{tenantBId:D}") evicts all Tenant B entries via umbrella tag
        await hybridCache.RemoveByTagAsync($"t:{tenantBId:D}");

        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantB);
        (await cache.GetOrDefaultAsync<string>("quiz:1")).Should().BeNull();

        // Platform entry is completely untouched
        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null);
        (await cache.GetOrDefaultAsync<string>("quiz:1")).Should().Be("val-P");
    }

    [Fact]
    public async Task TenantWolverineMiddleware_SetsTenantContextForBackgroundEnvelopeByIdOrSlug_RestoresPriorContextInFinally_AndThrowsForInactiveOrUnknownTenant()
    {
        var services = new ServiceCollection();
        services.AddMultiTenant<ExamPrismTenantInfo>();
        var sp = services.BuildServiceProvider();

        var accessor = sp.GetRequiredService<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        var setter = sp.GetRequiredService<IMultiTenantContextSetter>();

        var tenantAId = Guid.NewGuid();
        var tenantA = new ExamPrismTenantInfo(tenantAId.ToString("D"), "tenant-a")
        {
            TenantGuid = tenantAId,
            Status = TenantStatus.Active
        };

        var tenantSuspendedId = Guid.NewGuid();
        var tenantSuspended = new ExamPrismTenantInfo(tenantSuspendedId.ToString("D"), "tenant-suspended")
        {
            TenantGuid = tenantSuspendedId,
            Status = TenantStatus.Suspended
        };

        var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();
        store.GetByIdentifierAsync("tenant-a").Returns(tenantA);
        store.GetAsync(tenantAId.ToString("D")).Returns(tenantA);
        store.GetByIdentifierAsync("tenant-suspended").Returns(tenantSuspended);
        store.GetByIdentifierAsync("unknown").Returns((ExamPrismTenantInfo?)null);

        // 1. By slug: switches to tenant-a during handler and restores prior null context in Finally (Review Focus #5)
        {
            setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null);
            accessor.MultiTenantContext?.TenantInfo.Should().BeNull();

            var middleware = new TenantWolverineMiddleware();
            var envelope = new Envelope { TenantId = "tenant-a" };

            await middleware.BeforeAsync(envelope, accessor, setter, store);

            accessor.MultiTenantContext?.TenantInfo.Should().NotBeNull();
            accessor.MultiTenantContext!.TenantInfo!.Identifier.Should().Be("tenant-a");
            accessor.MultiTenantContext.TenantInfo.Id.Should().Be(tenantAId.ToString("D"));

            middleware.Finally(setter);

            accessor.MultiTenantContext?.TenantInfo.Should().BeNull();
        }

        // 2. By Id (Guid): switches to tenant-a and restores prior context
        {
            setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null);
            var middleware = new TenantWolverineMiddleware();
            var envelope = new Envelope { TenantId = tenantAId.ToString("D") };

            await middleware.BeforeAsync(envelope, accessor, setter, store);

            accessor.MultiTenantContext?.TenantInfo.Should().NotBeNull();
            accessor.MultiTenantContext!.TenantInfo!.Id.Should().Be(tenantAId.ToString("D"));

            middleware.Finally();

            accessor.MultiTenantContext?.TenantInfo.Should().BeNull();
        }

        // 3. Suspended tenant -> Throws InvalidOperationException
        {
            setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null);
            var middleware = new TenantWolverineMiddleware();
            var envelope = new Envelope { TenantId = "tenant-suspended" };

            var act = () => middleware.BeforeAsync(envelope, accessor, setter, store);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*inactive or unknown*");

            middleware.Finally(setter);
            accessor.MultiTenantContext?.TenantInfo.Should().BeNull();
        }

        // 4. Unknown tenant -> Throws InvalidOperationException
        {
            setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null);
            var middleware = new TenantWolverineMiddleware();
            var envelope = new Envelope { TenantId = "unknown" };

            var act = () => middleware.BeforeAsync(envelope, accessor, setter, store);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*inactive or unknown*");

            middleware.Finally(setter);
            accessor.MultiTenantContext?.TenantInfo.Should().BeNull();
        }

        // 5. Envelope with "*DEFAULT*" or null TenantId does nothing
        {
            setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null);
            var middleware = new TenantWolverineMiddleware();
            var envelope = new Envelope { TenantId = "*DEFAULT*" };

            await middleware.BeforeAsync(envelope, accessor, setter, store);
            accessor.MultiTenantContext?.TenantInfo.Should().BeNull();

            middleware.Finally(setter);
            accessor.MultiTenantContext?.TenantInfo.Should().BeNull();
        }

        // 6. Already matching tenant -> Fast path, does not re-query store or override context
        {
            setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantA);
            var middleware = new TenantWolverineMiddleware();
            var envelope = new Envelope { TenantId = "tenant-a" };

            store.ClearReceivedCalls();

            await middleware.BeforeAsync(envelope, accessor, setter, store);

            await store.DidNotReceiveWithAnyArgs().GetByIdentifierAsync(Arg.Any<string>());
            await store.DidNotReceiveWithAnyArgs().GetAsync(Arg.Any<string>());
            accessor.MultiTenantContext?.TenantInfo.Should().BeSameAs(tenantA);

            middleware.Finally(setter);

            // Preserved tenantA since it wasn't overridden
            accessor.MultiTenantContext?.TenantInfo.Should().BeSameAs(tenantA);
        }
    }

    [Fact]
    public async Task RateLimitingSetup_IsolatesRateLimitPartitions_AcrossTenantsAndPlatform()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["RateLimiting:Auth:PermitLimit"] = "1";
        builder.Configuration["RateLimiting:Auth:WindowSeconds"] = "60";
        builder.Configuration["RateLimiting:Global:PermitLimit"] = "1";
        builder.Configuration["RateLimiting:Global:WindowSeconds"] = "60";
        builder.Configuration["Cache:KeyPrefix"] = "examprism:";

        builder.Services.AddMultiTenant<ExamPrismTenantInfo>();
        builder.Services.AddExamPrismRateLimiting(builder.Configuration, useRedis: false);

        var app = builder.Build();
        app.UseMultiTenant();

        var tenantAId = Guid.NewGuid();
        var tenantA = new ExamPrismTenantInfo(tenantAId.ToString("D"), "tenant-a")
        {
            TenantGuid = tenantAId,
            Status = TenantStatus.Active
        };

        var tenantBId = Guid.NewGuid();
        var tenantB = new ExamPrismTenantInfo(tenantBId.ToString("D"), "tenant-b")
        {
            TenantGuid = tenantBId,
            Status = TenantStatus.Active
        };

        app.Use(async (ctx, next) =>
        {
            var header = ctx.Request.Headers["X-Test-Tenant"].ToString();
            if (header == "tenant-a")
            {
                ctx.Items[typeof(IMultiTenantContext)] = new MultiTenantContext<ExamPrismTenantInfo>(tenantA);
            }
            else if (header == "tenant-b")
            {
                ctx.Items[typeof(IMultiTenantContext)] = new MultiTenantContext<ExamPrismTenantInfo>(tenantB);
            }
            else
            {
                ctx.Items[typeof(IMultiTenantContext)] = new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null);
            }

            await next();
        });

        app.UseRateLimiter();

        app.MapPost("/auth-test", () => Results.Ok("ok")).RequireRateLimiting(RateLimitPolicies.Auth);

        await app.StartAsync();

        var client = app.GetTestClient();

        // 1. Tenant A: 1st request -> 200, 2nd request -> 429
        var reqA1 = new HttpRequestMessage(HttpMethod.Post, "/auth-test");
        reqA1.Headers.Add("X-Test-Tenant", "tenant-a");
        var resA1 = await client.SendAsync(reqA1);
        resA1.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var reqA2 = new HttpRequestMessage(HttpMethod.Post, "/auth-test");
        reqA2.Headers.Add("X-Test-Tenant", "tenant-a");
        var resA2 = await client.SendAsync(reqA2);
        resA2.StatusCode.Should().Be(System.Net.HttpStatusCode.TooManyRequests);

        // 2. Tenant B: from same client, 1st request -> 200 (isolated bucket!), 2nd -> 429
        var reqB1 = new HttpRequestMessage(HttpMethod.Post, "/auth-test");
        reqB1.Headers.Add("X-Test-Tenant", "tenant-b");
        var resB1 = await client.SendAsync(reqB1);
        resB1.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var reqB2 = new HttpRequestMessage(HttpMethod.Post, "/auth-test");
        reqB2.Headers.Add("X-Test-Tenant", "tenant-b");
        var resB2 = await client.SendAsync(reqB2);
        resB2.StatusCode.Should().Be(System.Net.HttpStatusCode.TooManyRequests);

        // 3. Platform: from same client, 1st request -> 200, 2nd -> 429
        var reqP1 = new HttpRequestMessage(HttpMethod.Post, "/auth-test");
        var resP1 = await client.SendAsync(reqP1);
        resP1.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var reqP2 = new HttpRequestMessage(HttpMethod.Post, "/auth-test");
        var resP2 = await client.SendAsync(reqP2);
        resP2.StatusCode.Should().Be(System.Net.HttpStatusCode.TooManyRequests);
    }
}
