using Prospect.Client.Screens;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class CargoWindowTests
{
    private static readonly GameContent Content = GameContent.Load(Path.Combine(AppContext.BaseDirectory, "content"));

    private static SessionSnapshot MakeSnapshot(byte designIndex, float cargoHold, HoldContainer[] cargo, long cargoTick = 1, JumpPhase jump = JumpPhase.None, uint hullId = 10)
    {
        var snapshot = new SessionSnapshot(Content)
        {
            Galaxy = Content.Galaxy.WithSeed(17),
            State = SessionState.Active,
            PlayerId = 1,
            OwnHull = 0,
            HullCount = 1,
            OwnCargoTick = cargoTick,
        };
        Array.Copy(cargo, snapshot.OwnCargo, cargo.Length);
        snapshot.Hulls[0] = new HullView
        {
            Id = hullId,
            Owner = 1,
            Design = designIndex,
            IsOwn = true,
            Jump = jump,
            Stats = new HullStats { CargoHold = cargoHold },
            Cargo = snapshot.OwnCargo.AsMemory(0, cargo.Length),
        };
        return snapshot;
    }

    private static byte ShipDesignIndex()
    {
        for (byte i = 0; i < Content.Designs.Length; i++)
            if (Content.Designs[i].BlockSize >= 1f)
                return i;
        throw new InvalidOperationException("No ship design found.");
    }

    private static byte DroneDesignIndex()
    {
        for (byte i = 0; i < Content.Designs.Length; i++)
            if (Content.Designs[i].BlockSize < 1f)
                return i;
        throw new InvalidOperationException("No drone design found.");
    }

    [Fact]
    public void TotalsAndGatesReflectShipDroneRacksAndFtlTransit()
    {
        var view = new CargoHoldView();
        var ship = ShipDesignIndex();
        var drone = DroneDesignIndex();

        // Ship with two containers: size 1 (2 u, 100 kg) and size 2 (4 u, 250 kg).
        var cargo = new[]
        {
            new HoldContainer(0, 1, 0, 100.0, 80f, default),
            new HoldContainer(1, 2, -1, 0.0, 200f, default),
        };
        var snap = MakeSnapshot(ship, 16f, cargo, cargoTick: 5, jump: JumpPhase.None);
        view.Update(snap);

        Assert.True(view.HasHull);
        Assert.False(view.IsDrone);
        Assert.True(view.HasRacks);
        Assert.False(view.InFtlTransit);
        Assert.Equal(6, view.UsedUnits);
        Assert.Equal(16, view.CapacityUnits);
        Assert.Equal(100.0, view.TotalMassKg);
        Assert.Equal(2, view.ContainerCount);

        // FTL transit flag.
        var ftlSnap = MakeSnapshot(ship, 16f, cargo, cargoTick: 5, jump: JumpPhase.Tunnel);
        view.Update(ftlSnap);
        Assert.True(view.InFtlTransit);

        // Ship without racks.
        var noRacks = MakeSnapshot(ship, 0f, []);
        view.Update(noRacks);
        Assert.False(view.HasRacks);
        Assert.Equal(0, view.UsedUnits);

        // Escape drone.
        var droneSnap = MakeSnapshot(drone, 0f, []);
        view.Update(droneSnap);
        Assert.True(view.IsDrone);
        Assert.False(view.HasRacks);
    }

    [Fact]
    public void SelectionClearsWhenHoldTickOrHullChanges()
    {
        var view = new CargoHoldView();
        var ship = ShipDesignIndex();
        var cargo = new[]
        {
            new HoldContainer(0, 0, 0, 50.0, 50f, default),
            new HoldContainer(0, 1, 1, 120.0, 100f, default),
        };

        var snap = MakeSnapshot(ship, 8f, cargo, cargoTick: 10, hullId: 10);
        view.Update(snap);
        view.Select(1);
        Assert.Equal(1, view.SelectedIndex);

        // Same tick and hull: selection stays.
        view.Update(snap);
        Assert.Equal(1, view.SelectedIndex);

        // Hold tick advances (e.g. after jettison): selection clears.
        var afterJettison = MakeSnapshot(ship, 8f, [cargo[0]], cargoTick: 11, hullId: 10);
        view.Update(afterJettison);
        Assert.Equal(-1, view.SelectedIndex);

        // Select again, then change hull id: selection clears.
        view.Select(0);
        Assert.Equal(0, view.SelectedIndex);
        var otherHull = MakeSnapshot(ship, 8f, [cargo[0]], cargoTick: 11, hullId: 11);
        view.Update(otherHull);
        Assert.Equal(-1, view.SelectedIndex);
    }

    [Fact]
    public void TileSizeGrowsWithContainerClassAndLayoutWrapsWithoutOverlap()
    {
        var prevArea = 0f;
        for (var size = 0; size < Content.Cargo.Sizes.Length; size++)
        {
            var (w, h) = CargoHoldView.TileSizeUnscaled(size);
            Assert.True(w > 0f && h > 0f);
            var area = w * h;
            Assert.True(area >= prevArea);
            prevArea = area;
        }
        Assert.True(CargoHoldView.TileSizeUnscaled(7).Width > CargoHoldView.TileSizeUnscaled(0).Width);

        var view = new CargoHoldView();
        var ship = ShipDesignIndex();
        var cargo = new[]
        {
            new HoldContainer(0, 0, 0, 10, 50, default),
            new HoldContainer(0, 2, 0, 20, 200, default),
            new HoldContainer(0, 4, 0, 30, 800, default),
            new HoldContainer(0, 6, 0, 40, 3200, default),
        };
        view.Update(MakeSnapshot(ship, 128f, cargo));

        // Narrow width forces wrapping across multiple rows.
        var totalHeight = view.Layout(cargo, 150f, 1f);
        var tiles = view.Tiles;
        Assert.Equal(4, tiles.Length);
        Assert.True(totalHeight > tiles[0].Height);

        for (var i = 0; i < tiles.Length; i++)
        {
            Assert.True(tiles[i].X >= 0f && tiles[i].Y >= 0f);
            Assert.True(tiles[i].X + tiles[i].Width <= 150f || tiles[i].X == 0f);
            for (var j = i + 1; j < tiles.Length; j++)
            {
                var a = tiles[i];
                var b = tiles[j];
                var overlapX = a.X < b.X + b.Width && a.X + a.Width > b.X;
                var overlapY = a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
                Assert.False(overlapX && overlapY);
            }
        }
    }

    [Fact]
    public void FillAndHealthFractionsHandleEmptyAndFullContainers()
    {
        var ship = ShipDesignIndex();
        var unit = Content.Cargo.UnitSize;
        var units = Content.Cargo.Sizes[1].Units;
        var density = Content.Cargo.Density(Content.Galaxy.Resources[0].Id);
        var halfMass = 0.5 * units * unit * unit * unit * density;

        var cargo = new[]
        {
            new HoldContainer(0, 1, 0, halfMass, 50f, default),
            new HoldContainer(0, 1, -1, 0.0, 100f, default),
        };
        var snap = MakeSnapshot(ship, 8f, cargo);

        Assert.InRange(CargoHoldView.FillFraction(snap, cargo[0]), 0.49f, 0.51f);
        Assert.InRange(CargoHoldView.HealthFraction(snap, cargo[0]), 0.49f, 0.51f);
        Assert.Equal(0f, CargoHoldView.FillFraction(snap, cargo[1]));
        Assert.Equal(1f, CargoHoldView.HealthFraction(snap, cargo[1]));
    }

    [Fact]
    public void UpdateAndLayoutAllocateZeroManagedBytes()
    {
        var view = new CargoHoldView();
        var ship = ShipDesignIndex();
        var cargo = new[]
        {
            new HoldContainer(0, 1, 0, 100, 100, default),
            new HoldContainer(0, 3, 1, 400, 400, default),
            new HoldContainer(0, 5, -1, 0, 1600, default),
        };
        var snap = MakeSnapshot(ship, 64f, cargo);

        // Warm up once, then probe steady-state frame updates and layout.
        view.Update(snap);
        view.Layout(cargo, 300f, 1f);

        var probe = new AllocationProbe();
        while (probe.Measuring())
        {
            view.Update(snap);
            view.Layout(cargo, 300f, 1f);
            _ = CargoHoldView.FillFraction(snap, cargo[0]);
            _ = CargoHoldView.HealthFraction(snap, cargo[0]);
        }
        Assert.Equal(0, probe.Bytes);
    }
}
