namespace ExamPrism.Tests.Architecture;

using System.Reflection;
using Common.Endpoints;
using Common.Handlers;
using Common.Results;
using ExamPrism.Features.Auth;
using ExamPrism.Features.Health;
using ExamPrism.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Xunit;

public class ArchitectureTests
{
    private static readonly Assembly AppAssembly = typeof(Program).Assembly;

    private static IEnumerable<Type> AppTypes => AppAssembly.GetTypes()
        .Where(t => !t.IsNested && !t.Name.Contains('<'));

    [Fact]
    public void Handlers_ImplementingIHandler_MustBePublicSealedInFeaturesEndWithHandlerAndImplementGenericIHandler()
    {
        var handlerTypes = AppTypes
            .Where(t => typeof(IHandler).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .ToList();

        handlerTypes.Should().NotBeEmpty();

        foreach (var handler in handlerTypes)
        {
            handler.IsPublic.Should().BeTrue($"{handler.FullName} must be public");
            handler.IsSealed.Should().BeTrue($"{handler.FullName} must be sealed");
            handler.Namespace.Should().StartWith("ExamPrism.Features", $"{handler.FullName} must be in ExamPrism.Features");
            handler.Name.Should().EndWith("Handler", $"{handler.FullName} must end with 'Handler'");

            var implementsGenericHandler = handler.GetInterfaces().Any(i =>
                i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(IHandler<>) ||
                 i.GetGenericTypeDefinition() == typeof(IHandler<,>)));

            implementsGenericHandler.Should().BeTrue(
                $"{handler.FullName} must implement closed generic IHandler<TRequest> or IHandler<TRequest, TResponse>");
        }

        var featureHandlerClasses = AppTypes
            .Where(t => t.Namespace != null &&
                        t.Namespace.StartsWith("ExamPrism.Features") &&
                        t.Name.EndsWith("Handler") &&
                        !t.IsInterface &&
                        !t.IsAbstract)
            .ToList();

        foreach (var cls in featureHandlerClasses)
        {
            typeof(IHandler).IsAssignableFrom(cls).Should().BeTrue($"{cls.FullName} must implement IHandler");
        }
    }

