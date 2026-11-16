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
    public void EveryPartIsOnExactlyOneCategoryBesidesAllAndTheDroneHasTheSameCategories()
    {
        foreach (var type in Content.Blocks.Types)
        {
            var tabs = Tuning.Categories.Where(c => c.Name != "All" && Tuning.Lists(c, type)).Select(c => c.Name).ToArray();
            Assert.True(tabs.Length == 1, $"{type.Id} is on {string.Join(", ", tabs)}");
            Assert.True(Tuning.Lists(Tab("All"), type));
        }

        // Tanks are only under Tanks, gyros under Thrust; there is no category of its own for the drone.
        Assert.Equal(["Storage"], Tuning.Categories.Where(c => Tuning.Lists(c, Content.Blocks.Get("hydrazineTank"))).Select(c => c.Name).Where(n => n != "All").ToArray());
        Assert.Equal(["Storage"], Tuning.Categories.Where(c => Tuning.Lists(c, Content.Blocks.Get("microNitrogenTank"))).Select(c => c.Name).Where(n => n != "All").ToArray());
        Assert.Equal(["Thrust"], Tuning.Categories.Where(c => Tuning.Lists(c, Content.Blocks.Get("gyro"))).Select(c => c.Name).Where(n => n != "All").ToArray());
        Assert.DoesNotContain(Tuning.Categories, c => c.Name == "Drone");

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
        var palette = new EditorPalette { Category = Array.IndexOf(Tuning.Categories, Tab("Thrust")) };
        string[] Shown() => Content.Blocks.Types.Where(t => palette.Shows(Tuning, t)).Select(t => t.Id).ToArray();

        Assert.Equal(["engine", "hotThruster", "coldGasThruster", "ionDrive", "ionThruster", "gyro"], Shown());
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
    public void ACategoryWithNoPartForTheCraftIsHiddenAndAChosenOneFallsBackToAll()
    {
        var types = Content.Blocks.Types;
        var ship = new EditorPalette { Craft = HullClass.Ship };
        var drone = new EditorPalette { Craft = HullClass.Fighter };
        string[] Visible(EditorPalette palette) => Tuning.Categories.Where((_, i) => palette.TabHasParts(Tuning, types, i)).Select(c => c.Name).ToArray();

        // A category is shown exactly when some part of it fits the craft.
        for (var tab = 0; tab < Tuning.Categories.Length; tab++)
        {
            Assert.Equal(types.Any(t => !t.Drone && Tuning.Lists(Tuning.Categories[tab], t)), ship.TabHasParts(Tuning, types, tab));
            Assert.Equal(types.Any(t => t.Drone && Tuning.Lists(Tuning.Categories[tab], t)), drone.TabHasParts(Tuning, types, tab));
        }

        // The drone shows the categories its parts fill, among them Hull, Power, Thrust, Tanks and Shield; never Sensors.
        Assert.Contains("All", Visible(drone));
        Assert.All(new[] { "Thrust", "Storage", "Shield", "Power" }, name => Assert.Contains(name, Visible(drone)));
        Assert.DoesNotContain("Sensors", Visible(drone));
        Assert.Contains("Sensors", Visible(ship));

        // Sensors was chosen on a ship; editing the drone falls back to All, and the list holds drone parts only.
        var sensors = Array.IndexOf(Tuning.Categories, Tab("Sensors"));
        drone.Category = sensors;
        drone.KeepTabShown(Tuning, types);
        Assert.Equal(0, drone.Category);
        Assert.All(types.Where(t => drone.Shows(Tuning, t)), t => Assert.True(t.Drone));
        ship.Category = sensors;
        ship.KeepTabShown(Tuning, types);
        Assert.Equal(sensors, ship.Category);
        Assert.DoesNotContain(types.Where(t => ship.Shows(Tuning, t)), t => t.Drone);

        // A drone part shows under its category, and a ship part never does.
        drone.Category = Array.IndexOf(Tuning.Categories, Tab("Storage"));
        Assert.Equal(["microBipropellantTank", "microNitrogenTank"], types.Where(t => drone.Shows(Tuning, t)).Select(t => t.Id).ToArray());
        ship.Category = Array.IndexOf(Tuning.Categories, Tab("Storage"));
        Assert.Equal(["bipropellantTank", "hydrazineTank", "nitrogenTank", "cargo", "tractorBeam", "tractorBeamUncommon", "tractorBeamRare", "tractorBeamEpic", "tractorBeamLegendary", "tractorBeamAlienTech"], types.Where(t => ship.Shows(Tuning, t)).Select(t => t.Id).ToArray());
    }

    [Fact]
    public void TheTanksCategoryShowsOneCardAndChoosingAFuelPlacesThatFuelsTankType()
    {
        var types = Content.Blocks.Types;
        var palette = new EditorPalette { Craft = HullClass.Ship, Category = Array.IndexOf(Tuning.Categories, Tab("Storage")) };
        string[] Cards() => Enumerable.Range(0, types.Count).Where(i => palette.ShowsCard(Tuning, types, i)).Select(i => types[i].Id).ToArray();

        // One card stands for the three tanks, and it places the chosen fuel's type.
        Assert.Equal(["bipropellantTank", "cargo", "tractorBeam", "tractorBeamUncommon", "tractorBeamRare", "tractorBeamEpic", "tractorBeamLegendary", "tractorBeamAlienTech"], Cards());
        palette.TankFuel = FuelKind.Hydrazine;
        Assert.Equal(["hydrazineTank", "cargo", "tractorBeam", "tractorBeamUncommon", "tractorBeamRare", "tractorBeamEpic", "tractorBeamLegendary", "tractorBeamAlienTech"], Cards());
        Assert.Equal(Array.FindIndex(types.ToArray(), t => t.Id == "nitrogenTank"), palette.TankTypeOf(types, FuelKind.Nitrogen));
        Span<FuelKind> fuels = stackalloc FuelKind[4];
        Assert.Equal(3, palette.TankFuels(types, fuels));
        Assert.Equal([FuelKind.Bipropellant, FuelKind.Hydrazine, FuelKind.Nitrogen], fuels[..3].ToArray());

        // "tank" and every fuel's name find the one card; a fuel's name finds it as that fuel's tank.
        foreach (var query in new[] { "tank", "hydrazine", "nitrogen", "bipropellant" })
        {
            palette.Query = query;
            Assert.Single(Cards(), id => EditorPalette.IsTank(Content.Blocks.Get(id)));
        }

        palette.Query = "nitrogen";
        Assert.Contains("nitrogenTank", Cards());
        Assert.DoesNotContain("hydrazineTank", Cards());
        palette.Query = "";

        // All shows it once as well, among the other parts.
        palette.Category = 0;
        Assert.Single(Cards(), id => EditorPalette.IsTank(Content.Blocks.Get(id)));

        // The drone has two fuels, and a remembered fuel it has no tank of falls back to one it has.
        palette.Craft = HullClass.Fighter;
        palette.Category = Array.IndexOf(Tuning.Categories, Tab("Storage"));
        Assert.Equal(2, palette.TankFuels(types, fuels));
        Assert.Equal(-1, palette.TankTypeOf(types, FuelKind.Hydrazine));
        Assert.Equal(["microBipropellantTank"], Cards());
        palette.TankFuel = FuelKind.Nitrogen;
        Assert.Equal(["microNitrogenTank"], Cards());
    }

    [Fact]
    public void AnUnknownIconIdOrAMissingTipIsRefusedWhenTheFileLoads()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "visuals", "editor.json"));
        var folder = Path.Combine(Path.GetTempPath(), "prospect-editor-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string Write(string name, string text)
            {
                var path = Path.Combine(folder, name);
                File.WriteAllText(path, text);
                return path;
            }

            Assert.NotNull(EditorTuning.Load(Write("good.json", source)));
            var unknown = Assert.Throws<InvalidDataException>(() => EditorTuning.Load(Write("icon.json", source.Replace("\"icon\": \"nozzle\"", "\"icon\": \"rocket\""))));
            Assert.Contains("rocket", unknown.Message);
            Assert.Throws<InvalidDataException>(() => EditorTuning.Load(Write("none.json", source.Replace("\"icon\": \"nozzle\", ", ""))));
            Assert.Throws<InvalidDataException>(() => EditorTuning.Load(Write("tip.json", source.Replace("\"tip\": \"Shield emitters.\"", "\"tip\": \"\""))));
        }
        finally
        {
            Directory.Delete(folder, true);
        }

        // Every id the file may name has a drawing.
        foreach (var id in EditorIcons.Names.Split(", "))
            Assert.True(EditorIcons.TryParse(id, out _), id);
        Assert.False(EditorIcons.TryParse(null, out _));
        Assert.All(Tuning.Categories, c => Assert.False(string.IsNullOrWhiteSpace(c.Tip)));
    }

    [Fact]
    public void TheShieldAndTheDroneShieldAreDifferentPartsWithTheirOwnFiguresThatGrowWithSize()
    {
        var rules = Content.Rules;
        var shield = PartFigures.Of(Content.Blocks.Get("shield"), 1, rules);
        var micro = PartFigures.Of(Content.Blocks.Get("microshield"), 1, rules);
        Assert.Equal((1500f, 37.5f * rules.ShieldRechargeMultiplier, 3f, 2.5f), (shield.Shield, shield.Recharge, shield.PowerDraw, shield.Mass));
        Assert.Equal((120f, 5f * rules.ShieldRechargeMultiplier, 0.7f, 0.3f), (micro.Shield, micro.Recharge, micro.PowerDraw, micro.Mass));

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
