using System.Numerics;
using Brutal.Numerics;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// Where each hull of the snapshot is drawn this frame, worked out once in the update and shared by the drawing, the
/// picking and the markers that follow hulls in the world, so what can be targeted is what is on screen. Another
/// hull's jump moves it off where the simulation has it: into a rift's or wormhole's opening and out of the far one,
/// cut away past the opening, or drawn out along its nose through hyperspace; between the ends it is not drawn at
/// all. Everything follows from the hull's replicated stage and progress, so every observer sees the same jump.
/// Strategic positions (the map, orders, ranges) stay the simulation's. Client side only: nothing here is game state.
/// </summary>
internal sealed class HullVisuals
{
    /// <summary>Jumps followed at once. The clipping planes of their openings go to the GPU, one per passage.</summary>
    public const int MaxPassages = 32;

    /// <summary>A move this far in one tick is a jump: there is nothing in between to interpolate.</summary>
    public const float JumpGap = 3000f;

    // Where a rift's or wormhole's opening sits, in hull radii: how far ahead of the nose it opens while the drive
    // works, how far behind an arriving hull it closes, and its radius when fully open.
    public const float OpeningAhead = 5f;
    public const float OpeningBehind = 2f;
    public const float OpeningRadius = 1.4f;

    // Hyperspace seen from outside, in hull radii and seconds: the light gathers this far ahead of the nose, the
    // hull is drawn out up to this many times its length as it lunges into it, an arriving hull streaks in from
    // this far behind where it stops, and the light it left at the start fades this long after the drive fires.
    public const float HyperspaceLight = 3f;
    private const float HyperspaceStretch = 6f;
    private const float HyperspaceApproach = 14f;
    private const float HyperspaceLead = 0.5f;
    private const float HyperspaceLand = 0.6f;
    public const float DepartureFlashSeconds = 0.8f;

    /// <summary>One followed jump: where its opening or light is, and how far its hull has been carried along.</summary>
    internal struct PassageState
    {
        /// <summary>The hull's id; 0 for a free slot.</summary>
        public uint Id;
        public bool Seen;

        /// <summary>The hull was jumping this frame. A slot is only freed once its jump is over and eased out.</summary>
        public bool Active;

        /// <summary>Rift or wormhole: the opening's centre. Hyperspace: the light the hull left from.</summary>
        public Double3 Mouth;
        public Vector3 Axis;
        public Vector3 Up;
        public FtlKind Kind;
        public float Radius;
        public float Seed;

        /// <summary>How far along the axis the hull is drawn from where the simulation has it, in metres.</summary>
        public float Offset;
        public float Open;

        /// <summary>Which side of the opening hides the hull: 1 past it, -1 before it, 0 neither.</summary>
        public float Clip;

        /// <summary>How far the hull is drawn out along its nose; 1 is not at all.</summary>
        public float Stretch;

        /// <summary>
        /// Hyperspace's light left at the start after the drive fires, 0 to 1 through its fade; 0 while the drive
        /// works, so it can fade once it has fired, and below 0 for none.
        /// </summary>
        public float Flash;
    }

    /// <summary>One hull as drawn this frame.</summary>
    internal struct HullVisual
    {
        /// <summary>The centre it is drawn at. For the pilot's own hull seen from inside, before the jump's offset.</summary>
        public Double3 Position;

        /// <summary>Where the simulation has it, between the last two ticks.</summary>
        public Double3 Anchor;
        public Quaternion Orientation;
        public float Stretch;

        /// <summary>The passage whose opening cuts the hull where it is past it, or -1.</summary>
        public int Clip;

        /// <summary>The blocks that can be picked: those it has, less those wholly past its opening.</summary>
        public ulong PickBlocks;

        /// <summary>
        /// Whether the hull has every block and is drawn as its design's one mesh, which leaves out the faces between
        /// blocks; otherwise each block it has is drawn as its own cube.
        /// </summary>
        public bool Intact;
        public bool Visible;

        /// <summary>The pilot's own hull seen from inside its jump, which <see cref="WorldView"/> moves itself.</summary>
        public bool Inside;
    }

    private readonly PassageState[] _passages = new PassageState[MaxPassages];
    private HullVisual[] _hulls = [];
    private int _count;

    /// <summary>The jumps followed, by slot; a slot's index is its clipping plane on the GPU.</summary>
    public ReadOnlySpan<PassageState> Passages => _passages;

    /// <summary>This frame's hulls, in the snapshot's order.</summary>
    public ReadOnlySpan<HullVisual> Hulls => _hulls.AsSpan(0, _count);

    /// <summary>A passage slot no jump is followed through this frame, whose clipping plane is free, or -1.</summary>
    public int FreePassage()
    {
        for (var i = 0; i < _passages.Length; i++)
        {
            if (_passages[i].Id == 0)
                return i;
        }

        return -1;
    }

