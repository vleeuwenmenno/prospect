using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Prospect.Game.World;

/// <summary>
/// One node of a hull's running sums: what the blocks under it add up to. A hull's sums are a span of these, laid
/// out by its design's <see cref="BoxHullSums"/>; nothing else reads the fields. The default value is the sum of
/// no blocks.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct HullSum
{
    [InlineArray(BoxHullSums.Totals)]
    internal struct Numbers
    {
        private double _first;
    }

    internal Numbers Values;

    /// <summary>The grid box around the blocks under the node; meaningless while <see cref="Count"/> is zero.</summary>
    internal int MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

    /// <summary>How many blocks under the node the hull still has.</summary>
    internal int Count;

    /// <summary>One more than the lowest id of a drive block under the node, or zero when there is none.</summary>
    internal int Drive;
    /// <summary>One more than the index of the chosen scanner among the design's mounts, or zero when there is none.</summary>
    internal int GasScanner, AsteroidScanner, AsteroidLocator;
}

/// <summary>
/// The evaluator of one design: what every block adds to the hull's figures, and the running sums that turn the
/// blocks a hull still has into <see cref="FlightParams"/> and <see cref="HullStats"/> without looking at every
/// block. The server and the predicting client both use exactly this.
/// <para>
/// The sums are doubles in a binary tree of fixed shape. One leaf covers <see cref="GroupSize"/> blocks that
/// follow each other in the file, added in file order; a parent is its two children added. Losing a block adds
/// its group up again from the blocks left in it and then redoes the leaf's ancestors, so nothing is ever
/// subtracted and no error builds up: the figures are a pure function of which blocks are left, the same to the
/// last bit whatever order they went in, and the same as sums built afresh from that set.
/// </para>
/// <para>
/// A design keeps the tree of the intact hull, and a hull's own tree starts as a copy of it. What a block adds
/// is not kept per block: the figures that follow from a block's type and size are kept once per kind of block
/// the design uses, and the inertia and bounds, which depend on where the block sits, are worked out from its
/// box when its group is added up. Nothing here depends on a world's rules; those apply when figures are read.
/// </para>
/// </summary>
public sealed class BoxHullSums
{
    /// <summary>How many blocks one leaf of the tree covers. A loss adds up this many blocks again.</summary>
    public const int GroupSize = 32;

    /// <summary>The numbers one node holds.</summary>
    internal const int Totals = FtlPower + 3;

    private const int Mass = 0;
    private const int Hull = 1;
    private const int Torque = 2;
    private const int PowerOutput = 3;
    private const int EnergyStorage = 4;
    private const int Shield = 5;
    private const int ShieldRecharge = 6;
    private const int Cargo = 7;
    private const int Crew = 8;

    /// <summary>
    /// Thrust per push direction and kind of part (three fuels, then ion): this index plus <see cref="ThrustTable.Index"/>
    /// or <see cref="ThrustTable.IonIndex"/>. Twenty-four numbers, so a hull's thrust in each direction and from each
    /// kind of part is read off the sums like everything else.
    /// </summary>
    private const int Thrust = 9;

    /// <summary>Fuel burnt at full thrust per push direction and fuel: this index plus <see cref="BurnIndex"/>. Ion parts burn none.</summary>
    private const int Burn = Thrust + Facings.Count * ThrustTable.Kinds;

    /// <summary>Tank capacity per fuel: this index plus the fuel, less one.</summary>
    private const int FuelCapacity = Burn + Facings.Count * Fuels.Count;

    /// <summary>Power drawn at full thrust by the parts of each fuel: this index plus the fuel, less one.</summary>
    private const int ThrustPower = FuelCapacity + Fuels.Count;

    /// <summary>
    /// Power drawn at full thrust by the ion parts of each push direction: this index plus the direction. Kept per
    /// direction, since power is all an ion part spends, and a pilot pushing one way should pay for that way only.
    /// </summary>
    private const int IonPower = ThrustPower + Fuels.Count;
    private const int IdlePower = IonPower + Facings.Count;
    private const int ShieldPower = IdlePower + 1;
    private const int Cells = ShieldPower + 1;
    private const int InertiaX = Cells + 1;
    private const int InertiaY = InertiaX + 1;
    private const int InertiaZ = InertiaY + 1;

    /// <summary>Jump power per kind of drive: this index plus the kind, less one.</summary>
    private const int MomentX = InertiaZ + 1;
    private const int MomentY = MomentX + 1;
    private const int MomentZ = MomentY + 1;
    private const int FtlPower = MomentZ + 1;

