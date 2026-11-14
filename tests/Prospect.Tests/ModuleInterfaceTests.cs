using Brutal.GlfwApi;
using Prospect.Client.Input;
using Prospect.Client.Screens;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The module interface apart from drawing: the content that colours it, the gates and keys, the knowledge lists the
/// windows read, the locator overlay's strengths and the real server answering the same requests the keys make.
/// </summary>
public sealed class ModuleInterfaceTests
{
    private static readonly string Content = Path.Combine(AppContext.BaseDirectory, "content");
    private static readonly GameContent Game = GameContent.Load();

    private static ModuleLook Look() => ModuleLook.Load(Path.Combine(Content, "visuals", "modules.json"));

    [Fact]
    public void EveryQualityHasItsOwnWordAndColourAndEveryClassItsOwnColour()
    {
        var look = Look();
        var colours = new HashSet<uint>();
        foreach (var quality in Enum.GetValues<ModuleQuality>())
        {
            var tier = look.Tier(quality);
            Assert.NotEmpty(tier.Label);
            Assert.True(colours.Add(tier.Rgb), $"{quality} shares a colour");
        }

        Assert.Equal(6, colours.Count);
        var classes = new HashSet<uint>();
        foreach (var cls in look.Classes)
            Assert.True(classes.Add(cls.Rgb));
        foreach (var family in new[] { ModuleFamily.GasScanner, ModuleFamily.AsteroidScanner, ModuleFamily.AsteroidLocator })
            Assert.NotEmpty(look.Family(family).Short);
    }

    [Fact]
    public void TheLocatorClassesCoverEveryResourceAndBareRockFromTheContent()
    {
        var look = Look();
        var galaxy = Game.Galaxy;
        var bare = look.ClassOf(galaxy, -1);
        Assert.True(look.Classes[bare].Bare);
        for (var r = 0; r < galaxy.Resources.Length; r++)
            Assert.False(look.Classes[look.ClassOf(galaxy, r)].Bare, galaxy.Resources[r].Id);
        var ice = Array.FindIndex(galaxy.Resources, x => x.Id == "ice");
        var iron = Array.FindIndex(galaxy.Resources, x => x.Id == "iron");
        var gold = Array.FindIndex(galaxy.Resources, x => x.Id == "gold");
        Assert.Equal("Ice", look.Classes[look.ClassOf(galaxy, ice)].Name);
        Assert.Equal("Common metal", look.Classes[look.ClassOf(galaxy, iron)].Name);
        Assert.Equal("Precious metal", look.Classes[look.ClassOf(galaxy, gold)].Name);
        // A resource index the galaxy does not have is bare rock, never an out-of-range read.
        Assert.Equal(bare, look.ClassOf(galaxy, 99));
    }

