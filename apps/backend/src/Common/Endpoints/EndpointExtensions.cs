namespace Common.Endpoints;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Common.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Provides extension methods for automatically discovering and registering Minimal API endpoints.
/// </summary>
public static class EndpointExtensions
{
    /// <summary>
    /// Scans the provided assemblies (or the calling, executing, and entry assemblies by default)
    /// for non-abstract classes implementing <see cref="IEndpoint"/> and invokes their static
    /// <see cref="IEndpoint.MapEndpoint"/> method.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <param name="assemblies">Optional explicit assemblies to scan.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="app"/> or <paramref name="assemblies"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if an endpoint type does not provide an invokable MapEndpoint method.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IEndpointRouteBuilder MapEndpoints(
        this IEndpointRouteBuilder app,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(assemblies);

        var targetAssemblies = assemblies.Length > 0
            ? assemblies.Where(a => a is not null)
            : new[] { Assembly.GetCallingAssembly(), Assembly.GetExecutingAssembly(), Assembly.GetEntryAssembly() }
                .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.FullName?.StartsWith("ExamPrism") == true && !a.FullName.Contains(".Tests")))
                .Where(a => a is not null);

        var endpointTypes = targetAssemblies
            .Distinct()
            .SelectMany(GetTypesSafely)
            .Where(t => !t.IsAbstract && !t.IsInterface && !t.IsGenericTypeDefinition && !t.ContainsGenericParameters && typeof(IEndpoint).IsAssignableFrom(t))
            .Distinct();

        var validationGroup = app.MapGroup("").AddEndpointFilterFactory(ValidationFilterFactory.Create);

        foreach (var type in endpointTypes)
        {
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

    /// <summary>
    /// Scans the assembly containing the marker type <typeparamref name="TMarker"/>
    /// and registers all discovered <see cref="IEndpoint"/> instances.
    /// </summary>
    /// <typeparam name="TMarker">A type residing in the assembly to scan.</typeparam>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
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
