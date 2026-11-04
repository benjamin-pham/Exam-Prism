namespace ExamPrism.Infrastructure.Health;

public sealed class HostDrainState
{
    private volatile bool _isDraining;

    public bool IsDraining => _isDraining;

    public void MarkDraining()
    {
        _isDraining = true;
    }

    public void Reset()
    {
        _isDraining = false;
    }
}