    [Fact]
    public void TheLookRefusesAFileThatNamesAColourWrongOrLeavesATierOut()
    {
        var text = File.ReadAllText(Path.Combine(Content, "visuals", "modules.json"));
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, text.Replace("B9C2CE", "nothex"));
            Assert.Throws<InvalidDataException>(() => ModuleLook.Load(path));
            File.WriteAllText(path, text.Replace("\"Legendary\"", "\"Mythic\""));
            Assert.Throws<InvalidDataException>(() => ModuleLook.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ModulesHaveTheirOwnEditorTabAndNoOtherPartIsOnIt()
    {
        var tuning = EditorTuning.Load(Path.Combine(Content, "visuals", "editor.json"));
        var tab = Array.Find(tuning.Categories, c => c.Name == "Module")!;
        var listed = Game.Blocks.Types.Where(t => tuning.Lists(tab, t)).Select(t => t.Id).Order().ToArray();
        Assert.Equal(["asteroidLocator", "asteroidScanner", "gasScanner"], listed);
        foreach (var id in listed)
            Assert.Equal(PartRole.Module, EditorGeometry.Role(Game.Blocks.Get(id)));
        var palette = new EditorPalette { Category = Array.IndexOf(tuning.Categories, tab) };
        Assert.True(palette.Shows(tuning, Game.Blocks.Get("gasScanner")));
        Assert.False(palette.Shows(tuning, Game.Blocks.Get("cargo")));
        // The search finds a module by what it is for.
        Assert.True(EditorPalette.Matches(tuning, Game.Blocks.Get("asteroidLocator"), "locator"));
    }

    [Fact]
    public void TheModuleKeysAreFreeAndEachActionIsBoundOnceAcrossTheCatalogue()
    {
        var actions = ActionMap.Load(Path.Combine(Content, "input", "bindings.json"));
        var mine = new[] { InputAction.ScanGas, InputAction.ScanAsteroid, InputAction.PulseLocator, InputAction.ToggleGas };
        foreach (var action in mine)
        {
            Assert.NotEmpty(actions.Bindings(action).ToArray());
            for (var other = 0; other < (int)InputAction.Count; other++)
            {
                if ((InputAction)other == action)
                    continue;
                foreach (var binding in actions.Bindings(action))
                    Assert.DoesNotContain(binding, actions.Bindings((InputAction)other).ToArray());
            }
        }

        // No axis half takes one of the keys either.
        foreach (var axis in Enum.GetValues<InputAxis>().Where(a => a != InputAxis.Count))
            foreach (var pair in actions.Bindings(axis))
                foreach (var action in mine)
                    foreach (var binding in actions.Bindings(action))
                        Assert.True(pair.Negative != binding && pair.Positive != binding);
    }

    private static ModuleEquipment Equipment(ModuleQuality quality = ModuleQuality.Common) => new(1, quality, 1, 3000, 0.2, 5, 0.75, 10);

    [Fact]
    public void TheGateSaysWhyAModuleCannotRunAndAnAbsentModuleShowsNothing()
    {
        var scan = new ModuleScanView();
        var none = default(ModuleTargets);
        Assert.Equal(ModuleGate.Absent, ModuleRules.Gate(ModuleFamily.AsteroidLocator, default, scan, 7, 100, none));
        Assert.Equal(ModuleGate.Absent, ModuleRules.Gate(ModuleFamily.AsteroidLocator, Equipment(), scan, 0, 100, none));
        Assert.Equal(ModuleGate.Ready, ModuleRules.Gate(ModuleFamily.AsteroidLocator, Equipment(), scan, 7, 100, none));
        Assert.Equal(ModuleGate.NoTarget, ModuleRules.Gate(ModuleFamily.AsteroidScanner, Equipment(), scan, 7, 100, none));
        Assert.Equal(ModuleGate.NoCloud, ModuleRules.Gate(ModuleFamily.GasScanner, Equipment(), scan, 7, 100, none));
        var rock = new ModuleTargets { Rock = new RockRef(default, 3) };
        Assert.Equal(ModuleGate.OutOfRange, ModuleRules.Gate(ModuleFamily.AsteroidScanner, Equipment(), scan, 7, 100, rock));
        rock.RockInRange = true;
        Assert.Equal(ModuleGate.Ready, ModuleRules.Gate(ModuleFamily.AsteroidScanner, Equipment(), scan, 7, 100, rock));
        var cloud = new ModuleTargets { HasCloud = true };
        Assert.Equal(ModuleGate.OutOfRange, ModuleRules.Gate(ModuleFamily.GasScanner, Equipment(), scan, 7, 100, cloud));
        cloud.CloudInRange = true;
        Assert.Equal(ModuleGate.Ready, ModuleRules.Gate(ModuleFamily.GasScanner, Equipment(), scan, 7, 100, cloud));

        // One request at a time, for the hull that began it; the cooldown is the owner's, whatever hull is flown.
        var running = new ModuleScanView { Hull = 7, Family = ModuleFamily.GasScanner, State = ModuleScanState.Scanning };
        Assert.Equal(ModuleGate.Running, ModuleRules.Gate(ModuleFamily.GasScanner, Equipment(), running, 7, 100, cloud));
        Assert.Equal(ModuleGate.Waiting, ModuleRules.Gate(ModuleFamily.AsteroidLocator, Equipment(), running, 7, 100, none));
        Assert.Equal(ModuleGate.Ready, ModuleRules.Gate(ModuleFamily.AsteroidLocator, Equipment(), running, 8, 100, none));
        var cooling = new ModuleScanView { LocatorCooldownUntilTick = 700 };
        Assert.Equal(ModuleGate.Cooling, ModuleRules.Gate(ModuleFamily.AsteroidLocator, Equipment(), cooling, 8, 100, none));
        Assert.Equal(10, ModuleRules.CooldownSeconds(cooling, ModuleFamily.AsteroidLocator, 100));
        Assert.Equal(1, ModuleRules.CooldownSeconds(cooling, ModuleFamily.AsteroidLocator, 640));
        Assert.Equal(0, ModuleRules.CooldownSeconds(cooling, ModuleFamily.AsteroidLocator, 700));
        // Another family's chip does not cool with it.
        Assert.Equal(ModuleGate.Ready, ModuleRules.Gate(ModuleFamily.GasScanner, Equipment(), cooling, 8, 100, new ModuleTargets { HasCloud = true, CloudInRange = true }));
        Assert.Equal(0, ModuleRules.CooldownSeconds(cooling, ModuleFamily.GasScanner, 100));
        Assert.Equal(0, ModuleRules.CooldownSeconds(cooling, ModuleFamily.AsteroidScanner, 100));
        Assert.Equal(ModuleGate.Ready, ModuleRules.Gate(ModuleFamily.AsteroidLocator, Equipment(), cooling, 8, 700, none));
    }

    [Fact]
    public void ProgressOnlyRunsWhileScanningAndEveryNoticeHasWordsAndAnExplanation()
    {
        var scan = new ModuleScanView { State = ModuleScanState.Scanning, StartedTick = 100, CompletesTick = 400 };
        Assert.Equal(0f, ModuleRules.Progress(scan, 100));
        Assert.Equal(0.5f, ModuleRules.Progress(scan, 250));
        Assert.Equal(1f, ModuleRules.Progress(scan, 9000));
        Assert.Equal(2.5f, ModuleRules.SecondsLeft(scan, 250));
        scan.State = ModuleScanState.Preparing;
        Assert.Equal(0f, ModuleRules.Progress(scan, 250));
        Assert.Equal(0f, ModuleRules.SecondsLeft(scan, 250));
        foreach (var notice in Enum.GetValues<ModuleNotice>().Where(n => n != ModuleNotice.None))
        {
            Assert.False(ModuleRules.Words(notice).IsEmpty, notice.ToString());
            if (notice != ModuleNotice.Done)
                Assert.False(ModuleRules.Sentence(notice).IsEmpty, notice.ToString());
        }

        // Every state the server can end a request in maps to a notice, except the ones that are still running.
        foreach (var state in Enum.GetValues<ModuleScanState>())
            Assert.Equal(state is ModuleScanState.Idle or ModuleScanState.Preparing or ModuleScanState.Scanning, ModuleRules.NoticeOf(state) == ModuleNotice.None);
    }

    [Fact]
    public void AKeyOnlyAsksWhenTheModuleIsReadyAndOtherwiseSaysWhyOnItsChip()
    {
        var map = ActionMap.Load(Path.Combine(Content, "input", "bindings.json"));
        var devices = new InputDevices();
        using var rig = new ModuleScanTests.Rig();
        var bridge = rig.Clients[0].Input;
        var track = new ModuleTracker();

        void Press(GlfwKey key, ModuleFamily family, ModuleGate gate, in ModuleTargets targets)
        {
            devices.BeginFrame();
            devices.OnKey(key, GlfwKeyAction.Press);
            Assert.True(map.Pressed(devices, ModuleRules.ActionOf(family), true));
            var notice = ModuleRules.Request(family, gate, targets, bridge);
            if (notice != ModuleNotice.None)
                track.Note(family, notice);
            devices.BeginFrame();
            devices.OnKey(key, GlfwKeyAction.Release);
        }

        // The asteroid scanner with nothing targeted: nothing is sent, the chip says so.
        Press(GlfwKey.X, ModuleFamily.AsteroidScanner, ModuleGate.NoTarget, default);
        Assert.False(bridge.ConsumeModuleScan(out _));
        Assert.Equal(ModuleNotice.NoTarget, track.Notice(ModuleFamily.AsteroidScanner, 6f));

        // Ready: the request goes out once and names the target the interface chose.
        var targets = new ModuleTargets { Rock = new RockRef(new SectorCoord(1, 2, 3), 9), RockInRange = true };
        Press(GlfwKey.X, ModuleFamily.AsteroidScanner, ModuleGate.Ready, targets);
        Assert.True(bridge.ConsumeModuleScan(out var request));
        Assert.Equal(new ModuleScanRequest(ModuleFamily.AsteroidScanner, new SectorCoord(1, 2, 3), 9), request);
        Assert.False(bridge.ConsumeModuleScan(out _));

        // The gas key names the cloud's anchor; the locator key names nothing.
        Press(GlfwKey.C, ModuleFamily.GasScanner, ModuleGate.Ready, new ModuleTargets { HasCloud = true, CloudInRange = true, Cloud = new SectorCoord(4, 5, 6) });
        Assert.True(bridge.ConsumeModuleScan(out request));
        Assert.Equal(new ModuleScanRequest(ModuleFamily.GasScanner, new SectorCoord(4, 5, 6)), request);
        Press(GlfwKey.Y, ModuleFamily.AsteroidLocator, ModuleGate.Ready, default);
        Assert.True(bridge.ConsumeModuleScan(out request));
        Assert.Equal(ModuleFamily.AsteroidLocator, request.Family);

        // Cooling, running and waiting send nothing; running says nothing, the others say why.
        foreach (var gate in new[] { ModuleGate.Cooling, ModuleGate.Waiting, ModuleGate.Running, ModuleGate.OutOfRange, ModuleGate.NoCloud })
        {
            Press(GlfwKey.Y, ModuleFamily.AsteroidLocator, gate, default);
            Assert.False(bridge.ConsumeModuleScan(out _));
        }

        Assert.Equal(ModuleNotice.NoCloud, track.Notice(ModuleFamily.AsteroidLocator, 6f));
    }

    [Fact]
    public void AChipsNoticeFadesAfterItsSecondsAndALocatorPulseStartsTheVision()
    {
        var track = new ModuleTracker();
        track.Note(ModuleFamily.GasScanner, ModuleNotice.NoPower);
        Assert.Equal(ModuleNotice.NoPower, track.Notice(ModuleFamily.GasScanner, 6f));
        Assert.Equal(ModuleNotice.None, track.Notice(ModuleFamily.AsteroidScanner, 6f));
        var idle = new ModuleScanView();
        track.Update(idle, 7, 5000, 7f);
        Assert.Equal(ModuleNotice.None, track.Notice(ModuleFamily.GasScanner, 6f));

        var done = new ModuleScanView { Serial = 1, Hull = 7, Family = ModuleFamily.AsteroidLocator, State = ModuleScanState.Completed, StartedTick = 50, CompletesTick = 170, Origin = new Double3(1, 2, 3) };
        track.Update(done, 7, 5000, 0.5f);
        Assert.Equal(ModuleNotice.Done, track.Notice(ModuleFamily.AsteroidLocator, 6f));
        Assert.Equal(0f, track.PulseAge);
        Assert.Equal(50, track.PulseTick);
        Assert.Equal(5000f, track.PulseRange);
        track.Update(done, 7, 5000, 1f);
        Assert.Equal(1f, track.PulseAge);

        // A result left over from another hull is not news for this one.
        var other = new ModuleTracker();
        other.Update(done, 8, 5000, 0.5f);
        Assert.Equal(ModuleNotice.None, other.Notice(ModuleFamily.AsteroidLocator, 6f));
        Assert.Equal(float.MaxValue, other.PulseAge);
    }

    [Fact]
    public void TheLocatorFadesInFlightKeepsAFaintMarkInTheCommandViewAndRevealsNearestFirst()
    {
        var look = Look().Locator;
        Assert.Equal(look.VisionAlpha, LocatorField.Alpha(true, false, 0f, look));
        Assert.Equal(look.VisionAlpha, LocatorField.Alpha(true, true, look.VisionSeconds, look));
        var middle = look.VisionSeconds + look.FadeSeconds * 0.5f;
        Assert.InRange(LocatorField.Alpha(true, true, middle, look), look.RetainedAlpha, look.VisionAlpha);
        Assert.Equal(look.RetainedAlpha, LocatorField.Alpha(true, true, 1000f, look));
        Assert.Equal(0f, LocatorField.Alpha(true, false, 1000f, look));
        Assert.Equal(0f, LocatorField.Alpha(false, false, 0f, look));
        Assert.Equal(look.RetainedAlpha, LocatorField.Alpha(false, true, 0f, look));
        // Strength never rises as the pulse ages.
        var last = float.MaxValue;
        for (var age = 0f; age < 30f; age += 0.5f)
        {
            var now = LocatorField.Alpha(true, true, age, look);
            Assert.True(now <= last + 1e-6f);
            last = now;
        }

        Assert.Equal(0f, LocatorField.Reveal(1000, 5000, 0f, look));
        Assert.Equal(1f, LocatorField.Reveal(1000, 5000, look.WaveSeconds, look));
        Assert.True(LocatorField.Reveal(200, 5000, 0.5f, look) > LocatorField.Reveal(4000, 5000, 0.5f, look));
    }

    private static ModuleObservation Gas(int sector, int region, float purity, float density, long tick, ModuleQuality quality, params float[] shares)
    {
        var record = new ModuleObservation { Owner = 1, Family = ModuleFamily.GasScanner, Quality = quality, Sector = new SectorCoord(sector, 0, 0), Index = region, RecordedTick = tick, Resource = -1 };
        record.Gas.Region = (byte)region;
        record.Gas.Purity = purity;
        record.Gas.Density = density;
        for (var i = 0; i < shares.Length; i++)
            record.Gas.Shares[i] = shares[i];
        return record;
    }

    [Fact]
    public void TheGasLedgerListsEachSampledCloudNewestFirstWithItsMeanAndNeverSpreadsWhatWasUnresolved()
    {
        var view = new ModuleKnowledgeView();
        view.Records[view.Count++] = Gas(5, 0, 0.5f, 0.8f, 100, ModuleQuality.Common, 0.5f, 0.25f, 0.05f);
        view.Records[view.Count++] = Gas(5, 3, 0.7f, 0.4f, 300, ModuleQuality.Rare, 0.3f, 0.35f, 0.05f);
        view.Records[view.Count++] = Gas(9, 1, 0.9f, 1f, 200, ModuleQuality.Epic, 1f);
        view.Records[view.Count++] = new ModuleObservation { Owner = 1, Family = ModuleFamily.AsteroidScanner, Sector = new SectorCoord(5, 0, 0), Index = 2, Resource = 0, Radius = 10, Mass = 5 };
        var ledger = new GasLedger();
        ledger.Update(view, null);
        Assert.Equal(2, ledger.Count);
        // The cloud with the newest sample leads.
        Assert.Equal(new SectorCoord(5, 0, 0), ledger.Anchor(ledger.At(0)));
        Assert.Equal(new SectorCoord(9, 0, 0), ledger.Anchor(ledger.At(1)));
        var five = ledger.Find(new SectorCoord(5, 0, 0));
        Assert.Equal(2, ledger.Regions(five));
        Assert.Equal(300, ledger.Newest(five));
        Assert.Equal(ModuleQuality.Rare, ledger.Best(five));
        Assert.Equal(0.4f, ledger.Share(five, 0), 4);
        Assert.Equal(0.3f, ledger.Share(five, 1), 4);
        Assert.Equal(0.05f, ledger.Share(five, 2), 4);
        Assert.Equal(0.6f, ledger.Purity(five), 4);
        Assert.Equal(0.6f, ledger.Density(five), 4);
        Assert.Equal(0.25f, ledger.Unresolved(five), 4);
        Assert.Equal(-1, ledger.Find(new SectorCoord(77, 0, 0)));
        Assert.Equal(0f, ledger.Unresolved(ledger.Find(new SectorCoord(9, 0, 0))), 4);

        // Nothing is rebuilt while the records are the same; a new sample changes the list.
        var rebuilds = ledger.Rebuilds;
        ledger.Update(view, null);
        ledger.Update(view, null);
        Assert.Equal(rebuilds, ledger.Rebuilds);
        view.Records[view.Count++] = Gas(12, 0, 0.5f, 0.5f, 900, ModuleQuality.Common, 0.5f);
        ledger.Update(view, null);
        Assert.Equal(3, ledger.Count);
        Assert.Equal(new SectorCoord(12, 0, 0), ledger.Anchor(ledger.At(0)));
    }

    [Fact]
    public void TheGasLedgerNamesTheCloudKindFromTheGalaxyForACloudItKnows()
    {
        var galaxy = Game.Galaxy;
        Span<GasCloudPlacement> clouds = stackalloc GasCloudPlacement[64];
        var found = GasCloudQuery.Collect(galaxy, default, 12, clouds);
        Assert.True(found > 0);
        var view = new ModuleKnowledgeView();
        var record = Gas(0, 0, 0.5f, 0.5f, 10, ModuleQuality.Common, 0.5f);
        record.Sector = clouds[0].Anchor;
        view.Records[view.Count++] = record;
        var ledger = new GasLedger();
        ledger.Update(view, galaxy);
        Assert.Equal(clouds[0].Cloud.Kind, ledger.Kind(ledger.Find(clouds[0].Anchor)));
    }

    [Fact]
    public void GasTargetingFindsTheCloudTheServerWouldSampleAndAsksOnlyWhenTheShipMovesOrTimePasses()
    {
        var galaxy = Game.Galaxy;
        Span<GasCloudPlacement> clouds = stackalloc GasCloudPlacement[64];
        Assert.True(GasCloudQuery.Collect(galaxy, default, 12, clouds) > 0);
        var cloud = clouds[0];
        var inside = GasCloudQuery.CenterOf(galaxy, cloud);
        var target = new GasTargeting();
        target.Update(galaxy, inside, 1500, 0f);
        Assert.True(target.Has);
        Assert.Equal(cloud.Anchor, target.Anchor);
        Assert.Equal(0, target.Distance, 3);
        Assert.True(target.InRange(1500));
        var queries = target.Queries;
        target.Update(galaxy, inside + new Double3(1, 0, 0), 1500, 0.1f);
        Assert.Equal(queries, target.Queries);
        target.Update(galaxy, inside + new Double3(1, 0, 0), 1500, 0.8f);
        Assert.Equal(queries + 1, target.Queries);

        // The server's own sample point: the ship itself inside the envelope, otherwise a point on the way to the centre.
        Assert.Equal(inside, GasCloudQuery.SamplePoint(galaxy, cloud, inside));
        var far = inside + new Double3(cloud.Cloud.HalfSize.X * 3.0, 0, 0);
        var point = GasCloudQuery.SamplePoint(galaxy, cloud, far);
        Assert.True((point - far).Length < (inside - far).Length);

        // Nowhere near any cloud with a short reach: none, so the chip says NO CLOUD.
        target.Reset();
        target.Update(galaxy, new Double3(1e12, 0, 0), 50, 5f);
        Assert.False(target.Has);
        Assert.False(target.InRange(50));
    }

    [Theory]
    [InlineData(30f, "S", 30f)]
    [InlineData(89f, "S", 89f)]
    [InlineData(120f, "MIN", 2f)]
    [InlineData(7200f, "H", 2f)]
    public void AgesReadInSecondsMinutesOrHours(float seconds, string unit, float value)
    {
        Assert.Equal(unit, ModuleAge.Unit(seconds));
        Assert.Equal(value, ModuleAge.Value(seconds), 3);
        Assert.Equal(2f, ModuleAge.Seconds(60, 180), 3);
    }

    [Fact]
    public void ARealScanThroughTheSameRequestAsTheKeyLeavesAnAnalysisThePanelFindsByRock()
    {
        using var rig = new ModuleScanTests.Rig();
        var rock = rig.PlaceNearRock();
        rig.Until(() => rig.Clients[0].AcquireSnapshot().Hulls[rig.Clients[0].AcquireSnapshot().OwnHull].Stats.Module(ModuleFamily.AsteroidScanner).Block != 0);
        var snapshot = rig.Clients[0].AcquireSnapshot();
        var own = snapshot.Hulls[snapshot.OwnHull];
        var equipment = own.Stats.Module(ModuleFamily.AsteroidScanner);
        Assert.NotEqual(0, equipment.Block);
        // Unscanned: the panel has nothing to show.
        Assert.Equal(-1, snapshot.Modules.Find(ModuleFamily.AsteroidScanner, rock.Sector, rock.Index));
        var targets = new ModuleTargets { Rock = rock, RockInRange = true };
        var gate = ModuleRules.Gate(ModuleFamily.AsteroidScanner, equipment, snapshot.Modules.Scan, own.Id, snapshot.ServerTick, targets);
        Assert.Equal(ModuleGate.Ready, gate);
        Assert.Equal(ModuleNotice.None, ModuleRules.Request(ModuleFamily.AsteroidScanner, gate, targets, rig.Clients[0].Input));
        rig.Until(() => rig.Clients[0].AcquireSnapshot().Modules.Scan.State == ModuleScanState.Scanning);
        var running = rig.Clients[0].AcquireSnapshot();
        Assert.Equal(ModuleGate.Running, ModuleRules.Gate(ModuleFamily.AsteroidScanner, equipment, running.Modules.Scan, own.Id, running.ServerTick, targets));
        Assert.Equal(ModuleGate.Waiting, ModuleRules.Gate(ModuleFamily.AsteroidLocator, own.Stats.Module(ModuleFamily.AsteroidLocator), running.Modules.Scan, own.Id, running.ServerTick, default));
        rig.Until(() => rig.Clients[0].AcquireSnapshot().Modules.Scan.State == ModuleScanState.Completed);
        snapshot = rig.Clients[0].AcquireSnapshot();
        var found = snapshot.Modules.Find(ModuleFamily.AsteroidScanner, rock.Sector, rock.Index);
        Assert.True(found >= 0);
        Assert.True(snapshot.Modules.Records[found].Mass > 0);
        Assert.Equal(ModuleGate.Cooling, ModuleRules.Gate(ModuleFamily.AsteroidScanner, equipment, snapshot.Modules.Scan, own.Id, snapshot.ServerTick, targets));
        // The second player knows nothing of it.
        Assert.Equal(-1, rig.Clients[1].AcquireSnapshot().Modules.Find(ModuleFamily.AsteroidScanner, rock.Sector, rock.Index));
    }

    [Fact]
    public void TheEveryFrameWorkOfTheModuleInterfaceTakesNoMemoryOnceWarm()
    {
        var galaxy = Game.Galaxy;
        Span<GasCloudPlacement> clouds = stackalloc GasCloudPlacement[8];
        Assert.True(GasCloudQuery.Collect(galaxy, default, 12, clouds) > 0);
        var inside = GasCloudQuery.CenterOf(galaxy, clouds[0]);
        var view = new ModuleKnowledgeView();
        for (var i = 0; i < 40; i++)
            view.Records[view.Count++] = Gas(i, i & 7, 0.5f, 0.5f, 10 + i, ModuleQuality.Common, 0.5f, 0.25f);
        view.Records[view.Count++] = new ModuleObservation { Owner = 1, Family = ModuleFamily.AsteroidLocator, Resource = 1, Position = inside };
        var ledger = new GasLedger();
        var target = new GasTargeting();
        var track = new ModuleTracker();
        var look = Look();
        var scan = new ModuleScanView { Hull = 3, State = ModuleScanState.Scanning, StartedTick = 1, CompletesTick = 99 };
        var equipment = Equipment();
        var targets = new ModuleTargets { HasCloud = true, CloudInRange = true, Rock = new RockRef(default, 1), RockInRange = true };
        var sink = 0f;
        var time = 0f;
        void Frame()
        {
            time += 1f / 60f;
            ledger.Update(view, galaxy);
            target.Update(galaxy, inside, 1500, time);
            track.Update(scan, 3, 5000, 1f / 60f);
            for (var f = 1; f <= 3; f++)
                sink += (float)ModuleRules.Gate((ModuleFamily)f, equipment, scan, 3, 50, targets);
            sink += LocatorField.Alpha(true, true, time, look.Locator) + LocatorField.Reveal(100, 5000, time, look.Locator);
            sink += look.Classes[look.ClassOf(galaxy, view.Records[view.Count - 1].Resource)].Rgb + ledger.Share(ledger.At(0), 0) + ledger.Unresolved(0);
            _ = ModuleRules.Words(ModuleNotice.NoPower).Length + ModuleAge.Unit(time).Length;
        }

        for (var i = 0; i < 20; i++)
            Frame();
        var probe = new AllocationProbe();
        while (probe.Measuring())
            Frame();
        Assert.Equal(0, probe.Bytes);
        Assert.True(sink != -1f);
    }
}
