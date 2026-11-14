using Prospect.Client.Input;
using Prospect.Client.Screens;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The notices apart from drawing: the content file and its refusals, the board's rules for the queue, the cap of
/// four, timing, hover, closing and overflow, and the tracker that turns snapshot changes into posts. The panel
/// itself is in <see cref="FrameOrderTests"/>, which owns Dear ImGui's one context.
/// </summary>
public sealed class NoticeTests
{
    private static readonly string Content = Path.Combine(AppContext.BaseDirectory, "content");
    private static string Shipped => Path.Combine(Content, "visuals", "notices.json");

    private static ActionMap Actions() => ActionMap.Load(Path.Combine(Content, "input", "bindings.json"));

    private static NoticeCatalogue Catalogue() => NoticeCatalogue.Load(Shipped, Actions());

    private static NoticeBoard Board() => new(Catalogue());

    /// <summary>The shipped file with one piece of text changed, written where the test can load it.</summary>
    private static string Edited(string from, string to)
    {
        var text = File.ReadAllText(Shipped);
        Assert.Contains(from, text);
        var path = Path.Combine(Path.GetTempPath(), "prospect-notices-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, text.Replace(from, to));
        return path;
    }

    /// <summary>Runs the board for <paramref name="seconds"/> in sixtieths.</summary>
    private static void Run(NoticeBoard board, float seconds)
    {
        for (var i = 0; i < (int)MathF.Round(seconds * 60); i++)
            board.Update(1f / 60f);
    }

    [Fact]
    public void TheShippedNoticesLoadWithTheKeysTheBindingsNameAndEveryKindHasOne()
    {
        var catalogue = Catalogue();
        foreach (var kind in NoticeKinds.All)
            Assert.True(catalogue.IndexOf(kind) >= 0, kind);
        foreach (var notice in catalogue.Notices)
        {
            Assert.DoesNotContain('{', notice.Text);
            Assert.NotEmpty(notice.TextBytes);
            Assert.InRange(notice.Seconds, 1f, 60f);
        }

        var gas = catalogue.Notices[catalogue.IndexOf(NoticeKinds.GasResults)];
        Assert.Contains("F4", gas.Text);
        Assert.Equal(NoticeAction.OpenGasWindow, gas.Action);
        Assert.Equal(8f, catalogue.Notices[catalogue.IndexOf(NoticeKinds.ModuleNoPower)].Seconds);
        Assert.Equal(6f, gas.Seconds);
    }

    [Fact]
    public void ARebindingChangesTheKeyANoticeNames()
    {
        var path = Path.Combine(Path.GetTempPath(), "prospect-bindings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, File.ReadAllText(Path.Combine(Content, "input", "bindings.json")).Replace("\"ToggleGas\": [\"Key:F4\"]", "\"ToggleGas\": [\"Key:G\"]"));
        var rebound = Catalogue().Rebind(ActionMap.Load(path));
        Assert.Contains("Press G", rebound.Notices[rebound.IndexOf(NoticeKinds.GasResults)].Text);
        Assert.Contains("F4", Catalogue().Notices[Catalogue().IndexOf(NoticeKinds.GasResults)].Text);
    }

