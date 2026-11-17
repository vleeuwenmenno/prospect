using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The camera as the hologram needs it: where it is, its axes, and how a point in front of it lands on the window. A
/// copy of what <c>WorldView.Project</c> works from, so the hologram's geometry can be built and tested without a
/// view. <see cref="Project"/> is that method's arithmetic exactly.
/// </summary>
internal readonly struct HologramCamera(Double3 position, Vector3 right, Vector3 up, Vector3 forward, float tanHalfFov, Vector2 screen)
{
    /// <summary>The nearest a point may be along the view and still be drawn, as the world's own projection has it.</summary>
    public const float NearPlane = 0.25f;

    public Double3 Position { get; } = position;

    /// <summary>A world point in metres along the camera's right, up and forward axes.</summary>
    public Vector3 ToView(Double3 world)
    {
        var relative = world.RelativeTo(Position);
        return new Vector3(Vector3.Dot(relative, right), Vector3.Dot(relative, up), Vector3.Dot(relative, forward));
    }

    /// <summary>Where a point given by <see cref="ToView"/> lands on the window; false when it is nearer than <see cref="NearPlane"/>.</summary>
    public bool Project(Vector3 view, out Vector2 at)
    {
        if (view.Z < NearPlane)
        {
            at = default;
            return false;
        }

        var aspect = screen.X / screen.Y;
        var x = view.X / (view.Z * tanHalfFov * aspect);
        var y = -view.Y / (view.Z * tanHalfFov);
        at = new Vector2((x * 0.5f + 0.5f) * screen.X, (y * 0.5f + 0.5f) * screen.Y);
        return true;
    }

    /// <summary>How many window pixels one metre is across at a distance along the view.</summary>
    public float PixelsPerMetre(float depth) => screen.Y * 0.5f / (depth * tanHalfFov);
}

/// <summary>What a heat dot says of the surface under it.</summary>
internal enum HologramDot : byte
{
    /// <summary>No ore near the surface here, and nothing unstable.</summary>
    Faint,

    /// <summary>Rich ore lies near the surface here.</summary>
    Easy,

    /// <summary>An unstable pocket lies under this part of the surface.</summary>
    Unstable,
}

/// <summary>
/// A detailed scan's map as primitives on the window: veins as thick line segments, pockets as circles, the heat map as
/// dots, each with a strength that drops on the far side of the rock's centre, so what is behind the rock reads as
/// behind it. Built from one record, the rock's pose and the camera into fixed buffers, so it takes no memory; the
/// interface only draws what is here. A record is the server's result and nothing else is read: the rock's shape, ore
/// and interior are not.
/// </summary>
internal sealed class ScanHologram
{
    /// <summary>The most segments: one fewer than the points of every vein.</summary>
    public const int MaxSegments = RockInteriorRules.MaxVeins * (RockInteriorRules.MaxVeinPoints - 1);

    /// <summary>A piece of a vein between two of its points.</summary>
    public struct Segment
    {
        public Vector2 A, B;

        /// <summary>The thickness on the window, in pixels.</summary>
        public float Width;

        /// <summary>The vein's ore, as a resource index; -1 for none.</summary>
        public int Resource;

        /// <summary>From <see cref="DroneScanLook.FarStrength"/> (far side) to 1 (near side).</summary>
        public float Strength;
    }

    /// <summary>A pocket as a circle.</summary>
    public struct Pocket
    {
        public Vector2 Centre;
        public float Radius;
        public bool Unstable;
        public int Resource;
        public float Strength;
    }

    /// <summary>A heat cell as a dot on the surface.</summary>
    public struct Dot
    {
        public Vector2 At;
        public HologramDot Kind;

        /// <summary>For an easy cell, how near the surface its ore is (0 to 1); one otherwise.</summary>
        public float Ease;
        public float Strength;
    }

    public readonly Segment[] Segments = new Segment[MaxSegments];
    public readonly Pocket[] Pockets = new Pocket[RockInteriorRules.MaxPockets];
    public readonly Dot[] Dots = new Dot[DroneScanning.HeatCells];

    public int SegmentCount { get; private set; }
    public int PocketCount { get; private set; }
    public int DotCount { get; private set; }

