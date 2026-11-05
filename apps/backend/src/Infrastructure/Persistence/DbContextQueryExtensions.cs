namespace ExamPrism.Infrastructure.Persistence;

using Common.Domain.Specifications;
using Common.Persistence;
using Microsoft.EntityFrameworkCore;

public static class DbContextQueryExtensions
{
    // --- IDbQuery ---

    public static Task<TResult> QueryAsync<TContext, TResult>(
        this TContext db,
        IDbQuery<TContext, TResult> query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        return query.ExecuteAsync(db, ct);
    }

    // --- DbContext Overloads ---

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

    public static async Task<TResult?> QueryFirstOrDefaultAsync<TEntity, TResult>(
        this DbContext db,
        ISpecification<TEntity, TResult> specification,
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

    public static async Task<List<TResult>> QueryListAsync<TEntity, TResult>(
        this DbContext db,
        ISpecification<TEntity, TResult> specification,
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

    // --- ReadOnlyCatalogDbContext Overloads ---

    public static async Task<TEntity?> QueryFirstOrDefaultAsync<TEntity>(
        this ReadOnlyCatalogDbContext db,
        ISpecification<TEntity> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification);
        return await query.FirstOrDefaultAsync(ct);
    }

    public static async Task<TResult?> QueryFirstOrDefaultAsync<TEntity, TResult>(
        this ReadOnlyCatalogDbContext db,
        ISpecification<TEntity, TResult> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification);
        return await query.FirstOrDefaultAsync(ct);
    }

    public static async Task<List<TEntity>> QueryListAsync<TEntity>(
        this ReadOnlyCatalogDbContext db,
        ISpecification<TEntity> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification);
        return await query.ToListAsync(ct);
    }

    public static async Task<List<TResult>> QueryListAsync<TEntity, TResult>(
        this ReadOnlyCatalogDbContext db,
        ISpecification<TEntity, TResult> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification);
        return await query.ToListAsync(ct);
    }

    public static async Task<bool> QueryAnyAsync<TEntity>(
        this ReadOnlyCatalogDbContext db,
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
        this ReadOnlyCatalogDbContext db,
        ISpecification<TEntity> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification, evaluateCriteriaOnly: true);
        return await query.CountAsync(ct);
    }

    // --- ReadOnlyAppDbContext Overloads ---

    public static async Task<TEntity?> QueryFirstOrDefaultAsync<TEntity>(
        this ReadOnlyAppDbContext db,
        ISpecification<TEntity> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification);
        return await query.FirstOrDefaultAsync(ct);
    }

    public static async Task<TResult?> QueryFirstOrDefaultAsync<TEntity, TResult>(
        this ReadOnlyAppDbContext db,
        ISpecification<TEntity, TResult> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification);
        return await query.FirstOrDefaultAsync(ct);
    }

    public static async Task<List<TEntity>> QueryListAsync<TEntity>(
        this ReadOnlyAppDbContext db,
        ISpecification<TEntity> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification);
        return await query.ToListAsync(ct);
    }

    public static async Task<List<TResult>> QueryListAsync<TEntity, TResult>(
        this ReadOnlyAppDbContext db,
        ISpecification<TEntity, TResult> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification);
        return await query.ToListAsync(ct);
    }

    public static async Task<bool> QueryAnyAsync<TEntity>(
        this ReadOnlyAppDbContext db,
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
        this ReadOnlyAppDbContext db,
        ISpecification<TEntity> specification,
        CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(specification);

        var query = SpecificationEvaluator.GetQuery(db.Set<TEntity>(), specification, evaluateCriteriaOnly: true);
        return await query.CountAsync(ct);
    }
}

