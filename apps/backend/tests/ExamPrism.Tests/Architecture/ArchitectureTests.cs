namespace ExamPrism.Tests.Architecture;

using System.Reflection;
using Common.Domain;
using Common.Domain.Specifications;
using Common.Endpoints;
using Common.Handlers;
using Common.Orleans;
using Common.Results;
using ExamPrism.Features.Auth;
using ExamPrism.Features.Health;
using ExamPrism.Infrastructure.Orleans;
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
    public void HasDbContextReference_DetectsDbContextInPropertiesReturnTypesAndGenerics()
    {
        HasDbContextReference(typeof(ProbeEndpointWithPropertyDbContext)).Should().BeTrue();
        HasDbContextReference(typeof(ProbeEndpointWithMethodReturnTypeDbContext)).Should().BeTrue();
        HasDbContextReference(typeof(ProbeEndpointWithGenericDbContextWrapper)).Should().BeTrue();
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
        var visited = new HashSet<Type>();
        var queue = new Queue<Type>();
        queue.Enqueue(type);

        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!visited.Add(t)) continue;

            foreach (var nested in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                queue.Enqueue(nested);
            }

            var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (fields.Any(f => IsDbContextOrContainsDbContext(f.FieldType)))
            {
                return true;
            }

            var properties = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (properties.Any(p => IsDbContextOrContainsDbContext(p.PropertyType)))
            {
                return true;
            }

            var constructors = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (constructors.Any(c => c.GetParameters().Any(p => IsDbContextOrContainsDbContext(p.ParameterType))))
            {
                return true;
            }

            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (methods.Any(m => IsDbContextOrContainsDbContext(m.ReturnType) || m.GetParameters().Any(p => IsDbContextOrContainsDbContext(p.ParameterType))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDbContextOrContainsDbContext(Type type) =>
        UnwrapTypes(type).Any(t => typeof(DbContext).IsAssignableFrom(t));

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

    [Fact]
    public void TypesInFeatures_MustNotReferenceRawOrleansGrainFactoryOrClusterClient()
    {
        var featureTypes = AppTypes
            .Where(t => t.Namespace != null && t.Namespace.StartsWith("ExamPrism.Features", StringComparison.Ordinal))
            .ToList();

        featureTypes.Should().NotBeEmpty();

        var forbiddenTypes = new[] { typeof(global::Orleans.IGrainFactory), typeof(global::Orleans.IClusterClient) };

        foreach (var type in featureTypes)
        {
            if (type.BaseType != null)
            {
                AssertNotReferencingForbiddenOrleansTypes(type.BaseType, type, "BaseType", forbiddenTypes);
            }

            foreach (var iface in type.GetInterfaces())
            {
                AssertNotReferencingForbiddenOrleansTypes(iface, type, $"Interface:{iface.Name}", forbiddenTypes);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertNotReferencingForbiddenOrleansTypes(field.FieldType, type, $"Field:{field.Name}", forbiddenTypes);
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertNotReferencingForbiddenOrleansTypes(prop.PropertyType, type, $"Property:{prop.Name}", forbiddenTypes);
            }

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var p in ctor.GetParameters())
                {
                    AssertNotReferencingForbiddenOrleansTypes(p.ParameterType, type, $"ConstructorParam:{p.Name}", forbiddenTypes);
                }
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertNotReferencingForbiddenOrleansTypes(method.ReturnType, type, $"MethodReturn:{method.Name}", forbiddenTypes);
                foreach (var p in method.GetParameters())
                {
                    AssertNotReferencingForbiddenOrleansTypes(p.ParameterType, type, $"MethodParam:{method.Name}:{p.Name}", forbiddenTypes);
                }
            }
        }
    }

    [Fact]
    public void TenantScopedGrains_MustBePublicSealedEndWithGrainAndImplementITenantScopedGrain()
    {
        var grainTypes = AppTypes
            .Where(t => typeof(TenantScopedGrain).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        grainTypes.Should().NotBeEmpty("at least one concrete TenantScopedGrain must be registered in the application");

        foreach (var grain in grainTypes)
        {
            grain.IsPublic.Should().BeTrue($"{grain.FullName} must be public");
            grain.IsSealed.Should().BeTrue($"{grain.FullName} must be sealed");
            grain.Name.Should().EndWith("Grain", $"{grain.FullName} name must end with 'Grain'");

            var implementsCustomTenantScopedGrainInterface = grain.GetInterfaces().Any(i =>
                i != typeof(ITenantScopedGrain) && typeof(ITenantScopedGrain).IsAssignableFrom(i));

            implementsCustomTenantScopedGrainInterface.Should().BeTrue(
                $"{grain.FullName} must implement at least one interface inheriting from ITenantScopedGrain other than ITenantScopedGrain itself");
        }
    }

    [Fact]
    public void TypesInCommon_MustNotReferenceInfrastructureFeaturesOrEndpoints()
    {
        var forbiddenPrefixes = new[]
        {
            "ExamPrism.Infrastructure",
            "ExamPrism.Features",
            "ExamPrism.Endpoints"
        };

        var commonTypes = AppAssembly.GetTypes()
            .Where(t => t.Namespace != null && (t.Namespace == "Common" || t.Namespace.StartsWith("Common.") || t.Namespace == "ExamPrism.Common" || t.Namespace.StartsWith("ExamPrism.Common.")))
            .ToList();

        commonTypes.Should().NotBeEmpty();

        foreach (var type in commonTypes)
        {
            if (type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType))
            {
                AssertNotForbiddenNamespace(type.BaseType, type, "BaseType", forbiddenPrefixes);
            }

            foreach (var iface in type.GetInterfaces())
            {
                AssertNotForbiddenNamespace(iface, type, $"Interface {iface.Name}", forbiddenPrefixes);
            }

            var members = type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (var member in members)
            {
                switch (member)
                {
                    case FieldInfo f:
                        AssertNotForbiddenNamespace(f.FieldType, type, f.Name, forbiddenPrefixes);
                        break;
                    case PropertyInfo p:
                        AssertNotForbiddenNamespace(p.PropertyType, type, p.Name, forbiddenPrefixes);
                        break;
                    case MethodBase m:
                        if (m is MethodInfo mi)
                        {
                            AssertNotForbiddenNamespace(mi.ReturnType, type, m.Name, forbiddenPrefixes);
                        }
                        foreach (var param in m.GetParameters())
                        {
                            AssertNotForbiddenNamespace(param.ParameterType, type, $"{m.Name}({param.Name})", forbiddenPrefixes);
                        }
                        break;
                }
            }
        }
    }

    [Fact]
    public void TypesInFeatures_MustNotDependOnInfrastructureInterfaces()
    {
        var featureTypes = AppAssembly.GetTypes()
            .Where(t => t.Namespace != null && t.Namespace.StartsWith("ExamPrism.Features"))
            .ToList();

        featureTypes.Should().NotBeEmpty();

        foreach (var type in featureTypes)
        {
            foreach (var iface in type.GetInterfaces())
            {
                AssertNotInfrastructureInterface(iface, type, $"Interface {iface.Name}");
            }

            var members = type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (var member in members)
            {
                switch (member)
                {
                    case FieldInfo f:
                        AssertNotInfrastructureInterface(f.FieldType, type, f.Name);
                        break;
                    case PropertyInfo p:
                        AssertNotInfrastructureInterface(p.PropertyType, type, p.Name);
                        break;
                    case MethodBase m:
                        if (m is MethodInfo mi)
                        {
                            AssertNotInfrastructureInterface(mi.ReturnType, type, m.Name);
                        }
                        foreach (var param in m.GetParameters())
                        {
                            AssertNotInfrastructureInterface(param.ParameterType, type, $"{m.Name}({param.Name})");
                        }
                        break;
                }
            }
        }
    }

    private static void AssertNotInfrastructureInterface(Type type, Type declaringType, string memberName)
    {
        var unwrappedTypes = UnwrapTypes(type);
        foreach (var t in unwrappedTypes)
        {
            if (t.IsInterface && t.Namespace != null && t.Namespace.StartsWith("ExamPrism.Infrastructure", StringComparison.Ordinal))
            {
                t.Namespace.Should().NotStartWith(
                    "ExamPrism.Infrastructure",
                    $"{declaringType.FullName}.{memberName} must not depend on Infrastructure interface '{t.FullName}'");
            }
        }
    }

    private static void AssertNotForbiddenNamespace(Type type, Type declaringType, string memberName, string[] forbiddenPrefixes)
    {
        var unwrappedTypes = UnwrapTypes(type);
        foreach (var t in unwrappedTypes)
        {
            var ns = t.Namespace;
            if (ns is null) continue;

            foreach (var prefix in forbiddenPrefixes)
            {
                ns.StartsWith(prefix, StringComparison.Ordinal).Should().BeFalse(
                    $"{declaringType.FullName}.{memberName} must not reference type '{t.FullName}' from forbidden namespace '{prefix}'");
            }
        }
    }

    private static void AssertNotReferencingForbiddenOrleansTypes(Type typeToCheck, Type declaringType, string memberName, Type[] forbiddenTypes)
    {
        var unwrapped = UnwrapTypes(typeToCheck);
        foreach (var t in unwrapped)
        {
            foreach (var forbidden in forbiddenTypes)
            {
                forbidden.IsAssignableFrom(t).Should().BeFalse(
                    $"{declaringType.FullName}.{memberName} must not reference raw Orleans type '{t.FullName}'. Use ITenantGrainFactory instead.");
            }
        }
    }

    [Fact]
    public void TypesInDomain_MustNotReferenceInfrastructureFeaturesEndpointsOrExternalFrameworks()
    {
        var domainTypes = AppTypes
            .Where(t => t.Namespace != null &&
                (t.Namespace.StartsWith("ExamPrism.Domain", StringComparison.Ordinal) ||
                 t.Namespace == "Common.Domain" ||
                 t.Namespace.StartsWith("Common.Domain.", StringComparison.Ordinal)))
            .ToList();

        domainTypes.Should().NotBeEmpty();

        var forbiddenPrefixes = new[]
        {
            "ExamPrism.Infrastructure",
            "ExamPrism.Features",
            "ExamPrism.Endpoints",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore",
            "Wolverine",
            "Orleans",
            "StackExchange.Redis",
            "Dapper"
        };

        foreach (var type in domainTypes)
        {
            if (type.BaseType is not null)
            {
                AssertNotForbiddenNamespace(type.BaseType, type, "BaseType", forbiddenPrefixes);
            }

            foreach (var iface in type.GetInterfaces())
            {
                AssertNotForbiddenNamespace(iface, type, "Interface", forbiddenPrefixes);
            }

            var members = type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (var member in members)
            {
                switch (member)
                {
                    case FieldInfo f:
                        AssertNotForbiddenNamespace(f.FieldType, type, f.Name, forbiddenPrefixes);
                        break;
                    case PropertyInfo p:
                        AssertNotForbiddenNamespace(p.PropertyType, type, p.Name, forbiddenPrefixes);
                        break;
                    case MethodBase m:
                        if (m is MethodInfo mi)
                        {
                            AssertNotForbiddenNamespace(mi.ReturnType, type, m.Name, forbiddenPrefixes);
                        }
                        foreach (var param in m.GetParameters())
                        {
                            AssertNotForbiddenNamespace(param.ParameterType, type, $"{m.Name}({param.Name})", forbiddenPrefixes);
                        }
                        break;
                }
            }
        }
    }

    [Fact]
    public void TypesInCommon_MustNotReferenceExamPrismDomain()
    {
        var commonTypes = AppTypes
            .Where(t => t.Namespace != null && (t.Namespace == "Common" || t.Namespace.StartsWith("Common.", StringComparison.Ordinal)))
            .ToList();

        commonTypes.Should().NotBeEmpty();

        var forbiddenPrefixes = new[] { "ExamPrism.Domain" };

        foreach (var type in commonTypes)
        {
            if (type.BaseType is not null)
            {
                AssertNotForbiddenNamespace(type.BaseType, type, "BaseType", forbiddenPrefixes);
            }

            foreach (var iface in type.GetInterfaces())
            {
                AssertNotForbiddenNamespace(iface, type, "Interface", forbiddenPrefixes);
            }

            var members = type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (var member in members)
            {
                switch (member)
                {
                    case FieldInfo f:
                        AssertNotForbiddenNamespace(f.FieldType, type, f.Name, forbiddenPrefixes);
                        break;
                    case PropertyInfo p:
                        AssertNotForbiddenNamespace(p.PropertyType, type, p.Name, forbiddenPrefixes);
                        break;
                    case MethodBase m:
                        if (m is MethodInfo mi)
                        {
                            AssertNotForbiddenNamespace(mi.ReturnType, type, m.Name, forbiddenPrefixes);
                        }
                        foreach (var param in m.GetParameters())
                        {
                            AssertNotForbiddenNamespace(param.ParameterType, type, $"{m.Name}({param.Name})", forbiddenPrefixes);
                        }
                        break;
                }
            }
        }
    }

    [Fact]
    public void DomainEvents_MustBePublicSealedRecords_EndingWithDomainEvent()
    {
        var domainEventTypes = AppTypes
            .Where(t => typeof(IDomainEvent).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .ToList();

        domainEventTypes.Should().NotBeEmpty();

        foreach (var evt in domainEventTypes)
        {
            evt.IsPublic.Should().BeTrue($"{evt.FullName} must be public");
            evt.IsSealed.Should().BeTrue($"{evt.FullName} must be sealed");
            evt.Name.Should().EndWith("DomainEvent", $"{evt.FullName} must end with 'DomainEvent'");
            evt.Namespace.Should().StartWith("ExamPrism.Domain", $"{evt.FullName} must be in ExamPrism.Domain");

            var isRecord = evt.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance) is not null;
            isRecord.Should().BeTrue($"{evt.FullName} must be a C# record");
        }
    }

    [Fact]
    public void DomainEntitiesAndAggregates_MustInheritFromEntityOrAggregateRoot()
    {
        var domainTypes = AppTypes
            .Where(t => t.Namespace != null &&
                        t.Namespace.StartsWith("ExamPrism.Domain", StringComparison.Ordinal) &&
                        !t.IsInterface &&
                        !t.IsAbstract &&
                        !t.IsEnum &&
                        !typeof(IDomainEvent).IsAssignableFrom(t) &&
                        !typeof(ValueObject).IsAssignableFrom(t) &&
                        !IsSubclassOfRawGeneric(typeof(Specification<>), t))
            .ToList();

        domainTypes.Should().NotBeEmpty();

        foreach (var type in domainTypes)
        {
            var isEntity = IsSubclassOfRawGeneric(typeof(Entity<>), type);
            var isAggregate = IsSubclassOfRawGeneric(typeof(AggregateRoot<>), type);

            (isEntity || isAggregate).Should().BeTrue(
                $"{type.FullName} in ExamPrism.Domain must inherit from Entity<TId>, AggregateRoot<TId>, ValueObject, IDomainEvent, or Specification<T>");
        }
    }

    [Fact]
    public void ValueObjects_WhenConcrete_MustBePublicSealed()
    {
        var voTypes = AppTypes
            .Where(t => typeof(ValueObject).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .ToList();

        voTypes.Should().NotBeEmpty();

        foreach (var vo in voTypes)
        {
            vo.IsPublic.Should().BeTrue($"{vo.FullName} must be public");
            vo.IsSealed.Should().BeTrue($"{vo.FullName} must be sealed");
            vo.Namespace.Should().StartWith("ExamPrism.Domain", $"{vo.FullName} must be in ExamPrism.Domain");
        }
    }

    [Fact]
    public void ReadOnlyDbContextWrappers_MustNotExistInAssembly()
    {
        AppAssembly.GetType("ExamPrism.Infrastructure.Persistence.ReadOnlyCatalogDbContext")
            .Should().BeNull("ReadOnlyCatalogDbContext wrapper must be removed; queries use CatalogDbContext directly with Dapper");

        AppAssembly.GetType("ExamPrism.Infrastructure.Persistence.ReadOnlyAppDbContext")
            .Should().BeNull("ReadOnlyAppDbContext wrapper must be removed; queries use AppDbContext directly with Dapper");

        AppAssembly.GetType("ExamPrism.Infrastructure.Persistence.DapperCommandExecutor")
            .Should().BeNull("DapperCommandExecutor must be removed; queries use native Dapper directly on DbConnection");
    }

    [Fact]
    public void AppDbContext_And_CatalogDbContext_MustBeInInfrastructurePersistence()
    {
        typeof(AppDbContext).Namespace.Should().Be("ExamPrism.Infrastructure.Persistence");
        typeof(CatalogDbContext).Namespace.Should().Be("ExamPrism.Infrastructure.Persistence.Catalog");
    }

    private static bool IsSubclassOfRawGeneric(Type generic, Type? toCheck)
    {
        while (toCheck != null && toCheck != typeof(object))
        {
            var cur = toCheck.IsGenericType ? toCheck.GetGenericTypeDefinition() : toCheck;
            if (generic == cur)
            {
                return true;
            }
            toCheck = toCheck.BaseType;
        }
        return false;
    }

    private sealed class ProbeEndpointWithLambdaDbContext : IEndpoint
    {
        public static void MapEndpoint(IEndpointRouteBuilder app)
        {
            var action = (AppDbContext db) => db.ToString();
            _ = action;
        }
    }

    private sealed class ProbeEndpointWithPropertyDbContext
    {
        public AppDbContext Db => null!;
    }

    private sealed class ProbeEndpointWithMethodReturnTypeDbContext
    {
        public AppDbContext GetDb() => null!;
    }

    private sealed class ProbeEndpointWithGenericDbContextWrapper
    {
        public Func<AppDbContext> Factory => null!;
    }
}
