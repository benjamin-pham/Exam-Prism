namespace ExamPrism.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Common.Domain.Specifications;
using Common.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore;

public static class DbContextQueryExtensions
{
    // --- IDbQuery (Write Side / Commands) ---

    public static Task<TResult> QueryAsync<TContext, TResult>(
        this TContext db,
        IDbQuery<TContext, TResult> query,
        CancellationToken ct = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        return query.ExecuteAsync(db, ct);
    }

    // --- ISpecification<TEntity> (Write Side / Aggregates) ---

    public static async Task<TEntity?> QueryFirstOrDefaultAsync<TEntity>(
        this DbContext db,
        ISpecification<TEntity> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification);
        return await query.FirstOrDefaultAsync(ct);
    }

    public static async Task<List<TEntity>> QueryListAsync<TEntity>(
        this DbContext db,
        ISpecification<TEntity> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification);
        return await query.ToListAsync(ct);
    }

    public static async Task<bool> QueryAnyAsync<TEntity>(
        this DbContext db,
        ISpecification<TEntity> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification, evaluateCriteriaOnly: true);
        return await query.AnyAsync(ct);
    }

    public static async Task<int> QueryCountAsync<TEntity>(
        this DbContext db,
        ISpecification<TEntity> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification, evaluateCriteriaOnly: true);
        return await query.CountAsync(ct);
    }

    // --- Dapper Raw SQL Extensions on DbContext ---

    public static Task<IReadOnlyList<T>> QueryAsync<T>(
        this DbContext db,
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        DapperCommandExecutor.QueryAsync<T>(db, sql, param, null, ct);

    public static Task<T?> QueryFirstOrDefaultAsync<T>(
        this DbContext db,
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        DapperCommandExecutor.QueryFirstOrDefaultAsync<T>(db, sql, param, null, ct);

    public static Task<T?> QuerySingleOrDefaultAsync<T>(
        this DbContext db,
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        DapperCommandExecutor.QuerySingleOrDefaultAsync<T>(db, sql, param, null, ct);

    public static Task<T> QuerySingleAsync<T>(
        this DbContext db,
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        DapperCommandExecutor.QuerySingleAsync<T>(db, sql, param, null, ct);

    public static Task<T?> ExecuteScalarAsync<T>(
        this DbContext db,
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        DapperCommandExecutor.ExecuteScalarAsync<T>(db, sql, param, null, ct);

    public static Task<TResult> QueryMultipleAsync<TResult>(
        this DbContext db,
        string sql,
        Func<SqlMapper.GridReader, Task<TResult>> map,
        object? param = null,
        CancellationToken ct = default) =>
        DapperCommandExecutor.QueryMultipleAsync(db, sql, map, param, null, ct);
}
