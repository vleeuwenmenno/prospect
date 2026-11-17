using System.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.Jobs;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The models and plumes of thrust parts: a model fits its block at any size and facing, keeps its nozzle when the
/// block only grows deeper, and the plumes follow what a hull fires, igniting once, within a fixed budget, taking no
/// memory in a frame.
/// </summary>
public sealed class PropulsionVisualTests
{
    private static readonly GameContent Content = FleetContent.Value;
    private static readonly PropulsionLook Look = PropulsionLook.Load();
    private static readonly GridSize One = new(1, 1, 1);
    private static readonly BlockFacing[] Directions = [BlockFacing.Right, BlockFacing.Left, BlockFacing.Up, BlockFacing.Down, BlockFacing.Back, BlockFacing.Front];

    private static HullDesign Design(string type, BlockFacing facing, GridSize size) =>
        TestDesigns.Of([new DesignBox(new GridSize(0, 0, 0), One, Content.Blocks.Get("core")),
            new DesignBox(new GridSize(1, 0, 0), size, Content.Blocks.Get(type), BlockShape.Box, facing)]);

    [Fact]
    public void EveryThrustPartShipsWithAModelAndAPlume()
    {
        foreach (var type in new[] { "engine", "hotThruster", "coldGasThruster", "microengine", "microthruster", "ionDrive", "ionThruster", "microIonDrive", "microIonThruster" })
        {
            Assert.True(Content.Blocks.Get(type).Thrust > 0, type);
            Assert.True(Look.Models.TryGetValue(type, out var model), type);
            Assert.True(Look.PlumeIndex(model!.Plume) >= 0, type);
        }
        Assert.True(Look.PlumeOrder[Look.PlumeIndex("cold")].IsPuff);
        Assert.False(Look.PlumeOrder[Look.PlumeIndex("engine")].IsPuff);

        // Ion parts end in a grid and show the steady ion glow: no sparks, shock node, flicker or burst.
        foreach (var type in new[] { "ionDrive", "ionThruster", "microIonDrive", "microIonThruster" })
        {
            Assert.True(Look.Models[type].Grid > 0, type);
            var plume = Look.PlumeOrder[Look.PlumeIndex(Look.Models[type].Plume)];
            Assert.True(plume.IsIon, type);
            Assert.Equal((0, 0f, 0f, 0f), (plume.Sparks, plume.Diamonds, plume.Flicker, plume.Burst));
        }
    }

    /// <summary>A core and, apart from it, a block of plating with one thruster mounted in the middle of a face.</summary>
    private static HullDesign Mounted(string type, BlockFacing face) =>
        TestDesigns.Mounted(
            [new DesignBox(new GridSize(0, 0, 0), One, Content.Blocks.Get("core")), new DesignBox(new GridSize(2, 0, 0), One, Content.Blocks.Get("hull"))],
            [new DesignMount(1, face, 3, 3, Content.Blocks.Get(type), Size: FaceMounts.DefaultSize(Content.Blocks.Get(type)))]);

