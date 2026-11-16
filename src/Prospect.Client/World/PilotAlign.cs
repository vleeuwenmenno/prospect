using System.Numerics;
using System.Text.Json;
using Prospect.Engine.IO;

namespace Prospect.Client.World;

/// <summary>Pilot-view tuning from <c>content/visuals/pilot.json</c>. Client data only.</summary>
internal sealed record PilotTuning
{
    /// <summary>How close the nose must be to the view line, in degrees, before a clicked align stops.</summary>
    public float AlignStopDegrees { get; init; } = 2f;

    /// <summary>Cosine of <see cref="AlignStopDegrees"/>: the nose is aligned when its dot with the view is at least this.</summary>
    public float AlignStopCosine => MathF.Cos(AlignStopDegrees * MathF.PI / 180f);

    public static PilotTuning Load() => Load(AppPaths.Content("visuals", "pilot.json"));

    /// <summary>Reads and checks the tuning; anything out of range is refused.</summary>
    public static PilotTuning Load(string path)
    {
        var tuning = JsonSerializer.Deserialize<PilotTuning>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"{path}: empty");
        if (!(tuning.AlignStopDegrees is >= 0.1f and <= 30f))
            throw new InvalidDataException($"{path}: alignStopDegrees is 0.1 to 30");
        return tuning;
    }
}

/// <summary>
/// The pilot's "turn the hull onto the view line" button, font-free. Held, it aligns. Let go, the hull keeps
/// following the view as it is each frame until its nose is within the stop angle of it (following the live view, not
/// the line at release, so the hull never settles on a direction the player has since looked away from). Thrust (which
/// aligns by itself), free look, a shown pointer or a jump end it, and a new press restarts it.
/// </summary>
internal sealed class PilotAlign(float stopCosine)
{
    private bool _wasHeld;

    /// <summary>True while a released click is still turning the hull.</summary>
    public bool Latched { get; private set; }

    /// <summary>Whether the hull should align with the view this frame. Takes no memory.</summary>
    public bool Update(bool held, bool blocked, Vector3 nose, Vector3 look)
    {
        if (blocked)
        {
            _wasHeld = false;
            Latched = false;
            return false;
        }

        if (held)
        {
            _wasHeld = true;
            Latched = false;
            return true;
        }

        if (_wasHeld)
        {
            _wasHeld = false;
            Latched = true;
        }

        if (Latched && Vector3.Dot(nose, look) >= stopCosine)
            Latched = false;
        return Latched;
    }

    /// <summary>Forgets a held button and a standing click, for a view that stops reading input.</summary>
    public void Clear()
    {
        _wasHeld = false;
        Latched = false;
    }
}
