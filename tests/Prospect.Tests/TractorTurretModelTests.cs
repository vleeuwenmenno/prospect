using System.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The tractor beam's turret: a base in the hull mesh, and a yoke, a head and the head's glowing bars as parts posed
/// each frame. The head must swing clear of the yoke at every pitch, the beam leaves from the bars, and an idle turret
/// rests looking along the hull.
/// </summary>
public sealed class TractorTurretModelTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly PropulsionLook Look = PropulsionLook.Load();
    private static readonly TractorLook Tractor = Look.Tractor!;

    public static TheoryData<BlockFacing> AllFacings => new(Enum.GetValues<BlockFacing>());

    private static Vector3 P(MeshVertex v) => new(v.Position.X, v.Position.Y, v.Position.Z);

    private static float Tag(MeshVertex v) => new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z).Length();

    private static List<MeshVertex> Build(Action<List<MeshVertex>, List<uint>> build)
    {
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        build(vertices, indices);
        Assert.NotEmpty(indices);
        Assert.Equal(0, indices.Count % 3);
        Assert.All(indices, index => Assert.True(index < vertices.Count));
        return vertices;
    }

    [Theory]
    [InlineData(0.3f)]
    [InlineData(0.075f)]
    [InlineData(1.2f)]
    public void TheHeadSwingsClearOfTheYokeAndTheBeamLeavesFromItsBars(float small)
    {
        var r = small * Tractor.TurretBaseRadius;
        var yoke = Build((v, i) => PropulsionModels.BuildTractorYoke(small, Tractor, v, i));
        var head = Build((v, i) => PropulsionModels.BuildTractorHead(small, Tractor, v, i));
        var bars = Build((v, i) => PropulsionModels.BuildTractorBars(small, Tractor, v, i));
        var pivot = PropulsionModels.TractorPivotOffset(small, Tractor) - PropulsionModels.TractorCollarOffset(small, Tractor);
        Assert.Equal(r * Tractor.TurretYokeHeight, pivot, 5);

        // The yoke's turntable, the lowest thing the head could meet, and its arms either side of the head.
        var table = yoke.Where(v => MathF.Abs(v.Position.X) < r * 0.3f && v.Position.Y > -r * 0.2f).Max(v => v.Position.Z);
        var arms = yoke.Where(v => v.Position.Z > pivot - r * 0.3f).Min(v => MathF.Abs(v.Position.X));
        // The head turns about the pitch axis (x) through the pivot: what matters is how far it reaches from that axis
        // behind the pivot, and how wide it is.
        var swing = head.Where(v => v.Position.Z < 0).Max(v => MathF.Sqrt(v.Position.Y * v.Position.Y + v.Position.Z * v.Position.Z));
        Assert.True(swing < pivot - table, $"head swings {swing}, room {pivot - table}");
        Assert.True(head.Max(v => MathF.Abs(v.Position.X)) <= arms + 1e-5f);

        // The bars stand in front of the recessed face, inside the bezel, and are where the beam starts.
        var lens = PropulsionModels.TractorLensOffset(small, Tractor);
        Assert.Equal(lens, bars.Max(v => v.Position.Z), 5);
        Assert.True(lens < head.Max(v => v.Position.Z));
        // A bar is four walls of four vertices and a face of five.
        Assert.Equal(Tractor.TurretBars * 21, bars.Count);
        Assert.All(bars, v => Assert.True(MathF.Abs(v.Position.X) < r * Tractor.TurretHeadWidth && MathF.Abs(v.Position.Y) < r * Tractor.TurretHeadHeight));
    }

    [Theory]
    [MemberData(nameof(AllFacings))]
    public void TheBaseStaysOnItsFootprintWithItsGlowSlots(BlockFacing facing)
    {
        var type = Content.Blocks.Get("tractorBeam");
        var axis = Facings.Axis(facing);
        var half = new Vector3(0.3f);
        half[axis] = 0.1f;
        var vertices = Build((v, i) => PropulsionModels.BuildPart(type, new PartBox(Vector3.Zero, half, facing, 1f), Look, v, i));
        foreach (var vertex in vertices)
        {
            var p = P(vertex);
            Assert.InRange(MathF.Abs(p[(axis + 1) % 3]), 0f, 0.3f + 1e-4f);
            Assert.InRange(MathF.Abs(p[(axis + 2) % 3]), 0f, 0.3f + 1e-4f);
            // It stands on the block's face and is the low part: the yoke begins at the collar's top.
            var height = Vector3.Dot(p, Facings.Vector(facing)) + 0.1f;
            Assert.InRange(height, -1e-4f, PropulsionModels.TractorCollarOffset(0.3f, Tractor) + 1e-4f);
        }
        // One lit panel a slot, four vertices each.
        Assert.Equal(Tractor.TurretSlots * 4, vertices.Count(v => Tag(v) >= 4.5f));
    }

    [Theory]
    [MemberData(nameof(AllFacings))]
    public void AnIdleTurretLooksAlongTheHull(BlockFacing face)
    {
        WorldView.TurretRest(face, Tractor.TurretRestPitch, out var azimuth, out var pitch);
        Assert.Equal(Tractor.TurretRestPitch * MathF.PI / 180f, pitch, 5);
        // The aim as the turret's draw turns it: pitched off the normal about x, then turned about the normal.
        var aim = new Vector3(-MathF.Sin(azimuth) * MathF.Sin(pitch), MathF.Cos(azimuth) * MathF.Sin(pitch), MathF.Cos(pitch));
        var axis = Facings.Axis(face);
        var n = Facings.Vector(face);
        var u = Vector3.Zero;
        u[(axis + 1) % 3] = 1f;
        var v = Vector3.Cross(n, u);
        var world = u * aim.X + v * aim.Y + n * aim.Z;
        // Along the nose; on the nose or the stern there is no such way across the face, and it looks up.
        var ahead = axis == Facings.Axis(BlockFacing.Front) ? Vector3.UnitY : Facings.Vector(BlockFacing.Front);
        Assert.Equal(MathF.Sin(pitch), Vector3.Dot(world, ahead), 4);
        Assert.Equal(MathF.Cos(pitch), Vector3.Dot(world, n), 4);
    }

    [Fact]
    public void TheLookRefusesATurretThatWouldCutThroughItself()
    {
        Tractor.Check();
        var text = File.ReadAllText(Prospect.Engine.IO.AppPaths.Content("visuals", "propulsion.json"));
        var folder = Directory.CreateTempSubdirectory("prospect-turret-look");
        try
        {
            var path = Path.Combine(folder.FullName, "propulsion.json");
            // A head wider than the gap between the arms, one whose back would swing through the turntable, and a rest
            // past the pitch the head can reach.
            foreach (var (from, to) in new[] { ("\"turretHeadWidth\": 0.48", "\"turretHeadWidth\": 0.58"), ("\"turretYokeHeight\": 0.9", "\"turretYokeHeight\": 0.6"), ("\"turretRestPitch\": 64", "\"turretRestPitch\": 85") })
            {
                Assert.Contains(from, text);
                File.WriteAllText(path, text.Replace(from, to));
                Assert.Throws<InvalidDataException>(() => PropulsionLook.Load(path));
            }
        }
        finally
        {
            folder.Delete(true);
        }
    }
}
