using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// What an order tells a craft to do. A new task (fetching a container, a scan, placing a charge, a mining laser's
/// cut) is a new kind here and a case in the server's one executor; whoever gives the order (a player now, a captain
/// or a miner later) gives it the same way.
/// </summary>
public enum OrderTask : byte
{
    None = 0,

    /// <summary>Hold station at <see cref="TaskOrder.Point"/>, which moves and turns with the base hull.</summary>
    Hold = 1,

    /// <summary>Fly back through the hatch of the bay and dock; docking puts the drone back in the bay.</summary>
    Recall = 2,
}

/// <summary>
/// One order, small and plain so the executor runs a fixed table of them each tick without taking memory: what to
/// do, who carries it out, who gave it, what it belongs to, where, on what, and until when. Every field a later task
/// needs is already here (<see cref="Subject"/> and <see cref="Until"/> are unused by holding and recalling).
/// </summary>
public struct TaskOrder
{
    /// <summary>What to do.</summary>
    public OrderTask Task;

    /// <summary>How far the task has come; its meaning is the task's (a recall lines up, then flies in).</summary>
    public byte Phase;

    /// <summary>The player who gave the order. The executor carries it out whoever that is.</summary>
    public uint Issuer;

    /// <summary>The craft that carries the order out.</summary>
    public HullHandle Actor;

    /// <summary>The hull the craft belongs to, and the index of its bay in that design's <see cref="HullDesign.Bays"/>.</summary>
    public HullHandle Base;
    public int Bay;

    /// <summary>The point the task works at, in the base hull's own frame, in metres from its pivot.</summary>
    public Vector3 Point;

    /// <summary>What the task acts on, by its durable id (a container, a rock); zero for nothing.</summary>
    public ulong Subject;

    /// <summary>The tick at which the order gives up; zero for never.</summary>
    public long Until;
}
