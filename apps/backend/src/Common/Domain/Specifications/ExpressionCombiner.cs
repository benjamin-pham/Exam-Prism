namespace Common.Domain.Specifications;

using System.Linq.Expressions;

public static class ExpressionCombiner
{
    public static Expression<Func<T, bool>> AndAlso<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var parameter = Expression.Parameter(typeof(T), left.Parameters[0].Name);
        var leftVisitor = new ReplaceParameterVisitor(left.Parameters[0], parameter);
        var leftBody = leftVisitor.Visit(left.Body);

        var rightVisitor = new ReplaceParameterVisitor(right.Parameters[0], parameter);
        var rightBody = rightVisitor.Visit(right.Body);

        var andBody = Expression.AndAlso(leftBody, rightBody);
        return Expression.Lambda<Func<T, bool>>(andBody, parameter);
    }

    public static Expression<Func<T, bool>> OrElse<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var parameter = Expression.Parameter(typeof(T), left.Parameters[0].Name);
        var leftVisitor = new ReplaceParameterVisitor(left.Parameters[0], parameter);
        var leftBody = leftVisitor.Visit(left.Body);

        var rightVisitor = new ReplaceParameterVisitor(right.Parameters[0], parameter);
        var rightBody = rightVisitor.Visit(right.Body);

        var orBody = Expression.OrElse(leftBody, rightBody);
        return Expression.Lambda<Func<T, bool>>(orBody, parameter);
    }

    public static Expression<Func<T, bool>> Not<T>(Expression<Func<T, bool>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var parameter = expression.Parameters[0];
        var notBody = Expression.Not(expression.Body);
        return Expression.Lambda<Func<T, bool>>(notBody, parameter);
    }

    private sealed class ReplaceParameterVisitor(
        ParameterExpression source,
        ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == source ? target : base.VisitParameter(node);
        }
    }
}

