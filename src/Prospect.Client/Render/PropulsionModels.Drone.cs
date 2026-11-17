using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Render;

internal static partial class PropulsionModels
{
    // How far a glowing strip stands off the dark glass under it, as a share of the body's half height.
    private const float DroneGlowLift = 0.02f;

    /// <summary>The measures of a work drone's model, worked out once from the design's block bounds and the look.</summary>
    private readonly struct DroneFrame
    {
        public readonly Vector3 Centre, Front;
        public readonly float L, W, H, PodOut, PodLength, PodHeight, PodAt, Belly;

        public DroneFrame(HullDesign design, WorkDroneLook look)
        {
            var low = new Vector3(float.MaxValue);
            var high = new Vector3(float.MinValue);
            for (var b = 0; b < design.BlockOffsets.Length; b++)
            {
                low = Vector3.Min(low, design.BlockOffsets[b] - design.BlockHalfExtents[b]);
                high = Vector3.Max(high, design.BlockOffsets[b] + design.BlockHalfExtents[b]);
            }
            Centre = 0.5f * (low + high);
            var half = 0.5f * (high - low);
            Front = Facings.Vector(BlockFacing.Front);
            L = half.Z * look.Length;
            W = half.X * look.Width;
            H = half.Y * look.Height;
            PodOut = half.X * look.PodWidth;
            PodLength = L * look.PodLength;
            PodHeight = H * look.PodHeight;
            PodAt = L * look.PodAt;
            Belly = H * look.Belly;
        }

        /// <summary>A point given across (right), up and along (toward the nose) from the model's centre.</summary>
        public Vector3 At(float x, float y, float z) => Centre + new Vector3(x, y, 0f) + Front * z;

        /// <summary>The frame of a pod: x along the drone, y up, z out of the body's side.</summary>
        public Frame Pod(int side) => new(At(side * W, 0f, PodAt), Vector3.UnitX * side, Front, Vector3.UnitY, 0f);

        /// <summary>The frame of the tractor array's tray: x across, y along the drone, z down from the body's underside.</summary>
        public Frame Tray() => new(At(0f, -H, 0.3f * L), -Vector3.UnitY, Vector3.UnitX, Front, 0f);
    }

