namespace Common.Endpoints;

using System.Reflection;
using System.Runtime.ExceptionServices;
using Common.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class EndpointExtensions
{
    public static IEndpointRouteBuilder MapEndpoints(
        this IEndpointRouteBuilder app,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(assemblies);

        IEnumerable<Assembly> targetAssemblies;
        if (assemblies.Length > 0)
        {
            targetAssemblies = assemblies.Where(a => a is not null);
        }
        else
        {
            var defaultList = new List<Assembly>();

            if (Assembly.GetEntryAssembly() is { } entryAssembly)
            {
                defaultList.Add(entryAssembly);
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (name != null && (name == "ExamPrism" || name.StartsWith("ExamPrism.Modules.", StringComparison.Ordinal)))
                {
                    defaultList.Add(asm);
                }
            }

            if (app.ServiceProvider.GetService<Common.Modules.IModuleRegistry>() is { } moduleRegistry)
            {
                defaultList.AddRange(moduleRegistry.ModuleAssemblies);
            }

            defaultList.Add(typeof(EndpointExtensions).Assembly);
            targetAssemblies = defaultList;
        }

        var endpointTypes = targetAssemblies
            .Distinct()
            .SelectMany(GetTypesSafely)
            .Where(t => !t.IsAbstract && !t.IsInterface && !t.IsGenericTypeDefinition && !t.ContainsGenericParameters && typeof(IEndpoint).IsAssignableFrom(t))
            .Distinct();

        var env = app.ServiceProvider.GetService<IHostEnvironment>();
        var isDevelopment = env?.IsDevelopment() ?? false;

        var validationGroup = app.MapGroup("").AddEndpointFilterFactory(ValidationFilterFactory.Create);

        foreach (var type in endpointTypes)
        {
            if (!isDevelopment && (type.Namespace == "ExamPrism.Endpoints.Test" || (type.Namespace?.StartsWith("ExamPrism.Endpoints.Test.") ?? false)))
            {
                continue;
            }

            var mapMethod = type.GetMethod(
                nameof(IEndpoint.MapEndpoint),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                [typeof(IEndpointRouteBuilder)])
                ?? GetExplicitInterfaceMethod(type);

            if (mapMethod is null)
            {
                throw new InvalidOperationException(
                    $"Type '{type.FullName}' implements '{nameof(IEndpoint)}' but no static '{nameof(IEndpoint.MapEndpoint)}' method could be found.");
            }

            try
            {
                mapMethod.Invoke(null, [validationGroup]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
        }

        return app;
    }

    public static IEndpointRouteBuilder MapEndpoints<TMarker>(this IEndpointRouteBuilder app)
        => app.MapEndpoints(typeof(TMarker).Assembly);

    private static MethodInfo? GetExplicitInterfaceMethod(Type type)
    {
        var map = type.GetInterfaceMap(typeof(IEndpoint));
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.InterfaceMethods[i].Name == nameof(IEndpoint.MapEndpoint))
            {
                return map.TargetMethods[i];
            }
        }

        return null;
    }

    private static IEnumerable<Type> GetTypesSafely(Assembly? assembly)
    {
        if (assembly is null)
        {
            return [];
        }

        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}

