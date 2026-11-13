using System.Numerics;
using Prospect.Client.Screens;
using Prospect.Client.World;
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
        var palette = new EditorPalette { Category = Array.IndexOf(Tuning.Categories, Tab("Move")) };
        string[] Shown() => Content.Blocks.Types.Where(t => palette.Shows(Tuning, t)).Select(t => t.Id).ToArray();

        Assert.Equal(["engine", "thruster", "gyro"], Shown());
        palette.Query = "shield";
        Assert.Equal(["shield", "microshield"], Shown());
        palette.Query = "drone";
        Assert.Equal(Content.Blocks.Types.Where(Tuning.Compact).Select(t => t.Id).ToArray(), Shown());
        palette.Query = "POWER";
        Assert.Contains("generator", Shown());
        Assert.Contains("microgenerator", Shown());
        palette.Query = "zzz";
        Assert.Empty(Shown());
    }

    [Fact]
    public void TheShieldAndTheDroneShieldAreDifferentPartsWithTheirOwnFiguresThatGrowWithSize()
    {
        var rules = Content.Rules;
        var shield = PartFigures.Of(Content.Blocks.Get("shield"), 1, rules);
        var micro = PartFigures.Of(Content.Blocks.Get("microshield"), 1, rules);
        Assert.Equal((1500f, 37.5f * rules.ShieldRechargeMultiplier, 1.5f, 2.5f), (shield.Shield, shield.Recharge, shield.PowerDraw, shield.Mass));
        Assert.Equal((120f, 5f * rules.ShieldRechargeMultiplier, 0.2f, 0.3f), (micro.Shield, micro.Recharge, micro.PowerDraw, micro.Mass));

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
        Assert.Equal([DesignWarning.NoPower, DesignWarning.NoForward, DesignWarning.NoTurn], Of(lone));

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
        flight.Acceleration = flight.Acceleration with { Z = Tuning.LowForwardAcceleration * 0.5f };
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