    /// <summary>
    /// Builds the primitives for <paramref name="record"/> of a rock at <paramref name="centre"/> turned by
    /// <paramref name="rotation"/>. False, with nothing built, for a survey or a rock wholly behind the camera.
    /// </summary>
    public bool Build(in DroneScanRecord record, in HologramCamera camera, Double3 centre, Quaternion rotation, ModuleLook.DroneScanLook look)
    {
        SegmentCount = PocketCount = DotCount = 0;
        if (record.Kind != DroneScanKind.Detailed) return false;
        var middle = camera.ToView(centre);
        if (middle.Z + record.Radius < HologramCamera.NearPlane) return false;
        ref readonly var map = ref record.Map;

        for (var v = 0; v < map.VeinCount && v < RockInteriorRules.MaxVeins; v++)
        {
            var points = Math.Min((int)map.Veins[v].Points, RockInteriorRules.MaxVeinPoints);
            var havePrevious = false;
            Vector2 previous = default;
            float previousWidth = 0, previousStrength = 0;
            for (var k = 0; k < points; k++)
            {
                var p = record.VeinPoint(v, k);
                var view = camera.ToView(centre + Vector3.Transform(new Vector3(p.X, p.Y, p.Z), rotation));
                var shown = camera.Project(view, out var at);
                var width = shown ? Math.Clamp(p.W * 2f * camera.PixelsPerMetre(view.Z), look.VeinMinPixels, look.VeinMaxPixels) : 0f;
                var strength = Strength(view.Z, middle.Z, record.Radius, look);
                if (shown && havePrevious && SegmentCount < Segments.Length)
                    Segments[SegmentCount++] = new Segment
                    {
                        A = previous,
                        B = at,
                        Width = (previousWidth + width) * 0.5f,
                        Resource = map.Veins[v].Resource,
                        Strength = (previousStrength + strength) * 0.5f,
                    };
                havePrevious = shown;
                previous = at;
                previousWidth = width;
                previousStrength = strength;
            }
        }

        for (var q = 0; q < map.PocketCount && q < RockInteriorRules.MaxPockets; q++)
        {
            var p = record.PocketAt(q);
            var view = camera.ToView(centre + Vector3.Transform(new Vector3(p.X, p.Y, p.Z), rotation));
            if (!camera.Project(view, out var at)) continue;
            var pocket = map.Pockets[q];
            Pockets[PocketCount++] = new Pocket
            {
                Centre = at,
                Radius = Math.Max(look.PocketMinPixels * 0.5f, p.W * camera.PixelsPerMetre(view.Z)),
                Unstable = pocket.Unstable,
                Resource = pocket.Resource,
                Strength = Strength(view.Z, middle.Z, record.Radius, look),
            };
        }

        for (var cell = 0; cell < DroneScanning.HeatCells; cell++)
        {
            var view = camera.ToView(centre + Vector3.Transform(record.HeatPoint(cell), rotation));
            if (!camera.Project(view, out var at)) continue;
            var ease = map.Ease(cell);
            Dots[DotCount++] = new Dot
            {
                At = at,
                Kind = map.Unstable(cell) ? HologramDot.Unstable : ease >= look.EasyThreshold ? HologramDot.Easy : HologramDot.Faint,
                Ease = ease,
                Strength = Strength(view.Z, middle.Z, record.Radius, look),
            };
        }

        return true;
    }

    /// <summary>
    /// How strongly a point at <paramref name="depth"/> along the view is drawn, for a rock whose centre is at
    /// <paramref name="centreDepth"/>: full on the near side, falling in a straight line to
    /// <see cref="DroneScanLook.FarStrength"/> a radius behind the centre, so a vein that runs round the rock fades
    /// as it goes behind it.
    /// </summary>
    public static float Strength(float depth, float centreDepth, float radius, ModuleLook.DroneScanLook look)
    {
        var behind = radius > 0f ? Math.Clamp(0.5f + (depth - centreDepth) / (2f * radius), 0f, 1f) : 0f;
        return 1f + (look.FarStrength - 1f) * behind;
    }
}
