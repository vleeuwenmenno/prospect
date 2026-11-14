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

    /// <summary>The front's width as a share of its radius, and at least this many metres.</summary>
    public float FrontShare { get; init; }

    public float FrontMinimum { get; init; }

    /// <summary>The pulse's colour, added to the displayed image.</summary>
    public float[] Color { get; init; } = [];

    /// <summary>Light added where a view ray crosses the front face on, before the rim brightens it.</summary>
    public float ShellLight { get; init; }

    /// <summary>The most the rim, where the front is seen edge on, is brightened over the face.</summary>
    public float RimLimit { get; init; }

    /// <summary>How many cells the faint lattice on the front has over the whole sphere.</summary>
    public float PatternCells { get; init; }

    /// <summary>The lattice's line width, as a share of a cell.</summary>
    public float PatternLine { get; init; }

    /// <summary>How much of the front's light is in the lattice's lines rather than spread over the cells, 0 to 1.</summary>
    public float PatternLight { get; init; }

    /// <summary>Light added to asteroids and hulls where the front passes over them.</summary>
    public float SurfaceLight { get; init; }

    /// <summary>How far behind the front a surface keeps a faint glow, as a share of the radius, and how bright.</summary>
    public float SurfaceTrailShare { get; init; }

    public float SurfaceTrailLight { get; init; }

    /// <summary>
    /// How strongly gas hides the pulse, times the cloud's own extinction over the smooth envelope the sensors
    /// use; 0 shows the pulse through gas undimmed.
    /// </summary>
    public float GasDimming { get; init; }

    /// <summary>
    /// The faint pings while a scan runs: one every <see cref="ScanningInterval"/> seconds, each lasting
    /// <see cref="ScanningSeconds"/> and reaching <see cref="ScanningReach"/> metres, at
    /// <see cref="ScanningStrength"/> of the completed pulse's light. A strength of 0 leaves them out.
    /// </summary>
    public float ScanningInterval { get; init; }

    public float ScanningSeconds { get; init; }
    public float ScanningReach { get; init; }
    public float ScanningStrength { get; init; }

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
        if (!(look.FrontShare is > 0f and <= 0.5f) || !(look.FrontMinimum is > 0f and <= 1000f))
            throw new InvalidDataException($"{path}: frontShare is above 0 to 0.5, frontMinimum above 0 to 1000 m");
        if (look.Color.Length != 3 || !(look.Color[0] is >= 0f and <= 1f) || !(look.Color[1] is >= 0f and <= 1f) || !(look.Color[2] is >= 0f and <= 1f))
            throw new InvalidDataException($"{path}: color is three numbers from 0 to 1");
        if (!(look.ShellLight is >= 0f and <= 1f) || !(look.RimLimit is >= 1f and <= 50f) || !(look.SurfaceLight is >= 0f and <= 1f)
            || !(look.SurfaceTrailLight is >= 0f and <= 1f) || !(look.SurfaceTrailShare is > 0f and <= 1f))
            throw new InvalidDataException($"{path}: lights are 0 to 1, rimLimit 1 to 50, surfaceTrailShare above 0 to 1");
        if (!(look.PatternCells is >= 12f and <= 20_000f) || !(look.PatternLine is > 0f and <= 1f) || !(look.PatternLight is >= 0f and <= 1f))
            throw new InvalidDataException($"{path}: patternCells is 12 to 20000, patternLine above 0 to 1, patternLight 0 to 1");
        if (!(look.GasDimming is >= 0f and <= 10f))
            throw new InvalidDataException($"{path}: gasDimming is 0 to 10");
        if (!(look.ScanningInterval is >= 0.3f and <= 10f) || !(look.ScanningSeconds > 0f && look.ScanningSeconds <= look.ScanningInterval)
            || !(look.ScanningReach is >= 10f and <= 100_000f) || !(look.ScanningStrength is >= 0f and <= 1f))
            throw new InvalidDataException($"{path}: scanningInterval is 0.3 to 10, scanningSeconds above 0 to the interval, scanningReach 10 to 100000 m, scanningStrength 0 to 1");
        return look;
    }
}

