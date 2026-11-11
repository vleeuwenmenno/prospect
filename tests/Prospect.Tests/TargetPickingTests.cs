using System.Diagnostics;
using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class TargetPickingTests
{
    private static Asteroid Rock(float radius, Vector3 position) => new() { Radius = radius, LocalPosition = position, Rotation = Quaternion.Identity };
    private static PickHull Hull(float x = 0, float z = 8000) => new(1, new Double3(x, 0, z), Quaternion.Identity, [Vector3.Zero], 2, 1, 2);
    private static PickSector Sector(params Asteroid[] rocks) => new(default, default, rocks, rocks.Length, null);
    private static PickHit[] Pick(PickHull[] hulls, PickSector sector, Vector3? ray = null, Double3 camera = default)
    {
        var hits = new PickHit[hulls.Length + sector.Count];
        var count = TargetPicking.Pick(ray ?? Vector3.UnitZ, camera, ray ?? Vector3.UnitZ, 1000, hulls, [sector], hits);
        return hits[..count];
    }

    [Fact]
    public void BoundingSphereMissPicksHullBehind()
    {
        var rock = Rock(3000, new Vector3(0, 2700, 4000));
        Assert.False(RockPicking.Hit(Vector3.UnitZ, rock.LocalPosition, rock, out _));
        var hits = Pick([Hull()], Sector(rock));
        Assert.Single(hits);
        Assert.Equal(1u, hits[0].Hull);
    }

    [Fact]
    public void MissingHullBlocksCannotBePicked()
    {
        Assert.Empty(Pick([Hull() with { Mask = 0 }], Sector()));
    }

    [Fact]
    public void GrabRadiusReachesBesideLargeHullBlocks()
    {
        var hull = Hull(55, 1000) with { BlockSize = 100, Radius = 87 };
        Assert.Single(Pick([hull], Sector()));
    }

    [Fact]
    public void SurfaceHitPicksRock()
    {
        var hits = Pick([], Sector(Rock(3000, new Vector3(0, 0, 6000))));
        Assert.Single(hits);
        Assert.Equal(new RockRef(default, 0), hits[0].Rock);
        Assert.InRange(hits[0].Distance, 3000, 6000);
    }

    [Fact]
    public void CameraInsideBoundsOutsideSurfaceCanPickAway()
    {
        var rock = Rock(3000, default);
        var x = (3000 * AsteroidShape.RadiusAlong(0, Vector3.UnitX) + 3000) / 2;
        Assert.Empty(Pick([], Sector(rock), Vector3.UnitX, new Double3(x, 0, 0)));
    }

    [Fact]
    public void HullInFrontOfRockWins()
    {
        var hits = Pick([Hull(z: 1000)], Sector(Rock(3000, new Vector3(0, 0, 6000))));
        Assert.Equal(2, hits.Length);
        Assert.Equal(1u, hits[0].Hull);
        Assert.True(hits[0].Distance < hits[1].Distance);
    }

    [Fact]
    public void HullBesidePointerWinsOverSlightlyNearerSurface()
    {
        var rock = Rock(100, new Vector3(0, 0, 1000));
        Assert.True(RockPicking.Hit(Vector3.UnitZ, rock.LocalPosition, rock, out var distance));
        var hits = Pick([Hull(8, distance + 20)], Sector(rock));
        Assert.Equal(2, hits.Length);
        Assert.Equal(1u, hits[0].Hull);
        Assert.True(hits[0].Distance > hits[1].Distance);
    }

    [Fact]
    public void SmallRockBesideBigOneKeepsGrabRadius()
    {
        var hits = Pick([], Sector(Rock(3000, new Vector3(0, 2700, 4000)), Rock(1, new Vector3(8, 0, 1000))));
        Assert.Single(hits);
        Assert.Equal(1, hits[0].Rock!.Value.Index);
    }

    [Fact]
    public void GrazingRayFindsSurfaceMinimumBetweenSamples()
    {
        // Find the silhouette in the XZ plane independently with a dense surface scan.
        var rock = Rock(100, new Vector3(0, 0, 300));
        var maximumX = 0f;
        var z = 0f;
        for (var i = 0; i < 100000; i++)
        {
            var angle = MathF.PI * 2 * i / 100000;
            var direction = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
            var point = direction * (rock.Radius * AsteroidShape.RadiusAlong(0, direction));
            if (point.X > maximumX) { maximumX = point.X; z = point.Z; }
        }
        Assert.True(RockPicking.Hit(Vector3.UnitZ, new Vector3(-maximumX, 0, 300), rock, out var hit));
        Assert.InRange(MathF.Abs(hit - (300 + z)), 0, 1);
        Assert.False(RockPicking.Hit(Vector3.UnitZ, new Vector3(-maximumX - 0.02f, 0, 300), rock, out _));
    }

    [Fact]
    public void RepeatedClicksCycleAndWrap()
    {
        PickHit[] hits = [new(1, null, 10), new(0, new RockRef(default, 0), 20)];
        var cycle = new PickCycle();
        var first = cycle.Click(hits, default, 0, null);
        var second = cycle.Click(hits, default, first.Hull, first.Rock);
        var third = cycle.Click(hits, default, second.Hull, second.Rock);
        Assert.Equal(hits[0], first);
        Assert.Equal(hits[1], second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void PointerMovementResetsCycleEvenWhenItReturns()
    {
        PickHit[] hits = [new(1, null, 10), new(2, null, 20)];
        var cycle = new PickCycle();
        cycle.Click(hits, default, 0, null);
        cycle.Observe(new Vector2(5, 0));
        cycle.Observe(default);
        Assert.Equal(hits[0], cycle.Click(hits, default, 1, null));
    }

    [Fact]
    public void DisplacedRockIsPickedAtCurrentPosition()
    {
        var sector = Sector(Rock(20, new Vector3(1000, 0, 1000)));
        Assert.Empty(Pick([], sector));
        sector = sector with { Offsets = [new Vector3(-1000, 0, 0)] };
        Assert.Single(Pick([], sector));
        Assert.Empty(Pick([], sector, Vector3.Normalize(new Vector3(1, 0, 1))));
    }

    [Fact]
    public void AdditiveClicksAdvancePastTwoSelectedHullsAndWrap()
    {
        PickHit[] hits = [new(1, null, 10), new(2, null, 20), new(3, null, 30)];
        var cycle = new PickCycle();
        var selected = new HashSet<uint>();
        for (var i = 0; i < 3; i++)
        {
            var hit = cycle.Click(hits, default);
            if (!selected.Add(hit.Hull)) selected.Remove(hit.Hull);
            Assert.Equal(i + 1, selected.Count);
        }
        Assert.Equal(1u, cycle.Click(hits, default).Hull);
        cycle.Reset();
        Assert.Equal(1u, cycle.Click(hits, default).Hull);
        cycle.Observe(new Vector2(5, 0));
        Assert.Equal(1u, cycle.Click(hits, default).Hull);
    }

    [Fact]
    public void MovingHullPickFollowsInterpolatedFrameAndSkipsJumpGap()
    {
        var previous = new Double3(-100, 0, 1000);
        var current = new Double3(100, 0, 1000);
        var position = TargetPicking.HullPosition(previous, current, 0.25f, 3000);
        Assert.Equal(new Double3(-50, 0, 1000), position);
        var hull = Hull() with { Position = position };
        Assert.Single(Pick([hull], Sector(), Vector3.Normalize(new Vector3(-50, 0, 1000))));
        Assert.Empty(Pick([hull], Sector(), Vector3.Normalize(new Vector3(100, 0, 1000))));
        var afterJump = TargetPicking.HullPosition(new Double3(-5000, 0, 1000), current, 0.25f, 3000);
        Assert.Equal(current, afterJump);
        Assert.Single(Pick([hull with { Position = afterJump }], Sector(), Vector3.Normalize(new Vector3(100, 0, 1000))));
    }

    [Theory]
    [InlineData(815, 0)]
    [InlineData(811, 1)]
    public void OffAxisSmallRockGrabUsesProjectedPixels(float x, int expected)
    {
        PickSector[] sectors = [Sector(Rock(1, new Vector3(x, 0, 1000)))];
        var hits = new PickHit[1];
        var count = TargetPicking.Pick(Vector3.Normalize(new Vector3(0.8f, 0, 1)), default,
            Vector3.UnitZ, 1000, [], sectors, hits);
        Assert.Equal(expected, count);
    }

    [Theory]
    [InlineData(60, 0)]
    [InlineData(58, 1)]
    public void HullCornerGrabIsCircularInScreenPixels(float offset, int expected)
    {
        var hull = Hull() with { Position = new Double3(offset, offset, 1000), BlockSize = 100, Radius = 87 };
        Assert.Equal(expected, Pick([hull], Sector()).Length);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
        var center = Vector3.Transform(new Vector3(offset, offset, 1000), rotation);
        hull = hull with { Position = new Double3(center.X, center.Y, center.Z), Rotation = rotation };
        Assert.Equal(expected, Pick([hull], Sector()).Length);
    }

    [Fact]
    public void RotatedBlockGrabClipsAtNearPlaneWithoutPickingBehindCamera()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
        var center = Vector3.Transform(new Vector3(0.151f, 0, 0.2f), rotation);
        var hull = Hull() with { Position = new Double3(center.X, center.Y, center.Z), Rotation = rotation, BlockSize = 0.2f, Radius = 0.174f };
        var ray = Vector3.Transform(Vector3.Normalize(new Vector3(0.16f, 0, 1)), rotation);
        var hits = new PickHit[1];
        Assert.Equal(1, TargetPicking.Pick(ray, default, Vector3.UnitZ, 1000, [hull], [], hits));
        hull = hull with { Position = new Double3(center.X, center.Y, -0.2f) };
        Assert.Equal(0, TargetPicking.Pick(ray, default, Vector3.UnitZ, 1000, [hull], [], hits));
    }

    [Theory]
    [InlineData(-0.3f, 1)]
    [InlineData(-0.095f, 1)]
    [InlineData(-0.08f, 0)]
    [InlineData(0.3f, 0)]
    public void ACutBlockIsPickedOnlyWhereItIsShownAndAroundThat(float x, int expected)
    {
        // A one-metre block whose centre is 0.1 m past the cut: the slab from x = -0.5 to -0.1 is drawn. The grab
        // reaches 12 pixels, 0.012 m at 100 m here, past the cut's edge, which is no edge of the box.
        var hull = new PickHull(1, new Double3(0, 0, 100), Quaternion.Identity, [Vector3.Zero], 1, 1, 1,
            CutPoint: new Vector3(-0.1f, 0, 0), CutNormal: Vector3.UnitX);
        var ray = Vector3.Normalize(new Vector3(x, 0, 100));
        var hits = new PickHit[1];
        Assert.Equal(expected, TargetPicking.Pick(ray, default, Vector3.UnitZ, 1e5f, [hull], [], hits));
        if (x < -0.1f)
            Assert.InRange(hits[0].Distance, 99.49f, 99.51f);
    }

    [Fact]
    public void ACubeCutOpenTowardsTheCameraShowsNothingToPick()
    {
        // The front face is past the cut and discarded, the others face away and are culled, and nothing caps the
        // cut: from here the block draws nothing, so nothing is picked, not even within the grab of its middle.
        var hull = new PickHull(1, new Double3(0, 0, 10), Quaternion.Identity, [Vector3.Zero], 1, 1, 0.87f,
            CutPoint: new Vector3(0, 0, 0.1f), CutNormal: -Vector3.UnitZ);
        var hits = new PickHit[1];
        Assert.Equal(0, TargetPicking.Pick(Vector3.UnitZ, default, Vector3.UnitZ, 1000, [hull], [], hits));
        // Uncut, its front face is there.
        Assert.Equal(1, TargetPicking.Pick(Vector3.UnitZ, default, Vector3.UnitZ, 1000, [hull with { CutNormal = default }], [], hits));
        Assert.InRange(hits[0].Distance, 9.4999f, 9.5001f);
    }

    [Theory]
    [InlineData(0f, 0.25f, 0, 0f)]
    [InlineData(0.2f, 0.3f, 1, 0.31623f)]
    [InlineData(0.2f, 0.2f, 0, 0f)]
    public void OnlyFacesDrawnBeyondTheNearPlaneArePicked(float x, float aim, int expected, float distance)
    {
        // A 0.2 m block from z = 0.15 to 0.35 with the near plane at 0.25 m. Straight ahead its front face is nearer
        // than the near plane and every other face is turned away, so nothing is drawn. Off to the side its -x face,
        // at x = x - 0.1, is drawn from 0.25 m on: the ray meeting it there picks it, the one meeting it nearer misses.
        var hull = new PickHull(1, new Double3(x, 0, 0.25f), Quaternion.Identity, [Vector3.Zero], 0.2f, 1, 0.18f);
        var ray = x == 0 ? Vector3.UnitZ : Vector3.Normalize(new Vector3(x - 0.1f, 0, aim));
        var hits = new PickHit[1];
        Assert.Equal(expected, TargetPicking.Pick(ray, default, Vector3.UnitZ, 1000, [hull], [], hits));
        if (expected == 1)
            Assert.InRange(hits[0].Distance, distance - 1e-4f, distance + 1e-4f);
    }

    [Fact]
    public void AnIntactHullCutOpenShowsNoFaceBetweenItsBlocksButACubePerBlockDoes()
    {
        // Two blocks one behind the other; the cut takes the front face of the nearer one. The intact hull's mesh
        // has no face between them, so through the opening there is nothing; drawn block by block, the farther
        // cube's own front face is there.
        byte front = 0x3F & ~(1 << 4);
        byte back = 0x3F & ~(1 << 5);
        var cubes = new PickHull(1, new Double3(0, 0, 10), Quaternion.Identity, [new Vector3(0, 0, -0.5f), new Vector3(0, 0, 0.5f)], 1, 3, 1.2f,
            CutPoint: new Vector3(0, 0, -0.1f), CutNormal: -Vector3.UnitZ);
        var hits = new PickHit[1];
        Assert.Equal(0, TargetPicking.Pick(Vector3.UnitZ, default, Vector3.UnitZ, 1000, [cubes with { Faces = [front, back] }], [], hits));
        Assert.Equal(1, TargetPicking.Pick(Vector3.UnitZ, default, Vector3.UnitZ, 1000, [cubes], [], hits));
        Assert.InRange(hits[0].Distance, 9.9999f, 10.0001f);
    }

    [Fact]
    public void ExteriorFacesAreTheFacesTheHullMeshHas()
    {
        foreach (var design in GameContent.Load().Designs)
        {
            var mesh = MeshBuilder.Hull(design);
            var faces = new byte[design.BlockOffsets.Length];
            for (var q = 0; q < mesh.Vertices.Count; q += 4)
            {
                var middle = Vector3.Zero;
                for (var k = 0; k < 4; k++)
                    middle += Point(mesh.Vertices[q + k].Position) * 0.25f;
                var normal = Point(mesh.Vertices[q].Normal);
                var axis = normal.X != 0 ? 0 : normal.Y != 0 ? 1 : 2;
                var block = Array.FindIndex(design.BlockOffsets, b => Vector3.Distance(b, middle - normal * design.BlockSize * 0.5f) < 1e-4f);
                Assert.True(block >= 0);
                faces[block] |= (byte)(1 << (2 * axis + (normal[axis] < 0 ? 1 : 0)));
            }
            Assert.Equal(faces, design.ExteriorFaces);
        }
    }

    private static Vector3 Point(float3 v) => new(v.X, v.Y, v.Z);

    [Fact]
    public void PicksMatchTheDrawnTrianglesOfCutDrawnOutHullsAcrossTheNearPlane()
    {
        // Lone cubes, intact hulls and hulls missing a block, rotated, drawn out along z, with a tilted cut through
        // them and the near plane through them too. The truth is the triangles the renderer draws: a triangle turned
        // away from the camera is culled, a point past the cut or nearer than the near plane is discarded, and
        // nothing fills either. A ray meeting a drawn point must pick the hull at that distance; otherwise the nearest
        // drawn point, sampled on each triangle, decides the grab, except within the sampling's error of its edge.
        const float Focal = 200f;
        const int Samples = 48;
        var designs = GameContent.Load().Designs;
        var random = new Random(17);
        var picks = new PickHit[1];
        var grab = TargetPicking.GrabPixels / Focal;
        var cube = MeshBuilder.Cube();
        int exact = 0, padded = 0, missed = 0;
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        for (var c = 0; c < 600; c++)
        {
            var kind = c % 3;
            var design = designs[c / 3 % designs.Length];
            var axis = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            var rotation = Quaternion.CreateFromAxisAngle(axis, random.NextSingle() * 6.28f);
            var stretch = 1f + random.NextSingle();
            var size = kind == 0 ? 0.5f : design.Flight.Radius;
            // The camera is outside the hull, and the near plane goes through it.
            var depth = 4f + 2f * size;
            var near = depth - (0.2f + 0.7f * random.NextSingle()) * size;
            var center = new Vector3((random.NextSingle() - 0.5f) * size, (random.NextSingle() - 0.5f) * size, depth);
            var normal = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            var cutPoint = new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f) * size * 0.8f;
            var mask = kind == 2 ? design.FullMask & ~(1UL << random.Next(design.BlockOffsets.Length)) : design.FullMask;
            var hull = kind == 0
                ? new PickHull(1, new Double3(center.X, center.Y, center.Z), rotation, [Vector3.Zero], 1, 1, 0.87f, stretch, cutPoint, normal)
                : new PickHull(1, new Double3(center.X, center.Y, center.Z), rotation, design.BlockOffsets, design.BlockSize, mask,
                    design.Flight.Radius, stretch, cutPoint, normal, kind == 1 ? design.ExteriorFaces : null);

            // The triangles as the mesh pass places them: one hull mesh, or a cube per block it has.
            triangles.Clear();
            if (kind == 1)
                Add(MeshBuilder.Hull(design), Vector3.Zero, 1);
            else
                for (var b = 0; b < hull.Blocks.Length; b++)
                    if ((mask & 1UL << b) != 0)
                        Add(cube, hull.Blocks[b] with { Z = hull.Blocks[b].Z * stretch }, hull.BlockSize);

            var spread = 1.6f * size / depth;
            var pointer = new Vector3(center.X / depth + (random.NextSingle() - 0.5f) * spread, center.Y / depth + (random.NextSingle() - 0.5f) * spread, 1);
            var ray = Vector3.Normalize(pointer);
            var hit = float.PositiveInfinity;
            var nearest = float.PositiveInfinity;
            var error = 0f;
            foreach (var (a, b, t) in triangles)
            {
                var facing = Vector3.Cross(b - a, t - a);
                if (Vector3.Dot(facing, a) >= 0) continue;
                // Where the ray meets the triangle's plane, if inside it.
                var along = Vector3.Dot(facing, a) / Vector3.Dot(facing, ray);
                var p = ray * along;
                if (along > 0 && Drawn(p) && Vector3.Dot(Vector3.Cross(b - a, p - a), facing) >= 0
                    && Vector3.Dot(Vector3.Cross(t - b, p - b), facing) >= 0 && Vector3.Dot(Vector3.Cross(a - t, p - t), facing) >= 0)
                    hit = Math.Min(hit, along);
                var longest = Math.Max(Vector3.Distance(a, b), Math.Max(Vector3.Distance(b, t), Vector3.Distance(t, a)));
                for (var i = 0; i <= Samples; i++)
                    for (var j = 0; i + j <= Samples; j++)
                    {
                        var s = a + (b - a) * i / Samples + (t - a) * j / Samples;
                        if (!Drawn(s)) continue;
                        var seen = Vector2.Distance(new Vector2(s.X, s.Y) / s.Z, new Vector2(pointer.X, pointer.Y));
                        if (seen < nearest) (nearest, error) = (seen, longest / Samples / near * 1.5f);
                    }
            }

            var count = TargetPicking.Pick(ray, default, Vector3.UnitZ, Focal, [hull], [], picks, nearPlane: near);
            if (float.IsFinite(hit))
            {
                Assert.True(count == 1 && MathF.Abs(picks[0].Distance - hit) < 1e-3f, $"case {c}: drawn at {hit}, picked {count} at {picks[0].Distance}");
                exact++;
                continue;
            }
            // A sliver the samples miss altogether, or a pointer at the grab's edge, is not decided by sampling.
            if (!float.IsFinite(nearest) || MathF.Abs(nearest - grab) < error) continue;
            var expected = nearest < grab ? 1 : 0;
            Assert.True(expected == count, $"case {c} ({kind}): nearest {nearest}, grab {grab}, error {error}");
            padded += expected;
            missed += 1 - expected;

            bool Drawn(Vector3 p) => p.Z >= near && Vector3.Dot(p - center - cutPoint, normal) <= 0;
            void Add(MeshData mesh, Vector3 offset, float scale)
            {
                for (var i = 0; i < mesh.Indices.Count; i += 3)
                {
                    // The mesh winds counter-clockwise seen from outside, which is the side it is drawn from.
                    var (p0, p1, p2) = (Vertex(0), Vertex(1), Vertex(2));
                    Assert.True(Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), Point(mesh.Vertices[(int)mesh.Indices[i]].Normal)) > 0);
                    triangles.Add((Place(p0), Place(p1), Place(p2)));
                    Vector3 Vertex(int k) => Point(mesh.Vertices[(int)mesh.Indices[i + k]].Position);
                    Vector3 Place(Vector3 v)
                    {
                        v *= scale;
                        v.Z *= stretch;
                        return center + Vector3.Transform(v + offset, rotation);
                    }
                }
            }
        }
        Console.WriteLine($"DRAWN TRIANGLES: {exact} exact, {padded} padded, {missed} missed");
        Assert.True(exact > 40 && padded > 40 && missed > 40, $"{exact} exact, {padded} padded, {missed} missed");
    }

    [Fact]
    public void CameraInsideRockPicksForwardExitAndOrdersBySurfaceDistance()
    {
        var rock = Rock(100, default);
        var expected = 100 * AsteroidShape.RadiusAlong(0, Vector3.UnitZ);
        Assert.True(RockPicking.Hit(Vector3.UnitZ, default, rock, out var distance));
        Assert.InRange(MathF.Abs(distance - expected), 0, 0.002f);
        var hits = Pick([], Sector(rock, Rock(1, new Vector3(0, 0, 20))));
        Assert.Equal(2, hits.Length);
        Assert.Equal(1, hits[0].Rock!.Value.Index);
        Assert.Equal(0, hits[1].Rock!.Value.Index);
        Assert.InRange(MathF.Abs(hits[1].Distance - expected), 0, 0.002f);
    }

    [Fact]
    public void TwoHundredFiftySixCutHullsOnTheRayTakeUnderFiveMillisecondsAndAllocateNothing()
    {
        // Intact hulls, cut and drawn out, every one inside the broad test: half on the ray, met face by face, and
        // half just beside it, where every drawn face of every block is clipped and measured for the grab.
        var design = GameContent.Load().Designs[2];
        var radius = design.Flight.Radius;
        var hulls = new PickHull[256];
        for (var i = 0; i < hulls.Length; i++)
            hulls[i] = new PickHull((uint)i + 1, new Double3(i % 2 == 0 ? 0 : radius * 0.9f, 0, 30 + i), Quaternion.CreateFromAxisAngle(Vector3.UnitY, i),
                design.BlockOffsets, design.BlockSize, design.FullMask, radius, 1.5f, new Vector3(0, 0, 0.3f), Vector3.Normalize(new Vector3(1, 0, -1)), design.ExteriorFaces);
        var hits = new PickHit[hulls.Length];
        for (var i = 0; i < 20; i++) TargetPicking.Pick(Vector3.UnitZ, default, Vector3.UnitZ, 1000, hulls, [], hits);
        var start = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var count = 0;
        for (var i = 0; i < 100; i++) count = TargetPicking.Pick(Vector3.UnitZ, default, Vector3.UnitZ, 1000, hulls, [], hits);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 100;
        Console.WriteLine($"PICK 256 HULLS: {milliseconds:F3} ms per pick; {count} hits; {allocated} bytes; budget 5 ms");
        Assert.InRange(count, 128, 256);
        Assert.Equal(0, allocated);
        Assert.True(milliseconds < 5, $"256 hulls: {milliseconds:F3} ms per pick; budget 5 ms");
    }

    [Fact]
    public void SectorOf4096RocksTakesUnderFiveMillisecondsAndAllocatesNothing()
    {
        var rocks = new Asteroid[4096];
        for (var i = 0; i < rocks.Length; i++) rocks[i] = Rock(10, new Vector3(500 + i % 64 * 100, i / 64 * 100, 5000));
        rocks[0] = Rock(100, new Vector3(0, 0, 5000));
        PickSector[] sectors = [Sector(rocks)];
        var hits = new PickHit[rocks.Length];
        for (var i = 0; i < 20; i++) TargetPicking.Pick(Vector3.UnitZ, default, Vector3.UnitZ, 1000, [], sectors, hits);
        var start = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var count = 0;
        for (var i = 0; i < 100; i++) count = TargetPicking.Pick(Vector3.UnitZ, default, Vector3.UnitZ, 1000, [], sectors, hits);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 100;
        Console.WriteLine($"PICK 4096: {milliseconds:F3} ms per pick; {allocated} bytes; budget 5 ms");
        Assert.Equal(1, count);
        Assert.Equal(0, allocated);
        Assert.True(milliseconds < 5, $"4096 rocks: {milliseconds:F3} ms per pick; budget 5 ms");
    }
}
