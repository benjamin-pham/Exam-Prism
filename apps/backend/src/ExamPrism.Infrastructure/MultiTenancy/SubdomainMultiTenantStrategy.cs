namespace ExamPrism.Infrastructure.MultiTenancy;

using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

public class SubdomainMultiTenantStrategy : IMultiTenantStrategy
{
    private readonly IOptions<MultiTenancyOptions> _options;
    private readonly IHostEnvironment _environment;

    public SubdomainMultiTenantStrategy(
        IOptions<MultiTenancyOptions> options,
        IHostEnvironment environment)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public int Priority => 0;

    public Task<string?> GetIdentifierAsync(object context)
    {
        if (context is not HttpContext httpContext)
        {
            return Task.FromResult<string?>(null);
        }

        var result = SubdomainParser.Parse(httpContext, _options.Value, _environment.IsDevelopment());
        var identifier = result.Kind == HostResolutionKind.TenantCandidate ? result.Identifier : null;
        return Task.FromResult(identifier);
    }
}
