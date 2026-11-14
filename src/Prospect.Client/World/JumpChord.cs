using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// The forward and boost controls that started the pilot's own plotted jump. Forward thrust held as part of that
/// start belongs to the jump, so the view holds it off until forward and boost have both been let go and, through the
/// jump's arrival, so thrust held all the way does not carry the hull out of it. No input cancels a jump, so the
/// chord is not about cancelling: letting go, tapping or pressing boost or forward ends nothing. Only halt does.
/// It reads the controls as they physically are, so a menu that hides them from the world is not a release.
/// </summary>
internal struct JumpChord
{
    /// <summary>
    /// Simulation ticks a started jump may take to show as the hull's order before the chord stops waiting for it:
    /// the client predicts its own order on the next tick, so only an order that was dropped takes longer.
    /// </summary>
    private const long PendingTicks = 4;

    private long _startedTick;
    private bool _active;
    private bool _seen;
    private bool _forwardHeld;

    /// <summary>True while forward thrust is still part of the jump's start and must not be sent.</summary>
    public readonly bool Active => _active;

    /// <summary>
    /// Reads the physical controls and the hull being flown once a frame. Returns true on the frame forward thrust
    /// went on. The chord ends when both controls are up, or when the jump it started is over or cancelled.
    /// </summary>
    public bool Observe(bool forwardHeld, bool boostHeld, in HullView own, long clientTick)
    {
        var pressed = forwardHeld && !_forwardHeld;
        _forwardHeld = forwardHeld;
        if (!_active)
            return pressed;

        if (own.Order.Kind == OrderKind.Jump)
            _seen = true;
        // The order ends as the hull leaves the tunnel; the chord lasts through the arrival, so thrust held all the
        // way does not carry the hull out of its own arrival before the jump is over.
        var gone = _seen ? own.Order.Kind != OrderKind.Jump && own.Jump == JumpPhase.None : clientTick - _startedTick > PendingTicks;
        if (gone || (!forwardHeld && !boostHeld))
            _active = false;
        return pressed;
    }

    /// <summary>Called on the frame the controls ordered the jump.</summary>
    public void Start(long clientTick)
    {
        _active = true;
        _seen = false;
        _startedTick = clientTick;
    }

    /// <summary>Ends the chord: the pilot took the controls, halted, or flies something else now.</summary>
    public void Clear() => _active = false;
}
