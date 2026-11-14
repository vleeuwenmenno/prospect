using System.Globalization;
using System.Text.Json;
using Brutal.Numerics;
using Prospect.Engine.IO;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>The status icons a hull can carry in the command view, the fleet lists and the pilot chip.</summary>
internal enum StatusIcon : byte
{
    /// <summary>A lightning bolt: in a lightning cloud, and while a burst is on, sensors disrupted.</summary>
    Lightning,

    /// <summary>A snowflake: in a cold cloud.</summary>
    Cold,
}

/// <summary>
/// How the status icons look, from <c>content/visuals/status.json</c>: size, colours, and how a burst pulses.
/// Client only; what a hull's status is, is the server's (<see cref="HullEnvironment"/>).
/// </summary>
internal sealed class StatusTuning
{
    /// <summary>An icon's height in unscaled pixels, and the gap between two icons or an icon and its text.</summary>
    public float IconSize { get; init; }
    public float IconGap { get; init; }

    /// <summary>The lightning and the cold icon's colour, as six hex digits (RRGGBB).</summary>
    public string LightningColor { get; init; } = "E6D36E";
    public string ColdColor { get; init; } = "BFE3F7";

    /// <summary>How strongly the lightning icon shows while the hull is in the cloud between bursts, and the cold icon always, 0 to 1.</summary>
    public float CloudAlpha { get; init; }
    public float ColdAlpha { get; init; }

    /// <summary>While a burst is on: pulses a second, the dimmest the icon gets, and how strong the ring round it is, 0 to 1.</summary>
    public float BurstPulseHz { get; init; }
    public float BurstMinAlpha { get; init; }
    public float BurstRingAlpha { get; init; }

    /// <summary>The extra height, unscaled, a fleet list row takes for the status line it shows when the hull has one.</summary>
    public float FleetRowExtra { get; init; }

    /// <summary>The lightning colour as 0xRRGGBB.</summary>
    public uint LightningRgb => Rgb(LightningColor);

    /// <summary>The cold colour as 0xRRGGBB.</summary>
    public uint ColdRgb => Rgb(ColdColor);

    private static uint Rgb(string hex) => uint.TryParse(hex, NumberStyles.AllowHexSpecifier, null, out var value) && hex.Length == 6 ? value : 0xFFFFFF;

    public static StatusTuning Load() => Load(AppPaths.Content("visuals", "status.json"));

    /// <summary>Reads and checks the tuning; anything out of range is refused.</summary>
    public static StatusTuning Load(string path)
    {
        StatusTuning tuning;
        try
        {
            tuning = JsonSerializer.Deserialize<StatusTuning>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"{path}: empty");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }

        static bool Unit(float value) => value is >= 0f and <= 1f;
        static bool Hex(string value) => value.Length == 6 && uint.TryParse(value, NumberStyles.AllowHexSpecifier, null, out _);
        if (!(tuning.IconSize is >= 6 and <= 24) || !(tuning.IconGap is >= 0 and <= 16))
            throw new InvalidDataException($"{path}: iconSize is 6 to 24, iconGap 0 to 16");
        if (!Hex(tuning.LightningColor) || !Hex(tuning.ColdColor))
            throw new InvalidDataException($"{path}: lightningColor and coldColor are six hex digits");
        if (!Unit(tuning.CloudAlpha) || !Unit(tuning.ColdAlpha) || !Unit(tuning.BurstMinAlpha) || !Unit(tuning.BurstRingAlpha))
            throw new InvalidDataException($"{path}: alphas are 0 to 1");
        if (!(tuning.BurstPulseHz is >= 0.25f and <= 8) || !(tuning.FleetRowExtra is >= 0 and <= 40))
            throw new InvalidDataException($"{path}: burstPulseHz is 0.25 to 8, fleetRowExtra 0 to 40");
        return tuning;
    }
}

/// <summary>
/// What a hull's authoritative <see cref="HullEnvironment"/> flags show as: which icons, in which order, with what
/// words, and how strongly each is drawn. Font-free and allocation-free, so every list reads the same mapping and
/// a test can drive it. The flags come from the snapshot, never from a hull's drawn position.
/// </summary>
internal static class HullStatus
{
    /// <summary>How many icons a hull with these flags shows: lightning first, then cold.</summary>
    public static int Count(HullEnvironment environment)
    {
        var count = 0;
        if (HasLightning(environment))
            count++;
        if ((environment & HullEnvironment.InColdCloud) != 0)
            count++;
        return count;
    }

