namespace Common.Validation;

using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using Common.Localization;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

/// <summary>
/// Route-group endpoint filter factory that inspects endpoint parameters at route build time
/// and automatically attaches validation filters for any parameter with a registered <see cref="IValidator{T}"/>.
/// Endpoints with no validatable parameters incur zero runtime overhead.
/// </summary>
public static class ValidationFilterFactory
{
    public static EndpointFilterDelegate Create(EndpointFilterFactoryContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.MethodInfo is null)
        {
            return next;
        }

        if (context.MethodInfo.GetCustomAttribute<SkipValidationAttribute>() is not null ||
            context.MethodInfo.DeclaringType?.GetCustomAttribute<SkipValidationAttribute>() is not null)
        {
            return next;
        }

        var parameters = context.MethodInfo.GetParameters();
        if (parameters.Length == 0)
        {
            return next;
        }

        var appServices = context.ApplicationServices;
        if (appServices is null)
        {
            return next;
        }

        var isService = appServices.GetService<IServiceProviderIsService>();
        var nullabilityContext = new NullabilityInfoContext();
        var validators = new List<IParameterValidator>();

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var paramType = parameter.ParameterType;

            if (parameter.GetCustomAttribute<SkipValidationAttribute>() is not null)
            {
                continue;
            }

            if (IsFrameworkOrInfrastructureType(parameter, paramType))
            {
                continue;
            }

            var validatorType = typeof(IValidator<>).MakeGenericType(paramType);
            var isRegistered = isService?.IsService(validatorType) ?? IsRegisteredViaScope(appServices, validatorType);

            if (!isRegistered)
            {
                continue;
            }

            var nullability = nullabilityContext.Create(parameter);
            var isOptional = parameter.HasDefaultValue ||
                             parameter.IsOptional ||
                             nullability.ReadState == NullabilityState.Nullable ||
                             Nullable.GetUnderlyingType(paramType) is not null;

            var paramValidator = (IParameterValidator)Activator.CreateInstance(
                typeof(ParameterValidator<>).MakeGenericType(paramType),
                i,
                parameter.Name ?? $"arg{i}",
                isOptional)!;

            validators.Add(paramValidator);
        }

        if (validators.Count == 0)
        {
            return next;
        }

        var validatorArray = validators.ToArray();

        return async invocationContext =>
        {
            var endpoint = invocationContext.HttpContext.GetEndpoint();
            if (endpoint?.Metadata.GetMetadata<SkipValidationAttribute>() is not null)
            {
                return await next(invocationContext);
            }

            List<ValidationError>? errors = null;
            var serviceProvider = invocationContext.HttpContext.RequestServices;
            var cancellationToken = invocationContext.HttpContext.RequestAborted;
            var localizer = serviceProvider.GetService<IStringLocalizer<SharedResources>>();

            for (var i = 0; i < validatorArray.Length; i++)
            {
                var descriptor = validatorArray[i];
                var argument = invocationContext.Arguments.Count > descriptor.ParameterIndex
                    ? invocationContext.Arguments[descriptor.ParameterIndex]
                    : null;

                if (argument is null)
                {
                    if (!descriptor.IsOptional)
                    {
                        errors ??= [];
                        var nullMessage = localizer?["RequestBodyRequired"].Value ?? "Request body cannot be null.";
                        errors.Add(new ValidationError(descriptor.ParameterName, "REQUIRED", nullMessage));
                    }
                    continue;
                }

                var validationResult = await descriptor.ValidateAsync(serviceProvider, argument, cancellationToken);
                if (validationResult is not null && !validationResult.IsValid)
                {
                    errors ??= [];
                    for (var errIdx = 0; errIdx < validationResult.Errors.Count; errIdx++)
                    {
                        var failure = validationResult.Errors[errIdx];
                        errors.Add(new ValidationError(failure.PropertyName, failure.ErrorCode, failure.ErrorMessage));
                    }
                }
            }

            if (errors is not null && errors.Count > 0)
            {
                var title = localizer?["ValidationErrorOccurred"].Value ?? "One or more validation errors occurred.";
                var traceId = Activity.Current?.Id ?? invocationContext.HttpContext.TraceIdentifier;

                var problemResponse = new ValidationProblemResponse
                {
                    Title = title,
                    Instance = invocationContext.HttpContext.Request.Path,
                    TraceId = traceId,
                    Errors = errors
                };

                return Results.Problem(problemResponse);
            }

            return await next(invocationContext);
        };
    }

    private static bool IsFrameworkOrInfrastructureType(ParameterInfo parameter, Type type)
    {
        if (type.IsByRef || type.IsPointer || type.ContainsGenericParameters)
        {
            return true;
        }

        if (typeof(HttpContext).IsAssignableFrom(type) ||
            typeof(HttpRequest).IsAssignableFrom(type) ||
            typeof(HttpResponse).IsAssignableFrom(type) ||
            typeof(ClaimsPrincipal).IsAssignableFrom(type) ||
            typeof(CancellationToken).IsAssignableFrom(type) ||
            typeof(IServiceProvider).IsAssignableFrom(type))
        {
            return true;
        }

        if (parameter.GetCustomAttributes().Any(a =>
                a.GetType().Name is "FromServicesAttribute" or "FromKeyedServicesAttribute"))
        {
            return true;
        }

        return false;
    }

    private static bool IsRegisteredViaScope(IServiceProvider services, Type serviceType)
    {
        try
        {
            using var scope = services.CreateScope();
            return scope.ServiceProvider.GetService(serviceType) is not null;
        }
        catch
        {
            return false;
        }
    }

    private interface IParameterValidator
    {
        int ParameterIndex { get; }
        string ParameterName { get; }
        bool IsOptional { get; }
        Task<ValidationResult?> ValidateAsync(IServiceProvider serviceProvider, object argument, CancellationToken cancellationToken);
    }

    private sealed class ParameterValidator<T> : IParameterValidator
    {
        public int ParameterIndex { get; }
        public string ParameterName { get; }
        public bool IsOptional { get; }

        public ParameterValidator(int parameterIndex, string parameterName, bool isOptional)
        {
            ParameterIndex = parameterIndex;
            ParameterName = parameterName;
            IsOptional = isOptional;
        }

        public async Task<ValidationResult?> ValidateAsync(IServiceProvider serviceProvider, object argument, CancellationToken cancellationToken)
        {
            if (argument is not T typedArgument)
            {
                return null;
            }

            var validator = serviceProvider.GetService<IValidator<T>>();
            if (validator is null)
            {
                return null;
            }

            return await validator.ValidateAsync(typedArgument, cancellationToken);
        }
    }
}
