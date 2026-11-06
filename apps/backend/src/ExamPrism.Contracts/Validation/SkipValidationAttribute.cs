namespace Common.Validation;

using System;

/// <summary>
/// Indicates that an endpoint, class, or parameter should be excluded from automatic validation.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class SkipValidationAttribute : Attribute
{
}
