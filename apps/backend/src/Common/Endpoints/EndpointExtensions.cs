namespace Common.Endpoints;

using System.Reflection;
using Microsoft.AspNetCore.Routing;

public static class EndpointExtensions
{
    public static IEndpointRouteBuilder MapEndpoints(
        this IEndpointRouteBuilder app,
        params Assembly[] assemblies)
    {
        var targetAssemblies = assemblies.Length > 0
            ? assemblies.Distinct()
            : [Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()];

        var endpointTypes = targetAssemblies
            .SelectMany(GetExportedTypesSafely)
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IEndpoint).IsAssignableFrom(t))
            .Distinct();

        foreach (var type in endpointTypes)
        {
            var mapMethod = type.GetMethod(
                nameof(IEndpoint.MapEndpoint),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                [typeof(IEndpointRouteBuilder)])
                ?? type.GetInterfaceMap(typeof(IEndpoint)).TargetMethods.FirstOrDefault();

            mapMethod?.Invoke(null, [app]);
        }

        return app;
    }

    public static IEndpointRouteBuilder MapEndpoints<TMarker>(this IEndpointRouteBuilder app)
        => app.MapEndpoints(typeof(TMarker).Assembly);

    private static IEnumerable<Type> GetExportedTypesSafely(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