    [Fact]
    public void Handlers_MustUseNonDefaultedConstructorInjection_AndDeclareSingleInstanceHandleWithRecordInputAndResultReturn()
    {
        var handlerTypes = AppTypes
            .Where(t => typeof(IHandler).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .ToList();

        foreach (var handler in handlerTypes)
        {
            var constructors = handler.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            constructors.Should().NotBeEmpty($"{handler.FullName} must declare a public instance constructor");

            foreach (var ctor in constructors)
            {
                foreach (var param in ctor.GetParameters())
                {
                    param.HasDefaultValue.Should().BeFalse(
                        $"{handler.FullName} constructor parameter '{param.Name}' must not have a default value to ensure clean Wolverine DI binding");
                }
            }

            var declaredPublicMethods = handler.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .ToList();

            declaredPublicMethods.Should().ContainSingle(
                $"{handler.FullName} must declare exactly one public method");

            var handleMethod = declaredPublicMethods.Single();
            handleMethod.Name.Should().Be("Handle", $"{handler.FullName} entry method must be named 'Handle'");
            handleMethod.IsStatic.Should().BeFalse($"{handler.FullName}.Handle must be an instance method");

            var parameters = handleMethod.GetParameters();
            (parameters.Length is 1 or 2).Should().BeTrue(
                $"{handler.FullName}.Handle must accept either 1 parameter (message) or 2 parameters (message, CancellationToken)");

            var messageParam = parameters[0];
            var messageType = messageParam.ParameterType;
            IsRecordType(messageType).Should().BeTrue(
                $"{handler.FullName}.Handle message parameter type '{messageType.Name}' must be a C# record");

            var paramTypeName = messageType.Name;
            var isValidMessageName = paramTypeName.EndsWith("Command") ||
                                     paramTypeName.EndsWith("Query") ||
                                     paramTypeName.EndsWith("Event");

            isValidMessageName.Should().BeTrue(
                $"{handler.FullName}.Handle first parameter type '{paramTypeName}' must end with Command, Query, or Event");

            if (parameters.Length == 2)
            {
                parameters[1].ParameterType.Should().Be(
                    typeof(CancellationToken),
                    $"{handler.FullName}.Handle optional second parameter must be CancellationToken");
            }

            IsValidHandlerReturnType(handleMethod.ReturnType).Should().BeTrue(
                $"{handler.FullName}.Handle return type '{handleMethod.ReturnType.Name}' must be Task<Result> or Task<Result<TResult>>");
        }
    }

    [Theory]
    [InlineData(typeof(Task<Result>), true)]
    [InlineData(typeof(Task<Result<RegisterResult>>), true)]
    [InlineData(typeof(Task<Result<GetHealthResult>>), true)]
    [InlineData(typeof(Task), false)]
    [InlineData(typeof(void), false)]
    [InlineData(typeof(Task<RegisterResult>), false)]
    [InlineData(typeof(Task<Result<string>>), false)]
    [InlineData(typeof(Task<Result<IResult>>), false)]
    [InlineData(typeof(int), false)]
    public void IsValidHandlerReturnType_ValidatesExpectedReturnTypes(Type type, bool expected)
    {
        IsValidHandlerReturnType(type).Should().Be(expected);
    }

    [Theory]
    [InlineData(typeof(RegisterCommand), true)]
    [InlineData(typeof(GetMeQuery), true)]
    [InlineData(typeof(RegisterHandler), false)]
    [InlineData(typeof(string), false)]
    public void IsRecordType_DetectsRecordsVersusClasses(Type type, bool expected)
    {
        IsRecordType(type).Should().Be(expected);
    }

    [Fact]
    public void HasDbContextReference_DetectsDbContextInsideNestedInlineLambdaClosure()
    {
        HasDbContextReference(typeof(ProbeEndpointWithLambdaDbContext)).Should().BeTrue();
    }

    [Fact]
    public void TypesInFeatures_MustNotReferenceAspNetCoreHttpRoutingOrMvc()
    {
        var forbiddenPrefixes = new[]
        {
            "Microsoft.AspNetCore.Http",
            "Microsoft.AspNetCore.Routing",
            "Microsoft.AspNetCore.Mvc"
        };

        var featureTypes = AppAssembly.GetTypes()
            .Where(t => t.Namespace != null && t.Namespace.StartsWith("ExamPrism.Features"))
            .ToList();

        foreach (var type in featureTypes)
        {
            if (type.BaseType != null && type.BaseType != typeof(object))
            {
                AssertNotForbidden(type.BaseType, type, "BaseType", forbiddenPrefixes);
            }

            foreach (var iface in type.GetInterfaces())
            {
                AssertNotForbidden(iface, type, $"Interface {iface.Name}", forbiddenPrefixes);
            }

            var members = type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (var member in members)
            {
                switch (member)
                {
                    case FieldInfo f:
                        AssertNotForbidden(f.FieldType, type, f.Name, forbiddenPrefixes);
                        break;
                    case PropertyInfo p:
                        AssertNotForbidden(p.PropertyType, type, p.Name, forbiddenPrefixes);
                        break;
                    case MethodBase m:
                        if (m is MethodInfo mi)
                        {
                            AssertNotForbidden(mi.ReturnType, type, m.Name, forbiddenPrefixes);
                        }
                        foreach (var param in m.GetParameters())
                        {
                            AssertNotForbidden(param.ParameterType, type, $"{m.Name}({param.Name})", forbiddenPrefixes);
                        }
                        break;
                }
            }
        }
    }

    [Fact]
    public void Endpoints_ImplementingIEndpoint_MustBeInlineWithoutHandleMethod_AndNeverInjectDbContextIncludingNestedLambdas()
    {
        var endpointTypes = AppTypes
            .Where(t => typeof(IEndpoint).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .ToList();

        endpointTypes.Should().NotBeEmpty();

        foreach (var ep in endpointTypes)
        {
            ep.IsPublic.Should().BeTrue($"{ep.FullName} must be public");
            ep.IsSealed.Should().BeTrue($"{ep.FullName} must be sealed");
            ep.Namespace.Should().StartWith("ExamPrism.Endpoints", $"{ep.FullName} must be in ExamPrism.Endpoints");
            ep.Name.Should().Match(n => n.EndsWith("Endpoint") || n.EndsWith("Endpoints"),
                $"{ep.FullName} must end with 'Endpoint' or 'Endpoints'");

            var declaredMethods = ep.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            var handleMethods = declaredMethods
                .Where(m => m.Name is "Handle" or "HandleAsync")
                .ToList();

            handleMethods.Should().BeEmpty(
                $"{ep.FullName} must not declare Handle/HandleAsync methods; route logic must be executed inline inside MapEndpoint");

            HasDbContextReference(ep).Should().BeFalse(
                $"{ep.FullName} (or one of its nested lambda closures) must not inject or reference DbContext directly");
        }

        var endpointClasses = AppTypes
            .Where(t => t.Namespace != null &&
                        t.Namespace.StartsWith("ExamPrism.Endpoints") &&
                        (t.Name.EndsWith("Endpoint") || t.Name.EndsWith("Endpoints")) &&
                        !t.IsInterface &&
                        !t.IsAbstract)
            .ToList();

        foreach (var cls in endpointClasses)
        {
            typeof(IEndpoint).IsAssignableFrom(cls).Should().BeTrue($"{cls.FullName} must implement IEndpoint");
        }
    }

    public static bool IsValidHandlerReturnType(Type returnType)
    {
        if (returnType == typeof(Task<Result>))
        {
            return true;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var taskInner = returnType.GetGenericArguments()[0];
            if (taskInner.IsGenericType && taskInner.GetGenericTypeDefinition() == typeof(Result<>))
            {
                var resultInner = taskInner.GetGenericArguments()[0];
                var unwrapped = Nullable.GetUnderlyingType(resultInner) ?? resultInner;
                if (unwrapped == typeof(IResult) || typeof(IResult).IsAssignableFrom(unwrapped))
                {
                    return false;
                }
                return unwrapped.Name.EndsWith("Result");
            }
        }

        return false;
    }

    public static bool IsRecordType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;
    }

    public static bool HasDbContextReference(Type type)
    {
        var typesToCheck = new List<Type> { type };
        typesToCheck.AddRange(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));

        foreach (var t in typesToCheck)
        {
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (fields.Any(f => typeof(DbContext).IsAssignableFrom(f.FieldType)))
            {
                return true;
            }

            var constructors = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (constructors.Any(c => c.GetParameters().Any(p => typeof(DbContext).IsAssignableFrom(p.ParameterType))))
            {
                return true;
            }

            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (methods.Any(m => m.GetParameters().Any(p => typeof(DbContext).IsAssignableFrom(p.ParameterType))))
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertNotForbidden(Type type, Type declaringType, string memberName, string[] forbiddenPrefixes)
    {
        var unwrappedTypes = UnwrapTypes(type);
        foreach (var t in unwrappedTypes)
        {
            var ns = t.Namespace;
            if (ns is null) continue;

            foreach (var prefix in forbiddenPrefixes)
            {
                ns.StartsWith(prefix, StringComparison.Ordinal).Should().BeFalse(
                    $"{declaringType.FullName}.{memberName} references forbidden HTTP type '{t.FullName}'");
            }
        }
    }

    private static IEnumerable<Type> UnwrapTypes(Type type)
    {
        yield return type;
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                foreach (var inner in UnwrapTypes(arg))
                {
                    yield return inner;
                }
            }
        }
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            foreach (var inner in UnwrapTypes(underlying))
            {
                yield return inner;
            }
        }
    }

    private sealed class ProbeEndpointWithLambdaDbContext : IEndpoint
    {
        public static void MapEndpoint(IEndpointRouteBuilder app)
        {
            var action = (AppDbContext db) => db.ToString();
            _ = action;
        }
    }
}