/// <summary>One frame's shell of the scan pulse: where it starts, how far its front is and how strong it is.</summary>
internal readonly record struct ScanShell(Double3 Origin, float Radius, float Strength);

/// <summary>
/// When the scan pulse shows. A pulse starts when the snapshot's scan goes from running to completed, once for each
/// completed scan, at the place the ship being flown is at that moment, and stays there while it spreads. A
/// cancelled or refused scan starts none, and neither does a completed scan this client never saw running (one
/// already over when the session was joined). While a scan runs, fainter pings follow the ship. Font-free and
/// allocation-free; <see cref="WorldView"/> drives it from its update.
/// </summary>
internal sealed class ScanPulse
{
    private const long None = long.MinValue;

    private readonly ScanPulseLook _look;
    private long _running = None;
    private long _pulsed = None;
    private float _scanning = -1f;

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
        _scanning = -1f;
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
            // A new scan (a different start) begins its pings again.
            if (_running != scan.StartedTick)
                _scanning = 0f;
            else
                _scanning += dt;
            _running = scan.StartedTick;
            return;
        }

        _scanning = -1f;
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

    /// <summary>
    /// The shell to draw this frame, if any: the completed scan's pulse, else a ping of a running scan around
    /// <paramref name="own"/>.
    /// </summary>
    public bool Shell(Double3 own, out ScanShell shell)
    {
        if (Active)
        {
            var t = Math.Clamp(Age / _look.Seconds, 0f, 1f);
            shell = new ScanShell(Origin, Radius(t, _look.Reach), Strength(Age, t, 1f));
            return shell.Strength > 0f;
        }

        if (_scanning >= 0f && _look.ScanningStrength > 0f)
        {
            var age = _scanning % _look.ScanningInterval;
            if (age < _look.ScanningSeconds)
            {
                var t = age / _look.ScanningSeconds;
                shell = new ScanShell(own, Radius(t, _look.ScanningReach), Strength(age, t, _look.ScanningStrength));
                return shell.Strength > 0f;
            }
        }

        shell = default;
        return false;
    }

    private float Radius(float t, float reach) => reach * (1f - MathF.Pow(1f - t, _look.Ease));

    private float Strength(float age, float t, float scale)
    {
        var rise = _look.FadeIn > 0f ? Math.Clamp(age / _look.FadeIn, 0f, 1f) : 1f;
        return scale * rise * MathF.Pow(1f - t, _look.Fade);
    }

    /// <summary>The front's width in metres at <paramref name="radius"/>.</summary>
    public float FrontWidth(float radius) => MathF.Max(_look.FrontMinimum, radius * _look.FrontShare);
}

/// <summary>The scan pulse pass's push constants. Matches <c>scan_pulse.frag</c>.</summary>
internal struct ScanPulseConstants
{
    /// <summary>The pulse's origin relative to the camera, and its front's radius in metres.</summary>
    public float4 Shell;

    /// <summary>The front's width in metres, the pulse's strength from 0 to 1, how far behind the front surfaces keep a glow, and how many lattice cells the sphere has.</summary>
    public float4 Shape;

    /// <summary>The colour, and the light where a view ray crosses the front face on.</summary>
    public float4 Color;

    /// <summary>The light of the front on surfaces, of the glow behind it, how much is in the lattice's lines, and the rim's limit.</summary>
    public float4 Light;

    /// <summary>The nearest cloud's centre relative to the camera, and its extinction times the dimming; 0 for none.</summary>
    public float4 Cloud;

    /// <summary>One over the radii of that cloud's smooth envelope, and the lattice's line width as a share of a cell.</summary>
    public float4 CloudRadii;
}
