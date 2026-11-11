using System.Numerics;
using System.Text.Json;
using Brutal.Numerics;
using Prospect.Engine.IO;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// How shields look, from <c>content/visuals/shields.json</c>: how long a collapse, a fade-in and a ripple last,
/// how a ripple spreads, at what charge a shield starts to waver, its colours and how far it bends what is behind
/// it. Durations stay well inside the time the simulation counts an event's age for, so an old change or hit is
/// always known to be old.
/// </summary>
internal sealed class ShieldLook
{
    /// <summary>
    /// The longest any shield effect may last, in seconds: under the simulation's saturated event age (two
    /// seconds) less the remote interpolation delay, with room to spare.
    /// </summary>
    public const float MaxEffectSeconds = 1.5f;

    public float CollapseSeconds { get; init; }
    public float FadeInSeconds { get; init; }
    public float RippleSeconds { get; init; }

    /// <summary>Metres a second a ripple's ring spreads, and how wide it is in metres.</summary>
    public float RippleSpeed { get; init; }
    public float RippleWidth { get; init; }

    /// <summary>The share of its capacity below which a shield starts to waver, and at which it wavers most.</summary>
    public float UnstableBelow { get; init; }
    public float UnstableFull { get; init; }

    public float[] CalmColor { get; init; } = [];
    public float[] StrainedColor { get; init; } = [];
    public float[] CollapseColor { get; init; } = [];

    /// <summary>How bright the shell's rim glows, in scene light.</summary>
    public float RimGlow { get; init; }

    /// <summary>How many pixels the shell bends what is behind it at its rim.</summary>
    public float RefractionPixels { get; init; }

    /// <summary>The look as the shield shader's push constants carry it; see <c>shield.frag</c>.</summary>
    public ShieldLookConstants Constants =>
        new()
        {
            Calm = new float4(CalmColor[0], CalmColor[1], CalmColor[2], RimGlow),
            Strained = new float4(StrainedColor[0], StrainedColor[1], StrainedColor[2], RefractionPixels),
            Collapse = new float4(CollapseColor[0], CollapseColor[1], CollapseColor[2], 0),
            Ripple = new float4(RippleSpeed, RippleWidth, RippleSeconds, 0),
        };

    public static ShieldLook Load() => Load(AppPaths.Content("visuals", "shields.json"));

    /// <summary>Reads and checks a look; anything out of range is refused.</summary>
    public static ShieldLook Load(string path)
    {
        var look = JsonSerializer.Deserialize<ShieldLook>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"{path}: empty");
        static bool Seconds(float s) => s >= 0.05f && s <= MaxEffectSeconds;
        static bool Colour(float[] c) => c.Length == 3 && c[0] is >= 0 and <= 4 && c[1] is >= 0 and <= 4 && c[2] is >= 0 and <= 4;
        if (!Seconds(look.CollapseSeconds) || !Seconds(look.FadeInSeconds) || !Seconds(look.RippleSeconds))
            throw new InvalidDataException($"{path}: collapse, fade-in and ripple last from 0.05 to {MaxEffectSeconds} s");
        if (!(look.RippleSpeed > 0 && look.RippleSpeed <= 100) || !(look.RippleWidth > 0 && look.RippleWidth <= 20))
            throw new InvalidDataException($"{path}: rippleSpeed is above 0 to 100 m/s, rippleWidth above 0 to 20 m");
        if (!(look.UnstableFull >= 0 && look.UnstableFull < look.UnstableBelow && look.UnstableBelow <= 1))
            throw new InvalidDataException($"{path}: needs 0 <= unstableFull < unstableBelow <= 1");
        if (!Colour(look.CalmColor) || !Colour(look.StrainedColor) || !Colour(look.CollapseColor))
            throw new InvalidDataException($"{path}: colours are three numbers from 0 to 4");
        if (!(look.RimGlow is >= 0 and <= 2) || !(look.RefractionPixels is >= 0 and <= 8))
            throw new InvalidDataException($"{path}: rimGlow is 0 to 2, refractionPixels 0 to 8");
        return look;
    }
}

/// <summary>The shield shader's push constants. Matches <c>shield.frag</c>.</summary>
internal struct ShieldLookConstants
{
    /// <summary>The calm colour, and the rim's glow.</summary>
    public float4 Calm;

    /// <summary>The colour near breaking, and how many pixels the rim bends.</summary>
    public float4 Strained;

    /// <summary>The collapse's colour.</summary>
    public float4 Collapse;

    /// <summary>A ripple's speed in m/s, width in metres and life in seconds.</summary>
    public float4 Ripple;
}