    /// <summary>
    /// Builds a work drone as one model about the design's block bounds, in the hull's frame: a chamfered body with dark
    /// end caps and accent panels, a sensor eye with a light either side on the nose, an antenna with a tip light and a
    /// round utility mount on top, a thruster pod on each side and the tractor array's tray under the nose. The array's
    /// segments and the pods' strips are dark glass here; their light is <see cref="BuildWorkDroneGlow"/>. The nozzles
    /// of the design's thrusters are listed on the pods, each firing the way its mount faces, so plumes leave the model
    /// and not the blocks it replaces. Collision, picking and the server know the blocks only. Built on a worker.
    /// </summary>
    public static void BuildWorkDrone(HullDesign design, PropulsionLook propulsion, WorkDroneLook look, List<MeshVertex> vertices, List<uint> indices, List<ThrustNozzle>? nozzles)
    {
        var f = new DroneFrame(design, look);
        var g = new Geometry(vertices, indices, new Frame(f.Centre, f.Front, Vector3.UnitX, Vector3.UnitY, 0f));
        var (l, w, h) = (f.L, f.W, f.H);
        var body = Paint(look.Body);
        var dark = Paint(look.Dark);
        var accent = Paint(look.Accent);
        var glass = Tag(0.03f);
        var metal = Tag(0.3f);
        var eyeLight = Emissive(RackLook.Rgb(look.EyeLight));
        var light = Emissive(RackLook.Rgb(look.Light));

        // The body, lofted along the drone. Face 1 of a section is the top, 5 the underside, 3 and 7 the sides, the
        // even ones the cut edges between.
        var cut = look.Chamfer * h;
        Span<Vector2> section = stackalloc Vector2[8];
        Span<Vector2> cap = stackalloc Vector2[8];
        Span<Vector2> nose = stackalloc Vector2[8];
        Octagon(section, w, h, cut);
        Octagon(cap, w * look.CapScale, h * look.CapScale, cut * look.CapScale);
        Octagon(nose, w * look.CapScale * 0.84f, h * look.CapScale * 0.8f, cut * look.CapScale * 0.8f);
        var capLength = look.CapLength * l;
        var rear = -l + capLength;
        var front = l - capLength;
        var seam = 0.012f * l;
        var afterDeck = -0.4f * l;
        var shoulder = 0.44f * l;

        g.Cap(cap, Vector2.Zero, -l, -1f, dark);
        g.Loft(cap, -l, cap, rear, Vector2.Zero, dark, 1f);
        g.Annulus(section, cap, Vector2.Zero, rear, -1f, dark);
        Span<float> plain = [body, body, body, body, dark, dark, dark, body];
        Span<float> deck = [accent, accent, accent, body, dark, dark, dark, body];
        Span<float> sides = [accent, body, accent, accent, dark, dark, dark, accent];
        Span<float> line = [dark, dark, dark, dark, dark, dark, dark, dark];
        g.Loft(section, rear, section, afterDeck - seam, Vector2.Zero, deck, 1f);
        g.Loft(section, afterDeck - seam, section, afterDeck + seam, Vector2.Zero, line, 1f);
        g.Loft(section, afterDeck + seam, section, shoulder - seam, Vector2.Zero, plain, 1f);
        g.Loft(section, shoulder - seam, section, shoulder + seam, Vector2.Zero, line, 1f);
        g.Loft(section, shoulder + seam, section, front, Vector2.Zero, sides, 1f);
        g.Annulus(section, cap, Vector2.Zero, front, 1f, dark);
        g.Loft(cap, front, nose, l, Vector2.Zero, dark, 1f);
        g.Cap(nose, Vector2.Zero, l, 1f, dark);

        // The sensor eye: a ring standing on the nose, dark glass in it and a lit pupil, with a light either side.
        var eye = look.Eye * h;
        Span<Vector2> ring = stackalloc Vector2[12];
        Span<Vector2> lens = stackalloc Vector2[12];
        Span<Vector2> pupil = stackalloc Vector2[12];
        Polygon(ring, Vector2.Zero, eye, 0f);
        Polygon(lens, Vector2.Zero, eye * 0.8f, 0f);
        Polygon(pupil, Vector2.Zero, eye * 0.36f, 0f);
        var ringTop = l + 0.035f * l;
        g.Loft(ring, l, ring, ringTop, Vector2.Zero, metal, 1f);
        g.Annulus(ring, lens, Vector2.Zero, ringTop, 1f, metal);
        g.Loft(lens, l + 0.012f * l, lens, ringTop, Vector2.Zero, glass, -1f);
        g.Cap(lens, Vector2.Zero, l + 0.012f * l, 1f, glass);
        g.Cap(pupil, Vector2.Zero, l + 0.018f * l, 1f, eyeLight);
        var lampAt = 0.5f * (eye + w * look.CapScale * 0.84f);
        for (var side = -1; side <= 1; side += 2)
            Slab(vertices, indices, f.At(side * lampAt, 0f, l), new Vector3(0.1f * eye, 0.62f * eye, 0.02f * l), light);

        // The antenna: a foot, a mast and a lit tip, on the nose's port quarter.
        var up = new Geometry(vertices, indices, new Frame(f.At(-0.5f * w, h, 0.56f * l), Vector3.UnitY, Vector3.UnitX, f.Front, 0f));
        Span<Vector2> foot = stackalloc Vector2[4];
        Span<Vector2> neck = stackalloc Vector2[4];
        Span<Vector2> mast = stackalloc Vector2[4];
        Span<Vector2> tip = stackalloc Vector2[4];
        Rectangle(foot, 0.3f * h, 0.3f * h);
        Rectangle(neck, 0.13f * h, 0.13f * h);
        Rectangle(mast, 0.045f * h, 0.045f * h);
        Rectangle(tip, 0.075f * h, 0.075f * h);
        var antenna = look.Antenna * h;
        up.Loft(foot, 0f, neck, 0.28f * h, Vector2.Zero, dark, 1f);
        up.Cap(neck, Vector2.Zero, 0.28f * h, 1f, dark);
        up.Loft(mast, 0.28f * h, mast, antenna, Vector2.Zero, metal, 1f);
        up.Loft(tip, antenna, tip, antenna + 0.15f * h, Vector2.Zero, light, 1f);
        up.Cap(tip, Vector2.Zero, antenna, -1f, light);
        up.Cap(tip, Vector2.Zero, antenna + 0.15f * h, 1f, light);

        // The utility mount: a low round socket on the after deck, an accent ring in it round a dark hub.
        var socket = new Geometry(vertices, indices, new Frame(f.At(0f, h, -0.05f * l), Vector3.UnitY, Vector3.UnitX, f.Front, 0f));
        var radius = look.Mount * w;
        Span<Vector2> outer = stackalloc Vector2[12];
        Span<Vector2> band = stackalloc Vector2[12];
        Span<Vector2> hub = stackalloc Vector2[12];
        Span<Vector2> core = stackalloc Vector2[12];
        Polygon(outer, Vector2.Zero, radius, 0f);
        Polygon(band, Vector2.Zero, radius * 0.74f, 0f);
        Polygon(hub, Vector2.Zero, radius * 0.5f, 0f);
        Polygon(core, Vector2.Zero, radius * 0.28f, 0f);
        socket.Loft(outer, 0f, outer, 0.1f * h, Vector2.Zero, dark, 1f);
        socket.Annulus(outer, band, Vector2.Zero, 0.1f * h, 1f, dark);
        socket.Annulus(band, hub, Vector2.Zero, 0.1f * h, 1f, accent);
        socket.Annulus(hub, core, Vector2.Zero, 0.1f * h, 1f, dark);
        socket.Loft(core, 0.1f * h, core, 0.16f * h, Vector2.Zero, metal, 1f);
        socket.Cap(core, Vector2.Zero, 0.16f * h, 1f, glass);

        // The thruster pods: a chamfered box out of each side, dark glass on its outer face under the strip.
        Span<Vector2> root = stackalloc Vector2[8];
        Span<Vector2> face = stackalloc Vector2[8];
        Span<Vector2> strip = stackalloc Vector2[4];
        Octagon(root, f.PodLength, f.PodHeight, 0.3f * f.PodHeight);
        Octagon(face, f.PodLength * 0.88f, f.PodHeight * 0.8f, 0.26f * f.PodHeight);
        Rectangle(strip, f.PodLength * 0.56f, f.PodHeight * 0.3f);
        for (var side = -1; side <= 1; side += 2)
        {
            var pod = new Geometry(vertices, indices, f.Pod(side));
            pod.Loft(root, 0f, root, f.PodOut * 0.72f, Vector2.Zero, dark, 1f);
            pod.Loft(root, f.PodOut * 0.72f, face, f.PodOut, Vector2.Zero, dark, 1f);
            pod.Cap(face, Vector2.Zero, f.PodOut, 1f, dark);
            pod.Cap(strip, Vector2.Zero, f.PodOut + 0.5f * DroneGlowLift * h, 1f, glass);
        }

        // The tractor array's tray under the nose, its underside dark glass under the segments.
        var tray = new Geometry(vertices, indices, f.Tray());
        Span<Vector2> trayTop = stackalloc Vector2[8];
        Span<Vector2> trayFloor = stackalloc Vector2[8];
        Octagon(trayTop, 0.8f * w, 0.46f * l, 0.2f * w);
        Octagon(trayFloor, 0.64f * w, 0.38f * l, 0.14f * w);
        tray.Loft(trayTop, 0f, trayFloor, f.Belly, Vector2.Zero, dark, 1f);
        tray.Cap(trayFloor, Vector2.Zero, f.Belly, 1f, glass);

        // The marking on both sides, read from outside, between the pod and the shoulder.
        if (look.Marking.Length > 0)
        {
            var from = MathF.Max(afterDeck, f.PodAt + f.PodLength) + 3f * seam;
            var to = shoulder - 3f * seam;
            var depth = 0.03f * h;
            for (var side = -1; side <= 1; side += 2)
            {
                var normal = Vector3.UnitX * side;
                // Seen from outside, text runs toward the viewer's right: aft on the starboard side, forward on port.
                var right = Vector3.Cross(-normal, Vector3.UnitY);
                Label(vertices, indices, look.Marking, f.At(side * w, 0f, 0.5f * (from + to)) - normal * depth, new Vector2(0.5f * (to - from), 0.34f * (h - cut)),
                    normal, right, Vector3.UnitY, depth, 0.16f, dark);
            }
        }

        if (nozzles is null)
            return;
        // Two thrusters push each way: sideways ones sit fore and aft on that side's pod, the others one a pod.
        Span<int> seen = stackalloc int[Facings.Count];
        for (var m = 0; m < design.Mounts.Length; m++)
        {
            var part = design.Mounts[m];
            if (part.Type.Thrust <= 0 || !propulsion.Models.TryGetValue(part.Type.Id, out var model))
                continue;
            var k = seen[(int)part.Face]++;
            var axis = Facings.Axis(part.Face);
            var sign = Facings.Sign(part.Face);
            var alternate = (k & 1) == 0 ? 1 : -1;
            var exit = axis switch
            {
                0 => f.Pod(sign).At(alternate * 0.78f * f.PodLength, 0f, f.PodOut),
                1 => f.Pod(alternate).At(0f, sign * f.PodHeight, 0.5f * f.PodOut),
                _ => f.Pod(alternate).At(Vector3.Dot(Facings.Vector(part.Face), f.Front) * f.PodLength, 0f, 0.5f * f.PodOut),
            };
            var bore = 0.2f * f.PodHeight;
            nozzles.Add(new ThrustNozzle(part.Parent, part.Type.Fuel, part.Face, exit, bore, 0.5f * bore, model.PlumeSlot, nozzles.Count));
        }
    }