    [Theory]
    [InlineData("hotThruster")]
    [InlineData("coldGasThruster")]
    [InlineData("microthruster")]
    [InlineData("ionThruster")]
    [InlineData("microIonThruster")]
    public void AMountedThrustersModelStaysInsideItsSmallBoxAndPointsOutOfItsFace(string type)
    {
        foreach (var face in Directions)
        {
            var design = Mounted(type, face);
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            var nozzles = new List<ThrustNozzle>();
            Assert.True(PropulsionModels.AddMount(design, 0, Look, vertices, indices, nozzles));
            Assert.NotEmpty(indices);
            var axis = Facings.Axis(face);

            // The box stands 0.2 of a block out of the face it sits on, and the model is all inside it.
            var half = design.MountHalfExtents[0];
            Assert.Equal(0.1f, half[axis], 5);
            Assert.Equal(design.BlockOffsets[1][axis] + Facings.Sign(face) * 0.6f, design.MountOffsets[0][axis], 5);
            var lo = design.MountOffsets[0] - half - new Vector3(1e-4f);
            var hi = design.MountOffsets[0] + half + new Vector3(1e-4f);
            foreach (var v in vertices)
            {
                var p = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
                Assert.True(p.X >= lo.X && p.Y >= lo.Y && p.Z >= lo.Z && p.X <= hi.X && p.Y <= hi.Y && p.Z <= hi.Z, $"{type} {face}: {p}");
            }

            for (var i = 0; i < indices.Count; i += 3)
            {
                var a = vertices[(int)indices[i]];
                var b = vertices[(int)indices[i + 1]];
                var c = vertices[(int)indices[i + 2]];
                var pa = new Vector3(a.Position.X, a.Position.Y, a.Position.Z);
                var cross = Vector3.Cross(new Vector3(b.Position.X, b.Position.Y, b.Position.Z) - pa, new Vector3(c.Position.X, c.Position.Y, c.Position.Z) - pa);
                Assert.True(Vector3.Dot(cross, new Vector3(a.Normal.X, a.Normal.Y, a.Normal.Z)) > 0, $"{type} {face} triangle {i / 3}");
            }

            // Its nozzles (four for a two by two cold gas array, one for the others) at the outer end of the box, pointing out
            // of the face. They are lit and lost with the block it sits on, so that is the block they name.
            Assert.Equal(type == "coldGasThruster" ? 4 : 1, nozzles.Count);
            foreach (var nozzle in nozzles)
            {
                Assert.Equal(face, nozzle.Facing);
                Assert.Equal(1, nozzle.Block);
                Assert.Equal(Content.Blocks.Get(type).Fuel, nozzle.Fuel);
                Assert.Equal(design.MountOffsets[0][axis] + Facings.Sign(face) * half[axis], nozzle.Exit[axis], 4);
                Assert.True(nozzle.ExitRadius > 0 && nozzle.Depth > 0);
            }

            // The hull's mesh has them, after the blocks.
            var mesh = MeshBuilder.Hull(design, Look);
            Assert.Equal(nozzles.Select(n => n with { Index = 0 }), mesh.Nozzles.Select(n => n with { Index = 0 }));
            Assert.True(mesh.BoundingRadius >= (design.MountOffsets[0] + Facings.Vector(face) * half[axis]).Length() - 1e-4f);

            // A hull that has lost the block has lost the thruster: its damaged mesh is the core alone.
            var alive = design.FullSet.ToArray();
            var whole = DamageMesh.Build(design, alive, default, 40000, 0.2f)!;
            BlockSet.Remove(alive, 1);
            var without = DamageMesh.Build(design, alive, default, 40000, 0.2f)!;
            Assert.Equal(36, without.Original.Length);
            Assert.True(whole.Original.Length > 72);
        }
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("ionDrive")]
    public void AModelStaysInsideItsBlockAndPointsItsNozzlesAlongTheFacing(string type)
    {
        foreach (var facing in Directions)
        {
            foreach (var size in new[] { One, new GridSize(2, 2, 2), new GridSize(2, 1, 1) })
            {
                var design = Design(type, facing, size);
                var vertices = new List<MeshVertex>();
                var indices = new List<uint>();
                var nozzles = new List<ThrustNozzle>();
                Assert.True(PropulsionModels.Add(design, 1, Look, vertices, indices, nozzles));
                var lo = design.BlockOffsets[1] - design.BlockHalfExtents[1] - new Vector3(1e-4f);
                var hi = design.BlockOffsets[1] + design.BlockHalfExtents[1] + new Vector3(1e-4f);
                foreach (var v in vertices)
                {
                    var p = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
                    Assert.True(p.X >= lo.X && p.Y >= lo.Y && p.Z >= lo.Z && p.X <= hi.X && p.Y <= hi.Y && p.Z <= hi.Z, $"{type} {facing} {size}: {p}");
                }

                // Every triangle is wound to face the way its normal points, whichever way the block's frame turns.
                for (var i = 0; i < indices.Count; i += 3)
                {
                    var a = vertices[(int)indices[i]];
                    var b = vertices[(int)indices[i + 1]];
                    var c = vertices[(int)indices[i + 2]];
                    var pa = new Vector3(a.Position.X, a.Position.Y, a.Position.Z);
                    var cross = Vector3.Cross(new Vector3(b.Position.X, b.Position.Y, b.Position.Z) - pa, new Vector3(c.Position.X, c.Position.Y, c.Position.Z) - pa);
                    Assert.True(Vector3.Dot(cross, new Vector3(a.Normal.X, a.Normal.Y, a.Normal.Z)) > 0, $"{type} {facing} triangle {i / 3}");
                }

                // Nozzles sit on the face the part faces, and burn the part's fuel.
                var face = design.BlockOffsets[1] + Facings.Vector(facing) * design.BlockHalfExtents[1][Facings.Axis(facing)];
                Assert.NotEmpty(nozzles);
                foreach (var nozzle in nozzles)
                {
                    Assert.Equal(facing, nozzle.Facing);
                    Assert.Equal(Content.Blocks.Get(type).Fuel, nozzle.Fuel);
                    Assert.Equal(face[Facings.Axis(facing)], nozzle.Exit[Facings.Axis(facing)], 4);
                    Assert.True(nozzle.ExitRadius > 0 && nozzle.Depth > 0);
                }
            }
        }
    }

    [Fact]
    public void ADeeperEngineGrowsItsHousingNotItsBell()
    {
        var shallow = Nozzles("engine", new GridSize(4, 4, 6));
        var deep = Nozzles("engine", new GridSize(4, 4, 12));
        Assert.Single(shallow);
        Assert.Single(deep);
        Assert.Equal(shallow[0].ExitRadius, deep[0].ExitRadius);
        Assert.Equal(shallow[0].Depth, deep[0].Depth);

        // A wider face is a larger bell; a face twice as wide as tall carries two.
        Assert.True(Nozzles("engine", new GridSize(4, 4, 6))[0].ExitRadius > Nozzles("engine", new GridSize(1, 1, 6))[0].ExitRadius * 3.5f);
        Assert.Equal(2, Nozzles("engine", new GridSize(2, 1, 1)).Count);
    }

    [Fact]
    public void AThrusterWithNoModelIsDrawnAsItsPlainSmallBox()
    {
        // No look at all: every part is a plain block, and a mounted one the five sides of its box that show.
        var design = Mounted("coldGasThruster", BlockFacing.Up);
        var plain = MeshBuilder.Hull(design, new PropulsionLook());
        Assert.Empty(plain.Nozzles);
        Assert.Equal((6 + 6 + 5) * 4, plain.Vertices.Count);
        Assert.False(PropulsionModels.AddMount(design, 0, new PropulsionLook(), [], [], null));
    }

