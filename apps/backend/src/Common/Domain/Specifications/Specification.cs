namespace Common.Domain.Specifications;

using System.Linq.Expressions;

public class Specification<TEntity> : ISpecification<TEntity>
{
    private readonly List<Expression<Func<TEntity, object?>>> _includes = [];
    private readonly List<string> _includeStrings = [];
    private readonly List<OrderExpression<TEntity>> _orderExpressions = [];
    private Func<TEntity, bool>? _compiledCriteria;

    public Expression<Func<TEntity, bool>>? Criteria { get; protected set; }
    public IReadOnlyList<Expression<Func<TEntity, object?>>> Includes => _includes.AsReadOnly();
    public IReadOnlyList<string> IncludeStrings => _includeStrings.AsReadOnly();
    public IReadOnlyList<OrderExpression<TEntity>> OrderExpressions => _orderExpressions.AsReadOnly();
    public int? Skip { get; private set; }
    public int? Take { get; private set; }
    public bool AsNoTracking { get; private set; } = true;
    public bool AsSplitQuery { get; private set; }
    public bool IgnoreQueryFilters { get; private set; }

    public virtual bool IsSatisfiedBy(TEntity entity)
    {
        if (Criteria is null)
        {
            return true;
        }

        _compiledCriteria ??= Criteria.Compile();
        return _compiledCriteria(entity);
    }

    protected void Where(Expression<Func<TEntity, bool>> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        Criteria = Criteria is null
            ? criteria
            : ExpressionCombiner.AndAlso(Criteria, criteria);

        _compiledCriteria = null;
    }

    protected void AddInclude(Expression<Func<TEntity, object?>> includeExpression)
    {
        ArgumentNullException.ThrowIfNull(includeExpression);
        _includes.Add(includeExpression);
    }

    protected void AddInclude(string includeString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(includeString);
        _includeStrings.Add(includeString);
    }

    protected void ApplyOrderBy(Expression<Func<TEntity, object?>> keySelector)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _orderExpressions.Add(new OrderExpression<TEntity>(keySelector, Descending: false));
    }

    protected void ApplyOrderByDescending(Expression<Func<TEntity, object?>> keySelector)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _orderExpressions.Add(new OrderExpression<TEntity>(keySelector, Descending: true));
    }

    protected void ApplyPaging(int skip, int take)
    {
        Skip = skip;
        Take = take;
    }

    public Specification<TEntity> WithTracking()
    {
        AsNoTracking = false;
        return this;
    }

    public Specification<TEntity> WithSplitQuery()
    {
        AsSplitQuery = true;
        return this;
    }

    public Specification<TEntity> WithIgnoreQueryFilters()
    {
        IgnoreQueryFilters = true;
        return this;
    }

    public Specification<TEntity> And(Specification<TEntity> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new CombinedSpecification<TEntity>(this, other, CombinationType.And);
    }

    public Specification<TEntity> Or(Specification<TEntity> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new CombinedSpecification<TEntity>(this, other, CombinationType.Or);
    }

    public Specification<TEntity> Not()
    {
        return new NegatedSpecification<TEntity>(this);
    }

    private enum CombinationType { And, Or }

    private sealed class CombinedSpecification<T> : Specification<T>
    {
        public CombinedSpecification(
            Specification<T> left,
            Specification<T> right,
            CombinationType type)
        {
            if (left.Criteria is not null && right.Criteria is not null)
            {
                Criteria = type == CombinationType.And
                    ? ExpressionCombiner.AndAlso(left.Criteria, right.Criteria)
                    : ExpressionCombiner.OrElse(left.Criteria, right.Criteria);
            }
            else if (type == CombinationType.And)
            {
                Criteria = left.Criteria ?? right.Criteria;
            }
            else
            {
                // In OR combination, if either side is unconditional match-all (null Criteria),
                // (True OR Criteria) is True, which is also an unconditional match-all (null Criteria).
                Criteria = null;
            }

            foreach (var inc in left.Includes.Concat(right.Includes))
            {
                AddInclude(inc);
            }

            foreach (var incStr in left.IncludeStrings.Concat(right.IncludeStrings))
            {
                AddInclude(incStr);
            }

            foreach (var ord in left.OrderExpressions.Concat(right.OrderExpressions))
            {
                if (ord.Descending)
                {
                    ApplyOrderByDescending(ord.KeySelector);
                }
                else
                {
                    ApplyOrderBy(ord.KeySelector);
                }
            }

            if (left.Skip.HasValue) ApplyPaging(left.Skip.Value, left.Take ?? 0);
            else if (right.Skip.HasValue) ApplyPaging(right.Skip.Value, right.Take ?? 0);

            if (!left.AsNoTracking || !right.AsNoTracking) WithTracking();
            if (left.AsSplitQuery || right.AsSplitQuery) WithSplitQuery();
            if (left.IgnoreQueryFilters || right.IgnoreQueryFilters) WithIgnoreQueryFilters();
        }
    }

    private sealed class NegatedSpecification<T> : Specification<T>
    {
        public NegatedSpecification(Specification<T> source)
        {
            if (source.Criteria is not null)
            {
                Criteria = ExpressionCombiner.Not(source.Criteria);
            }

            foreach (var inc in source.Includes) AddInclude(inc);
            foreach (var incStr in source.IncludeStrings) AddInclude(incStr);
            foreach (var ord in source.OrderExpressions)
            {
                if (ord.Descending) ApplyOrderByDescending(ord.KeySelector);
                else ApplyOrderBy(ord.KeySelector);
            }

            if (source.Skip.HasValue) ApplyPaging(source.Skip.Value, source.Take ?? 0);
            if (!source.AsNoTracking) WithTracking();
            if (source.AsSplitQuery) WithSplitQuery();
            if (source.IgnoreQueryFilters) WithIgnoreQueryFilters();
        }
    }
}

public class Specification<TEntity, TResult> : Specification<TEntity>, ISpecification<TEntity, TResult>
{
    public Expression<Func<TEntity, TResult>> Selector { get; private set; } = null!;

    protected void Select(Expression<Func<TEntity, TResult>> selector)
    {
        Selector = selector ?? throw new ArgumentNullException(nameof(selector));
    }
}