    /// <summary>Forgets a departed session's hulls and passages without discarding reusable storage.</summary>
    public void Clear()
    {
        Array.Clear(_passages);
        _count = 0;
    }

    /// <summary>
    /// Works out every hull's place this frame. <paramref name="alpha"/> is how far the frame is between the last two
    /// ticks; <paramref name="ownInside"/> whether the pilot sees their own hull's jump from inside, where it is not
    /// a passage seen from outside; <paramref name="ownPreview"/> a drive effect drawn for the own hull instead of its
    /// own, or none.
    /// </summary>
    public void Update(ReadOnlySpan<HullView> hulls, HullDesign[] designs, float alpha, float dt, bool ownInside, FtlKind ownPreview)
    {
        // Sized once for the snapshot's capacity, never per frame.
        if (_hulls.Length < hulls.Length)
            _hulls = new HullVisual[hulls.Length];
        _count = hulls.Length;
        dt = Math.Clamp(dt, 0f, 0.1f);
        for (var i = 0; i < hulls.Length; i++)
        {
            ref readonly var hull = ref hulls[i];
            var design = designs[Math.Min(hull.Design, designs.Length - 1)];
            var anchor = TargetPicking.HullPosition(hull.PreviousPosition, hull.Position, alpha, JumpGap);
            ref var visual = ref _hulls[i];
            visual = new HullVisual
            {
                Position = anchor,
                Anchor = anchor,
                Orientation = Quaternion.Slerp(hull.PreviousOrientation, hull.Orientation, alpha),
                Stretch = 1f,
                Clip = -1,
                PickBlocks = hull.Blocks,
                Intact = hull.Blocks == design.FullMask,
                Visible = true,
                Inside = hull.IsOwn && ownInside,
            };
            if (!visual.Inside)
                Jump(hull, design, hull.IsOwn && ownPreview != FtlKind.None ? ownPreview : design.Flight.Ftl, ref visual, dt);
        }

        EndPassages(dt);
    }

    private void Jump(in HullView hull, HullDesign design, FtlKind kind, ref HullVisual visual, float dt)
    {
        var flight = design.Flight;
        var jumping = hull.Jump != JumpPhase.None && kind != FtlKind.None;
        var forward = Vector3.Transform(-Vector3.UnitZ, visual.Orientation);
        var progress = jumping ? WorldView.JumpProgress(hull, flight) : 0f;
        var tunnel = TunnelSeconds(hull, flight);
        var index = PassageSlot(hull.Id, jumping);
        if (index < 0)
        {
            // Every slot is taken: the hull is still moved and hidden as its jump says, without an opening or
            // anything to ease out of a cancel.
            if (kind == FtlKind.Hyperspace && jumping)
            {
                var light = Hyperspace(hull.Jump, progress, flight, tunnel);
                visual.Position += forward * light.Offset;
                visual.Stretch = light.Stretch;
                visual.Visible = light.Visible;
            }
            else
            {
                visual.Visible = hull.Jump != JumpPhase.Tunnel;
            }

            return;
        }

        ref var slot = ref _passages[index];
        slot.Seen = true;
        slot.Active = jumping;
        if (jumping)
        {
            slot.Axis = forward;
            slot.Up = Vector3.Transform(Vector3.UnitY, visual.Orientation);
            slot.Kind = kind;
            slot.Radius = flight.Radius;
            slot.Seed = (hull.Id & 0xFF) / 255f;
            if (kind == FtlKind.Hyperspace)
            {
                var light = Hyperspace(hull.Jump, progress, flight, tunnel);
                slot.Offset = light.Offset;
                slot.Stretch = light.Stretch;
                slot.Open = 0f;
                slot.Clip = 0f;
                visual.Visible = light.Visible;
                if (hull.Jump is JumpPhase.Align or JumpPhase.Charge)
                {
                    slot.Mouth = visual.Anchor + forward * (flight.Radius * HyperspaceLight);
                    slot.Flash = 0f;
                }
                else if (slot.Flash >= 0f)
                {
                    // Only a drive seen working leaves its light behind; one first seen in transit has none here.
                    var since = hull.Jump == JumpPhase.Tunnel ? progress * tunnel : tunnel + progress * flight.FtlArriveSeconds;
                    slot.Flash = since < DepartureFlashSeconds ? Math.Max(since / DepartureFlashSeconds, 1e-4f) : -1f;
                }
            }
            else
            {
                var passage = Passage(hull.Jump, progress, flight, tunnel);
                slot.Mouth = visual.Anchor + forward * passage.Mouth;
                slot.Offset = passage.Offset;
                slot.Open = passage.Open;
                slot.Clip = passage.Clip;
                slot.Stretch = 1f;
                slot.Flash = -1f;
                visual.Visible = passage.Visible;
            }
        }
        else
        {
            Decay(ref slot, dt);
        }

        visual.Position = visual.Anchor + slot.Axis * slot.Offset;
        visual.Stretch = slot.Stretch;
        if (slot.Clip != 0f)
        {
            visual.Clip = index;
            visual.PickBlocks &= Uncut(design, visual, slot);
        }
    }

