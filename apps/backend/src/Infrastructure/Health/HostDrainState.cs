namespace ExamPrism.Infrastructure.Health;

public sealed class HostDrainState
{
    private volatile bool _isDraining;
    private volatile bool _isStartupComplete = true;

    public bool IsDraining => _isDraining;
    public bool IsStartupComplete => _isStartupComplete;
    public bool IsReady => !_isDraining && _isStartupComplete;

    public void MarkDraining()
    {
        _isDraining = true;
    }

    public void MarkStartupIncomplete()
    {
        _isStartupComplete = false;
    }

    public void MarkStartupComplete()
    {
        _isStartupComplete = true;
    }

    public void Reset()
    {
        _isDraining = false;
        _isStartupComplete = true;
    }
}
