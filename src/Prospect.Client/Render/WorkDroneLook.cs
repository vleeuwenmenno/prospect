using System.Numerics;

namespace Prospect.Client.Render;

/// <summary>
/// How a work drone is drawn, from the <c>workDrone</c> entry of <c>content/visuals/propulsion.json</c>
/// (<see cref="PropulsionModels.BuildWorkDrone"/>): one model of its own in place of the design's blocks. Lengths
/// across, up and along the drone are shares of half the design's block bounds on that axis, so the model stays
/// about the box that collision and picking know; the smaller details are shares of the body's half height.
/// </summary>
internal sealed class WorkDroneLook
{
    /// <summary>The body's half length, half width and half height, as shares of the design's bounds.</summary>
    public float Length { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }

    /// <summary>The cut of the body's four long edges, as a share of its half height.</summary>
    public float Chamfer { get; init; }

    /// <summary>The length of the dark cap at each end, as a share of the half length, and its section as a share of the body's.</summary>
    public float CapLength { get; init; }
    public float CapScale { get; init; }

    /// <summary>
    /// A thruster pod on each side: its half length (share of the body's half length), how far it stands out (share of
    /// the bounds' half width), its half height (share of the body's) and where its middle is along the body, from
    /// -1 (stern) to 1 (nose).
    /// </summary>
    public float PodLength { get; init; }
    public float PodWidth { get; init; }
    public float PodHeight { get; init; }
    public float PodAt { get; init; }

    /// <summary>How far the tractor array's tray hangs under the body, as a share of the body's half height, and its glowing segments.</summary>
    public float Belly { get; init; }
    public int BellySegments { get; init; }

    /// <summary>The sensor eye's radius, the antenna's height above the body and the utility mount's radius (a share of the body's half width).</summary>
    public float Eye { get; init; }
    public float Antenna { get; init; }
    public float Mount { get; init; }

    /// <summary>Raised strokes on both sides (<see cref="StrokeFont"/>); empty for none.</summary>
    public string Marking { get; init; } = "";

    /// <summary>Paint of the plating, of the end caps and pods, and of the accent panels, RGB from 0 to 1.</summary>
    public float[] Body { get; init; } = [];
    public float[] Dark { get; init; } = [];
    public float[] Accent { get; init; } = [];

    /// <summary>The light of the sensor eye and of the small lights (beside the eye, on the antenna), RGB from 0 to 1.</summary>
    public float[] EyeLight { get; init; } = [];
    public float[] Light { get; init; } = [];

    /// <summary>The colour of the tractor array and of the pods' strips, whose brightness follows what the drone does.</summary>
    public float[] Glow { get; init; } = [];

    /// <summary>Brightness of the array and the strips while nothing happens.</summary>
    public float IdleGlow { get; init; }

    /// <summary>Brightness of the array while the drone carries, with its pulses a second and their depth as a share.</summary>
    public float CarryGlow { get; init; }
    public float PulseRate { get; init; }
    public float PulseDepth { get; init; }

    /// <summary>Brightness of the pods' strips at full thrust.</summary>
    public float ThrustGlow { get; init; }

    /// <summary>Metres past a carrier's reach within which the array brightens as the drone closes on what it carries.</summary>
    public float CarryFadeMetres { get; init; }

    /// <summary>The array and the strips are left dark on a drone whose radius on screen is under this many pixels.</summary>
    public float GlowPixels { get; init; }

    /// <summary>
    /// The instance colour the model is drawn with. Its paint is this look's, and the mesh shader mixes a little of the
    /// instance's colour into every paint: the dark paint leaves the model's own colours as they are written. Worked
    /// out when asked: a static field here or in the view would be set up inside the frame that draws the first drone.
    /// </summary>
    public Vector3 Tint => new(Dark[0], Dark[1], Dark[2]);

    /// <summary>The glow at <paramref name="level"/> times its colour, as the light a part gives off.</summary>
    public Vector3 GlowLight(float level) => new(Glow[0] * level, Glow[1] * level, Glow[2] * level);

    /// <summary>
    /// How much a drone <paramref name="distance"/> metres from the middle of a body of <paramref name="radius"/> counts
    /// as carrying it, from 0 to 1: wholly within <paramref name="reach"/> of its side (as far as the shared rule lets a
    /// carrier be: the tether, the attach gap and the drone's own radius), fading out over
    /// <see cref="CarryFadeMetres"/> beyond.
    /// </summary>
    public float Carry(float distance, float radius, float reach) =>
        Math.Clamp((radius + reach + CarryFadeMetres - distance) / CarryFadeMetres, 0f, 1f);

    /// <summary>The tractor array's brightness for a drone carrying by <paramref name="carry"/> (0 to 1) at <paramref name="time"/> seconds.</summary>
    public float ArrayLevel(float carry, float time) =>
        IdleGlow + (CarryGlow * (1f + PulseDepth * MathF.Sin(time * PulseRate * MathF.Tau)) - IdleGlow) * Math.Clamp(carry, 0f, 1f);

    /// <summary>The pods' strips' brightness at <paramref name="throttle"/>, the largest share of thrust in use on any axis.</summary>
    public float StripLevel(float throttle) => IdleGlow + (ThrustGlow - IdleGlow) * Math.Clamp(throttle, 0f, 1f);

    /// <summary>Refuses figures that would leave the box the drone collides as far behind, or that make no shape.</summary>
    internal void Check(string path)
    {
        static bool Colour(float[] c) => c.Length == 3 && c[0] is >= 0 and <= 1 && c[1] is >= 0 and <= 1 && c[2] is >= 0 and <= 1;
        foreach (var c in Marking)
            if (!StrokeFont.Has(c))
                throw new InvalidDataException($"{path}: the work drone's marking has a character the stroke font lacks");
        if (!(Length is >= 0.5f and <= 1.6f) || !(Width is >= 0.3f and <= 1f) || !(Height is >= 0.2f and <= 1f)
            || !(Chamfer is > 0 and <= 0.6f) || !(CapLength is >= 0.05f and <= 0.35f) || !(CapScale is >= 0.6f and < 1f)
            || !(PodLength is >= 0.1f and <= 0.5f) || !(PodWidth > 0 && Width + PodWidth <= 1.3f) || !(PodHeight is >= 0.2f and <= 1f)
            // A pod stays behind the nose cap and before the stern cap.
            || !(MathF.Abs(PodAt) + PodLength <= 1f - CapLength)
            || !(Belly is > 0 and <= 0.6f) || BellySegments is < 1 or > 6
            || !(Eye is >= 0.15f and <= 0.6f) || !(Antenna is >= 0.3f and <= 3f) || !(Mount is >= 0.2f and <= 0.8f)
            || Marking.Length > 8
            || !Colour(Body) || !Colour(Dark) || !Colour(Accent) || !Colour(EyeLight) || !Colour(Light) || !Colour(Glow)
            || !(IdleGlow is >= 0 and <= 8) || !(CarryGlow >= IdleGlow && CarryGlow <= 8) || !(ThrustGlow >= IdleGlow && ThrustGlow <= 8)
            || !(PulseRate is >= 0 and <= 10) || !(PulseDepth is >= 0 and <= 1)
            || !(CarryFadeMetres is > 0 and <= 50) || !(GlowPixels is >= 0 and <= 64))
            throw new InvalidDataException($"{path}: the work drone look is out of range");
    }
}