    /// <summary>
    /// Builds the two lit parts of a work drone, in the hull's frame: the segments of the tractor array under the nose
    /// and the strip on each pod. Each is drawn as light of its own brightness, a little off the dark glass
    /// <see cref="BuildWorkDrone"/> leaves under it. Built on a worker.
    /// </summary>
    public static void BuildWorkDroneGlow(HullDesign design, WorkDroneLook look, List<MeshVertex> array, List<uint> arrayIndices, List<MeshVertex> strips, List<uint> stripIndices)
    {
        var f = new DroneFrame(design, look);
        var lift = DroneGlowLift * f.H;
        var tray = new Geometry(array, arrayIndices, f.Tray());
        var room = 2f * 0.64f * f.W * 0.86f;
        var pitch = room / look.BellySegments;
        Span<Vector2> cell = stackalloc Vector2[4];
        for (var k = 0; k < look.BellySegments; k++)
        {
            var centre = new Vector2((k + 0.5f) * pitch - 0.5f * room, 0.04f * f.L);
            Rectangle(cell, 0.4f * pitch, 0.24f * f.L);
            for (var c = 0; c < 4; c++)
                cell[c] += centre;
            tray.Cap(cell, centre, f.Belly + lift, 1f, 1f);
        }

        Span<Vector2> strip = stackalloc Vector2[4];
        Rectangle(strip, f.PodLength * 0.5f, f.PodHeight * 0.22f);
        for (var side = -1; side <= 1; side += 2)
            new Geometry(strips, stripIndices, f.Pod(side)).Cap(strip, Vector2.Zero, f.PodOut + lift, 1f, 1f);
    }
}