    /// <summary>How fast a hull above its rated speed sheds the excess, as a share of its strongest acceleration.</summary>
    internal const float ShedShare = 1.125f;

    /// <summary>What a block of one type and size adds, wherever it sits: every number but the inertia.</summary>
    private struct Kind
    {
        public HullSum.Numbers Values;
        public bool Drive;
    }

    private readonly BoxDesign _design;
    private readonly DesignBox[] _boxes;
    private readonly Kind[] _kinds;
    private readonly int[] _kindOf;

    // The parts mounted on block faces, kept by parent block: block b has the mounts from _mountStart[b] up to
    // _mountStart[b + 1]. A mount is added with its parent and goes with it, so it needs no place in the tree.
    private readonly DesignMount[] _mounts;
    private readonly int[] _mountStart;
    private readonly int[] _mountKindOf;
    private readonly HullSum[] _intact;
    private readonly int _leaves;

    // The ore bins' blocks, in block order: the order a hull keeps what they hold in.
    private readonly int[] _bins;

    /// <summary>Works out what each block of a design adds, and the sums of the intact hull.</summary>
    public BoxHullSums(BoxDesign design)
    {
        _design = design;
        _boxes = design.Boxes;
        _kindOf = new int[_boxes.Length];
        var kinds = new List<Kind>();
        var known = new Dictionary<(BlockType, GridSize, bool, BlockFacing), int>();
        var radius = 0f;
        for (var i = 0; i < _boxes.Length; i++)
        {
            ref readonly var box = ref _boxes[i];
            // Only a thrust part's figures depend on the way it faces; every other block shares one kind.
            var facing = box.Type.Thrust > 0 ? box.Facing : BlockFacing.Back;
            if (!known.TryGetValue((box.Type, box.Size, box.Shape != BlockShape.Box, facing), out var kind))
            {
                kind = kinds.Count;
                kinds.Add(KindOf(box.Type, BoxDesign.Volume(box), box.Shape != BlockShape.Box, facing));
                known.Add((box.Type, box.Size, box.Shape != BlockShape.Box, facing), kind);
            }

            _kindOf[i] = kind;

            // The sphere that holds the block: out to its centre, and on to its farthest corner with the same
            // small margin a unit cube has always had.
            var corner = MathF.Sqrt((box.Size.X * box.Size.X + box.Size.Y * box.Size.Y + box.Size.Z * box.Size.Z) / 3f);
            radius = Math.Max(radius, design.Offset(box).Length() + design.BlockSize * 0.87f * corner);
        }

        // A mount's figures follow from its type, its size and the way its face points, which is the way its nozzle does.
        _mounts = design.MountArray;
        _mountStart = design.MountStart;
        _mountKindOf = new int[_mounts.Length];
        var knownMounts = new Dictionary<(BlockType, BlockFacing, int), int>();
        for (var i = 0; i < _mounts.Length; i++)
        {
            ref readonly var mount = ref _mounts[i];
            if (!knownMounts.TryGetValue((mount.Type, mount.Face, mount.Size), out var kind))
            {
                kind = kinds.Count;
                kinds.Add(MountKind(mount.Type, mount.Face, mount.Size));
                knownMounts.Add((mount.Type, mount.Face, mount.Size), kind);
            }

            _mountKindOf[i] = kind;
        }

        _kinds = [.. kinds];
        var bins = new List<int>();
        for (var i = 0; i < _boxes.Length; i++)
            if (_boxes[i].Type.OreCapacity > 0) bins.Add(i);
        _bins = [.. bins];
        Radius = radius;
        _leaves = (_boxes.Length + GroupSize - 1) / GroupSize;
        _intact = new HullSum[NodeCount];
        var all = new ulong[BlockSet.WordCount(_boxes.Length)];
        BlockSet.Fill(all, _boxes.Length);
        Build(_intact, all);
    }

    /// <summary>How many blocks the design has.</summary>
    public int BlockCount => _boxes.Length;

    /// <summary>How many nodes a hull's sums take: the length of the span every method here is given.</summary>
    public int NodeCount => 2 * _leaves - 1;

    /// <summary>The bytes one node takes.</summary>
    public static int NodeBytes => Unsafe.SizeOf<HullSum>();

    /// <summary>The bytes one hull's sums take: every hull of the design has this much, and the design has it once.</summary>
    public long StorageBytes => (long)NodeCount * NodeBytes;