    /// <summary>
    /// The blocks with any part on the shown side of the opening that cuts the hull, drawn out as it is drawn. Picking
    /// cuts what is past the opening away from each of them exactly; see <see cref="Pick"/>.
    /// </summary>
    private static ulong Uncut(HullDesign design, in HullVisual visual, in PassageState slot)
    {
        var inverse = Quaternion.Conjugate(visual.Orientation);
        var hidden = Vector3.Transform(slot.Axis * slot.Clip, inverse);
        var from = Vector3.Transform(visual.Position.RelativeTo(slot.Mouth), inverse);
        // How far a block's box reaches towards the shown side from its centre.
        var reach = design.BlockSize * 0.5f * (MathF.Abs(hidden.X) + MathF.Abs(hidden.Y) + MathF.Abs(hidden.Z) * visual.Stretch);
        var mask = 0UL;
        for (var b = 0; b < design.BlockOffsets.Length; b++)
        {
            var block = design.BlockOffsets[b];
            block.Z *= visual.Stretch;
            if (Vector3.Dot(from + block, hidden) - reach <= 0f)
                mask |= 1UL << b;
        }

        return mask;
    }

    /// <summary>
    /// A hull as picking sees it, drawn at <paramref name="position"/>: its pickable blocks with the faces the mesh
    /// pass draws of them, drawn out and cut at its opening exactly as that pass draws it, so what can be picked is
    /// what is on screen.
    /// </summary>
    public PickHull Pick(uint id, in HullVisual visual, Double3 position, HullDesign design)
    {
        var hull = new PickHull(id, position, visual.Orientation, design.BlockOffsets, design.BlockSize, visual.PickBlocks,
            design.Flight.Radius, visual.Stretch, Faces: visual.Intact ? design.ExteriorFaces : null);
        if (visual.Clip < 0)
            return hull;
        ref readonly var slot = ref _passages[visual.Clip];
        return hull with { CutPoint = slot.Mouth.RelativeTo(position), CutNormal = slot.Axis * slot.Clip };
    }

    /// <summary>Eases a passage whose hull is no longer jumping, or no longer there, back to nothing.</summary>
    private static void Decay(ref PassageState slot, float dt)
    {
        var back = MathF.Exp(-5f * dt);
        slot.Offset *= back;
        slot.Stretch = 1f + (slot.Stretch - 1f) * back;
        slot.Open *= MathF.Exp(-4f * dt);
        if (slot.Flash > 0f)
            slot.Flash = slot.Flash + dt / DepartureFlashSeconds < 1f ? slot.Flash + dt / DepartureFlashSeconds : -1f;
    }

    /// <summary>The slot that follows a hull's passage, a free one for a new passage, or -1.</summary>
    private int PassageSlot(uint id, bool start)
    {
        var free = -1;
        for (var i = 0; i < _passages.Length; i++)
        {
            if (_passages[i].Id == id)
                return i;
            if (free < 0 && _passages[i].Id == 0)
                free = i;
        }

        if (!start || free < 0)
            return -1;
        _passages[free] = new PassageState { Id = id, Stretch = 1f, Flash = -1f };
        return free;
    }

    /// <summary>
    /// Eases out the passages of hulls that are no longer in the snapshot, and frees every slot whose jump is over
    /// and has eased to nothing.
    /// </summary>
    private void EndPassages(float dt)
    {
        for (var i = 0; i < _passages.Length; i++)
        {
            ref var slot = ref _passages[i];
            if (slot.Id == 0)
                continue;
            if (!slot.Seen)
            {
                slot.Active = false;
                Decay(ref slot, dt);
            }

            if (!slot.Active && slot.Open < 0.01f && MathF.Abs(slot.Offset) < 0.01f && MathF.Abs(slot.Stretch - 1f) < 0.01f && slot.Flash <= 0f)
                slot.Id = 0;
            slot.Seen = false;
        }
    }

    /// <summary>A hull's passage through a rift or wormhole, as an observer draws it. See <see cref="Passage"/>.</summary>
    internal readonly record struct PassageShape(float Offset, float Mouth, float Open, float Clip, bool Visible);

