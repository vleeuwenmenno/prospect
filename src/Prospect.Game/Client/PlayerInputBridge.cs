using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Game.Client;

/// <summary>What the player is asking of their hull, gathered by the input thread between simulation ticks.</summary>
public struct PlayerControls
{
    /// <summary>Axes in -1..1: strafe right, climb, forward.</summary>
    public float Strafe;
    public float Vertical;
    public float Forward;

    /// <summary>Roll rate in -1..1, positive rolling right.</summary>
    public float Roll;

    /// <summary>The direction the player is looking, as a unit vector in the world.</summary>
    public Vector3 Aim;

    /// <summary>When set, the hull turns its nose toward <see cref="Aim"/>; otherwise it holds its heading.</summary>
    public bool Align;

    public bool Boost;
    public bool ToggleDampeners;
}

/// <summary>
/// Carries the player's intent from the input thread to the client simulation. Held values replace each other;
/// one-shot values (toggles, orders) accumulate until the simulation takes them, so nothing
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
            var toggle = _controls.ToggleDampeners ^ frame.ToggleDampeners;
            _controls = frame;
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
            _controls.ToggleDampeners = false;
            hasOrder = _hasOrder;
            order = _order;
            _hasOrder = false;
            return result;
        }
    }
}
