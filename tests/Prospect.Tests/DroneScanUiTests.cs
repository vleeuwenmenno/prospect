using System.Numerics;
using Prospect.Client.Screens;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The drone scans in the interface apart from drawing: the keys, the chip's words, the hologram's switch and its geometry.</summary>
public sealed class DroneScanUiTests
{
    private static readonly string Content = Path.Combine(AppContext.BaseDirectory, "content");
    private static readonly RockRef Rock = new(new SectorCoord(1, 0, 2), 7);
    private static readonly RockRef Other = new(new SectorCoord(1, 0, 2), 8);

    private static BayView View(DroneScanPhase phase, RockRef? rock = null)
    {
        var view = new BayView { Hull = 5, Count = 1, ScanPhase = phase, ScanRock = phase == DroneScanPhase.None ? default : rock ?? Rock };
        view.Bays[0] = new BayEntry { Capacity = 4, Stocked = 4, Live = true, HatchClear = true };
        return view;
    }

    private static DroneScanCommandMessage Sent(PlayerInputBridge bridge)
    {
        Assert.True(bridge.ConsumeDroneScan(out var message));
        Assert.False(bridge.ConsumeDroneScan(out _));
        return message;
    }

    [Fact]
    public void AKeyWithNothingUnderWayAsksForAScanOfTheTargetedRockAndWithoutOneDoesNothing()
    {
        var bridge = new PlayerInputBridge();
        Assert.Equal(DroneScanKeyAction.None, DroneScanUi.Press(View(DroneScanPhase.None), 5, null, bridge));
        Assert.False(bridge.ConsumeDroneScan(out _));
        Assert.Equal(DroneScanKeyAction.Request, DroneScanUi.Press(View(DroneScanPhase.None), 5, Rock, bridge));
        var message = Sent(bridge);
        Assert.Equal(DroneScanAction.Request, message.Action);
        Assert.Equal(Rock, message.Rock);
        Assert.Equal(5u, message.Hull);
    }

    [Fact]
    public void WhenTheDronesAreInPlaceTheKeyOrdersTheScan()
    {
        var bridge = new PlayerInputBridge();
        Assert.Equal(DroneScanKeyAction.Start, DroneScanUi.Press(View(DroneScanPhase.Ready), 5, Other, bridge));
        Assert.Equal(DroneScanAction.Start, Sent(bridge).Action);
    }

    [Fact]
    public void WhileMovingOrScanningTheKeyGivesItUp()
    {
        foreach (var phase in new[] { DroneScanPhase.Moving, DroneScanPhase.Scanning })
        {
            var bridge = new PlayerInputBridge();
            Assert.Equal(DroneScanKeyAction.Cancel, DroneScanUi.Press(View(phase), 5, Rock, bridge));
            Assert.Equal(DroneScanAction.Cancel, Sent(bridge).Action);
        }
    }

    [Fact]
    public void AScanThatIsReturningCanBeAskedAgainSoTheServerSaysBusy()
    {
        var bridge = new PlayerInputBridge();
        Assert.Equal(DroneScanKeyAction.Request, DroneScanUi.Press(View(DroneScanPhase.Returning), 5, Rock, bridge));
        Assert.Equal(DroneScanAction.Request, Sent(bridge).Action);
    }

    [Fact]
    public void NothingHappensForAnotherHullOrOneWithoutBays()
    {
        var bridge = new PlayerInputBridge();
        Assert.Equal(DroneScanKeyAction.None, DroneScanUi.Press(View(DroneScanPhase.Ready), 6, Rock, bridge));
        var noBays = new BayView { Hull = 5, Count = 0 };
        Assert.Equal(DroneScanKeyAction.None, DroneScanUi.Press(noBays, 5, Rock, bridge));
        Assert.False(DroneScanUi.StartIfReady(noBays, 5, bridge));
        Assert.False(bridge.ConsumeDroneScan(out _));
    }

    [Fact]
    public void TheButtonsStartOnlyAReadyScanAndCancelOnlyOneUnderWay()
    {
        var bridge = new PlayerInputBridge();
        Assert.False(DroneScanUi.StartIfReady(View(DroneScanPhase.Moving), 5, bridge));
        Assert.False(DroneScanUi.CancelIfActive(View(DroneScanPhase.None), 5, bridge));
        Assert.False(DroneScanUi.CancelIfActive(View(DroneScanPhase.Returning), 5, bridge));
        Assert.False(bridge.ConsumeDroneScan(out _));
        Assert.True(DroneScanUi.StartIfReady(View(DroneScanPhase.Ready), 5, bridge));
        Assert.Equal(DroneScanAction.Start, Sent(bridge).Action);
        foreach (var phase in new[] { DroneScanPhase.Moving, DroneScanPhase.Ready, DroneScanPhase.Scanning })
        {
            Assert.True(DroneScanUi.CancelIfActive(View(phase), 5, bridge));
            Assert.Equal(DroneScanAction.Cancel, Sent(bridge).Action);
        }
    }

