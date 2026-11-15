using System.Numerics;
using Prospect.Client.Screens;
using Prospect.Client.World;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// What the ship editor's panels show, without an interface: which parts a tab and a search list, the figures a
/// part card gives, the warnings a design's figures raise, and the console's hull read against the blueprint.
/// </summary>
public sealed class EditorPanelTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly EditorTuning Tuning = EditorTuning.Load(Path.Combine(AppContext.BaseDirectory, "content", "visuals", "editor.json"));

    private static PartCategory Tab(string name) => Array.Find(Tuning.Categories, c => c.Name == name)!;

    [Fact]
    public void EveryPartIsOnExactlyOneTabBesidesAllAndDronePartsAreOnlyOnTheirOwn()
    {
        foreach (var type in Content.Blocks.Types)
        {
            var tabs = Tuning.Categories.Where(c => c.Name != "All" && Tuning.Lists(c, type)).Select(c => c.Name).ToArray();
            Assert.True(tabs.Length == 1, $"{type.Id} is on {string.Join(", ", tabs)}");
            Assert.True(Tuning.Lists(Tab("All"), type));
            Assert.Equal(Tuning.Compact(type), tabs[0] == "Drone");
        }

        // The drone parts are told apart by their figures, not by a list of ids.
        Assert.True(Tuning.Compact(Content.Blocks.Get("microshield")));
        Assert.True(Tuning.Compact(Content.Blocks.Get("riftdrive")));
        Assert.False(Tuning.Compact(Content.Blocks.Get("shield")));
        Assert.False(Tuning.Compact(Content.Blocks.Get("cargo")));
    }

    [Fact]
    public void ASearchCoversEveryTabAndFindsPartsByNameRoleAndDrone()
    {
        // A ship is edited: search and tabs list ship parts only.
        var palette = new EditorPalette { Category = Array.IndexOf(Tuning.Categories, Tab("Move")) };
        string[] Shown() => Content.Blocks.Types.Where(t => palette.Shows(Tuning, t)).Select(t => t.Id).ToArray();

        Assert.Equal(["engine", "hotThruster", "coldGasThruster", "ionDrive", "ionThruster", "bipropellantTank", "hydrazineTank", "nitrogenTank", "gyro"], Shown());
        palette.Query = "ion";
        Assert.Equal(["ionDrive", "ionThruster"], Shown());
        palette.Query = "nitrogen";
        Assert.Equal(["coldGasThruster", "nitrogenTank"], Shown());
        palette.Query = "tank";
        Assert.Equal(["bipropellantTank", "hydrazineTank", "nitrogenTank"], Shown());
        palette.Query = "shield";
        Assert.Equal(["shield"], Shown());
        palette.Query = "drone";
        Assert.Empty(Shown());
        palette.Query = "POWER";
        Assert.Contains("generator", Shown());
        Assert.DoesNotContain("microgenerator", Shown());
        palette.Query = "zzz";
        Assert.Empty(Shown());

        // The drone is edited: the same searches find drone parts only.
        palette.Craft = HullClass.Fighter;
        palette.Query = "ion";
        Assert.Equal(["microIonDrive", "microIonThruster"], Shown());
        palette.Query = "nitrogen";
        Assert.Equal(["microthruster", "microNitrogenTank"], Shown());
        palette.Query = "shield";
        Assert.Equal(["microshield"], Shown());
        palette.Query = "drone";
        Assert.Equal(Content.Blocks.Types.Where(Tuning.Compact).Select(t => t.Id).ToArray(), Shown());
        palette.Query = "POWER";
        Assert.Equal(["microgenerator"], Shown());
    }

    [Fact]
    public void ThePaletteOffersExactlyThePartsTheDraftAcceptsForTheCraftBeingEdited()
    {
        foreach (var (craft, design) in new[] { (HullClass.Ship, Content.Pod), (HullClass.Fighter, Content.Designs[Content.DroneDesign]) })
        {
            Assert.Equal(craft, design.Class);
            var draft = BuildDraft.FromBlueprint(design, Content);
            var palette = new EditorPalette { Craft = craft, Category = 0 };
            foreach (var type in Content.Blocks.Types)
            {
                // The draft's own refusal is the authority; the palette is derived from the same flag.
                var refused = type.Mount is null
                    ? draft.CanAdd(new GridSize(100, 100, 100), new GridSize(1, 1, 1), type) == DraftEdit.WrongCraft
                    : draft.CanAddMount(0, BlockFacing.Back, 0, 0, type) == DraftEdit.WrongCraft;
                Assert.Equal(!refused, palette.Shows(Tuning, type));
                Assert.Equal(type.Drone == (craft == HullClass.Fighter), palette.Shows(Tuning, type));
            }
        }
    }

    [Fact]
    public void DebugFillMapsSelectedTanksToTheHullsOwnTanksAndASpareDraftTankSaysApplyFirst()
    {
        var pod = Content.Pod;
        var tanks = pod.Tanks.ToArray();
        var draft = BuildDraft.FromBlueprint(pod, Content);
        Span<int> blocks = stackalloc int[EditorRequestMessage.MaxFill];

        // The tanks as built: selected, they map to the hull's own blocks, in the order selected.
        Assert.Equal(FillOffer.Ready, EditorFill.Plan(draft, pod, [tanks[1].Block, tanks[0].Block], blocks, out var count));
        Assert.Equal([tanks[1].Block, tanks[0].Block], blocks[..count].ToArray());

        // Blocks that are no tank are left out; none at all offers nothing.
        Assert.Equal(FillOffer.Ready, EditorFill.Plan(draft, pod, [FirstBlockOf(pod, "cargo"), tanks[0].Block], blocks, out count));
        Assert.Equal([tanks[0].Block], blocks[..count].ToArray());
        Assert.Equal(FillOffer.None, EditorFill.Plan(draft, pod, [FirstBlockOf(pod, "cargo")], blocks, out count));
        Assert.Equal(FillOffer.None, EditorFill.Plan(draft, pod, [], blocks, out _));
        Assert.Equal(FillOffer.None, EditorFill.Plan(draft, pod, [-1, 9999], blocks, out _));

        // A tank that is only in the draft cannot be filled: apply first, even next to one that can.
        var one = new GridSize(1, 1, 1);
        var added = -1;
        for (var target = 0; target < draft.Boxes.Length && added < 0; target++)
            for (var side = 0; side < 6 && added < 0; side++)
                if (draft.Add(draft.Snap(target, side, one), one, Content.Blocks.Get("nitrogenTank")) == DraftEdit.Done)
                    added = draft.Boxes.Length - 1;
        Assert.True(added > 0);
        Assert.Equal(FillOffer.ApplyFirst, EditorFill.Plan(draft, pod, [added], blocks, out count));
        Assert.Equal(0, count);
        Assert.Equal(FillOffer.ApplyFirst, EditorFill.Plan(draft, pod, [tanks[0].Block, added], blocks, out _));

        // A tank resized in the draft is another tank until it is applied.
        var resized = BuildDraft.FromBlueprint(pod, Content);
        var box = resized.Boxes[tanks[0].Block];
        var grown = false;
        for (var face = 0; face < 6 && !grown; face++)
            grown = resized.Resize(tanks[0].Block, face, new[] { box.Size.X, box.Size.Y, box.Size.Z }[face >> 1] + 1) == DraftEdit.Done;
        Assert.True(grown);
        Assert.NotEqual(box.Size, resized.Boxes[tanks[0].Block].Size);
        Assert.Equal(FillOffer.ApplyFirst, EditorFill.Plan(resized, pod, [tanks[0].Block], blocks, out _));
        Assert.Equal(FillOffer.Ready, EditorFill.Plan(resized, pod, [tanks[1].Block], blocks, out count));

        // More than a request carries.
        var many = new int[EditorRequestMessage.MaxFill + 1];
        Array.Fill(many, tanks[0].Block);
        Assert.Equal(FillOffer.TooMany, EditorFill.Plan(draft, pod, many, blocks, out _));
    }

    private static int FirstBlockOf(HullDesign design, string type)
    {
        for (var i = 0; i < design.Boxes.Length; i++)
            if (design.Boxes[i].Type.Id == type)
                return i;
        throw new InvalidOperationException(type);
    }

    [Fact]
    public void ATabWithNoPartForTheCraftIsHiddenAndAChosenOneFallsBackToAll()
    {
        var types = Content.Blocks.Types;
        var ship = new EditorPalette { Craft = HullClass.Ship };
        var drone = new EditorPalette { Craft = HullClass.Fighter };
        var droneTab = Array.IndexOf(Tuning.Categories, Tab("Drone"));
        Assert.False(ship.TabHasParts(Tuning, types, droneTab));
        Assert.True(drone.TabHasParts(Tuning, types, droneTab));
        Assert.True(ship.TabHasParts(Tuning, types, 0));
        Assert.True(drone.TabHasParts(Tuning, types, 0));
        for (var tab = 0; tab < Tuning.Categories.Length; tab++)
            Assert.Equal(types.Any(t => !t.Drone && Tuning.Lists(Tuning.Categories[tab], t)), ship.TabHasParts(Tuning, types, tab));

        // The ship tabs with parts are all but Drone; on the drone, the role tabs list none (drone parts live on their own tab).
        Assert.DoesNotContain(Tuning.Categories.Where((_, i) => ship.TabHasParts(Tuning, types, i)), c => c.Name == "Drone");
        Assert.All(Tuning.Categories.Where((_, i) => drone.TabHasParts(Tuning, types, i)), c => Assert.Contains(c.Name, new[] { "All", "Drone" }));

        // The Drone tab was chosen while the drone was edited; editing a ship falls back to All, and the list is a ship's.
        ship.Category = droneTab;
        ship.KeepTabShown(Tuning, types);
        Assert.Equal(0, ship.Category);
        Assert.DoesNotContain(types.Where(t => ship.Shows(Tuning, t)), t => t.Drone);
        drone.Category = droneTab;
        drone.KeepTabShown(Tuning, types);
        Assert.Equal(droneTab, drone.Category);
        Assert.All(types.Where(t => drone.Shows(Tuning, t)), t => Assert.True(t.Drone));
    }

    [Fact]
    public void TheShieldAndTheDroneShieldAreDifferentPartsWithTheirOwnFiguresThatGrowWithSize()
    {
        var rules = Content.Rules;
        var shield = PartFigures.Of(Content.Blocks.Get("shield"), 1, rules);
        var micro = PartFigures.Of(Content.Blocks.Get("microshield"), 1, rules);
        Assert.Equal((1500f, 37.5f * rules.ShieldRechargeMultiplier, 3f, 2.5f), (shield.Shield, shield.Recharge, shield.PowerDraw, shield.Mass));
        Assert.Equal((120f, 5f * rules.ShieldRechargeMultiplier, 0.4f, 0.3f), (micro.Shield, micro.Recharge, micro.PowerDraw, micro.Mass));

        // A card follows the size parts are placed at, by the size rules: a generator's output grows faster than its
        // volume, a core's draw not at all.
        var palette = new EditorPalette();
        var types = Content.Blocks.Types;
        var generator = types.ToList().FindIndex(t => t.Id == "generator");
        var core = types.ToList().FindIndex(t => t.Id == "core");
        var one = palette.Figures(Content, new GridSize(1, 1, 1))[generator];
        var big = palette.Figures(Content, new GridSize(2, 2, 2));
        Assert.Equal(6f, one.PowerOutput, 3);
        Assert.Equal(6.0 * Math.Pow(8, 1.25), big[generator].PowerOutput, 2);
        Assert.Equal(8f * 4f, big[generator].Mass, 3);
        Assert.Equal(0.2f, big[core].PowerDraw, 4);
    }

    [Fact]
    public void StructuralMergeFiguresShowMassSeparatelyFromHullPoints()
    {
        var figures = new PartFigures { Mass = 12f, Hull = 800f };
        Assert.Equal(12f, figures.Main(PartRole.Structure));
    }

    [Fact]
    public void WarningsSayWhatADesignCannotDoAndAWellBuiltShipHasNone()
    {
        static DesignWarning[] Of(FlightParams flight)
        {
            var warnings = new DesignWarning[DesignWarnings.Count];
            return warnings[..DesignWarnings.Of(flight, Tuning, warnings)];
        }

        // The shipped pod flies, turns and powers itself.
        Assert.Empty(Of(Content.Pod.Flight));

        // A core alone: no power, no movement, no turning; nothing about shields it does not have.
        var lone = new FlightParams { IdlePower = 0.2f, MaxSpeed = 0f };
        Assert.Equal([DesignWarning.NoPower, DesignWarning.NoForward, DesignWarning.NoDirection, DesignWarning.NoTurn], Of(lone));

        // Every shipped design can be pushed all six ways and feeds every part it has. The drone is the one that may
        // ask for more power than it makes: its ion parts hold full forward thrust on the generator, but strafing,
        // climbing and pushing forward at once runs the battery down, and the editor says so.
        foreach (var design in Content.Designs)
            Assert.Equal(design.Name == "Drone" ? [DesignWarning.ThrustOverload] : [], Of(design.Flight));

        // The pod without its thrusters facing front cannot brake; without its nitrogen tank no thruster fires.
        var draft = BuildDraft.FromBlueprint(Content.Pod, Content);
        var brakes = Enumerable.Range(0, draft.Mounts.Length).Where(i => draft.Mounts[i].Face == BlockFacing.Front).ToArray();
        Assert.Equal(4, brakes.Length);
        Assert.Equal(DraftEdit.Done, draft.Delete(default, brakes));
        var noBrake = draft.Export().Design!.Flight;
        Assert.Equal([DesignWarning.NoDirection], Of(noBrake));
        Assert.Equal(1 << (int)BlockFacing.Back, DesignWarnings.MissingDirections(noBrake));
        Assert.True(draft.Undo());
        var tank = draft.Boxes.ToArray().ToList().FindIndex(box => box.Type.Tank == FuelKind.Nitrogen);
        Assert.Equal(DraftEdit.Done, draft.Delete(tank));
        var dry = draft.Export().Design!.Flight;
        Assert.Equal([DesignWarning.NoTank], Of(dry));
        Assert.Equal(FuelKind.Nitrogen, HullDesign.MissingTank(dry));

        // A shield without a battery never recharges under the rules; with too small a generator it drains it.
        var flight = Content.Pod.Flight;
        flight.EnergyStorage = 0f;
        Assert.Contains(DesignWarning.NoBattery, Of(flight));
        flight = Content.Pod.Flight;
        flight.ShieldPower = flight.PowerGenerated - flight.IdlePower + 1f;
        Assert.Contains(DesignWarning.ShieldOverload, Of(flight));
        flight = Content.Pod.Flight;
        flight.ThrustPower = flight.PowerGenerated;
        Assert.Contains(DesignWarning.ThrustOverload, Of(flight));
        flight.IdlePower = flight.PowerGenerated + 1f;
        Assert.Contains(DesignWarning.IdleOverload, Of(flight));

        // Below the content's thresholds: slow, but able to move.
        flight = Content.Pod.Flight;
        for (var fuel = FuelKind.Nitrogen; fuel <= FuelKind.Bipropellant; fuel++)
            flight.Thrust[ThrustTable.Index(BlockFacing.Front, fuel)] = fuel == FuelKind.Bipropellant ? Tuning.LowForwardAcceleration * 0.5f : 0f;
        flight.MaxSpeed = Tuning.LowTopSpeed * 0.5f;
        flight.TurnRate = new Vector3(Tuning.LowTurnDegrees * 0.5f * MathF.PI / 180f);
        Assert.Equal([DesignWarning.SlowForward, DesignWarning.SlowTop, DesignWarning.SlowTurn], Of(flight));
    }

    [Fact]
    public void ADamagedHullReadsAgainstItsBlueprintAndAnEmptyShieldIsAnEmptyBar()
    {
        var design = Content.Pod;
        var index = (byte)Array.IndexOf(Content.Designs, design);
        var damaged = design.Flight;
        damaged.HullPoints = design.Flight.HullPoints * 0.6f;
        var shown = FlightConsole.WithBlueprintHull(damaged, Content.Designs, index);
        Assert.Equal(design.Flight.HullPoints, shown.HullPoints);
        Assert.Equal(0.5f, FlightConsole.Fraction(design.Flight.HullPoints * 0.5f, shown.HullPoints), 4);
        // Every other figure stays the live one.
        Assert.Equal(damaged.Mass, shown.Mass);

        // An unpowered shell has no shield at all: the bar is empty, never NaN.
        Assert.Equal(0f, FlightConsole.Fraction(0f, 0f));
        Assert.Equal(0f, FlightConsole.Fraction(10f, 0f));
    }
}
