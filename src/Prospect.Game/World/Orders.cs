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

    /// <summary>
    /// Hold on beside the free container <see cref="TaskOrder.Subject"/> at <see cref="TaskOrder.Point"/> (an offset
    /// from the container's centre along the base hull's axes) and carry it with the other drones of the group to the
    /// base hull's tractor beam. Phase 1 while the drone is close enough to its place to pull.
    /// </summary>
    Fetch = 3,

    /// <summary>
    /// Take a place around the rock of the hull's drone scan (<see cref="TaskOrder.Point"/>, in the rock's own frame,
    /// metres from its centre), flying around the rock's shell, and hold it for the group scan. Phase 1 while the drone
    /// holds its place; <see cref="ScanLeaving"/> once the scan is over and it flies around the rock toward home.
    /// </summary>
    Survey = 4,

    /// <summary>The same for the detailed scan, which maps veins and pockets.</summary>
    DetailedScan = 5,

    /// <summary>
    /// <see cref="Fetch"/> for a loose ore chunk: <see cref="TaskOrder.Subject"/> is the chunk, and the group carries
    /// it to the base hull's tractor beam, which leads it into an ore hopper.
    /// </summary>
    FetchOre = 6,
}

/// <summary>Phases shared by the scan tasks.</summary>
public static class ScanOrderPhase
{
    /// <summary>On the way to its place.</summary>
    public const byte Moving = 0;

    /// <summary>Holding its place.</summary>
    public const byte InPlace = 1;

    /// <summary>The scan is over: around the rock toward home, then a recall.</summary>
    public const byte ScanLeaving = 2;
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

    /// <summary>
    /// The point the task works at, in the base hull's own frame, in metres from its pivot; for a fetch, the drone's
    /// place as an offset from the container's centre along the base hull's axes.
    /// </summary>
    public Vector3 Point;

    /// <summary>What the task acts on, by its durable id (a container, a rock); zero for nothing.</summary>
    public ulong Subject;

    /// <summary>The tick at which the order gives up; zero for never.</summary>
    public long Until;
}