    [Fact]
    public void TheHullsScanKnowsItsRockItsTimeLeftAndItsProgress()
    {
        var view = View(DroneScanPhase.Scanning);
        view.ScanStarted = 100;
        view.ScanCompletes = 100 + 6 * TickLoop.SimulationHz;
        Assert.True(DroneScanUi.OnRock(view, Rock));
        Assert.False(DroneScanUi.OnRock(view, Other));
        Assert.False(DroneScanUi.OnRock(View(DroneScanPhase.None), Rock));
        Assert.Equal(6f, DroneScanUi.SecondsLeft(view, 100), 3);
        Assert.Equal(3f, DroneScanUi.SecondsLeft(view, 100 + 3 * TickLoop.SimulationHz), 3);
        Assert.Equal(0f, DroneScanUi.SecondsLeft(view, 100 + 9 * TickLoop.SimulationHz));
        Assert.Equal(0.5f, DroneScanUi.Progress(view, 100 + 3 * TickLoop.SimulationHz), 3);
        Assert.Equal(1f, DroneScanUi.Progress(view, 100 + 99 * TickLoop.SimulationHz));
        Assert.Equal(0f, DroneScanUi.SecondsLeft(View(DroneScanPhase.Ready), 100));
        Assert.Equal(0f, DroneScanUi.Progress(View(DroneScanPhase.Moving), 100));
    }

    [Fact]
    public void EveryScanAnswerHasWordsAndOnlyRefusalsAreRefusals()
    {
        foreach (var reply in Enum.GetValues<DroneReply>())
            if (reply >= DroneReply.ScanSent)
                Assert.NotEmpty(DroneUi.Words(reply).ToArray());
        Assert.Equal("SCAN SENT", System.Text.Encoding.UTF8.GetString(DroneUi.Words(DroneReply.ScanSent)));
        Assert.Equal("NOT IN PLACE", System.Text.Encoding.UTF8.GetString(DroneUi.Words(DroneReply.ScanNotReady)));
        Assert.Equal("NO DRONE SCANS", System.Text.Encoding.UTF8.GetString(DroneUi.Words(DroneReply.ScanUnavailable)));
        foreach (var good in new[] { DroneReply.ScanSent, DroneReply.ScanReady, DroneReply.ScanRunning, DroneReply.ScanDone, DroneReply.ScanCancelled })
            Assert.False(DroneUi.Refused(good), good.ToString());
        foreach (var bad in new[] { DroneReply.TooLarge, DroneReply.ScanBusy, DroneReply.ScanNotReady, DroneReply.ScanAborted, DroneReply.ScanTimedOut, DroneReply.ScanUnavailable })
        {
            Assert.True(DroneUi.Refused(bad), bad.ToString());
            Assert.True(DroneUi.IsScanReply(bad));
        }

        Assert.False(DroneUi.IsScanReply(DroneReply.Launching));
    }

    [Fact]
    public void PunctuationKeysAreWrittenAsTheirMarkOnTheNarrowButtons()
    {
        Assert.Equal(";", DroneScanUi.ShortKey("SEMICOLON"));
        Assert.Equal("'", DroneScanUi.ShortKey("APOSTROPHE"));
        Assert.Equal("\\", DroneScanUi.ShortKey("BACKSLASH"));
        Assert.Equal("UNBOUND", DroneScanUi.ShortKey("UNBOUND"));
    }

    [Fact]
    public void TheFooterHeightGrowsForTheLegend()
    {
        var look = ModuleLook.Load(Path.Combine(Content, "visuals", "modules.json")).DroneScan;
        var plain = DroneScanUi.RecordFooterHeight(look, false);
        var legend = DroneScanUi.RecordFooterHeight(look, true);
        Assert.True(plain > look.LineHeight * 2);
        Assert.Equal(look.LineHeight, legend - plain);
        Assert.True(DroneScanUi.ButtonBlockHeight(look) > look.ButtonHeight);
    }

    private static DroneScanKnowledgeView Knowledge(params (RockRef Rock, long Tick)[] records)
    {
        var view = new DroneScanKnowledgeView();
        foreach (var (rock, tick) in records)
            view.Records[view.Count++] = new DroneScanRecord { Owner = 1, Sector = rock.Sector, Index = rock.Index, RecordedTick = tick, Radius = 10f };
        view.Revision++;
        return view;
    }

