using Prospect.Game.World;

namespace Prospect.Game.Client;

/// <summary>What the player is asking of their hull, gathered by the input thread between simulation ticks.</summary>
public struct PlayerControls
{
    /// <summary>Axes in -1..1: strafe right, climb, forward.</summary>
    public float Strafe;
    public float Vertical;
    public float Forward;

    /// <summary>Rate axes in -1..1 from keys and sticks: nose up, turn right, roll right.</summary>
    public float Pitch;
    public float Yaw;
    public float Roll;

    /// <summary>Turn requested by pointer movement since the last tick, in radians: nose up and turn right.</summary>
    public float LookPitch;
    public float LookYaw;

    public bool Boost;
    public bool ToggleDampeners;
}

/// <summary>
/// Carries the player's intent from the input thread to the client simulation. Held values replace each other;
/// one-shot values (pointer movement, toggles, orders) accumulate until the simulation takes them, so nothing
/// that happens between two ticks is lost.
/// </summary>
public sealed class PlayerInputBridge
{
    private readonly Lock _lock = new();
    private PlayerControls _controls;
    private bool _hasOrder;
    private HullOrder _order;

    /// <summary>Called by the input thread once per frame.</summary>
    public void Submit(in PlayerControls frame)
    {
        lock (_lock)
        {
            var lookPitch = _controls.LookPitch + frame.LookPitch;
            var lookYaw = _controls.LookYaw + frame.LookYaw;
            var toggle = _controls.ToggleDampeners ^ frame.ToggleDampeners;
            _controls = frame;
            _controls.LookPitch = lookPitch;
            _controls.LookYaw = lookYaw;
            _controls.ToggleDampeners = toggle;
        }
    }

    /// <summary>Gives the player's hull a standing order, replacing any it has. Kind None cancels.</summary>
    public void IssueOrder(in HullOrder order)
    {
        lock (_lock)
        {
            _order = order;
            _hasOrder = true;
        }
    }

    /// <summary>Called by the simulation once per tick: takes the accumulated one-shot values.</summary>
    public PlayerControls Consume(out bool hasOrder, out HullOrder order)
    {
        lock (_lock)
        {
            var result = _controls;
            _controls.LookPitch = 0;
            _controls.LookYaw = 0;
            _controls.ToggleDampeners = false;
            hasOrder = _hasOrder;
            order = _order;
            _hasOrder = false;
            return result;
        }
    }
}