    [Fact]
    public void AFileWithAnUnknownKindActionColourOrActionKeyOrARepeatedIdIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => NoticeCatalogue.Load(Edited("\"id\": \"gasResults\"", "\"id\": \"mystery\""), Actions()));
        Assert.Throws<InvalidDataException>(() => NoticeCatalogue.Load(Edited("\"openGasWindow\"", "\"openNothing\""), Actions()));
        Assert.Throws<InvalidDataException>(() => NoticeCatalogue.Load(Edited("\"accent\": \"bad\"", "\"accent\": \"purple\""), Actions()));
        Assert.Throws<InvalidDataException>(() => NoticeCatalogue.Load(Edited("{ToggleGas}", "{NoSuchAction}"), Actions()));
        Assert.Throws<InvalidDataException>(() => NoticeCatalogue.Load(Edited("\"id\": \"asteroidResults\"", "\"id\": \"gasResults\""), Actions()));
        Assert.Throws<InvalidDataException>(() => NoticeCatalogue.Load(Edited("\"seconds\": 8", "\"seconds\": 0"), Actions()));
        Assert.Throws<InvalidDataException>(() => NoticeCatalogue.Load(Edited("\"width\": 320", "\"width\": 10"), Actions()));
    }

    [Fact]
    public void AKindWithNoEntryInTheFileIsRefused()
    {
        var text = File.ReadAllText(Shipped);
        var start = text.IndexOf("    { \"id\": \"shipBuilt\"", StringComparison.Ordinal);
        Assert.True(start > 0);
        var path = Path.Combine(Path.GetTempPath(), "prospect-notices-" + Guid.NewGuid().ToString("N") + ".json");
        // The last entry removed, with the comma before it.
        var trimmed = text[..start].TrimEnd().TrimEnd(',') + "\n  ]\n}\n";
        File.WriteAllText(path, trimmed);
        Assert.Throws<InvalidDataException>(() => NoticeCatalogue.Load(path, Actions()));
    }

    [Fact]
    public void NewestShowsOnTopAndOnlyFourShowTheRestWait()
    {
        var board = Board();
        string[] kinds = [NoticeKinds.GasResults, NoticeKinds.SectorCharted, NoticeKinds.AsteroidResults, NoticeKinds.ModuleNoPower, NoticeKinds.LocatorResults, NoticeKinds.ShipBuilt];
        foreach (var kind in kinds)
            Assert.True(board.Post(kind));
        Assert.Equal(6, board.Count);
        Assert.Equal(4, board.Visible);
        // Row zero is the newest of those shown; the fifth and sixth are not.
        Assert.Equal(NoticeKinds.ModuleNoPower, board.NoticeAt(0).Id);
        Assert.Equal(NoticeKinds.GasResults, board.NoticeAt(3).Id);
    }

    [Fact]
    public void AWaitingNoticeAppearsWhenAnOlderOneLeavesAndStartsItsOwnTimer()
    {
        var board = Board();
        for (var i = 0; i < 4; i++)
            board.Post(NoticeKinds.ModuleCancelled);
        board.Post(NoticeKinds.GasResults);
        Assert.Equal(4, board.Visible);
        Run(board, 5.9f);
        Assert.Equal(5, board.Count);
        Run(board, 0.2f);
        // The four ran out together; the waiting one is shown now, not aged while it waited.
        Assert.Equal(1, board.Count);
        Assert.Equal(NoticeKinds.GasResults, board.NoticeAt(0).Id);
        Assert.InRange(board.AgeAt(0), 0f, 0.2f);
        Run(board, 5.7f);
        Assert.Equal(1, board.Count);
        Run(board, 0.4f);
        Assert.Equal(0, board.Count);
    }

    [Fact]
    public void AShortScreenLowersTheLimitAndTheNoticesThatDoNotFitWaitWithoutAging()
    {
        var board = Board();
        for (var i = 0; i < 4; i++)
            board.Post(NoticeKinds.ModuleCancelled);
        board.Limit = 2;
        Assert.Equal(2, board.Visible);
        Assert.Equal(4, board.Count);
        Run(board, 6.1f);
        // The two shown ran out; the two that waited start now, not aged.
        Assert.Equal(2, board.Count);
        Assert.InRange(board.AgeAt(0), 0f, 0.2f);
        board.Limit = 0;
        Assert.Equal(1, board.Limit);
        board.Limit = 99;
        Assert.Equal(NoticeBoard.MaxVisible, board.Limit);
    }

    [Fact]
    public void ANoticeRunsOutAfterItsOwnDurationAndFadesOverTheLastOfIt()
    {
        var board = Board();
        board.Post(NoticeKinds.ModuleNoPower);
        Assert.Equal(0f, board.AlphaAt(0));
        Run(board, 0.2f);
        Assert.InRange(board.AlphaAt(0), 0.3f, 0.7f);
        Run(board, 0.3f);
        Assert.Equal(1f, board.AlphaAt(0));
        Run(board, 7.4f);
        Assert.Equal(1, board.Count);
        Assert.InRange(board.AlphaAt(0), 0.01f, 0.99f);
        Run(board, 0.5f);
        Assert.Equal(0, board.Count);
    }

    [Fact]
    public void AHeldNoticeDoesNotAgeAndTheOthersDo()
    {
        var board = Board();
        board.Post(NoticeKinds.ModuleCancelled);
        board.Post(NoticeKinds.ModuleCancelled);
        for (var i = 0; i < 600; i++)
        {
            // Row zero is the newer one: the pointer rests on it, and it is the one that stays while the older leaves.
            board.Hover(0);
            board.Update(1f / 60f);
        }

        Assert.Equal(1, board.Count);
        Assert.Equal(0f, board.AgeAt(0));
        board.Update(1f / 60f);
        Assert.True(board.AgeAt(0) > 0f);
        // Let go: it runs out as any other.
        Run(board, 6.1f);
        Assert.Equal(0, board.Count);
    }

    [Fact]
    public void ClosingFadesTheNoticeOutAtOnceEvenWhileThePointerIsOverIt()
    {
        var board = Board();
        board.Post(NoticeKinds.GasResults);
        board.Post(NoticeKinds.SectorCharted);
        Run(board, 1f);
        board.Close(1);
        for (var i = 0; i < 30; i++)
        {
            board.Hover(1);
            board.Update(1f / 60f);
        }

        // The older notice left; the newer is untouched.
        Assert.Equal(1, board.Count);
        Assert.Equal(NoticeKinds.SectorCharted, board.NoticeAt(0).Id);
        Assert.True(board.AgeAt(0) >= 1f);
    }

    [Fact]
    public void AFullQueueDropsTheOldestWaitingNoticeAndNeverAShownOne()
    {
        var board = Board();
        board.Post(NoticeKinds.GasResults);
        board.Post(NoticeKinds.SectorCharted);
        board.Post(NoticeKinds.AsteroidResults);
        board.Post(NoticeKinds.ModuleNoPower);
        for (var i = 0; i < NoticeBoard.Capacity - 4; i++)
            board.Post(NoticeKinds.ModuleCancelled);
        Assert.Equal(NoticeBoard.Capacity, board.Count);
        board.Post(NoticeKinds.ShipBuilt);
        Assert.Equal(NoticeBoard.Capacity, board.Count);
        Assert.Equal(1, board.Dropped);
        Assert.Equal(NoticeKinds.ModuleNoPower, board.NoticeAt(0).Id);
        Assert.Equal(NoticeKinds.GasResults, board.NoticeAt(3).Id);
    }

    [Fact]
    public void NothingPostsWhileOffOrForAnUnknownKind()
    {
        var board = Board();
        Assert.False(board.Post("mystery"));
        board.Enabled = false;
        Assert.False(board.Post(NoticeKinds.GasResults));
        Assert.Equal(0, board.Count);
        board.Enabled = true;
        board.Post(NoticeKinds.GasResults);
        board.Clear();
        Assert.Equal(0, board.Count);
    }

    [Fact]
    public void PostingUpdatingHoveringAndClosingTakeNoMemory()
    {
        var board = Board();
        string[] kinds = [NoticeKinds.GasResults, NoticeKinds.SectorCharted, NoticeKinds.AsteroidResults];
        // Warm every path once, so a first call's binding is not counted.
        board.Post(kinds[0]);
        board.Hover(0);
        board.Close(0);
        board.Update(1f);
        board.Clear();

        var allocation = new AllocationProbe();
        while (allocation.Measuring())
        {
            for (var i = 0; i < 200; i++)
            {
                board.Post(kinds[i % 3]);
                board.Hover(0);
                board.Update(0.7f);
                _ = board.AlphaAt(0);
                if (i % 5 == 0)
                    board.Close(0);
            }
        }

        Assert.Equal(0, allocation.Bytes);
    }

    [Fact]
    public void TheTrackerPostsOnceForEachOwnFinishedScanAndNotForOthers()
    {
        var board = Board();
        var tracker = new NoticeTracker();
        const uint own = 7;
        var none = default(ScanView);
        var noShip = default(NewShipView);
        var module = new ModuleScanView { Serial = 1, Hull = own, Family = ModuleFamily.GasScanner, State = ModuleScanState.Scanning };

        // The first look is taken as it is; a scan already running is not news.
        tracker.Update(board, module, none, noShip, own);
        Assert.Equal(0, board.Count);

        module.State = ModuleScanState.Completed;
        tracker.Update(board, module, none, noShip, own);
        tracker.Update(board, module, none, noShip, own);
        Assert.Equal(1, board.Count);
        Assert.Equal(NoticeKinds.GasResults, board.NoticeAt(0).Id);

        // Another hull's result, a refusal for no power, a cancel, each its own kind; a cooldown nothing.
        module = new ModuleScanView { Serial = 2, Hull = 99, Family = ModuleFamily.AsteroidScanner, State = ModuleScanState.Completed };
        tracker.Update(board, module, none, noShip, own);
        Assert.Equal(1, board.Count);
        module = new ModuleScanView { Serial = 3, Hull = own, Family = ModuleFamily.AsteroidScanner, State = ModuleScanState.Completed };
        tracker.Update(board, module, none, noShip, own);
        module = new ModuleScanView { Serial = 4, Hull = own, Family = ModuleFamily.AsteroidLocator, State = ModuleScanState.NoPower };
        tracker.Update(board, module, none, noShip, own);
        module = new ModuleScanView { Serial = 5, Hull = own, Family = ModuleFamily.AsteroidLocator, State = ModuleScanState.Cancelled };
        tracker.Update(board, module, none, noShip, own);
        module = new ModuleScanView { Serial = 5, Hull = own, Family = ModuleFamily.AsteroidLocator, State = ModuleScanState.Cooldown };
        tracker.Update(board, module, none, noShip, own);
        Assert.Equal(4, board.Count);
        Assert.Equal(NoticeKinds.AsteroidResults, board.NoticeAt(2).Id);
        Assert.Equal(NoticeKinds.ModuleNoPower, board.NoticeAt(1).Id);
        Assert.Equal(NoticeKinds.ModuleCancelled, board.NoticeAt(0).Id);
    }

    [Fact]
    public void TheTrackerPostsForTheSectorChartAndANewShipAndStartsFreshAfterAReset()
    {
        var board = Board();
        var tracker = new NoticeTracker();
        const uint own = 7;
        var module = default(ModuleScanView);
        var sector = new ScanView { State = ScanState.Idle, Hull = own };
        var ship = new NewShipView(NewShipStatus.None, EditorReason.None, 0, 0);
        tracker.Update(board, module, sector, ship, own);

        sector.State = ScanState.Scanning;
        tracker.Update(board, module, sector, ship, own);
        Assert.Equal(0, board.Count);
        sector.State = ScanState.Completed;
        tracker.Update(board, module, sector, ship, own);
        Assert.Equal(NoticeKinds.SectorCharted, board.NoticeAt(0).Id);
        // A chart another hull made, or a cancelled scan, posts nothing.
        sector = new ScanView { State = ScanState.Cancelled, Hull = own };
        tracker.Update(board, module, sector, ship, own);
        sector = new ScanView { State = ScanState.Completed, Hull = 99 };
        tracker.Update(board, module, sector, ship, own);
        Assert.Equal(1, board.Count);

        ship = new NewShipView(NewShipStatus.Refused, EditorReason.DuplicateName, 0, 1);
        tracker.Update(board, module, sector, ship, own);
        Assert.Equal(1, board.Count);
        ship = new NewShipView(NewShipStatus.Created, EditorReason.None, 5, 2);
        tracker.Update(board, module, sector, ship, own);
        Assert.Equal(2, board.Count);
        Assert.Equal(NoticeKinds.ShipBuilt, board.NoticeAt(0).Id);

        // A new game: what is left from before is the first look again, not news.
        tracker.Reset();
        board.Clear();
        tracker.Update(board, module, new ScanView { State = ScanState.Completed, Hull = own }, ship, own);
        Assert.Equal(0, board.Count);
    }

    [Fact]
    public void TheTrackerTakesNoMemory()
    {
        var board = Board();
        var tracker = new NoticeTracker();
        var module = new ModuleScanView { Serial = 1, Hull = 7, Family = ModuleFamily.GasScanner, State = ModuleScanState.Scanning };
        var sector = default(ScanView);
        var ship = default(NewShipView);
        tracker.Update(board, module, sector, ship, 7);
        module.State = ModuleScanState.Completed;
        tracker.Update(board, module, sector, ship, 7);
        board.Clear();

        var allocation = new AllocationProbe();
        while (allocation.Measuring())
        {
            for (var i = 0; i < 100; i++)
            {
                module.Serial = (uint)i + 10;
                tracker.Update(board, module, sector, ship, 7);
                board.Clear();
            }
        }

        Assert.Equal(0, allocation.Bytes);
    }
}