    /// <summary>The icon in slot <paramref name="slot"/> (0 to <see cref="Count"/> - 1).</summary>
    public static StatusIcon Icon(HullEnvironment environment, int slot) =>
        slot == 0 && HasLightning(environment) ? StatusIcon.Lightning : StatusIcon.Cold;

    /// <summary>True while a lightning burst disrupts this hull's sensors: the icon pulses.</summary>
    public static bool Bursting(HullEnvironment environment, int slot) =>
        Icon(environment, slot) == StatusIcon.Lightning && (environment & HullEnvironment.SensorsDisrupted) != 0;

    /// <summary>The short name beside an icon where there is room for words.</summary>
    public static ReadOnlySpan<byte> Label(HullEnvironment environment, int slot) =>
        Icon(environment, slot) == StatusIcon.Cold ? "COLD CLOUD"u8
            : Bursting(environment, slot) ? "SENSORS DISRUPTED"u8
            : "LIGHTNING CLOUD"u8;

    /// <summary>One of the two lines of an icon's tooltip, <paramref name="line"/> 0 or 1.</summary>
    public static ReadOnlySpan<byte> Meaning(HullEnvironment environment, int slot, int line) =>
        Icon(environment, slot) == StatusIcon.Cold ? (line == 0 ? "Inside a cold cloud."u8 : "Asteroids here are ice shards."u8)
            : Bursting(environment, slot) ? (line == 0 ? "Lightning is jamming this ship's sensors."u8 : "Its reach is cut until the burst passes."u8)
            : line == 0 ? "Inside a lightning cloud."u8 : "Bursts jam sensors every few seconds."u8;

    /// <summary>The icon's colour.</summary>
    public static uint Rgb(StatusTuning tuning, StatusIcon icon) => icon == StatusIcon.Cold ? tuning.ColdRgb : tuning.LightningRgb;

    /// <summary>
    /// How strongly the icon is drawn at <paramref name="time"/> seconds: steady in a cloud, pulsing between its
    /// dimmest and full while a burst is on.
    /// </summary>
    public static float Alpha(StatusTuning tuning, HullEnvironment environment, int slot, float time)
    {
        if (Icon(environment, slot) == StatusIcon.Cold)
            return tuning.ColdAlpha;
        return Bursting(environment, slot) ? tuning.BurstMinAlpha + (1f - tuning.BurstMinAlpha) * Pulse(tuning, time) : tuning.CloudAlpha;
    }

    /// <summary>A smooth 0 to 1 wave at the burst's pulse rate.</summary>
    public static float Pulse(StatusTuning tuning, float time) => 0.5f + 0.5f * MathF.Sin(time * tuning.BurstPulseHz * MathF.Tau);

    /// <summary>The width of all of a hull's icons side by side, in pixels already scaled; zero for none.</summary>
    public static float IconsWidth(HullEnvironment environment, float size, float gap)
    {
        var count = Count(environment);
        return count == 0 ? 0f : count * size + (count - 1) * gap;
    }

    /// <summary>The slot whose icon <paramref name="pointer"/> is on when the icons start at <paramref name="left"/> and are centred on <paramref name="middle"/> in height, or -1.</summary>
    public static int Hit(HullEnvironment environment, float left, float middle, float size, float gap, float2 pointer)
    {
        var half = size * 0.5f;
        if (pointer.Y < middle - half || pointer.Y > middle + half)
            return -1;
        for (var slot = 0; slot < Count(environment); slot++)
        {
            var x = left + slot * (size + gap);
            if (pointer.X >= x && pointer.X <= x + size)
                return slot;
        }

        return -1;
    }

    /// <summary>The height a fleet list row takes: <paramref name="row"/>, and the status line's extra when the hull has an icon.</summary>
    public static float FleetRowHeight(HullEnvironment environment, float row, float extra) => Count(environment) > 0 ? row + extra : row;

    private static bool HasLightning(HullEnvironment environment) => (environment & (HullEnvironment.InLightningCloud | HullEnvironment.SensorsDisrupted)) != 0;
}
