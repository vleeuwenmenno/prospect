using System.Numerics;
using Prospect.Game.Protocol;
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
    private readonly FleetOrder[] _fleetOrders = new FleetOrder[FleetOrdersMessage.MaxOrders];
    private int _fleetOrderCount;
    private bool _hasControlRequest;
    private uint _controlRequest;

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

    /// <summary>
    /// Orders a hull the player owns but is not flying. A newer order for the same hull replaces a queued one.
    /// </summary>
    public void IssueFleetOrder(uint hull, in HullOrder order)
    {
        lock (_lock)
        {
            for (var i = 0; i < _fleetOrderCount; i++)
            {
                if (_fleetOrders[i].Hull != hull)
                    continue;
                _fleetOrders[i].Order = order;
                return;
            }

            if (_fleetOrderCount < _fleetOrders.Length)
                _fleetOrders[_fleetOrderCount++] = new FleetOrder { Hull = hull, Order = order };
        }
    }

    /// <summary>
    /// Asks to take direct control of a hull the player owns. Zero asks to leave the current hull in a drone.
    /// </summary>
    public void RequestControl(uint hull)
    {
        lock (_lock)
        {
            _controlRequest = hull;
            _hasControlRequest = true;
        }
    }

    /// <summary>Called by the simulation once per tick: takes a pending control request.</summary>
    public bool ConsumeControlRequest(out uint hull)
    {
        lock (_lock)
        {
            hull = _controlRequest;
            var pending = _hasControlRequest;
            _hasControlRequest = false;
            return pending;
        }
    }

    /// <summary>Called by the simulation once per tick: takes the queued fleet orders.</summary>
    public int ConsumeFleetOrders(Span<FleetOrder> destination)
    {
        lock (_lock)
        {
            var count = _fleetOrderCount;
            _fleetOrders.AsSpan(0, count).CopyTo(destination);
            _fleetOrderCount = 0;
            return count;
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