    /// <summary>
    /// The bytes the design keeps for evaluation besides its boxes: the intact sums, an index per block and the
    /// table of kinds. Object headers are not counted.
    /// </summary>
    public long DesignBytes => StorageBytes + ((long)_kindOf.Length + _mountKindOf.Length) * sizeof(int) + (long)_kinds.Length * Unsafe.SizeOf<Kind>();

    /// <summary>The sums of the intact hull. A hull's own sums start as a copy of these.</summary>
    public ReadOnlySpan<HullSum> Intact => _intact;

    /// <summary>
    /// The radius of the sphere about the design's centre that holds every block of the intact hull, in metres.
    /// It stays that of the intact hull: what is left of one still turns about the same point.
    /// </summary>
    public float Radius { get; }

    /// <summary>The hull points a block has when intact, by its type and size, before a world's hull strength.</summary>
    public double HullPoints(int block) => _kinds[_kindOf[block]].Values[Hull];

    /// <summary>The kilograms of <paramref name="fuel"/> a block holds when full, by its type and size; zero for a block that is no tank of it.</summary>
    public double TankCapacity(int block, FuelKind fuel) => fuel == FuelKind.None ? 0.0 : _kinds[_kindOf[block]].Values[FuelCapacity + (int)fuel - 1];

    /// <summary>
    /// Builds the sums of the hull that has the blocks of <paramref name="alive"/>, a <see cref="BlockSet"/> of
    /// the design, into <paramref name="sums"/>. It looks at every block, so it is for a design being made and
    /// for tools; a hull in play starts from <see cref="Intact"/> and is told of each loss.
    /// </summary>
    public void Build(Span<HullSum> sums, ReadOnlySpan<ulong> alive)
    {
        Check(sums, alive);
        for (var group = 0; group < _leaves; group++)
            AddUp(sums, alive, group);
        for (var node = _leaves - 1; node > 0; node--)
            Merge(sums, node);
    }

