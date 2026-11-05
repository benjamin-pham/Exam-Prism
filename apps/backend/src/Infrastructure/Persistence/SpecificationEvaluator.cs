namespace ExamPrism.Infrastructure.Persistence;

using System.Linq;
using Common.Domain.Specifications;
using Microsoft.EntityFrameworkCore;

public static class SpecificationEvaluator
{
    public static IQueryable<TEntity> GetQuery<TEntity>(
        IQueryable<TEntity> inputQuery,
        ISpecification<TEntity> specification,
        bool evaluateCriteriaOnly = false)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(inputQuery);
        ArgumentNullException.ThrowIfNull(specification);

        var query = inputQuery.TagWith(specification.GetType().Name);

        if (specification.IgnoreQueryFilters)
        {
            query = query.IgnoreQueryFilters();
        }

        if (specification.Criteria is not null)
        {
            query = query.Where(specification.Criteria);
        }

        if (evaluateCriteriaOnly)
        {
            return query;
        }

        query = specification.AsNoTracking
            ? query.AsNoTracking()
            : query.AsTracking();

        if (specification.AsSplitQuery)
        {
            query = query.AsSplitQuery();
        }

        foreach (var include in specification.Includes)
        {
            query = query.Include(include);
        }

        foreach (var includeString in specification.IncludeStrings)
        {
            query = query.Include(includeString);
        }

        IOrderedQueryable<TEntity>? orderedQuery = null;
        for (var i = 0; i < specification.OrderExpressions.Count; i++)
        {
            var orderExpr = specification.OrderExpressions[i];
            if (i == 0)
            {
                orderedQuery = orderExpr.Descending
                    ? query.OrderByDescending(orderExpr.KeySelector)
                    : query.OrderBy(orderExpr.KeySelector);
            }
            else if (orderedQuery is not null)
            {
                orderedQuery = orderExpr.Descending
                    ? orderedQuery.ThenByDescending(orderExpr.KeySelector)
                    : orderedQuery.ThenBy(orderExpr.KeySelector);
            }
        }

        if (orderedQuery is not null)
        {
            query = orderedQuery;
        }

        if (specification.Skip.HasValue)
        {
            query = query.Skip(specification.Skip.Value);
        }

        if (specification.Take.HasValue)
        {
            query = query.Take(specification.Take.Value);
        }

        return query;
    }

    public static IQueryable<TResult> GetQuery<TEntity, TResult>(
        IQueryable<TEntity> inputQuery,
        ISpecification<TEntity, TResult> specification)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(specification.Selector);

        var entityQuery = GetQuery(inputQuery, specification, evaluateCriteriaOnly: false);
        return entityQuery.Select(specification.Selector);
    }
}

