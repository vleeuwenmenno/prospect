using System.Numerics;
using Prospect.Client.Render;
using Prospect.Engine.Jobs;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The work drone's own model, drawn in place of the design's blocks: it stays about the box the blocks fill, lists
/// the design's thrusters as nozzles on its pods, keeps its changing lights as parts of their own, and belongs to the
/// work drone's design alone.
/// </summary>
public sealed class WorkDroneModelTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly PropulsionLook Look = PropulsionLook.Load();
    private static readonly HullDesign Drone = Content.Designs[Content.WorkDroneDesign];

    private static Vector3 P(MeshVertex v) => new(v.Position.X, v.Position.Y, v.Position.Z);

    private static (Vector3 Centre, Vector3 Half) Bounds()
    {
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        for (var b = 0; b < Drone.BlockCount; b++)
        {
            low = Vector3.Min(low, Drone.BlockOffsets[b] - Drone.BlockHalfExtents[b]);
            high = Vector3.Max(high, Drone.BlockOffsets[b] + Drone.BlockHalfExtents[b]);
        }
        return (0.5f * (low + high), 0.5f * (high - low));
    }

    [Fact]
    public void TheLookComesFromContentAndRefusesBadFigures()
    {
        var look = Assert.IsType<WorkDroneLook>(Look.WorkDrone);
        look.Check("propulsion.json");
        Assert.Throws<InvalidDataException>(() => new WorkDroneLook().Check("empty"));
        var text = File.ReadAllText(Prospect.Engine.IO.AppPaths.Content("visuals", "propulsion.json"));
        var folder = Directory.CreateTempSubdirectory("prospect-drone-look");
        try
        {
            var path = Path.Combine(folder.FullName, "propulsion.json");
            // A marking the stroke font cannot write, a pod that runs into the nose cap, and light that dims with work.
            foreach (var (from, to) in new[] { ("\"marking\": \"MD-7\"", "\"marking\": \"md?7\""), ("\"podAt\": -0.46", "\"podAt\": 0.9"), ("\"carryGlow\": 1.6", "\"carryGlow\": 0.1") })
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

    [Fact]
    public void TheModelStaysAboutTheBoxItsBlocksFill()
    {
        var mesh = MeshBuilder.WorkDrone(Drone, Look);
        var (centre, half) = Bounds();
        Assert.InRange(mesh.Vertices.Count, 400, 6000);
        Assert.Equal(0, mesh.Indices.Count % 3);
        foreach (var index in mesh.Indices)
            Assert.True(index < mesh.Vertices.Count);
        var reach = Vector3.Zero;
        foreach (var vertex in mesh.Vertices)
            reach = Vector3.Max(reach, Vector3.Abs(P(vertex) - centre));
        // As wide as the blocks, a little longer and flatter; only the antenna stands above them.
        Assert.InRange(reach.X / half.X, 0.9f, 1.02f);
        Assert.InRange(reach.Z / half.Z, 1.0f, 1.45f);
        Assert.InRange(reach.Y / half.Y, 0.5f, 1.3f);
        Assert.InRange(mesh.BoundingRadius / Drone.Flight.Radius, 0.7f, 1.2f);
        // Nothing of it is ordinary plating: every facet carries a paint, a bare metal or a light of its own.
        Assert.DoesNotContain(mesh.Vertices, vertex => MathF.Abs(new Vector3(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z).Length() - 1f) < 0.01f);
    }

    [Fact]
    public void EveryThrusterOfTheDesignFiresFromAPod()
    {
        var mesh = MeshBuilder.WorkDrone(Drone, Look);
        var (centre, half) = Bounds();
        var look = Look.WorkDrone!;
        var thrusters = 0;
        for (var m = 0; m < Drone.Mounts.Length; m++)
            if (Drone.Mounts[m].Type.Thrust > 0) thrusters++;
        Assert.True(thrusters > 0);
        Assert.Equal(thrusters, mesh.Nozzles.Length);
        Span<int> perFacing = stackalloc int[Facings.Count];
        var used = new bool[Drone.Mounts.Length];
        for (var i = 0; i < mesh.Nozzles.Length; i++)
        {
            var nozzle = mesh.Nozzles[i];
            Assert.Equal(i, nozzle.Index);
            Assert.Equal(FuelKind.None, nozzle.Fuel);
            Assert.True(nozzle.Plume >= 0 && nozzle.ExitRadius > 0);
            // It stands for one mount of the design: the same block, pushing the same way.
            var mount = -1;
            for (var m = 0; m < Drone.Mounts.Length && mount < 0; m++)
                if (!used[m] && Drone.Mounts[m].Parent == nozzle.Block && Drone.Mounts[m].Face == nozzle.Facing && Drone.Mounts[m].Type.Thrust > 0) mount = m;
            Assert.True(mount >= 0);
            used[mount] = true;
            perFacing[(int)nozzle.Facing]++;
            // On a pod: outside the body's side, within the model's width.
            var across = MathF.Abs(nozzle.Exit.X - centre.X);
            Assert.InRange(across, half.X * look.Width, half.X * (look.Width + look.PodWidth) + 1e-4f);
            // A sideways thruster is on the pod of the side it fires from.
            if (Facings.Axis(nozzle.Facing) == 0)
                Assert.Equal(Facings.Sign(nozzle.Facing), MathF.Sign(nozzle.Exit.X - centre.X));
        }
        // No two nozzles share a place.
        for (var i = 0; i < mesh.Nozzles.Length; i++)
            for (var j = i + 1; j < mesh.Nozzles.Length; j++)
                Assert.True(Vector3.Distance(mesh.Nozzles[i].Exit, mesh.Nozzles[j].Exit) > 1e-3f || mesh.Nozzles[i].Facing != mesh.Nozzles[j].Facing);
    }

    [Fact]
    public void TheLightsThatChangeArePartsOfTheirOwnOffTheModel()
    {
        var look = Look.WorkDrone!;
        var (centre, half) = Bounds();
        var array = new List<MeshVertex>();
        var arrayIndices = new List<uint>();
        var strips = new List<MeshVertex>();
        var stripIndices = new List<uint>();
        PropulsionModels.BuildWorkDroneGlow(Drone, look, array, arrayIndices, strips, stripIndices);
        Assert.Equal(look.BellySegments * 12, arrayIndices.Count);
        Assert.Equal(2 * 12, stripIndices.Count);
        var body = MeshBuilder.WorkDrone(Drone, Look);
        var lowest = body.Vertices.Min(vertex => vertex.Position.Y);
        var widest = body.Vertices.Max(vertex => MathF.Abs(vertex.Position.X - centre.X));
        // The array hangs under everything the model has, the strips stand off its sides: neither lies in a surface.
        Assert.All(array, vertex => Assert.True(vertex.Position.Y < lowest));
        Assert.All(strips, vertex => Assert.True(MathF.Abs(vertex.Position.X - centre.X) > widest));
        Assert.All(array, vertex => Assert.True(MathF.Abs(vertex.Position.X - centre.X) < half.X * look.Width));
    }

    [Fact]
    public void TheArrayBrightensWithCarryingAndTheStripsWithThrust()
    {
        var look = Look.WorkDrone!;
        // Only what the server says is carrying lights the array; nearness to a loose body does not.
        Assert.Equal(look.IdleGlow, look.ArrayLevel(false, 0.37f));
        for (var time = 0f; time < 3f; time += 0.05f)
        {
            Assert.InRange(look.ArrayLevel(true, time), look.CarryGlow * (1f - look.PulseDepth) - 1e-4f, look.CarryGlow * (1f + look.PulseDepth) + 1e-4f);
            Assert.True(look.ArrayLevel(true, time) > look.IdleGlow);
        }
        // A beam from the drone starts in the array's own light: on the tray's glowing plane, among its segments, under
        // the body rather than in a surface.
        var (centre, half) = Bounds();
        var array = PropulsionModels.WorkDroneArrayPoint(Drone, look);
        var vertices = new List<MeshVertex>();
        var stripVertices = new List<MeshVertex>();
        PropulsionModels.BuildWorkDroneGlow(Drone, look, vertices, [], stripVertices, []);
        Assert.All(vertices, vertex => Assert.Equal(array.Y, vertex.Position.Y, 4));
        Assert.InRange(array.X, vertices.Min(vertex => vertex.Position.X), vertices.Max(vertex => vertex.Position.X));
        Assert.InRange(array.Z, vertices.Min(vertex => vertex.Position.Z), vertices.Max(vertex => vertex.Position.Z));
        Assert.True(array.Y < centre.Y - half.Y * look.Height);
        Assert.Equal(look.IdleGlow, look.StripLevel(0f));
        Assert.Equal(look.ThrustGlow, look.StripLevel(1f));
        Assert.Equal(look.ThrustGlow, look.StripLevel(3f));
        Assert.True(look.StripLevel(0.5f) > look.IdleGlow && look.StripLevel(0.5f) < look.ThrustGlow);
    }

    [Fact]
    public void OnlyTheWorkDronesDesignIsBuiltAsTheModel()
    {
        var designs = Content.Designs;
        using var jobs = new JobSystem(1, "Meshes");
        var preparation = new CatalogueMeshes(jobs);
        Assert.Null(preparation.Prepare(designs, Content.WorkDroneDesign));
        jobs.CompleteAll();
        var meshes = Assert.IsType<MeshData[]>(preparation.Prepare(designs, Content.WorkDroneDesign));
        for (var i = 0; i < designs.Length; i++)
        {
            var expected = i == Content.WorkDroneDesign ? MeshBuilder.WorkDrone(designs[i]) : MeshBuilder.Hull(designs[i]);
            Assert.Equal(expected.Vertices, meshes[i].Vertices);
            Assert.Equal(expected.Indices, meshes[i].Indices);
            Assert.Equal(expected.Nozzles, meshes[i].Nozzles);
        }
        Assert.NotEqual(MeshBuilder.Hull(Drone).Vertices.Count, meshes[Content.WorkDroneDesign].Vertices.Count);
        // Without a look for it, the work drone is its blocks like any hull.
        var plain = new PropulsionLook { Models = Look.Models, Plumes = Look.Plumes };
        Assert.Equal(MeshBuilder.Hull(Drone, plain).Vertices, MeshBuilder.WorkDrone(Drone, plain).Vertices);
    }
}
