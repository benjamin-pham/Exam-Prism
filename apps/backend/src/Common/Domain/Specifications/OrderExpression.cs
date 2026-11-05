namespace Common.Domain.Specifications;

using System.Linq.Expressions;

public sealed record OrderExpression<TEntity>(
    Expression<Func<TEntity, object?>> KeySelector,
    bool Descending = false);