    [Theory]
    [InlineData("hotThruster", 1)]
    [InlineData("microthruster", 1)]
    [InlineData("ionThruster", 1)]
    [InlineData("microIonThruster", 1)]
    public void AMountIsAPodOnAFacetedPlateThatSitsOnItsFace(string type, int nozzleCount)
    {
        var model = Look.Models[type];
        var pod = Assert.IsType<PodLook>(model.Pod);
        var fuel = Content.Blocks.Get(type).Fuel;
        foreach (var facing in Directions)
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            var nozzles = new List<ThrustNozzle>();
            var box = new PartBox(new Vector3(3, -2, 1), Turned(new Vector3(0.2f, 0.2f, 0.1f), facing), facing, 1f);
            PropulsionModels.Build(model, box, fuel, 0, true, vertices, indices, nozzles);
            var axis = Facings.Axis(facing);
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            var back = box.Center[axis] - Facings.Sign(facing) * box.Half[axis];
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            var backArea = 0f;
            for (var i = 0; i < indices.Count; i += 3)
            {
                var a = P(vertices[(int)indices[i]]);
                var b = P(vertices[(int)indices[i + 1]]);
                var c = P(vertices[(int)indices[i + 2]]);
                lo = Vector3.Min(lo, Vector3.Min(a, Vector3.Min(b, c)));
                hi = Vector3.Max(hi, Vector3.Max(a, Vector3.Max(b, c)));
                if (MathF.Abs(a[axis] - back) < 1e-4f && MathF.Abs(b[axis] - back) < 1e-4f && MathF.Abs(c[axis] - back) < 1e-4f)
                    backArea += 0.5f * Vector3.Cross(b - a, c - a).Length();
            }

            // The plate spans the footprint edge to edge and lies on the face; only its corners are cut.
            foreach (var k in new[] { u, v })
            {
                Assert.Equal(box.Center[k] - box.Half[k], lo[k], 4);
                Assert.Equal(box.Center[k] + box.Half[k], hi[k], 4);
            }
            var cut = pod.PlateChamfer * MathF.Min(box.Half[u], box.Half[v]);
            Assert.Equal(4f * box.Half[u] * box.Half[v] - 2f * cut * cut, backArea, 4);

            // Its nozzles end on the box's outer face, side by side on the housing, all pointing out.
            Assert.Equal(nozzleCount, nozzles.Count);
            var front = box.Center[axis] + Facings.Sign(facing) * box.Half[axis];
            Assert.All(nozzles, n => Assert.Equal(front, n.Exit[axis], 4));
            Assert.All(nozzles, n => Assert.Equal(facing, n.Facing));
            Assert.Equal(nozzleCount, nozzles.Select(n => (MathF.Round(n.Exit[u], 4), MathF.Round(n.Exit[v], 4))).Distinct().Count());
        }
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("microengine")]
    [InlineData("ionDrive")]
    [InlineData("microIonDrive")]
    public void AModelCoversItsWholeMountingFaceSoNeighboursMeetItFlush(string type)
    {
        var model = Look.Models[type];
        var fuel = Content.Blocks.Get(type).Fuel;
        // A part in its cell, a large engine, a thin one and a wide one.
        foreach (var half in new[] { new Vector3(0.5f), new Vector3(2, 2, 3), new Vector3(0.5f, 0.5f, 0.1f), new Vector3(1, 0.5f, 0.5f) })
        {
            foreach (var facing in Directions)
            {
                var vertices = new List<MeshVertex>();
                var indices = new List<uint>();
                var box = new PartBox(new Vector3(3, -2, 1), Turned(half, facing), facing, 1f);
                PropulsionModels.Build(model, box, fuel, 0, true, vertices, indices, null);
                var axis = Facings.Axis(facing);
                var back = box.Center[axis] - Facings.Sign(facing) * box.Half[axis];
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                var backArea = 0f;
                for (var i = 0; i < indices.Count; i += 3)
                {
                    var a = P(vertices[(int)indices[i]]);
                    var b = P(vertices[(int)indices[i + 1]]);
                    var c = P(vertices[(int)indices[i + 2]]);
                    lo = Vector3.Min(lo, Vector3.Min(a, Vector3.Min(b, c)));
                    hi = Vector3.Max(hi, Vector3.Max(a, Vector3.Max(b, c)));
                    if (MathF.Abs(a[axis] - back) < 1e-4f && MathF.Abs(b[axis] - back) < 1e-4f && MathF.Abs(c[axis] - back) < 1e-4f)
                        backArea += 0.5f * Vector3.Cross(b - a, c - a).Length();
                }
                // The model fills the box's whole width on every side, and its back face is the whole mounting face.
                for (var k = 0; k < 3; k++)
                {
                    Assert.Equal(box.Center[k] - box.Half[k], lo[k], 3);
                    Assert.Equal(box.Center[k] + box.Half[k], hi[k], 3);
                }
                var u = (axis + 1) % 3;
                var v = (axis + 2) % 3;
                Assert.Equal(4f * box.Half[u] * box.Half[v], backArea, 3);
            }
        }
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("microengine")]
    public void AnEngineIsABoltedPlateARingCollarAndAFacetedBellWithALitThroat(string type)
    {
        var model = Look.Models[type];
        var bell = Assert.IsType<BellLook>(model.Bell);
        Assert.Same(model, Look.GlowModels[model.GlowSlot]);
        var fuel = Content.Blocks.Get(type).Fuel;
        foreach (var facing in Directions)
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            var nozzles = new List<ThrustNozzle>();
            var box = new PartBox(new Vector3(3, -2, 1), new Vector3(0.5f), facing, 1f);
            PropulsionModels.Build(model, box, fuel, 0, true, vertices, indices, nozzles);
            var axis = Facings.Axis(facing);
            var sign = Facings.Sign(facing);
            var nozzle = Assert.Single(nozzles);
            // The plume leaves the bell's exit, on the box's outer face, and the throat's glow is the look's own.
            Assert.Equal(box.Center + Facings.Vector(facing) * 0.5f, nozzle.Exit);
            Assert.Equal(model.GlowSlot, nozzle.Glow);

            // Measured from the nozzle's axis and from the back of the box.
            float Across(MeshVertex v)
            {
                var d = P(v) - box.Center;
                d[axis] = 0f;
                return d.Length();
            }
            float Along(MeshVertex v) => (P(v)[axis] - box.Center[axis]) * sign + 0.5f;
            var start = 1f - nozzle.Depth;

            // The bell is as many flats as the look says, all the way out to its lip, and opens toward the exit.
            var lip = vertices.Where(v => MathF.Abs(Along(v) - 1f) < 1e-4f && Across(v) > nozzle.ExitRadius * 1.01f).Select(v => P(v)).Distinct().Count();
            Assert.Equal(model.Sides, lip);
            var wall = vertices.Where(v => Tag(v) < 4.5f && Along(v) > start + 1e-4f && Across(v) > nozzle.ExitRadius * model.ThroatShare).ToArray();
            Assert.True(wall.Where(v => Along(v) < start + 0.5f * nozzle.Depth).Max(Across) < wall.Where(v => Along(v) > 1f - 1e-4f).Max(Across));

            // Light: one panel on each side of the collar, behind the bell, and in the bell the lining near the throat
            // with the throat's disc. All of the lining's light is inside the bell.
            var lit = vertices.Where(v => Tag(v) >= 4.5f).ToArray();
            var sideLights = lit.Where(v => Along(v) < start).ToArray();
            var throat = lit.Where(v => Along(v) >= start).ToArray();
            Assert.Equal(2 * 4, sideLights.Length);
            Assert.Equal(model.Sides * 4 + model.Sides + 1, throat.Length);
            Assert.All(throat, v => Assert.True(Across(v) < nozzle.ExitRadius && Along(v) <= start + bell.GlowReach * nozzle.Depth + 1e-4f));
            // The collar stands between the plate and the bell and is rounder than the bell.
            Assert.True(sideLights.Min(Along) > bell.PlateDepth * 0.5f * 0.5f);
            Assert.True(bell.CollarSides > model.Sides);

            // Four bolt heads a nozzle: hexagons standing on the plate, clear of the collar, inside the box's corners.
            var bolts = vertices.Where(v => Across(v) > nozzle.ExitRadius * 1.1f && Along(v) > bell.PlateDepth * 0.5f + 1e-4f && Along(v) < start).Select(v => P(v)).Distinct().Count();
            Assert.Equal(4 * 6, bolts);
        }

        // A face twice as wide carries two bells on one plate, each with its own collar, bolts and glow.
        var wide = new List<ThrustNozzle>();
        PropulsionModels.Build(model, new PartBox(Vector3.Zero, new Vector3(1f, 0.5f, 0.5f), BlockFacing.Back, 1f), fuel, 0, true, [], [], wide);
        Assert.Equal(2, wide.Count);
        Assert.All(wide, n => Assert.Equal(model.GlowSlot, n.Glow));
    }

    private static float Tag(MeshVertex v) => new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z).Length();

    [Fact]
    public void TheThroatsGlowLiesInsideTheBellAndIsOnlyGivenToAFiringEngineLargeEnoughToSee()
    {
        var model = Look.Models["engine"];
        var bell = model.Bell!;
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        PropulsionModels.BuildEngineGlow(model, bell, vertices, indices);
        Assert.NotEmpty(indices);
        // For a nozzle of exit radius 1 and length 1: the lit part of the lining, a hair inside it, and nothing else.
        Assert.All(vertices, v => Assert.InRange(v.Position.Z, 0f, bell.GlowReach + 1e-5f));
        Assert.All(vertices, v => Assert.True(MathF.Sqrt(v.Position.X * v.Position.X + v.Position.Y * v.Position.Y) < 1f));
        Assert.All(vertices, v => Assert.Equal(1f, Tag(v), 4));

        // Only parts with a bell look have one: a thruster's or an ion drive's nozzle names none.
        var design = Design("engine", BlockFacing.Back, One);
        var nozzles = MeshBuilder.Hull(design, Look).Nozzles;
        var nozzle = Assert.Single(nozzles);
        Assert.Equal(model.GlowSlot, nozzle.Glow);
        Assert.All(Nozzles("ionDrive", One), n => Assert.Equal(-1, n.Glow));

        var effects = new ThrustEffects(Look);
        var output = new PlumeInstance[PropulsionLook.PlumeCapacity];
        var blocks = new[] { ulong.MaxValue };
        var firing = Hull(7, -127, 4, blocks);
        var at = new Vector3(0, 0, -30);
        Begin(effects, 10);
        effects.Add(effects.Observe(firing), firing, nozzles, at, Quaternion.Identity, 1, output);
        Assert.Equal(1, effects.End());
        // The bell: its throat end is its length behind the exit, along the way the nozzle points.
        Assert.Equal(model.GlowSlot, effects.Glows[0]);
        Assert.Equal(new Vector3(nozzle.ExitRadius, nozzle.ExitRadius, nozzle.Depth), effects.Bells[0].Size);
        var exit = at + nozzle.Exit;
        Assert.True(Vector3.Distance(exit - Vector3.UnitZ * nozzle.Depth, effects.Bells[0].Start) < 1e-4f);
        Assert.True(Vector3.Distance(Vector3.UnitZ, Vector3.Transform(Vector3.UnitZ, effects.Bells[0].Turn)) < 1e-4f);
        Assert.True(effects.Strengths[0] > 0f);
        Assert.Equal(Vector3.Zero, bell.GlowLight(0f));
        Assert.True(bell.GlowLight(1f).Z > bell.GlowLight(0.3f).Z);

        // Too small on screen, the plume is still drawn and the throat's own glow is left out.
        var far = new Vector3(0, 0, -30f * 23f / (0.5f * (Look.MinPixels + bell.GlowPixels)));
        Assert.True(bell.GlowPixels > Look.MinPixels);
        Begin(effects, 11);
        effects.Add(effects.Observe(firing), firing, nozzles, far, Quaternion.Identity, 1, output);
        Assert.Equal(1, effects.End());
        Assert.Equal(-1, effects.Glows[0]);

        var allocation = new AllocationProbe();
        while (allocation.Measuring())
        {
            Begin(effects, 12);
            effects.Add(effects.Observe(firing), firing, nozzles, at, Quaternion.Identity, 1, output);
            effects.End();
            bell.GlowLight(effects.Strengths[0]);
        }
        Assert.Equal(0, allocation.Bytes);
    }

    [Fact]
    public void ABellLookOutOfRangeIsRefused()
    {
        var text = File.ReadAllText(Prospect.Engine.IO.AppPaths.Content("visuals", "propulsion.json"));
        var folder = Directory.CreateTempSubdirectory("prospect-bell-look");
        try
        {
            var path = Path.Combine(folder.FullName, "propulsion.json");
            // A collar that is no ring of quarters, a plate thicker than half the block, and a glow past its range.
            foreach (var (from, to) in new[] { ("\"collarSides\": 16", "\"collarSides\": 14"), ("\"plateDepth\": 0.2", "\"plateDepth\": 0.9"), ("\"glowReach\": 0.4", "\"glowReach\": 0.95") })
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
    public void AModelIsFlatShadedFacetByFacet()
    {
        var design = Design("engine", BlockFacing.Back, new GridSize(4, 4, 6));
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        PropulsionModels.Add(design, 1, Look, vertices, indices, null);
        for (var i = 0; i < indices.Count; i += 3)
        {
            var a = vertices[(int)indices[i]];
            var b = vertices[(int)indices[i + 1]];
            var c = vertices[(int)indices[i + 2]];
            Assert.Equal(a.Normal, b.Normal);
            Assert.Equal(a.Normal, c.Normal);
            var facet = Vector3.Normalize(Vector3.Cross(P(b) - P(a), P(c) - P(a)));
            Assert.True(Vector3.Dot(facet, Vector3.Normalize(new Vector3(a.Normal.X, a.Normal.Y, a.Normal.Z))) > 0.999f, $"triangle {i / 3}");
        }
        // Low poly: a large engine is a few thousand vertices, not tens of thousands.
        Assert.InRange(vertices.Count, 500, 6000);
    }

    [Fact]
    public void APartModelIsBuiltOnAWorkerThenFoundWithoutTakingMemory()
    {
        using var jobs = new JobSystem(1, "Parts");
        var models = new PartModels(jobs, Look);
        var engine = Content.Blocks.Get("engine");
        var half = new Vector3(1, 1, 1.5f);
        models.Begin();
        Assert.False(models.TryGet(engine, BlockShape.Box, half, BlockFacing.Back, 1f, out _));
        // A part with no model is never asked for.
        Assert.False(models.TryGet(Content.Blocks.Get("core"), BlockShape.Box, half, BlockFacing.Back, 1f, out _));
        models.Flush();
        jobs.CompleteAll();
        models.Begin();
        Assert.True(models.TryGet(engine, BlockShape.Box, half, BlockFacing.Back, 1f, out var model));
        Assert.Equal(0, model.Length % 3);
        Assert.True(model.Length > 100);
        Assert.Equal(1, models.Built);

        // The same model as a design's own, centred on its box.
        var design = Design("engine", BlockFacing.Back, new GridSize(2, 2, 3));
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        PropulsionModels.Add(design, 1, Look, vertices, indices, null);
        Assert.Equal(indices.Count, model.Length);

        var allocation = new AllocationProbe();
        while (allocation.Measuring())
        {
            models.Begin();
            models.TryGet(engine, BlockShape.Box, half, BlockFacing.Back, 1f, out _);
            models.Flush();
        }
        Assert.Equal(0, allocation.Bytes);
    }

    [Fact]
    public void AStoppedPartsLastGasFliesOffAndClears()
    {
        var pod = Content.Designs.First(d => d.Name == "Command Pod");
        var nozzles = MeshBuilder.Hull(pod, Look).Nozzles;
        var effects = new ThrustEffects(Look);
        var output = new PlumeInstance[PropulsionLook.PlumeCapacity];
        var blocks = new[] { ulong.MaxValue };
        var firing = new HullView { Id = 4, Thrust = new ThrustState { X = 127, Flags = 1 }, Blocks = blocks };
        var idle = new HullView { Id = 4, Blocks = blocks };
        var cold = Look.PlumeOrder[Look.PlumeIndex("cold")];

        void Frame(double seconds, in HullView hull)
        {
            Begin(effects, seconds);
            effects.Add(effects.Observe(hull), hull, nozzles, new Vector3(0, 0, -30), Quaternion.Identity, 1, output);
            effects.End();
        }

        Frame(1, firing);
        Frame(2, firing);
        Assert.Equal(16, effects.Count);
        Assert.True(output[0].Flow.X < 0);
        var steady = output[0].CoreBright.W;

        // The first frame the valve is seen closed, the gas is released; a little later it is still there, fading;
        // then it is gone.
        Frame(2.05, idle);
        Assert.Equal(16, effects.Count);
        Assert.Equal(0f, output[0].Flow.X, 3);
        Frame(2.05 + cold.FadeSeconds * 0.5, idle);
        Assert.Equal(16, effects.Count);
        Assert.Equal(cold.FadeSeconds * 0.5f, output[0].Flow.X, 3);
        Assert.True(output[0].CoreBright.W < steady);
        Frame(2.05 + cold.FadeSeconds * 1.01, idle);
        Assert.Equal(0, effects.Count);

        // Opening again bursts denser than the steady jet: the burst rides to the shader, and the light follows.
        Frame(3, firing);
        Assert.Equal(cold.Burst, output[0].Shape.Y, 3);
        Assert.True(output[0].CoreBright.W > steady * 1.3f);
        Frame(3 + cold.BurstSeconds * 6, firing);
        Assert.True(output[0].Shape.Y < 0.01f);
    }

    private static Vector3 P(MeshVertex v) => new(v.Position.X, v.Position.Y, v.Position.Z);

    // Half extents given as across, across, depth, laid along the facing's axes.
    private static Vector3 Turned(Vector3 half, BlockFacing facing)
    {
        var axis = Facings.Axis(facing);
        var turned = Vector3.Zero;
        turned[axis] = half.Z;
        turned[(axis + 1) % 3] = half.X;
        turned[(axis + 2) % 3] = half.Y;
        return turned;
    }

    [Fact]
    public void AHullMeshListsTheNozzlesOfItsThrustPartsAndDrawsThemAsModels()
    {
        var pod = Content.Designs.First(d => d.Name == "Command Pod");
        var mesh = MeshBuilder.Hull(pod, Look);
        // One engine and, each way but back, four mounted cold gas pods of four nozzles each.
        Assert.Equal(1 + 5 * 4 * 4, mesh.Nozzles.Length);
        Assert.Equal(pod.Mounts.Length * 4, mesh.Nozzles.Count(nozzle => nozzle.Fuel == FuelKind.Nitrogen));
        Assert.Contains(mesh.Vertices, v => new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z).Length() > 1.5f);
        Assert.All(mesh.Indices, i => Assert.InRange((int)i, 0, mesh.Vertices.Count - 1));
    }

    private static List<ThrustNozzle> Nozzles(string type, GridSize size)
    {
        var nozzles = new List<ThrustNozzle>();
        PropulsionModels.Add(Design(type, BlockFacing.Back, size), 1, Look, [], [], nozzles);
        return nozzles;
    }

    private static HullView Hull(uint id, sbyte z, byte flags, ulong[] blocks) => new()
    {
        Id = id,
        Thrust = new ThrustState { Z = z, Flags = flags },
        Blocks = blocks,
    };

    // Plumes are drawn in front of a camera at the origin looking down -Z, the hull 30 m ahead.
    private static void Begin(ThrustEffects effects, double seconds) =>
        effects.Begin(seconds, Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ, 0.6f, 1.6f, 1080f);

    [Fact]
    public void AnEngineIgnitesOnceWhenItStartsAndIsDarkWhenIdle()
    {
        var pod = Content.Designs.First(d => d.Name == "Command Pod");
        var nozzles = MeshBuilder.Hull(pod, Look).Nozzles;
        var effects = new ThrustEffects(Look);
        var output = new PlumeInstance[PropulsionLook.PlumeCapacity];
        var blocks = new[] { ulong.MaxValue };
        var at = new Vector3(0, 0, -30);

        // Idle: nothing.
        Begin(effects, 10);
        var slot = effects.Observe(Hull(7, 0, 0, blocks));
        effects.Add(slot, Hull(7, 0, 0, blocks), nozzles, at, Quaternion.Identity, 1, output);
        Assert.Equal(0, effects.End());

        // Forward on bipropellant: the engine fires, its sparks start now.
        var firing = Hull(7, -127, 4, blocks);
        Begin(effects, 10.5);
        slot = effects.Observe(firing);
        effects.Add(slot, firing, nozzles, at, Quaternion.Identity, 1, output);
        Assert.Equal(1, effects.End());
        Assert.Equal(0f, output[0].SparkAge.W, 3);
        Assert.Equal(0f, output[0].EdgeKind.W);
        Assert.True(output[0].Extent.Z > 0);
        var lit = output[0].AxisLength.W;

        // A second later it has grown and thrown its sparks; boost makes it longer and brighter.
        Begin(effects, 11.5);
        slot = effects.Observe(firing);
        effects.Add(slot, firing, nozzles, at, Quaternion.Identity, 1, output);
        effects.End();
        Assert.Equal(1f, output[0].SparkAge.W, 3);
        Assert.Equal(0f, output[0].Extent.Z);
        Assert.True(output[0].AxisLength.W > lit);
        Assert.Equal(new Vector3(0, 0, 1), new Vector3(output[0].AxisLength.X, output[0].AxisLength.Y, output[0].AxisLength.Z));
        var plain = output[0];
        var boosted = Hull(7, -127, 4 | 8, blocks);
        Begin(effects, 11.6);
        slot = effects.Observe(boosted);
        effects.Add(slot, boosted, nozzles, at, Quaternion.Identity, 1, output);
        effects.End();
        Assert.True(output[0].AxisLength.W > plain.AxisLength.W * 1.3f);
        Assert.True(output[0].CoreBright.W > plain.CoreBright.W);
        // Boost raises the cap, never past the plume's own cap times boost: viewed into it, it never burns out.
        Assert.True(output[0].CoreBright.W <= Look.PlumeOrder[Look.PlumeIndex("engine")].Brightness * Look.BoostBrightness + 1e-4f);

        // A hull first seen while it fires lit before it came into view: no sparks.
        Begin(effects, 12);
        slot = effects.Observe(Hull(9, -127, 4, blocks));
        effects.Add(slot, Hull(9, -127, 4, blocks), nozzles, at, Quaternion.Identity, 1, output);
        effects.End();
        Assert.Equal(0f, output[0].Extent.Z);

        // A lost engine fires nothing.
        var lost = (ulong[])blocks.Clone();
        var engine = nozzles.First(n => n.Fuel == FuelKind.Bipropellant).Block;
        lost[0] &= ~(1UL << engine);
        Begin(effects, 12.1);
        slot = effects.Observe(Hull(9, -127, 4, lost));
        effects.Add(slot, Hull(9, -127, 4, lost), nozzles, at, Quaternion.Identity, 1, output);
        Assert.Equal(0, effects.End());
    }

    [Fact]
    public void TheDronesIonPartsGlowSteadilyOnlyWhileTheyHavePowerAndClearWhenTheyStop()
    {
        var drone = Content.Designs[Content.DroneDesign];
        var nozzles = MeshBuilder.Hull(drone, Look).Nozzles;
        // A two-cell ion drive (one nozzle per cell) and twenty-four ion thrusters: every nozzle is an ion emitter, burning no fuel.
        Assert.Equal(drone.Mounts.Length + 2, nozzles.Length);
        Assert.All(nozzles, n => Assert.Equal(FuelKind.None, n.Fuel));
        var ion = Look.PlumeOrder[Look.PlumeIndex("ion")];
        var effects = new ThrustEffects(Look);
        var output = new PlumeInstance[PropulsionLook.PlumeCapacity];
        var blocks = new[] { ulong.MaxValue };
        var at = new Vector3(0, 0, -6);

        // Thrust asked for but no power to give it: the step leaves the ion bit clear, and nothing glows.
        Begin(effects, 1);
        var dark = Hull(5, -127, 0, blocks);
        effects.Add(effects.Observe(dark), dark, nozzles, at, Quaternion.Identity, 1, output);
        Assert.Equal(0, effects.End());

        // Forward on power: the drive's two nozzles glow, as ion glows, with no sparks; strafing lights that side's
        // four pods as well.
        var forward = Hull(5, -127, 16, blocks);
        Begin(effects, 2);
        effects.Add(effects.Observe(forward), forward, nozzles, at, Quaternion.Identity, 1, output);
        Assert.Equal(2, effects.End());
        Assert.Equal(2f, output[0].EdgeKind.W);
        Assert.Equal(0f, output[0].Extent.Z);
        Assert.Equal(0f, output[0].Motion.Y);
        Assert.Equal(0f, output[0].Shape.Y);
        Assert.Equal(new Vector3(0, 0, 1), new Vector3(output[0].AxisLength.X, output[0].AxisLength.Y, output[0].AxisLength.Z));
        var strafing = new HullView { Id = 5, Thrust = new ThrustState { X = 127, Z = -127, Flags = 16 }, Blocks = blocks };
        Begin(effects, 3);
        effects.Add(effects.Observe(strafing), strafing, nozzles, at, Quaternion.Identity, 1, output);
        Assert.Equal(6, effects.End());
        Assert.Equal(4, output.AsSpan(0, 6).ToArray().Count(p => p.AxisLength.X == -1f));

        // Steady: two frames apart, a firing emitter is the same but for the clock.
        Begin(effects, 4);
        effects.Add(effects.Observe(forward), forward, nozzles, at, Quaternion.Identity, 1, output);
        effects.End();
        var first = output[0];
        Begin(effects, 4.5);
        effects.Add(effects.Observe(forward), forward, nozzles, at, Quaternion.Identity, 1, output);
        effects.End();
        Assert.Equal(first.CoreBright, output[0].CoreBright);
        Assert.Equal(first.AxisLength, output[0].AxisLength);

        // Stopped, it clears over its fade seconds, then is gone.
        var idle = Hull(5, 0, 0, blocks);
        Begin(effects, 5);
        effects.Add(effects.Observe(idle), idle, nozzles, at, Quaternion.Identity, 1, output);
        Assert.Equal(2, effects.End());
        Begin(effects, 5 + ion.FadeSeconds * 1.01);
        effects.Add(effects.Observe(idle), idle, nozzles, at, Quaternion.Identity, 1, output);
        Assert.Equal(0, effects.End());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void AHullAtRestAfterTurningShowsNoPlume(int index)
    {
        // The plumes read only what the step wrote: a hull steered about at rest with its dampeners on writes
        // nothing, so after the last braking gas has cleared, nothing is drawn, frame after frame.
        var design = Content.Designs[index];
        var flight = design.Flight;
        var nozzles = MeshBuilder.Hull(design, Look).Nozzles;
        var effects = new ThrustEffects(Look);
        var output = new PlumeInstance[PropulsionLook.PlumeCapacity];
        var blocks = new[] { ulong.MaxValue, ulong.MaxValue };
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Velocity = new Vector3(1f, 0.5f, -2f);
        var drawn = 0;
        for (var tick = 0; tick < 60 * 12; tick++)
        {
            var input = new FlightInput { Dampeners = true, Yaw = (sbyte)(tick % 7 - 3), Pitch = (sbyte)(tick % 5 - 2), Roll = (sbyte)(tick % 3 - 1) };
            FlightModel.Step(ref state, input, flight, HullSim.Dt);
            var hull = new HullView { Id = 11, Thrust = state.Thrust, Blocks = blocks };
            Begin(effects, tick / 60.0);
            effects.Add(effects.Observe(hull), hull, nozzles, new Vector3(0, 0, -8), Quaternion.Identity, 1, output);
            var count = effects.End();
            if (tick >= 60 * 6)
                drawn += count;
        }
        Assert.Equal(0, drawn);
    }

    [Fact]
    public void StrafingPuffsTheColdGasThrustersOfThatSide()
    {
        var pod = Content.Designs.First(d => d.Name == "Command Pod");
        var nozzles = MeshBuilder.Hull(pod, Look).Nozzles;
        var effects = new ThrustEffects(Look);
        var output = new PlumeInstance[PropulsionLook.PlumeCapacity];
        var hull = new HullView { Id = 3, Thrust = new ThrustState { X = 127, Flags = 1 }, Blocks = new[] { ulong.MaxValue } };
        Begin(effects, 1);
        effects.Add(effects.Observe(hull), hull, nozzles, new Vector3(0, 0, -30), Quaternion.Identity, 1, output);
        // Pushed right, the left-facing cluster puffs: four pods of four nozzles, pointing left.
        Assert.Equal(16, effects.End());
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(1f, output[i].EdgeKind.W);
            Assert.Equal(-1f, output[i].AxisLength.X);
        }
    }

    [Fact]
    public void PastTheBudgetTheLargestOnScreenStayAndAFrameTakesNoMemory()
    {
        var pod = Content.Designs.First(d => d.Name == "Command Pod");
        var nozzles = MeshBuilder.Hull(pod, Look).Nozzles;
        var effects = new ThrustEffects(Look);
        var output = new PlumeInstance[PropulsionLook.PlumeCapacity];
        var blocks = new[] { ulong.MaxValue };
        var hulls = new HullView[200];
        for (var i = 0; i < hulls.Length; i++)
            hulls[i] = Hull((uint)i + 1, -127, 4, blocks);

        void Frame(double seconds)
        {
            Begin(effects, seconds);
            for (var i = 0; i < hulls.Length; i++)
            {
                var slot = effects.Observe(hulls[i]);
                // The first hull is the nearest; the rest are far back.
                effects.Add(slot, hulls[i], nozzles, new Vector3(0, 0, i == 0 ? -20 : -60 - i), Quaternion.Identity, 1, output);
            }
            effects.End();
        }

        Frame(1);
        Assert.Equal(Look.MaxPlumes, effects.Count);
        Assert.Contains(output.AsSpan(0, effects.Count).ToArray(), p => p.ExitRadius.Z > -21);
        for (var i = 0; i < 3; i++) Frame(2 + i);
        var allocation = new AllocationProbe();
        var t = 10.0;
        while (allocation.Measuring()) Frame(t += 0.016);
        Assert.Equal(0, allocation.Bytes);
    }

    [Fact]
    public void ScriptedThrustReadsTheFlightKeys()
    {
        Assert.True(ScriptedThrust.TryParse("forward,boost", out var thrust));
        Assert.Equal(new ScriptedThrust(0, 0, 1, true), thrust);
        Assert.True(ScriptedThrust.TryParse("back, left,up", out thrust));
        Assert.Equal(new ScriptedThrust(-1, 1, -1, false), thrust);
        Assert.False(ScriptedThrust.TryParse("sideways", out _));
    }
}