    /// <summary>
    /// Brings a hull's sums up to date with the loss of one block. <paramref name="alive"/> is the hull's block
    /// set with the block already taken out. The block's group is added up again and its ancestors redone:
    /// nothing is allocated and no other block is looked at. Losing a block that was gone already changes nothing.
    /// </summary>
    public void Lose(Span<HullSum> sums, ReadOnlySpan<ulong> alive, int block)
    {
        Check(sums, alive);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)block, (uint)_boxes.Length);
        var group = block / GroupSize;
        AddUp(sums, alive, group);
        for (var node = (_leaves + group) >> 1; node > 0; node >>= 1)
            Merge(sums, node);
    }

    private void Check(Span<HullSum> sums, ReadOnlySpan<ulong> alive)
    {
        if (sums.Length != NodeCount || alive.Length != BlockSet.WordCount(_boxes.Length))
            throw new ArgumentException("The sums or the block set were not sized for this design.");
    }

    /// <summary>Adds up one group from the blocks left in it, in file order, into the group's leaf.</summary>
    private void AddUp(Span<HullSum> sums, ReadOnlySpan<ulong> alive, int group)
    {
        var sum = default(HullSum);
        var end = Math.Min(_boxes.Length, (group + 1) * GroupSize);
        for (var block = group * GroupSize; block < end; block++)
        {
            if (BlockSet.Has(alive, block))
                Add(ref sum, block);
        }

        // Node n is kept at n - 1; the leaves are nodes _leaves and up.
        sums[_leaves + group - 1] = sum;
    }

    /// <summary>The evaluated dry mass of a block and every part mounted on it, in tonnes.</summary>
    internal double BlockMass(int block)
    {
        var mass = _kinds[_kindOf[block]].Values[Mass];
        for (var m = _mountStart[block]; m < _mountStart[block + 1]; m++)
            mass += _kinds[_mountKindOf[m]].Values[Mass];
        return mass;
    }

    /// <summary>Adds one block to a sum: its kind's figures, then what follows from where it sits.</summary>
    internal void Add(ref HullSum sum, int block)
    {
        ref readonly var kind = ref _kinds[_kindOf[block]];
        ref readonly var box = ref _boxes[block];
        for (var j = 0; j < Totals; j++)
            sum.Values[j] += kind.Values[j];

        // A solid box turning about the design's original centre: its own inertia plus the parallel-axis term.
        var scale = (double)_design.BlockSize;
        var x = (box.At.X + (box.Size.X - 1) * 0.5 - _design.Center.X) * scale;
        var y = (box.At.Y + (box.Size.Y - 1) * 0.5 - _design.Center.Y) * scale;
        var z = (box.At.Z + (box.Size.Z - 1) * 0.5 - _design.Center.Z) * scale;
        var sx = box.Size.X * scale;
        var sy = box.Size.Y * scale;
        var sz = box.Size.Z * scale;
        var mass = kind.Values[Mass];
        if (box.Shape == BlockShape.Box)
        {
            sum.Values[InertiaX] += mass * (y * y + z * z + (sy * sy + sz * sz) / 12);
            sum.Values[InertiaY] += mass * (x * x + z * z + (sx * sx + sz * sz) / 12);
            sum.Values[InertiaZ] += mass * (x * x + y * y + (sx * sx + sy * sy) / 12);
        }
        else
        {
            var c = BoxDesign.Centroid(box);
            x = (c.X - _design.Center.X) * scale; y = (c.Y - _design.Center.Y) * scale; z = (c.Z - _design.Center.Z) * scale;
            BlockShapes.Decode(box.Shape, out var e, out _, out _, out _, out _);
            var vx = sx * sx / (e == 0 ? 12 : 18);
            var vy = sy * sy / (e == 1 ? 12 : 18);
            var vz = sz * sz / (e == 2 ? 12 : 18);
            sum.Values[InertiaX] += mass * (y * y + z * z + vy + vz);
            sum.Values[InertiaY] += mass * (x * x + z * z + vx + vz);
            sum.Values[InertiaZ] += mass * (x * x + y * y + vx + vy);
        }

        sum.Values[MomentX] += mass * x;
        sum.Values[MomentY] += mass * y;
        sum.Values[MomentZ] += mass * z;

        if (sum.Count == 0)
        {
            (sum.MinX, sum.MinY, sum.MinZ) = (box.At.X, box.At.Y, box.At.Z);
            (sum.MaxX, sum.MaxY, sum.MaxZ) = (box.At.X + box.Size.X, box.At.Y + box.Size.Y, box.At.Z + box.Size.Z);
        }
        else
        {
            (sum.MinX, sum.MinY, sum.MinZ) = (Math.Min(sum.MinX, box.At.X), Math.Min(sum.MinY, box.At.Y), Math.Min(sum.MinZ, box.At.Z));
            (sum.MaxX, sum.MaxY, sum.MaxZ) = (Math.Max(sum.MaxX, box.At.X + box.Size.X), Math.Max(sum.MaxY, box.At.Y + box.Size.Y), Math.Max(sum.MaxZ, box.At.Z + box.Size.Z));
        }

        sum.Count++;

        // Blocks are added in rising order, so the first drive seen is the lowest.
        if (kind.Drive && sum.Drive == 0)
            sum.Drive = block + 1;

        // What is mounted on the block is there exactly while the block is: its thrust, burn, power and mass are
        // added with it, in the design's order, and go with it when its group is added up again without it.
        for (var m = _mountStart[block]; m < _mountStart[block + 1]; m++)
            AddMount(ref sum, m);
    }

    /// <summary>Adds one mounted part to a sum: its kind's figures and the inertia of its small box where it sits.</summary>
    private void AddMount(ref HullSum sum, int index)
    {
        ref readonly var kind = ref _kinds[_mountKindOf[index]];
        ref readonly var mount = ref _mounts[index];
        for (var j = 0; j < Totals; j++)
            sum.Values[j] += kind.Values[j];
        var scale = (double)_design.BlockSize;
        var middle = _design.MountMiddle(mount);
        var size = BoxDesign.MountSize(mount);
        var x = (middle.X - _design.Center.X) * scale;
        var y = (middle.Y - _design.Center.Y) * scale;
        var z = (middle.Z - _design.Center.Z) * scale;
        var sx = size.X * scale;
        var sy = size.Y * scale;
        var sz = size.Z * scale;
        var mass = kind.Values[Mass];
        sum.Values[MomentX] += mass * x;
        sum.Values[MomentY] += mass * y;
        sum.Values[MomentZ] += mass * z;
        sum.Values[InertiaX] += mass * (y * y + z * z + (sy * sy + sz * sz) / 12);
        sum.Values[InertiaY] += mass * (x * x + z * z + (sx * sx + sz * sz) / 12);
        sum.Values[InertiaZ] += mass * (x * x + y * y + (sx * sx + sy * sy) / 12);

        // A scanner is a mounted part, so it is equipment exactly while its block is: the sums name it by its place in
        // the design's mounts, one more than its index.
        switch (mount.Type.Module.Family)
        {
            case ModuleFamily.GasScanner: sum.GasScanner = SelectModule(sum.GasScanner, index + 1); break;
            case ModuleFamily.AsteroidScanner: sum.AsteroidScanner = SelectModule(sum.AsteroidScanner, index + 1); break;
            case ModuleFamily.AsteroidLocator: sum.AsteroidLocator = SelectModule(sum.AsteroidLocator, index + 1); break;
        }
    }

    /// <summary>Makes a node the sum of its two children.</summary>
    private void Merge(Span<HullSum> sums, int node)
    {
        ref readonly var a = ref sums[node * 2 - 1];
        ref readonly var b = ref sums[node * 2];
        ref var sum = ref sums[node - 1];
        for (var j = 0; j < Totals; j++)
            sum.Values[j] = a.Values[j] + b.Values[j];
        sum.Count = a.Count + b.Count;
        sum.GasScanner = SelectModule(a.GasScanner, b.GasScanner);
        sum.AsteroidScanner = SelectModule(a.AsteroidScanner, b.AsteroidScanner);
        sum.AsteroidLocator = SelectModule(a.AsteroidLocator, b.AsteroidLocator);
        ref readonly var low = ref (a.Count == 0 ? ref b : ref a);
        ref readonly var high = ref (b.Count == 0 ? ref a : ref b);
        (sum.MinX, sum.MinY, sum.MinZ) = (Math.Min(low.MinX, high.MinX), Math.Min(low.MinY, high.MinY), Math.Min(low.MinZ, high.MinZ));
        (sum.MaxX, sum.MaxY, sum.MaxZ) = (Math.Max(low.MaxX, high.MaxX), Math.Max(low.MaxY, high.MaxY), Math.Max(low.MaxZ, high.MaxZ));

        // The tree's shape does not keep the groups in file order from left to right, so the lowest id decides.
        sum.Drive = a.Drive == 0 ? b.Drive : b.Drive == 0 ? a.Drive : Math.Min(a.Drive, b.Drive);
    }

    private int SelectModule(int a, int b)
    {
        if (a == 0) return b;
        if (b == 0) return a;
        var first = _mounts[a - 1].Type.Module;
        var second = _mounts[b - 1].Type.Module;
        if (first.Quality != second.Quality) return first.Quality > second.Quality ? a : b;
        if (first.Mark != second.Mark) return first.Mark > second.Mark ? a : b;
        return Math.Min(a, b);
    }

    private void AddLoad(ref HullSum sum, in DesignBox box, double tonnes)
    {
        var centre = _design.Offset(box);
        var dimensions = new Vector3(box.Size.X, box.Size.Y, box.Size.Z) * _design.BlockSize;
        double x = centre.X, y = centre.Y, z = centre.Z;
        double sx = dimensions.X, sy = dimensions.Y, sz = dimensions.Z;
        sum.Values[Mass] += tonnes;
        sum.Values[MomentX] += tonnes * x; sum.Values[MomentY] += tonnes * y; sum.Values[MomentZ] += tonnes * z;
        sum.Values[InertiaX] += tonnes * (y * y + z * z + (sy * sy + sz * sz) / 12);
        sum.Values[InertiaY] += tonnes * (x * x + z * z + (sx * sx + sz * sz) / 12);
        sum.Values[InertiaZ] += tonnes * (x * x + y * y + (sx * sx + sy * sy) / 12);
    }

    private ModuleEquipment Equipment(int mount) => mount == 0 ? default : ModuleEquipment.Of(mount, _mounts[mount - 1].Type.Module);

    /// <summary>Where the burn of a push direction and a fuel sits after <see cref="Burn"/>.</summary>
    private static int BurnIndex(BlockFacing push, FuelKind fuel) => (byte)push * Fuels.Count + (byte)fuel - 1;

    /// <summary>
    /// What a block of a type that fills this many cells adds, by the type's size rules. A thrust part adds its
    /// thrust and burn to the direction opposite its <paramref name="facing"/>, under the fuel it burns; an ion part
    /// adds its thrust and its power to that direction's ion figures.
    /// </summary>
    internal static HullSum.Numbers ScaledFigures(BlockType type, long cells, out bool drive, bool half = false, BlockFacing facing = BlockFacing.Back)
    {
        var values = default(HullSum.Numbers);
        double Scaled(string stat, double value) => half ? BoxDesign.ScaleHalf(type, stat, value, cells) : BoxDesign.Scale(type, stat, value, cells);

        values[Mass] = Scaled("mass", type.Mass);
        values[Hull] = Scaled("hull", type.Hull);
        values[Torque] = Scaled("torque", type.Torque);
        values[PowerOutput] = Scaled("powerOutput", type.PowerOutput);
        values[EnergyStorage] = Scaled("energyStorage", type.EnergyStorage);
        values[Shield] = Scaled("shield", type.Shield);
        values[ShieldRecharge] = Scaled("shieldRecharge", type.ShieldRecharge);
        values[Cargo] = Scaled("cargo", type.Cargo);

        // Crew capacity is whole people, rounded down once per whole block.
        var crew = Scaled("crew", type.Crew);
        values[Crew] = Math.Floor(crew + Math.Abs(crew) * 1e-9);

        // A thrust part pushes the hull away from the way it faces; its power draw counts with what the block is for.
        if (type.Thrust > 0 && type.Fuel != FuelKind.None)
        {
            var push = Facings.Opposite(facing);
            values[Thrust + ThrustTable.Index(push, type.Fuel)] = Scaled("thrust", type.Thrust);
            values[Burn + BurnIndex(push, type.Fuel)] = Scaled("fuelBurn", type.FuelBurn);
            values[ThrustPower + (int)type.Fuel - 1] = Scaled("powerDraw", type.PowerDraw);
        }
        else if (type.Thrust > 0 && type.Ion)
        {
            var push = Facings.Opposite(facing);
            values[Thrust + ThrustTable.IonIndex(push)] = Scaled("thrust", type.Thrust);
            values[IonPower + (int)push] = Scaled("powerDraw", type.PowerDraw);
        }
        else
        {
            values[type.Shield > 0 ? ShieldPower : IdlePower] = Scaled("powerDraw", type.PowerDraw);
        }

        if (type.Tank != FuelKind.None)
            values[FuelCapacity + (int)type.Tank - 1] = Scaled("fuelCapacity", type.FuelCapacity);
        values[Cells] = half ? cells * 0.5 : cells;
        drive = type.Ftl != FtlKind.None;
        if (drive)
            values[FtlPower + (int)type.Ftl - 1] = Scaled("ftlPower", type.FtlPower);
        return values;
    }

    /// <summary>
    /// What one mounted part adds: its type's own figures, pushing away from the face it sits on. It fills no cell
    /// and has no hit points of its own, so it adds neither volume nor hull points. A part placed larger
    /// (<see cref="DesignMount.Size"/>) adds them once for each of its units, its size squared: thrust, burn, power
    /// and mass alike. A part of size 1 adds the type's figures untouched.
    /// </summary>
    internal static HullSum.Numbers MountFigures(BlockType type, BlockFacing face, int size = 1)
    {
        var values = ScaledFigures(type, 1, out _, false, face);
        values[Hull] = 0;
        values[Cells] = 0;
        if (size != 1)
        {
            double units = size * size;
            for (var j = 0; j < Totals; j++)
                values[j] *= units;
        }

        return values;
    }

    private static Kind MountKind(BlockType type, BlockFacing face, int size) => new() { Values = MountFigures(type, face, size) };

    private static Kind KindOf(BlockType type, long cells, bool half, BlockFacing facing)
    {
        var kind = new Kind { Values = ScaledFigures(type, cells, out var drive, half, facing) };
        kind.Drive = drive;
        return kind;
    }

    /// <summary>
    /// Reads a hull's flight figures and data sheet off its sums, under a world's rules: mass and moments of
    /// inertia, acceleration in each direction from that direction's thrust over mass, turning from gyro torque
    /// over inertia, the tanks, and the power budget.
    /// Constant time. The first drive left in file order decides the kind of drive; only drives of that kind
    /// supply jump power.
    /// </summary>
    public void Evaluate(ReadOnlySpan<HullSum> sums, FlightRules rules, out FlightParams flight, out HullStats stats, ReadOnlySpan<HoldContainer> cargo = default, ReadOnlySpan<OreBin> ore = default)
    {
        if (sums.Length != NodeCount)
            throw new ArgumentException("The sums were not sized for this design.", nameof(sums));

        var sum = sums[0];
        // Payload is distributed uniformly through its rack, and raw ore through its bin, for mass, centre and
        // diagonal inertia.
        foreach (var container in cargo)
            AddLoad(ref sum, _boxes[container.Rack], container.Amount / 1000.0);
        for (var i = 0; i < ore.Length && i < _bins.Length; i++)
            if (!ore[i].IsEmpty) AddLoad(ref sum, _boxes[_bins[i]], ore[i].Kg / 1000.0);
        var size = _design.BlockSize;
        var mass = Math.Max((float)sum.Values[Mass], 0.01f);
        var actualMass = sum.Values[Mass];
        var cx = actualMass > 0 ? sum.Values[MomentX] / actualMass : 0;
        var cy = actualMass > 0 ? sum.Values[MomentY] / actualMass : 0;
        var cz = actualMass > 0 ? sum.Values[MomentZ] / actualMass : 0;
        var center = new Vector3((float)cx, (float)cy, (float)cz);
        var inertia = Vector3.Max(new Vector3(
            (float)(sum.Values[InertiaX] - actualMass * (cy * cy + cz * cz)),
            (float)(sum.Values[InertiaY] - actualMass * (cx * cx + cz * cz)),
            (float)(sum.Values[InertiaZ] - actualMass * (cx * cx + cy * cy))), new Vector3(0.001f));
        var hull = (float)sum.Values[Hull] * rules.HullStrength;
        var shield = (float)sum.Values[Shield];
        var recharge = (float)sum.Values[ShieldRecharge] * rules.ShieldRechargeMultiplier;
        var torque = (float)sum.Values[Torque];
        var generated = (float)sum.Values[PowerOutput];
        var storage = (float)sum.Values[EnergyStorage];
        var idlePower = (float)sum.Values[IdlePower];
        var thrustPowerByFuel = new FuelTanks
        {
            Nitrogen = (float)sum.Values[ThrustPower],
            Hydrazine = (float)sum.Values[ThrustPower + 1],
            Bipropellant = (float)sum.Values[ThrustPower + 2],
        };
        var ionPower = default(DirectionFigures);
        for (var direction = 0; direction < Facings.Count; direction++)
            ionPower[direction] = (float)sum.Values[IonPower + direction];

        // Fuelled parts draw by the largest throttle asked; ion parts by each direction's own, so the most they
        // ever draw at once is the larger of each axis's two directions.
        var thrustPower = thrustPowerByFuel.Nitrogen + thrustPowerByFuel.Hydrazine + thrustPowerByFuel.Bipropellant + ionPower.AtOnce;
        var fuelCapacity = new FuelTanks
        {
            Nitrogen = (float)sum.Values[FuelCapacity],
            Hydrazine = (float)sum.Values[FuelCapacity + 1],
            Bipropellant = (float)sum.Values[FuelCapacity + 2],
        };
        var shieldPower = (float)sum.Values[ShieldPower] * rules.ShieldRechargeMultiplier;
        var blocks = sum.Count;
        var ftl = sum.Drive == 0 ? FtlKind.None : _boxes[sum.Drive - 1].Type.Ftl;

        // A drive that takes power faster charges sooner; what a jump costs in all is the same.
        var ftlPower = ftl == FtlKind.None ? 0f : (float)sum.Values[FtlPower + (int)ftl - 1] * rules.FtlChargeMultiplier;

        // Acceleration per push direction and kind of part. The rated speed follows the strongest direction, and a
        // hull above it sheds the excess an eighth faster than that direction can add to it, so holding the throttle
        // open never keeps a hull above its rating.
        var thrust = default(ThrustTable);
        var burn = default(ThrustTable);
        var strongest = 0f;
        for (var direction = 0; direction < Facings.Count; direction++)
        {
            var push = 0f;
            for (var kind = 0; kind < ThrustTable.Kinds; kind++)
            {
                var at = direction * ThrustTable.Kinds + kind;
                thrust[at] = (float)sum.Values[Thrust + at] / mass;
                push += thrust[at];
            }

            for (var fuel = 0; fuel < Fuels.Count; fuel++)
                burn[direction * ThrustTable.Kinds + fuel] = (float)sum.Values[Burn + direction * Fuels.Count + fuel];
            strongest = Math.Max(strongest, push);
        }

        var turnAcceleration = new Vector3(torque) / inertia;
        var turnRate = new Vector3(MathF.Sqrt(turnAcceleration.X), MathF.Sqrt(turnAcceleration.Y), MathF.Sqrt(turnAcceleration.Z)) * rules.TurnRatePerRootAcceleration;
        var maxSpeed = rules.SpeedPerRootAcceleration * MathF.Sqrt(strongest);

        flight = new FlightParams
        {
            Thrust = thrust,
            Burn = burn,
            FuelCapacity = fuelCapacity,
            BoostFuelMultiplier = rules.BoostFuelMultiplier,
            ShedAcceleration = strongest * ShedShare,
            BoostMultiplier = rules.BoostThrustMultiplier,
            MaxSpeed = maxSpeed,
            BoostMaxSpeed = maxSpeed * rules.BoostSpeedMultiplier,
            TurnRate = turnRate,
            TurnAcceleration = turnAcceleration,
            HeatRate = 1f / rules.BoostHeatSeconds,
            CoolRate = 1f / rules.CoolSeconds,
            ColdHeating = rules.ColdCloudHeating,
            ColdCooling = rules.ColdCloudCooling,
            Mass = mass,
            Inertia = inertia,
            CenterOfMass = center,
            Radius = Radius,
            HullPoints = hull,
            ShieldCapacity = shield,
            ShieldRecharge = recharge,
            ShieldDelay = rules.ShieldRechargeDelaySeconds,
            PowerGenerated = generated,
            IdlePower = idlePower,
            ThrustPower = thrustPower,
            ThrustPowerByFuel = thrustPowerByFuel,
            IonPower = ionPower,
            ShieldPower = shieldPower,
            ShieldRestartFraction = rules.ShieldRestartFraction,
            ShieldRestartPower = shieldPower * rules.ShieldRestartPowerMultiplier,
            BoostPowerMultiplier = rules.BoostPowerMultiplier,
            EnergyStorage = storage,
            BoostEnergyFloor = rules.BoostEnergyFloor * storage,
            LaserEnergyFloor = rules.LaserResumeBattery * storage,
            ShieldContactSpeed = rules.ShieldContactSpeed,
            ShieldImpactEnergyFraction = rules.ShieldImpactEnergyFraction,
            CollisionSafeSpeed = rules.CollisionSafeSpeed,
            CollisionDamagePerTonneSpeedSquared = rules.CollisionDamagePerTonneSpeedSquared,
            CollisionBounce = rules.CollisionBounce,
            CollisionShieldCost = rules.CollisionShieldCost,
            Ftl = ftl,
            FtlPower = ftlPower,
            FtlEnergy = rules.FtlEnergyPerTonne * mass,
            FtlMaxDistance = sum.Drive == 0 ? 0 : _boxes[sum.Drive - 1].Type.FtlMaxSectors * rules.SectorSize,
            FtlAlignCosine = MathF.Cos(rules.FtlAlignDegrees * MathF.PI / 180f),
            FtlCalculateSeconds = rules.FtlCalculateSeconds,
            FtlTunnelSeconds = rules.FtlTunnelSeconds,
            FtlSpeed = rules.FtlSpeed,
            FtlArriveSeconds = rules.FtlArriveSeconds,
        };

        stats = new HullStats
        {
            GasScanner = Equipment(sum.GasScanner),
            AsteroidScanner = Equipment(sum.AsteroidScanner),
            AsteroidLocator = Equipment(sum.AsteroidLocator),
            HullPoints = hull,
            ShieldCapacity = shield,
            ShieldRecharge = recharge,
            Mass = mass,
            Volume = (float)sum.Values[Cells] * size * size * size,
            Size = blocks > 0 ? new Vector3(sum.MaxX - sum.MinX, sum.MaxY - sum.MinY, sum.MaxZ - sum.MinZ) * size : Vector3.Zero,
            Blocks = blocks,
            CargoHold = (float)sum.Values[Cargo],
            Crew = checked((int)sum.Values[Crew]),
            Acceleration = flight.Push(BlockFacing.Front),
            Deceleration = flight.Push(BlockFacing.Back),
            LateralAcceleration = Math.Min(flight.Acceleration.X, flight.Acceleration.Y),
            FuelCapacity = fuelCapacity,
            MaxSpeed = maxSpeed,
            BoostMaxSpeed = maxSpeed * rules.BoostSpeedMultiplier,
            TurnRate = turnRate,
            PowerRequired = flight.PowerRequired,
            PowerGenerated = generated,
            EnergyStorage = storage,
            Ftl = ftl,
            FtlEnergy = rules.FtlEnergyPerTonne * mass,
            FtlPower = ftlPower,
            FtlMaxDistance = flight.FtlMaxDistance,
        };
    }
}
