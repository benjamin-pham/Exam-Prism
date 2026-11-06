namespace Common.Persistence;

public interface IDbQuery<in TContext, TResult>
{
    Task<TResult> ExecuteAsync(TContext db, CancellationToken ct = default);
}

