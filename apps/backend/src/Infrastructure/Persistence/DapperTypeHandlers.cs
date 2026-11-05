namespace ExamPrism.Infrastructure.Persistence;

using System;
using System.Data;
using Common.MultiTenancy;
using Dapper;

public sealed class StringEnumTypeHandler<TEnum> : SqlMapper.TypeHandler<TEnum>
    where TEnum : struct, Enum
{
    public override void SetValue(IDbDataParameter parameter, TEnum value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToString();
    }

    public override TEnum Parse(object value) => value switch
    {
        TEnum enumVal => enumVal,
        string str when Enum.TryParse<TEnum>(str, ignoreCase: true, out var parsed) => parsed,
        int intVal => (TEnum)Enum.ToObject(typeof(TEnum), intVal),
        _ => throw new DataException($"Cannot convert value '{value}' ({value?.GetType().FullName}) to {typeof(TEnum).Name}.")
    };
}

public static class DapperTypeHandlers
{
    private static int _registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        SqlMapper.AddTypeHandler(new StringEnumTypeHandler<TenantStatus>());
    }
}
