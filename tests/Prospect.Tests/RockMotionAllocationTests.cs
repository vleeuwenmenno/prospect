using System.Numerics;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class RockMotionAllocationTests
{
    [Fact]
    public void FirstPushNewSectorsGrowthAndRefusalTakeNoMemory()
    {
        var galaxy = GameContent.Load().Galaxy;
        var probe = new AllocationProbe();
        while (probe.Run())
        {
            var field = new RockField(galaxy, 40, 2);
            field.Sector(default);
            field.Sector(new SectorCoord(1, 0, 0));
            for (var i = 0; i < 40; i++)
            {
                var delta = new RockDelta { Rock = new RockRef(new SectorCoord(i % 2, 0, 0), i / 2), Velocity = Vector3.UnitX };
                probe.Start();
                var accepted = field.Set(delta);
                probe.Stop();
                Assert.True(accepted);
            }
            var refused = new RockDelta { Rock = new RockRef(new SectorCoord(2, 0, 0), 0) };
            probe.Start();
            var overflow = field.Set(refused);
            probe.Stop();
            Assert.False(overflow);
            Assert.Equal(2, field.MovedSectors);
            var moved = field.MovedIn(default)!;
            var seen = 0;
            for (var entry = moved.First; entry >= 0; entry = moved.Next(entry))
            {
                Assert.Equal(new RockRef(default, seen), field.DeltaAt(entry).Rock);
                Assert.Equal(entry + 1, moved.EntryOf(seen++));
            }
            Assert.Equal(20, seen);
            var update = new RockDelta { Rock = new RockRef(default, 0), Velocity = Vector3.UnitY };
            probe.Start();
            var updated = field.Set(update);
            probe.Stop();
            Assert.True(updated);
            Assert.Equal(40, field.Deltas.Length);
            Assert.Equal(Vector3.UnitY, field.DeltaAt(moved.EntryOf(0) - 1).Velocity);
        }
        Assert.Equal(0, probe.Bytes);
    }
}