    [Fact]
    public void TheHologramTurnsOnWhenAResultArrivesForTheTargetedRockNotForWhatWasKnown()
    {
        var state = new ScanHologramState();
        var known = Knowledge((Rock, 50));
        state.Observe(known, Rock, null);
        Assert.False(state.On);

        // A scan of a rock nobody looks at leaves it off.
        known.Records[known.Count++] = new DroneScanRecord { Owner = 1, Sector = Rock.Sector, Index = Rock.Index + 6, RecordedTick = 70, Radius = 10f };
        known.Revision++;
        state.Observe(known, Rock, null);
        Assert.False(state.On);

        // The same rock scanned again, newer: on.
        known.Records[0].RecordedTick = 80;
        known.Revision++;
        state.Observe(known, Rock, null);
        Assert.True(state.On);
    }

    [Fact]
    public void AResultForTheRockOfTheHullsScanAlsoTurnsItOnAndTheKeyTogglesIt()
    {
        var state = new ScanHologramState();
        var known = Knowledge();
        state.Observe(known, null, null);
        known.Records[known.Count++] = new DroneScanRecord { Owner = 1, Sector = Rock.Sector, Index = Rock.Index, RecordedTick = 10, Radius = 10f };
        known.Revision++;
        state.Observe(known, Other, Rock);
        Assert.True(state.On);
        state.Toggle();
        Assert.False(state.On);
        // Looking at it again without a new result changes nothing: the player's choice stands.
        state.Observe(known, Rock, Rock);
        Assert.False(state.On);
        state.Toggle();
        Assert.True(state.On);
    }

    [Fact]
    public void ANewSessionWithFewerResultsIsReadAsFreshNotAsNew()
    {
        var state = new ScanHologramState();
        var old = Knowledge((Rock, 5000));
        old.Revision = 9;
        state.Observe(old, Rock, null);
        var fresh = Knowledge((Rock, 40));
        fresh.Revision = 1;
        state.Observe(fresh, Rock, null);
        Assert.False(state.On);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The hologram's geometry
    // ---------------------------------------------------------------------------------------------------------------

    private static ModuleLook.DroneScanLook Look() => ModuleLook.Load(Path.Combine(Content, "visuals", "modules.json")).DroneScan;

    /// <summary>A camera 100 m from the world's origin looking along -Z, so +Z of the world is toward it, in a 1920 by 1080 window.</summary>
    private static HologramCamera Camera() => new(new Double3(0, 0, 100), Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ, MathF.Tan(MathF.PI / 8), new Vector2(1920, 1080));

    private static short Share(float metres, float radius) => (short)MathF.Round(metres / radius * 32767f);

    private static DroneScanRecord Record(float radius = 10f)
    {
        var record = new DroneScanRecord { Owner = 1, Radius = radius, Resource = 0, Grade = 0.4f };
        record.Map.VeinCount = 2;
        record.Map.Veins[0] = new DroneScanVein { Points = 3, Resource = 2, Grade = 200 };
        record.Map.Veins[1] = new DroneScanVein { Points = 2, Resource = 1, Grade = 100 };
        void Point(int vein, int k, float x, float y, float z, float r) =>
            record.Map.Points[vein * RockInteriorRules.MaxVeinPoints + k] = new DroneScanPoint { X = Share(x, radius), Y = Share(y, radius), Z = Share(z, radius), R = (ushort)MathF.Round(r / radius * 65535f) };
        // Vein 0 runs from the near side (+Z, toward the camera) to the far side; vein 1 lies across the middle.
        Point(0, 0, 0, 0, 8, 0.5f);
        Point(0, 1, 0, 0, 0, 0.5f);
        Point(0, 2, 0, 0, -8, 0.5f);
        Point(1, 0, -5, 3, 0, 0.02f);
        Point(1, 1, 5, 3, 0, 0.02f);
        record.Map.PocketCount = 2;
        record.Map.Pockets[0] = new DroneScanPocket { X = Share(4, radius), Y = Share(-4, radius), Z = Share(6, radius), R = (ushort)MathF.Round(1.5f / radius * 65535f), Kind = RockPocketKind.Ore, Resource = 1 };
        record.Map.Pockets[1] = new DroneScanPocket { X = Share(-4, radius), Y = Share(4, radius), Z = Share(-6, radius), R = (ushort)MathF.Round(1.5f / radius * 65535f), Kind = RockPocketKind.Volatile, Resource = -1 };
        for (var cell = 0; cell < DroneScanning.HeatCells; cell++)
        {
            record.Map.Surface[cell] = 255;
            record.Map.Heat[cell] = (byte)(cell == 0 ? 0x80 : cell % 2 == 0 ? 127 : 0);
        }

        return record;
    }

    [Fact]
    public void TheHologramProjectsVeinsPocketsAndHeatCellsAndDimsTheFarSide()
    {
        var look = Look();
        var hologram = new ScanHologram();
        Assert.True(hologram.Build(Record(), Camera(), default, Quaternion.Identity, look));
        Assert.Equal(3, hologram.SegmentCount);
        Assert.Equal(2, hologram.PocketCount);
        Assert.Equal(DroneScanning.HeatCells, hologram.DotCount);

        // Vein 0's first segment is on the near side, its second on the far side.
        Assert.True(hologram.Segments[0].Strength > hologram.Segments[1].Strength);
        Assert.True(hologram.Segments[1].Strength >= look.FarStrength - 1e-4f);
        Assert.True(hologram.Segments[0].Strength <= 1f);
        Assert.Equal(2, hologram.Segments[0].Resource);
        Assert.Equal(1, hologram.Segments[2].Resource);

        // A vein is clamped to the figures however big or small it is on screen.
        foreach (var i in new[] { 0, 1, 2 })
        {
            Assert.InRange(hologram.Segments[i].Width, look.VeinMinPixels, look.VeinMaxPixels);
            Assert.True(float.IsFinite(hologram.Segments[i].A.X) && float.IsFinite(hologram.Segments[i].B.Y));
        }

        Assert.Equal(look.VeinMaxPixels, hologram.Segments[0].Width);
        Assert.Equal(look.VeinMinPixels, hologram.Segments[2].Width);

        // The near pocket is stronger than the far one, which is the unstable one; each has at least the minimum size.
        Assert.False(hologram.Pockets[0].Unstable);
        Assert.True(hologram.Pockets[1].Unstable);
        Assert.True(hologram.Pockets[0].Strength > hologram.Pockets[1].Strength);
        Assert.True(hologram.Pockets[1].Radius >= look.PocketMinPixels * 0.5f);

        // The heat cells: cell 0 is unstable, the even ones easy, the odd ones faint.
        Assert.Equal(HologramDot.Unstable, hologram.Dots[0].Kind);
        Assert.Equal(HologramDot.Easy, hologram.Dots[2].Kind);
        Assert.Equal(1f, hologram.Dots[2].Ease, 3);
        Assert.Equal(HologramDot.Faint, hologram.Dots[1].Kind);
    }

    [Fact]
    public void TheRocksTurnMovesTheMapWithIt()
    {
        var look = Look();
        var a = new ScanHologram();
        var b = new ScanHologram();
        Assert.True(a.Build(Record(), Camera(), default, Quaternion.Identity, look));
        Assert.True(b.Build(Record(), Camera(), default, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), look));
        // Half a turn about the vertical swaps the near end of vein 0 with its far end, and with it their strengths.
        Assert.Equal(a.Segments[0].Strength, b.Segments[1].Strength, 3);
        Assert.True(b.Pockets[0].Strength < a.Pockets[0].Strength);
    }

