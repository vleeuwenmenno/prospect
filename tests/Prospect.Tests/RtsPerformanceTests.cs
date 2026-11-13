using System.Diagnostics;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Settings;
using Prospect.Client.World;
using Prospect.Engine.Jobs;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Opt-in matched CPU measurements; ordinary checks do not run timing experiments.</summary>
public sealed class RtsPerformanceTests
{
    [Fact]
    public void MatchedProductionScanAndGeneration()
    {
        var output = Environment.GetEnvironmentVariable("PROSPECT_RTS_CPU");
        if (output is null) return;
        using var writer = new StreamWriter(output);
        writer.WriteLine("kind,scale,x,y,z,population,iteration,ms,bytes,result");
        var content = GameContent.Load();
        foreach (var scale in new[] { 1, 5 })
        {
            var settings = content.SettingCatalog.Build(null, new[] { "asteroidAmount", "asteroidSize", "asteroidFieldSize", "asteroidFrequency", "asteroidSpacing", "asteroidOreShare", "asteroidOreGrade" }.Select(x => $"{x}={scale}").ToArray());
            var galaxy = content.WithSettings(settings).Galaxy.WithSeed(42);
            foreach (var coord in new SectorCoord[] { default, new(-40, 0, -29), new(8, 0, -8), new(-14, 0, -2) })
            {
                var sector = new SectorContent();
                for (var warm = 0; warm < 10; warm++) galaxy.Generate(coord, sector);
                for (var i = 0; i < 100; i++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    galaxy.Generate(coord, sector);
                    var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    writer.WriteLine(FormattableString.Invariant($"generation,{scale},{coord.X},{coord.Y},{coord.Z},{sector.AsteroidCount},{i},{ms},{bytes},{sector.AsteroidCount}"));
                    Assert.Equal(0, bytes);
                }
                for (var i = 0; i < 100; i++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    var count = galaxy.Summarize(coord).AsteroidCount;
                    var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    writer.WriteLine(FormattableString.Invariant($"summary,{scale},{coord.X},{coord.Y},{coord.Z},{sector.AsteroidCount},{i},{ms},{bytes},{count}"));
                }
            }
        }
        foreach (var population in new[] { 8192, 40960, 2088960 })
        {
            using var jobs = new JobSystem(1, "CpuVisibility");
            var actions = ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));
            var world = new WorldView(new InputDevices(), actions, new ClientSettings(), jobs, null!, content.Designs);
            var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            var type = typeof(WorldView);
            void Set(string name, object value) => type.GetField(name, flags)!.SetValue(world, value);
            Set("_galaxy", content.Galaxy);
            Set("_viewport", new float2(1280, 720));
            var slots = (Array)type.GetField("_slots", flags)!.GetValue(world)!;
            var slotType = slots.GetType().GetElementType()!;
            var active = (population + 20479) / 20480;
            var count = Expression.Variable(typeof(int), "count");
            var body = new List<Expression> { Expression.Assign(count, Expression.Constant(3)) };
            var camera = new Double3(20000, 0, 0);
            for (var slotIndex = 0; slotIndex < active; slotIndex++)
            {
                var slot = slots.GetValue(slotIndex)!;
                void Field(string name, object value) => slotType.GetField(name, flags)!.SetValue(slot, value);
                var coord = new SectorCoord(slotIndex, 0, 0);
                var n = Math.Min(20480, population - slotIndex * 20480);
                Field("Galaxy", content.Galaxy); Field("Coord", coord); Field("State", 2); Field("Displaced", true); Field("Offsets", new Vector3[n]);
                var sector = (SectorContent)slotType.GetField("Content", flags)!.GetValue(slot)!;
                sector.AsteroidCount = n; sector.Asteroids = new Asteroid[n];
                for (var i = 0; i < n; i++) sector.Asteroids[i] = new Asteroid { Radius = 30 + i % 100, Rotation = Quaternion.Identity, LocalPosition = new Double3(20000, 0, -10000).RelativeTo(content.Galaxy.CenterOf(coord)) };
                if (type.GetMethod("AddRocks", flags) is { } old)
                    body.Add(Expression.Call(Expression.Constant(world), old, Expression.Constant(slot, slotType), Expression.Constant(content.Galaxy), Expression.Constant(camera), Expression.Constant(1280f / 720), count));
            }
            if (type.GetMethod("PrepareRockInstances", flags) is { } current)
                body.Add(Expression.Call(Expression.Constant(world), current, Expression.Constant(camera), Expression.Constant(1280f / 720), count));
            body.Add(count);
            var run = Expression.Lambda<Func<int>>(Expression.Block([count], body)).Compile();
            var meshCounts = (int[])type.GetField("_meshCounts", flags)!.GetValue(world)!;
            for (var i = 0; i < 20; i++) { Array.Clear(meshCounts); run(); }
            for (var i = 0; i < 100; i++)
            {
                Array.Clear(meshCounts);
                var before = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                var result = run();
                var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                writer.WriteLine(FormattableString.Invariant($"scan-transform,0,0,0,0,{population},{i},{ms},{bytes},{result}"));
                Assert.Equal(Math.Min(32768, population + 3), result);
                Assert.Equal(0, bytes);
            }
        }
    }
}
