namespace ExamPrism.Tests.Architecture;

using System.Reflection;
using Common.Endpoints;
using Common.Handlers;
using ExamPrism.Features.Auth;
using ExamPrism.Features.Health;
using ExamPrism.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

public class ArchitectureTests
{
    private static readonly Assembly AppAssembly = typeof(Program).Assembly;

    private static IEnumerable<Type> AppTypes => AppAssembly.GetTypes()
        .Where(t => !t.IsNested && !t.Name.Contains('<'));

    [Fact]
    public void Handlers_ImplementingIHandler_MustBePublicSealedInFeaturesAndEndWithHandler()
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
    public void Handlers_MustDeclareSinglePublicStaticHandleEntry_WithValidMessageInputAndResultReturn()
    {
        var handlerTypes = AppTypes
            .Where(t => typeof(IHandler).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .ToList();

        var allowedOtherPublicMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "Load", "LoadAsync",
            "Before", "BeforeAsync",
            "After", "AfterAsync",
            "Validate", "ValidateAsync"
        };

        foreach (var handler in handlerTypes)
        {
            var declaredMethods = handler.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .ToList();

            var handleMethods = declaredMethods
                .Where(m => m.IsStatic && (m.Name == "Handle" || m.Name == "HandleAsync"))
                .ToList();

            handleMethods.Should().ContainSingle(
                $"{handler.FullName} must declare exactly one public static Handle or HandleAsync entry method");

            var handleMethod = handleMethods.Single();

            var otherPublicMethods = declaredMethods.Except(handleMethods);
            foreach (var other in otherPublicMethods)
            {
                allowedOtherPublicMethods.Should().Contain(
                    other.Name,
                    $"{handler.FullName} method {other.Name} is not an allowed Wolverine lifecycle method");
            }

            var parameters = handleMethod.GetParameters();
            parameters.Should().NotBeEmpty($"{handler.FullName}.{handleMethod.Name} must accept at least one parameter");

            var messageParam = parameters[0];
            var paramTypeName = messageParam.ParameterType.Name;
            var isValidMessage = paramTypeName.EndsWith("Command") ||
                                 paramTypeName.EndsWith("Query") ||
                                 paramTypeName.EndsWith("Event");

            isValidMessage.Should().BeTrue(
                $"{handler.FullName}.{handleMethod.Name} first parameter type '{paramTypeName}' must end with Command, Query, or Event");

            IsValidHandlerReturnType(handleMethod.ReturnType).Should().BeTrue(
                $"{handler.FullName}.{handleMethod.Name} return type '{handleMethod.ReturnType.Name}' must be void, Task, ValueTask, or end with Result");
        }
    }

    [Theory]
    [InlineData(typeof(void), true)]
    [InlineData(typeof(Task), true)]
    [InlineData(typeof(ValueTask), true)]
    [InlineData(typeof(Task<RefreshTokenResult?>), true)]
    [InlineData(typeof(Task<RegisterResult>), true)]
    [InlineData(typeof(GetHealthResult), true)]
    [InlineData(typeof(Task<IResult>), false)]
    [InlineData(typeof(Task<string>), false)]
    [InlineData(typeof(int), false)]
    public void IsValidHandlerReturnType_ValidatesExpectedReturnTypes(Type type, bool expected)
    {
        IsValidHandlerReturnType(type).Should().Be(expected);
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
    public void Endpoints_ImplementingIEndpoint_MustBePublicSealedInEndpointsAndNotInjectAppDbContext()
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

            var constructors = ep.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

            foreach (var ctor in constructors)
            {
                foreach (var p in ctor.GetParameters())
                {
                    typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(p.ParameterType).Should().BeFalse(
                        $"{ep.FullName} constructor must not inject DbContext directly");
                }
            }

            var methods = ep.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (var m in methods)
            {
                foreach (var p in m.GetParameters())
                {
                    typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(p.ParameterType).Should().BeFalse(
                        $"{ep.FullName}.{m.Name} must not inject DbContext directly");
                }
            }
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
        if (returnType == typeof(void) || returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            return true;
        }

        if (returnType.IsGenericType &&
            (returnType.GetGenericTypeDefinition() == typeof(Task<>) ||
             returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            var inner = returnType.GetGenericArguments()[0];
            var unwrapped = Nullable.GetUnderlyingType(inner) ?? inner;
            if (unwrapped == typeof(IResult) || typeof(IResult).IsAssignableFrom(unwrapped))
            {
                return false;
            }
            return unwrapped.Name.EndsWith("Result");
        }

        var nonGenericUnwrapped = Nullable.GetUnderlyingType(returnType) ?? returnType;
        if (nonGenericUnwrapped == typeof(IResult) || typeof(IResult).IsAssignableFrom(nonGenericUnwrapped))
        {
            return false;
        }
        return nonGenericUnwrapped.Name.EndsWith("Result");
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
}