    [Fact]
    public void ARockBehindTheCameraBuildsNothing()
    {
        var look = Look();
        var hologram = new ScanHologram();
        Assert.False(hologram.Build(Record(), Camera(), new Double3(0, 0, 500), Quaternion.Identity, look));
        Assert.Equal(0, hologram.SegmentCount + hologram.PocketCount + hologram.DotCount);
    }

    [Fact]
    public void TheFarSideFadesLinearlyThroughTheCentre()
    {
        var look = Look();
        Assert.Equal(1f, ScanHologram.Strength(90, 100, 10, look), 4);
        Assert.Equal((1f + look.FarStrength) * 0.5f, ScanHologram.Strength(100, 100, 10, look), 4);
        Assert.Equal(look.FarStrength, ScanHologram.Strength(110, 100, 10, look), 4);
        Assert.Equal(look.FarStrength, ScanHologram.Strength(500, 100, 10, look), 4);
        Assert.Equal(1f, ScanHologram.Strength(1, 100, 10, look), 4);
    }

    [Fact]
    public void BuildingTheHologramTakesNoMemory()
    {
        var look = Look();
        var hologram = new ScanHologram();
        var record = Record();
        var camera = Camera();
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
        Assert.True(hologram.Build(record, camera, default, turn, look));
        var probe = new AllocationProbe();
        while (probe.Measuring())
            hologram.Build(record, camera, default, turn, look);
        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void TheShippedFiguresLoadAndAnUnknownColourOrOutOfRangeFigureIsRefused()
    {
        var path = Path.Combine(Content, "visuals", "modules.json");
        var text = File.ReadAllText(path);
        Assert.NotNull(ModuleLook.Load(path).DroneScan);
        var scratch = Path.Combine(Path.GetTempPath(), "prospect-modules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var bad = Path.Combine(scratch, "modules.json");
            File.WriteAllText(bad, text.Replace("\"unstableColor\": \"FF4D4D\"", "\"unstableColor\": \"red\""));
            Assert.Throws<InvalidDataException>(() => ModuleLook.Load(bad));
            File.WriteAllText(bad, text.Replace("\"veinMaxPixels\": 12", "\"veinMaxPixels\": 0.1"));
            Assert.Throws<InvalidDataException>(() => ModuleLook.Load(bad));
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }
}
