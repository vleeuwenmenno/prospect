using System.Text.Json;
using Brutal.Numerics;
using Prospect.Engine.IO;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// How the scan pulse looks, from <c>content/visuals/scan.json</c>. Client data: the pulse is only something the
/// scanning player sees, and changes nothing anyone else sees or the server decides.
/// </summary>
internal sealed class ScanPulseLook
{
    /// <summary>How long the pulse after a completed scan lasts, in seconds.</summary>
    public float Seconds { get; init; }

    /// <summary>How far the pulse's front has gone when it ends, in metres.</summary>
    public float Reach { get; init; }

    /// <summary>How sharply the front shoots out and then slows: the radius is <c>reach × (1 − (1 − t)^ease)</c>.</summary>
    public float Ease { get; init; }

    /// <summary>Seconds over which the pulse comes up at its start, so its first frame does not flash.</summary>
    public float FadeIn { get; init; }

    /// <summary>How fast the pulse dims over its life: its strength is <c>(1 − t)^fade</c>.</summary>
    public float Fade { get; init; }

    /// <summary>
    /// How deep the band behind the front is, in which surfaces show the grid: a share of the front's radius, and at
    /// least <see cref="BandMinimum"/> metres.
    /// </summary>
    public float BandShare { get; init; }

    public float BandMinimum { get; init; }

    /// <summary>The pulse's colour, added to the displayed image.</summary>
    public float[] Color { get; init; } = [];

    /// <summary>The width of the front's edge and of the grid's lines, in pixels.</summary>
    public float LineWidth { get; init; }

    /// <summary>
    /// The grid's finest spacing in metres. Farther away it doubles, so its cells are never under
    /// <see cref="GridPixels"/> pixels across.
    /// </summary>
    public float GridMinimum { get; init; }

    public float GridPixels { get; init; }

    /// <summary>Light of the front's edge where it crosses a surface.</summary>
    public float EdgeLight { get; init; }

    /// <summary>Light of the grid's lines on surfaces in the band behind the front, at its front.</summary>
    public float GridLight { get; init; }

    /// <summary>Light spread evenly over the band on surfaces, under the grid.</summary>
    public float FillLight { get; init; }

    /// <summary>
    /// Light of the grid on the front where it passes through gas, and spread evenly there, both times the gas's
    /// density on the smooth envelope the sensors use (1 at a cloud's core).
    /// </summary>
    public float GasLight { get; init; }

    public float GasFill { get; init; }

    /// <summary>The grid's finest spacing in gas, in metres: coarser than on surfaces, so the fog holds a few clear lines.</summary>
    public float GasGrid { get; init; }

    /// <summary>How strongly gas in front hides the pulse, times the cloud's own extinction; 0 shows it undimmed.</summary>
    public float GasDimming { get; init; }

    public static ScanPulseLook Load() => Load(AppPaths.Content("visuals", "scan.json"));

    /// <summary>Reads and checks a look; anything out of range is refused.</summary>
    public static ScanPulseLook Load(string path)
    {
        var look = JsonSerializer.Deserialize<ScanPulseLook>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"{path}: empty");
        if (!(look.Seconds is >= 0.3f and <= 10f) || !(look.Reach is >= 100f and <= 100_000f) || !(look.Ease is >= 1f and <= 6f))
            throw new InvalidDataException($"{path}: seconds is 0.3 to 10, reach 100 to 100000 m, ease 1 to 6");
        if (!(look.FadeIn >= 0f && look.FadeIn < look.Seconds) || !(look.Fade is >= 0.2f and <= 6f))
            throw new InvalidDataException($"{path}: fadeIn is 0 to under seconds, fade 0.2 to 6");
        if (!(look.BandShare is > 0f and <= 1f) || !(look.BandMinimum is > 0f and <= 5000f))
            throw new InvalidDataException($"{path}: bandShare is above 0 to 1, bandMinimum above 0 to 5000 m");
        if (look.Color.Length != 3 || !(look.Color[0] is >= 0f and <= 1f) || !(look.Color[1] is >= 0f and <= 1f) || !(look.Color[2] is >= 0f and <= 1f))
            throw new InvalidDataException($"{path}: color is three numbers from 0 to 1");
        if (!(look.LineWidth is >= 0.5f and <= 6f) || !(look.GridMinimum is >= 0.5f and <= 1000f) || !(look.GridPixels is >= 4f and <= 200f))
            throw new InvalidDataException($"{path}: lineWidth is 0.5 to 6 px, gridMinimum 0.5 to 1000 m, gridPixels 4 to 200");
        if (!(look.EdgeLight is >= 0f and <= 2f) || !(look.GridLight is >= 0f and <= 2f) || !(look.FillLight is >= 0f and <= 2f)
            || !(look.GasLight is >= 0f and <= 2f) || !(look.GasFill is >= 0f and <= 2f))
            throw new InvalidDataException($"{path}: lights are 0 to 2");
        if (!(look.GasGrid is >= 1f and <= 5000f))
            throw new InvalidDataException($"{path}: gasGrid is 1 to 5000 m");
        if (!(look.GasDimming is >= 0f and <= 10f))
            throw new InvalidDataException($"{path}: gasDimming is 0 to 10");
        return look;
    }
}

