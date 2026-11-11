namespace Prospect.Engine.Net;

/// <summary>Bounded retry schedule for RakNet's per-address connection cooldown.</summary>
internal struct ConnectRetry
{
    private long _at;
    internal int Count { get; private set; }

    internal bool Schedule(long now, long frequency, int jitterMilliseconds)
    {
        if (Count == 8)
        {
            Cancel();
            return false;
        }
        Count++;
        _at = now + frequency * (120 + 40 * Count + jitterMilliseconds) / 1000;
        return true;
    }

    internal bool TakeDue(long now)
    {
        if (_at == 0 || now < _at)
            return false;
        _at = 0;
        return true;
    }

    internal void Cancel() => _at = 0;
}
