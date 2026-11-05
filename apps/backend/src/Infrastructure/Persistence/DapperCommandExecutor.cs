namespace ExamPrism.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

internal static class DapperCommandExecutor
{
    static DapperCommandExecutor()
    {
        DapperTypeHandlers.EnsureRegistered();
    }

    public static async Task<IReadOnlyList<T>> QueryAsync<T>(
        DbContext db,
        string sql,
        object? param = null,
        SlowQueryTelemetryInterceptor? interceptor = null,
        CancellationToken ct = default)
    {
        var (connection, command) = PrepareCommand(db, sql, param, ct);
        var start = Stopwatch.GetTimestamp();
        try
        {
            var rows = await connection.QueryAsync<T>(command);
            return rows.AsList().AsReadOnly();
        }
        finally
        {
            RecordTelemetry(db, interceptor, sql, Stopwatch.GetElapsedTime(start));
        }
    }

    public static async Task<T?> QueryFirstOrDefaultAsync<T>(
        DbContext db,
        string sql,
        object? param = null,
        SlowQueryTelemetryInterceptor? interceptor = null,
        CancellationToken ct = default)
    {
        var (connection, command) = PrepareCommand(db, sql, param, ct);
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await connection.QueryFirstOrDefaultAsync<T>(command);
        }
        finally
        {
            RecordTelemetry(db, interceptor, sql, Stopwatch.GetElapsedTime(start));
        }
    }

    public static async Task<T?> QuerySingleOrDefaultAsync<T>(
        DbContext db,
        string sql,
        object? param = null,
        SlowQueryTelemetryInterceptor? interceptor = null,
        CancellationToken ct = default)
    {
        var (connection, command) = PrepareCommand(db, sql, param, ct);
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await connection.QuerySingleOrDefaultAsync<T>(command);
        }
        finally
        {
            RecordTelemetry(db, interceptor, sql, Stopwatch.GetElapsedTime(start));
        }
    }

    public static async Task<T> QuerySingleAsync<T>(
        DbContext db,
        string sql,
        object? param = null,
        SlowQueryTelemetryInterceptor? interceptor = null,
        CancellationToken ct = default)
    {
        var (connection, command) = PrepareCommand(db, sql, param, ct);
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await connection.QuerySingleAsync<T>(command);
        }
        finally
        {
            RecordTelemetry(db, interceptor, sql, Stopwatch.GetElapsedTime(start));
        }
    }

    public static async Task<T?> ExecuteScalarAsync<T>(
        DbContext db,
        string sql,
        object? param = null,
        SlowQueryTelemetryInterceptor? interceptor = null,
        CancellationToken ct = default)
    {
        var (connection, command) = PrepareCommand(db, sql, param, ct);
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await connection.ExecuteScalarAsync<T>(command);
        }
        finally
        {
            RecordTelemetry(db, interceptor, sql, Stopwatch.GetElapsedTime(start));
        }
    }

    public static async Task<TResult> QueryMultipleAsync<TResult>(
        DbContext db,
        string sql,
        Func<SqlMapper.GridReader, Task<TResult>> map,
        object? param = null,
        SlowQueryTelemetryInterceptor? interceptor = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        var (connection, command) = PrepareCommand(db, sql, param, ct);
        var start = Stopwatch.GetTimestamp();
        try
        {
            await using var grid = await connection.QueryMultipleAsync(command);
            return await map(grid);
        }
        finally
        {
            RecordTelemetry(db, interceptor, sql, Stopwatch.GetElapsedTime(start));
        }
    }

    private static (IDbConnection Connection, CommandDefinition Command) PrepareCommand(
        DbContext db,
        string sql,
        object? param,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var connection = db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var commandTimeout = db.Database.GetCommandTimeout();

        var command = new CommandDefinition(
            commandText: sql,
            parameters: param,
            transaction: transaction,
            commandTimeout: commandTimeout,
            commandType: CommandType.Text,
            flags: CommandFlags.Buffered,
            cancellationToken: ct);

        return (connection, command);
    }

    private static void RecordTelemetry(
        DbContext db,
        SlowQueryTelemetryInterceptor? interceptor,
        string sql,
        TimeSpan elapsed)
    {
        try
        {
            interceptor ??= db.GetService<IDbContextOptions>()
                ?.Extensions
                .OfType<CoreOptionsExtension>()
                .FirstOrDefault()
                ?.Interceptors
                ?.OfType<SlowQueryTelemetryInterceptor>()
                .FirstOrDefault();

            interceptor?.RecordIfSlow(sql, elapsed);
        }
        catch
        {
            // Telemetry failure must never fail the query
        }
    }
}