/// <summary>One frame's front of the scan pulse: where it starts, how far it is and how strong it is.</summary>
internal readonly record struct ScanShell(Double3 Origin, float Radius, float Strength);

/// <summary>
/// When the scan pulse shows. A pulse starts when the snapshot's scan goes from running to completed, once for each
/// completed scan, at the place the ship being flown is at that moment, and stays there while it spreads. A
/// cancelled or refused scan starts none, and neither does a completed scan this client never saw running (one
/// already over when the session was joined). Nothing shows while a scan runs. Font-free and allocation-free;
/// <see cref="WorldView"/> drives it from its update.
/// </summary>
internal sealed class ScanPulse
{
    private const long None = long.MinValue;

    private readonly ScanPulseLook _look;
    private long _running = None;
    private long _pulsed = None;

    public ScanPulse(ScanPulseLook look) => _look = look;

    /// <summary>How many pulses have started since this was made or reset.</summary>
    public int Started { get; private set; }

    /// <summary>True while the pulse of a completed scan shows.</summary>
    public bool Active { get; private set; }

    /// <summary>Seconds since the current pulse started.</summary>
    public float Age { get; private set; }

    /// <summary>Where the current pulse started.</summary>
    public Double3 Origin { get; private set; }

    /// <summary>Forgets everything: a new session, or the world view starting over.</summary>
    public void Reset()
    {
        _running = _pulsed = None;
        Active = false;
        Age = 0f;
        Started = 0;
    }

    /// <summary>
    /// Follows the snapshot's scan for one frame of <paramref name="dt"/> seconds, with the ship being flown at
    /// <paramref name="own"/>.
    /// </summary>
    public void Observe(in ScanView scan, Double3 own, float dt)
    {
        if (Active)
        {
            Age += dt;
            if (Age >= _look.Seconds)
                Active = false;
        }

        if (scan.State == ScanState.Scanning)
        {
            _running = scan.StartedTick;
            return;
        }

        if (scan.State == ScanState.Completed && _running == scan.StartedTick && _pulsed != scan.StartedTick)
        {
            _pulsed = scan.StartedTick;
            Active = true;
            Age = 0f;
            Origin = own;
            Started++;
        }

        _running = None;
    }

    /// <summary>The pulse's front this frame, if one shows.</summary>
    public bool Shell(out ScanShell shell)
    {
        if (!Active)
        {
            shell = default;
            return false;
        }

        var t = Math.Clamp(Age / _look.Seconds, 0f, 1f);
        var rise = _look.FadeIn > 0f ? Math.Clamp(Age / _look.FadeIn, 0f, 1f) : 1f;
        shell = new ScanShell(Origin, _look.Reach * (1f - MathF.Pow(1f - t, _look.Ease)), rise * MathF.Pow(1f - t, _look.Fade));
        return shell.Strength > 0f;
    }

    /// <summary>How deep the band behind the front is, in metres, at <paramref name="radius"/>.</summary>
    public float Band(float radius) => MathF.Max(_look.BandMinimum, radius * _look.BandShare);
}

/// <summary>The scan pulse pass's push constants. Matches <c>scan_pulse.frag</c>.</summary>
internal struct ScanPulseConstants
{
    /// <summary>The pulse's origin relative to the camera, and its front's radius in metres.</summary>
    public float4 Shell;

    /// <summary>The band's depth in metres, the pulse's strength from 0 to 1, the grid's finest spacing in metres and its smallest cell in pixels.</summary>
    public float4 Band;

    /// <summary>The colour, and the line width in pixels.</summary>
    public float4 Color;

    /// <summary>The light of the front's edge, of the grid in the band, of the band's even fill, and of the grid in gas.</summary>
    public float4 Light;

    /// <summary>The nearest cloud's centre relative to the camera, and its extinction times the dimming.</summary>
    public float4 Cloud;

    /// <summary>One over the radii of that cloud's smooth envelope, zero for no cloud.</summary>
    public float4 CloudRadii;

    /// <summary>The grid's spacing in gas in metres, and the even light in gas.</summary>
    public float4 Gas;
}