    /// <summary>
    /// How a hull's jump through a rift or a wormhole is drawn from outside, from its replicated stage and
    /// progress alone. Late in the drive's work the hull accelerates along its nose into the opening ahead, which is
    /// hidden past (clip 1) and closes behind it before the drive fires; at the far end the opening grows behind
    /// where the hull will be, and over the last second of the transit and the arrival the hull comes out of it
    /// (hidden before it, clip -1) and slows to where the server has it. Offset is along the nose from the hull's
    /// position, Mouth where the opening is along it, in metres; Visible is false while it is between the openings.
    /// <paramref name="tunnelSeconds"/> is the hull's own transit, or 0 for the drive's base one. A longer transit
    /// only waits longer between the openings: the far one opens over the base transit's length before the end.
    /// </summary>
    internal static PassageShape Passage(JumpPhase phase, float progress, in FlightParams flight, float tunnelSeconds = 0f)
    {
        var radius = flight.Radius;
        var ahead = radius * OpeningAhead;
        if (phase is JumpPhase.Align or JumpPhase.Charge)
        {
            var start = phase == JumpPhase.Charge ? progress : 0f;
            var through = ahead + radius * 2.2f;
            var move = Math.Clamp((start - 0.6f) / 0.4f, 0f, 1f);
            var offset = through / MathF.Pow(0.925f, 2.2f) * MathF.Pow(move, 2.2f);
            var open = (0.05f + 0.95f * Smooth(start)) * (1f - Smooth((offset - ahead - radius) / (radius * 2f)));
            return new PassageShape(offset, ahead, open, 1f, offset < ahead + radius * 1.5f);
        }

        var tunnel = Math.Max(tunnelSeconds > 0f ? tunnelSeconds : flight.FtlTunnelSeconds, 0.1f);
        var opening = Math.Clamp(flight.FtlTunnelSeconds, 0.1f, tunnel);
        var arrive = Math.Max(flight.FtlArriveSeconds, 0.1f);
        var since = phase == JumpPhase.Tunnel ? progress * tunnel : tunnel + progress * arrive;
        const float Lead = 1f;
        var lead = Math.Min(Lead, tunnel);
        var u = Math.Clamp((since - (tunnel - lead)) / (lead + arrive), 0f, 1f);
        var behind = radius * (OpeningBehind + 2f);
        var exit = phase == JumpPhase.Tunnel ? Smooth((since - (tunnel - opening)) / opening) : 1f - Smooth(progress);
        return new PassageShape(-behind * (1f - u) * (1f - u), -radius * OpeningBehind, exit, -1f, u > 0f);
    }

    /// <summary>A hull's hyperspace jump as an observer draws it. See <see cref="Hyperspace"/>.</summary>
    internal readonly record struct HyperspaceShape(float Offset, float Stretch, bool Visible);

    /// <summary>
    /// How a hull's hyperspace jump is drawn from outside, from its replicated stage and progress alone. Over the
    /// last part of the drive's work the hull lunges along its nose into the light gathered ahead of it, drawn out
    /// as it goes, and is gone when the drive fires. At the far end, just before the transit is over, it streaks in
    /// from behind where it stops, drawn out, and settles there as the arrival flashes. Offset is along the nose
    /// from where the simulation has the hull, in metres; Stretch how many times its length it is drawn.
    /// <paramref name="tunnelSeconds"/> is the hull's own transit, or 0 for the drive's base one.
    /// </summary>
    internal static HyperspaceShape Hyperspace(JumpPhase phase, float progress, in FlightParams flight, float tunnelSeconds = 0f)
    {
        var radius = flight.Radius;
        if (phase is JumpPhase.Align or JumpPhase.Charge)
        {
            var start = phase == JumpPhase.Charge ? progress : 0f;
            var move = Math.Clamp((start - 0.85f) / 0.15f, 0f, 1f);
            return new HyperspaceShape(radius * HyperspaceLight * move * move * move, 1f + HyperspaceStretch * move * move, true);
        }

        var tunnel = Math.Max(tunnelSeconds > 0f ? tunnelSeconds : flight.FtlTunnelSeconds, 0.1f);
        var arrive = Math.Max(flight.FtlArriveSeconds, 0.1f);
        var since = phase == JumpPhase.Tunnel ? progress * tunnel : tunnel + progress * arrive;
        var lead = Math.Min(HyperspaceLead, tunnel);
        var u = Math.Clamp((since - (tunnel - lead)) / (lead + Math.Min(HyperspaceLand, arrive)), 0f, 1f);
        var rest = 1f - u;
        return new HyperspaceShape(-radius * HyperspaceApproach * rest * rest * rest, 1f + HyperspaceStretch * rest * rest, u > 0f);
    }

    /// <summary>
    /// How long a hull's transit lasts: the duration its jump latched, which grows with the distance, or the drive's
    /// base transit for a hull that has none.
    /// </summary>
    internal static float TunnelSeconds(in HullView hull, in FlightParams flight) =>
        hull.JumpTunnelSeconds > 0f && float.IsFinite(hull.JumpTunnelSeconds) ? hull.JumpTunnelSeconds : flight.FtlTunnelSeconds;

    internal static float Smooth(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }
}
