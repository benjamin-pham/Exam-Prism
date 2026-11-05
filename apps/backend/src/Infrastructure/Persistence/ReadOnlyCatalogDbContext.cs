namespace ExamPrism.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;

public sealed class ReadOnlyCatalogDbContext : IDisposable, IAsyncDisposable
{
    private readonly CatalogDbContext _inner;
    private readonly SlowQueryTelemetryInterceptor? _slowQueryInterceptor;

    public ReadOnlyCatalogDbContext(
        CatalogDbContext inner,
        SlowQueryTelemetryInterceptor? slowQueryInterceptor = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _slowQueryInterceptor = slowQueryInterceptor;
        _inner.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        _inner.ChangeTracker.AutoDetectChangesEnabled = false;
    }

    public CatalogDbContext DatabaseContext => _inner;
    public CatalogDbContext Context => _inner;
    public CatalogDbContext Inner => _inner;

    public Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        DapperCommandExecutor.QueryAsync<T>(_inner, sql, param, _slowQueryInterceptor, ct);

    public Task<T?> QueryFirstOrDefaultAsync<T>(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        DapperCommandExecutor.QueryFirstOrDefaultAsync<T>(_inner, sql, param, _slowQueryInterceptor, ct);

    public Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        DapperCommandExecutor.QuerySingleOrDefaultAsync<T>(_inner, sql, param, _slowQueryInterceptor, ct);

    public Task<T> QuerySingleAsync<T>(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        DapperCommandExecutor.QuerySingleAsync<T>(_inner, sql, param, _slowQueryInterceptor, ct);

    public Task<T?> ExecuteScalarAsync<T>(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        DapperCommandExecutor.ExecuteScalarAsync<T>(_inner, sql, param, _slowQueryInterceptor, ct);

    public Task<TResult> QueryMultipleAsync<TResult>(
        string sql,
        Func<SqlMapper.GridReader, Task<TResult>> map,
        object? param = null,
        CancellationToken ct = default) =>
        DapperCommandExecutor.QueryMultipleAsync(_inner, sql, map, param, _slowQueryInterceptor, ct);

    public void Dispose() => _inner.Dispose();
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
