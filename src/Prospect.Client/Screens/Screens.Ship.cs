using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    /// <summary>Height of one figure row, unscaled.</summary>
    private const float ShipRowHeight = 19f;

    /// <summary>
    /// The Ship window: the stats of the ship being flown, grouped as identity, flight, power, fuel, shield, cargo,
    /// drones and modules. Every figure is the hull's own published sheet (<see cref="HullView.Stats"/>), the
    /// design's blocks or the flown hull's live state; nothing is worked out here. In the command view it follows
    /// the first selected hull, or shows a selected asteroid's survey (what the old data sheet did).
    /// </summary>
    private void DrawShipPane(SessionSnapshot snapshot, WorldView world)
    {
        if (!BeginPane(SocialPane.Ship, "##ship"u8, "Ship"u8, true, out var width, out _))
            return;

        var draw = ImGui.GetWindowDrawList();
        var hasOwn = snapshot.OwnHull >= 0;
        var picked = default(HullView);
        var hasPicked = world.Mode == ViewMode.Command && world.Selection.Length > 0 && world.TryGetHull(world.Selection[0], out picked);
        var subject = ShipSheet.Choose(world.Mode, hasOwn, hasPicked, world.SelectedRocks.Length > 0);
        switch (subject)
        {
            case ShipSheetSubject.Rock:
                DrawRockRows(draw, width, snapshot, world, world.SelectedRocks[0]);
                break;
            case ShipSheetSubject.None:
                Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "No ship flown."u8, width / Kit.Scale);
                break;
            default:
                var hull = subject == ShipSheetSubject.Selected ? picked : world.Own;
                DrawShipRows(draw, width, snapshot, snapshot.Content.Designs[Math.Min(hull.Design, snapshot.Content.Designs.Length - 1)], hull);
                break;
        }

        EndPane();
    }

    /// <summary>A section's heading: its name in caps over a hairline.</summary>
    private void ShipSection(ImDrawListPtr draw, float width, ReadOnlySpan<byte> title, bool first = false)
    {
        if (!first)
            Kit.Space(Theme.Space3);
        var at = ImGui.GetCursorScreenPos();
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, at, Theme.Accent, title);
        var height = Kit.Px(18);
        draw.AddLine(new float2(at.X, at.Y + height - Kit.Px(3)), new float2(at.X + width, at.Y + height - Kit.Px(3)), Kit.Color(Theme.Line, 0.7f));
        ImGui.Dummy(new float2(width, height));
    }

    /// <summary>One figure: its label at the left, its value at the right.</summary>
    private void ShipRow(ImDrawListPtr draw, float width, ImString label, ImString value, uint rgb = Theme.Text)
    {
        var at = ImGui.GetCursorScreenPos();
        Kit.Text(draw, _ui.Sans, Theme.FontSmall, at, Theme.TextDim, label);
        var size = Kit.Measure(_ui.Mono, Theme.FontSmall, value);
        Kit.Text(draw, _ui.Mono, Theme.FontSmall, new float2(at.X + width - size.X, at.Y), rgb, value);
        ImGui.Dummy(new float2(width, Kit.Px(ShipRowHeight)));
    }

    /// <summary>A figure whose label carries a quality tag in the tier's colour.</summary>
    private void ShipTierRow(ImDrawListPtr draw, float width, ImString label, ModuleQuality quality, ImString value)
    {
        var at = ImGui.GetCursorScreenPos();
        var tier = ModuleLook.Current.Tier(quality);
        Kit.Text(draw, _ui.Sans, Theme.FontSmall, at, Theme.TextDim, label);
        var labelWidth = Kit.Measure(_ui.Sans, Theme.FontSmall, label).X;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(at.X + labelWidth + Kit.Px(Theme.Space2), at.Y + Kit.Px(1)), tier.Rgb, tier.Label);
        var size = Kit.Measure(_ui.Mono, Theme.FontSmall, value);
        Kit.Text(draw, _ui.Mono, Theme.FontSmall, new float2(at.X + width - size.X, at.Y), Theme.Text, value);
        ImGui.Dummy(new float2(width, Kit.Px(ShipRowHeight)));
    }

    private void DrawShipRows(ImDrawListPtr draw, float width, SessionSnapshot snapshot, HullDesign design, in HullView hull)
    {
        // The session publishes the sheet with the hull, for the blocks it has left.
        var stats = hull.Stats;

        var at = ImGui.GetCursorScreenPos();
        Kit.ClippedText(draw, _ui.SansBold, Theme.FontHeading, at, Theme.Text, design.Name, width);
        ImGui.Dummy(new float2(width, Kit.Px(26)));

        ShipSection(draw, width, "IDENTITY"u8, true);
        // Points now against the blueprint's full strength: the sheet's own figure shrinks with lost blocks.
        ShipRow(draw, width, "Hull (now / built)"u8, Kit.F($"{FlightConsole.Shown(hull.Hull, float.MaxValue):0} / {design.Flight.HullPoints:0} hp"),
            hull.Hull < design.Flight.HullPoints ? Theme.Bad : Theme.Text);
        ShipRow(draw, width, "Mass"u8, Kit.F($"{stats.Mass:0.0} t"));
        ShipRow(draw, width, "Volume"u8, Kit.F($"{stats.Volume:0} m³"));
        ShipRow(draw, width, "W x H x L"u8, Kit.F($"{stats.Size.X:0} x {stats.Size.Y:0} x {stats.Size.Z:0} m"));
        ShipRow(draw, width, "Blocks"u8, stats.Blocks == design.BlockCount ? Kit.F($"{stats.Blocks}") : Kit.F($"{stats.Blocks} of {design.BlockCount}"),
            stats.Blocks == design.BlockCount ? Theme.Text : Theme.Bad);
        ShipRow(draw, width, "Crew"u8, Kit.F($"{stats.Crew}"));

        ShipSection(draw, width, "FLIGHT"u8);
        ShipRow(draw, width, "Max velocity"u8, Kit.F($"{stats.MaxSpeed:0} m/s"));
        ShipRow(draw, width, "Max velocity, boost"u8, Kit.F($"{stats.BoostMaxSpeed:0} m/s"));
        ShipRow(draw, width, "Acceleration"u8, Kit.F($"{stats.Acceleration:0.0} m/s²"));
        ShipRow(draw, width, "Deceleration"u8, Kit.F($"{stats.Deceleration:0.0} m/s²"));
        ShipRow(draw, width, "Sideways and vertical"u8, Kit.F($"{stats.LateralAcceleration:0.0} m/s²"));
        ShipRow(draw, width, "Yaw (left / right)"u8, Kit.F($"{stats.TurnRate.Y:0.00} rad/s"));
        ShipRow(draw, width, "Pitch (up / down)"u8, Kit.F($"{stats.TurnRate.X:0.00} rad/s"));
        ShipRow(draw, width, "Roll"u8, Kit.F($"{stats.TurnRate.Z:0.00} rad/s"));

        // The margin between what the generators make and what everything running at once would draw.
        ShipSection(draw, width, "POWER"u8);
        var margin = stats.PowerGenerated - stats.PowerRequired;
        ShipRow(draw, width, "Generated"u8, Kit.F($"{stats.PowerGenerated:0.00} MW"));
        ShipRow(draw, width, "Required, all running"u8, Kit.F($"{stats.PowerRequired:0.00} MW"));
        ShipRow(draw, width, "Margin"u8, Kit.F($"{margin:+0.00;-0.00} MW"), margin < 0 ? Theme.Bad : Theme.Good);
        if (hull.IsOwn)
            ShipRow(draw, width, "Battery"u8, Kit.F($"{hull.Energy:0} / {stats.EnergyStorage:0} MJ"));
        else
            ShipRow(draw, width, "Storable energy"u8, Kit.F($"{stats.EnergyStorage:0} MJ"));
        ShipRow(
            draw, width, "FTL drive"u8,
            stats.Ftl switch
            {
                FtlKind.Hyperspace => "Hyperspace"u8,
                FtlKind.Wormhole => "Wormhole"u8,
                FtlKind.Rift => "Rift"u8,
                _ => "None"u8,
            },
            stats.Ftl == FtlKind.None ? Theme.TextMute : Theme.Info);
        if (stats.Ftl != FtlKind.None)
            ShipRow(draw, width, "Jump charge"u8, Kit.F($"{stats.FtlEnergy:0} MJ at {stats.FtlPower:0.0} MW"));

        if (ShipSheet.Shows(ShipSheet.Section.Fuel, stats, design, hull.IsOwn))
        {
            ShipSection(draw, width, "FUEL"u8);
            for (var i = 0; i < ConsoleFuel.Kinds; i++)
            {
                var kind = ConsoleFuel.KindAt(i);
                var capacity = ConsoleFuel.Capacity(hull.Fuel, kind);
                if (capacity <= 0f)
                    continue;
                ShipRow(draw, width, ConsoleFuel.FullName(kind), Kit.F($"{ConsoleFuel.Amount(hull.Fuel, kind):0} / {capacity:0} kg"), ConsoleFuel.Color(hull.Fuel, kind));
                var minutes = ConsoleFuel.MinutesLeft(hull.Fuel, kind);
                if (!float.IsNaN(minutes))
                    ShipRow(draw, width, "Left at this burn"u8, ConsoleFuel.MinutesText(minutes));
            }
        }

        ShipSection(draw, width, "SHIELD"u8);
        ShipRow(draw, width, "Capacity"u8, Kit.F($"{stats.ShieldCapacity:0} hp"));
        ShipRow(draw, width, "Recharge"u8, Kit.F($"{stats.ShieldRecharge:0} hp/s"));
        if (hull.IsOwn && stats.ShieldCapacity > 0f)
            ShipRow(draw, width, "Now"u8, hull.ShieldOffline ? "Offline"u8 : Kit.F($"{hull.Shield:0} hp"), hull.ShieldOffline ? Theme.Bad : Theme.Text);

        if (ShipSheet.Shows(ShipSheet.Section.Cargo, stats, design, hull.IsOwn))
        {
            ShipSection(draw, width, "CARGO"u8);
            var cargoUnits = 0;
            var cargoMass = 0.0;
            foreach (var container in hull.Cargo.Span) { cargoUnits += 1 << container.Size; cargoMass += container.Amount; }
            ShipRow(draw, width, "Hold"u8, hull.IsOwn ? Kit.F($"{cargoUnits} / {stats.CargoHold:0} u") : Kit.F($"{stats.CargoHold:0} u"));
            if (hull.IsOwn)
                ShipRow(draw, width, "Carried"u8, Kit.F($"{cargoMass / 1000.0:0.0} t"));
        }

        if (ShipSheet.Shows(ShipSheet.Section.Drones, stats, design, hull.IsOwn))
        {
            ShipSection(draw, width, "DRONES"u8);
            ShipRow(draw, width, "Bays"u8, Kit.F($"{design.Bays.Length}"));
            ShipRow(draw, width, "Drones kept"u8, Kit.F($"{ShipSheet.BayCapacity(design)}"));
        }

        if (ShipSheet.Shows(ShipSheet.Section.Modules, stats, design, hull.IsOwn))
        {
            ShipSection(draw, width, "MODULES"u8);
            var look = ModuleLook.Current;
            for (var family = ModuleFamily.GasScanner; family <= ModuleFamily.AsteroidLocator; family++)
            {
                var module = stats.Module(family);
                if (module.Mount == 0)
                    continue;
                var range = module.Range;
                ShipTierRow(draw, width, look.Family(family).Label, module.Quality, range >= 1000 ? Kit.F($"{range / 1000:0.##} km") : Kit.F($"{range:0} m"));
            }

            // At most a few rows; a ship with many beams says how many more.
            const int MaxBeamRows = 4;
            var shown = 0;
            foreach (var mount in design.Tractors)
            {
                if (design.Mounts[mount].Type.Tractor is not { } beam)
                    continue;
                if (shown++ < MaxBeamRows)
                    ShipTierRow(draw, width, "Tractor beam"u8, beam.Quality, Kit.F($"{beam.Range:0} m, {beam.MaxMass / 1000:0.#} t"));
            }

            if (shown > MaxBeamRows)
                ShipRow(draw, width, "More tractor beams"u8, Kit.F($"+{shown - MaxBeamRows}"), Theme.TextMute);
        }
    }

    /// <summary>An asteroid's survey: only what the player's scan found, never what the sector generated.</summary>
    private void DrawRockRows(ImDrawListPtr draw, float width, SessionSnapshot snapshot, WorldView world, RockRef rock)
    {
        if (!world.TryGetRock(rock, out var position, out var radius))
        {
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "Nothing selected."u8, width / Kit.Scale);
            return;
        }

        var galaxy = world.Galaxy!;
        var at = ImGui.GetCursorScreenPos();
        Kit.Text(draw, _ui.SansBold, Theme.FontHeading, at, Theme.Text, "Asteroid"u8);
        ImGui.Dummy(new float2(width, Kit.Px(26)));
        ShipSection(draw, width, "SURVEY"u8, true);

        var range = position.RelativeTo(world.OwnPosition).Length();
        // Ore, grade and mass are what the player's scan found; an unscanned rock does not give them away.
        var analysis = FindAnalysis(snapshot, rock);
        if (analysis < 0)
        {
            ShipRow(draw, width, "Resource"u8, "Unscanned"u8, Theme.TextMute);
            ShipRow(draw, width, "Grade"u8, "Unscanned"u8, Theme.TextMute);
        }
        else if (snapshot.Modules.Records[analysis].Resource is >= 0 and var resource && resource < galaxy.Resources.Length)
        {
            ref var found = ref snapshot.Modules.Records[analysis];
            ShipRow(draw, width, "Resource"u8, galaxy.Resources[resource].Name);
            ShipRow(draw, width, "Grade"u8, ShareText(found.Grade));
            var first = MixText(galaxy, found.Mix1, found.Mix1Share);
            var second = MixText(galaxy, found.Mix2, found.Mix2Share);
            if (second.Length != 0)
                ShipRow(draw, width, "Mixed in"u8, Kit.F($"{first}, {second}"));
            else if (first.Length != 0)
                ShipRow(draw, width, "Mixed in"u8, first);
            ShipRow(draw, width, "Rock"u8, RockShareText(RockComposition.From(found, galaxy.Resources.Length)));
        }
        else
        {
            ShipRow(draw, width, "Resource"u8, "None (bare rock)"u8);
        }

        ShipRow(draw, width, "Sector"u8, Kit.F($"{rock.Sector.X} {rock.Sector.Y} {rock.Sector.Z}"));
        ShipRow(draw, width, "Catalogue number"u8, Kit.F($"{rock.Index + 1}"));
        if (galaxy.AsteroidFields is { } fields)
            ShipRow(draw, width, "Size"u8, fields.SizeName(radius));
        ShipRow(draw, width, "Diameter"u8, radius >= 500f ? Kit.F($"{radius * 2 / 1000f:0.00} km") : Kit.F($"{radius * 2:0} m"));
        if (analysis < 0)
            ShipRow(draw, width, "Mass"u8, "Unscanned"u8, Theme.TextMute);
        else
            ShipRow(draw, width, "Mass"u8, MassText(snapshot.Modules.Records[analysis].Mass));
        ShipRow(draw, width, "Drift"u8, Kit.F($"{world.RockDrift(rock):0.00} m/s"));
        ShipRow(draw, width, "Distance from pod"u8, range >= 1000f ? Kit.F($"{range / 1000f:0.00} km") : Kit.F($"{range:0} m"));
    }
}
