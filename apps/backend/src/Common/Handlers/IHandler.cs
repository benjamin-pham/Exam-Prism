namespace Common.Handlers;

using Common.Results;
using Wolverine;

/// <summary>
/// Root marker contract for Wolverine discovery and architectural verification.
/// </summary>
public interface IHandler : IWolverineHandler;

/// <summary>
/// Contract for command or event handlers returning a status <see cref="Result"/> without payload.
/// </summary>
public interface IHandler<in TRequest> : IHandler
{
    Task<Result> Handle(TRequest request, CancellationToken ct = default);
}

/// <summary>
/// Contract for command or query handlers returning a typed <see cref="Result{TResponse}"/>.
/// </summary>
public interface IHandler<in TRequest, TResponse> : IHandler
{
    Task<Result<TResponse>> Handle(TRequest request, CancellationToken ct = default);
}
