using System.Numerics;

namespace Prospect.Client.Render;

/// <summary>
/// How containers are seen going into and out of a rack (<see cref="World.CargoMotion"/>): how fast they slide, how
/// long the mag-locks take, how long a released one takes to leave, and the colours of the lock lights in each stage.
/// The <c>motion</c> entry of the <c>rack</c> look in <c>visuals/propulsion.json</c>, checked on load. Client look
/// only: the server stows and releases as it always did. Colours are linear, as <c>mesh.frag</c> writes them.
/// </summary>
internal sealed class CargoMotionLook
{
    /// <summary>Metres a second a container slides inside a rack, to its slot or to another after a repack.</summary>
    public float SlideSpeed { get; init; }

    /// <summary>The shortest slide, so a container already beside its slot still visibly settles.</summary>
    public float MinSeconds { get; init; }

    /// <summary>The longest slide, however far it goes.</summary>
    public float MaxSeconds { get; init; }

    /// <summary>How long the mag-locks flash after a container reaches its slot.</summary>
    public float LockSeconds { get; init; }

    /// <summary>How long a released container waits in its slot while the locks let go.</summary>
    public float UnlockSeconds { get; init; }

    /// <summary>How long a released container takes to slide out to the loose body it hands over to.</summary>
    public float LeaveSeconds { get; init; }

    /// <summary>How long a hold change and a loose body may be apart and still be the same container.</summary>
    public float MatchSeconds { get; init; }

    /// <summary>Lock light pulses a second while a container moves.</summary>
    public float PulseHz { get; init; }

    /// <summary>Lock light RGB of a container held still in its slot.</summary>
    public float[] StoredLight { get; init; } = [];
    /// <summary>Brightness of <see cref="StoredLight"/>.</summary>
    public float StoredLevel { get; init; }

    /// <summary>Lock light RGB while a container moves in the rack.</summary>
    public float[] MovingLight { get; init; } = [];
    /// <summary>Brightness of <see cref="MovingLight"/> at the top of its pulse.</summary>
    public float MovingLevel { get; init; }

    /// <summary>Lock light RGB of the flash when the mag-locks engage; it fades to <see cref="StoredLight"/>.</summary>
    public float[] LockLight { get; init; } = [];
    /// <summary>Brightness of <see cref="LockLight"/> at the start of the flash.</summary>
    public float LockLevel { get; init; }

    /// <summary>Lock light RGB while the locks let a released container go.</summary>
    public float[] UnlockLight { get; init; } = [];
    /// <summary>Brightness of <see cref="UnlockLight"/>.</summary>
    public float UnlockLevel { get; init; }

    /// <summary>Refuses durations or colours out of range.</summary>
    public void Check()
    {
        if (!(SlideSpeed is > 0 and <= 50) || !(MinSeconds is >= 0 and <= 5) || !(MaxSeconds >= MinSeconds && MaxSeconds <= 20)
            || !(LockSeconds is > 0 and <= 5) || !(UnlockSeconds is >= 0 and <= 5) || !(LeaveSeconds is > 0 and <= 10)
            || !(MatchSeconds is > 0 and <= 10) || !(PulseHz is >= 0 and <= 20))
            throw new InvalidDataException("Invalid cargo motion timing.");
        foreach (var (rgb, level) in new[] { (StoredLight, StoredLevel), (MovingLight, MovingLevel), (LockLight, LockLevel), (UnlockLight, UnlockLevel) })
            if (rgb.Length != 3 || rgb.Any(c => !float.IsFinite(c) || c < 0 || c > 1) || !(level is >= 0 and <= 20))
                throw new InvalidDataException("Invalid cargo lock light.");
    }

    /// <summary>A light's colour at a brightness.</summary>
    public static Vector3 Light(float[] rgb, float level) => new Vector3(rgb[0], rgb[1], rgb[2]) * level;
}
