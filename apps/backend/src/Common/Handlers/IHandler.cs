namespace Common.Handlers;

using Wolverine;

/// <summary>
/// Marker contract for all application use-case and message handlers.
/// Enables IDE implementation discovery, Wolverine handler discovery,
/// and automated architectural verification while preserving static method injection.
/// </summary>
public interface IHandler : IWolverineHandler;
