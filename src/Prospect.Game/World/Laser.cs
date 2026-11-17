using System.Numerics;
using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>
/// One face-mounted mining laser's figures after its tier: how far it reaches, the radius of the cut it makes, how
/// deep it cuts a second, the power it draws while firing and how far off its face's outward normal it may aim. Read
/// from a block's <c>"laser"</c> entry and the catalogue's <c>laserTiers</c>, as <see cref="TractorSpec"/> reads
/// tractors: only the Common type is in the palette. Its heat figures (<c>heatSeconds</c>, <c>coolSeconds</c>,
/// <c>unlockHeat</c>) are the laser's own and no tier scales them; <see cref="LaserHeat"/> is the rule that uses them.
/// </summary>
public sealed record LaserSpec(float Range, float CutRadius, float CutRate, float Power, float GimbalDegrees, ModuleQuality Quality = ModuleQuality.Common)
{
    /// <summary>The cosine of <see cref="GimbalDegrees"/>: a beam whose direction is closer than this to the face's normal may fire.</summary>
    public float GimbalCosine { get; } = MathF.Cos(GimbalDegrees * MathF.PI / 180f);

    /// <summary>Heat the laser gains a second while it fires, from 0 to 1: it overheats at 1 and shuts down.</summary>
    public float HeatRate { get; init; } = 1f / 20f;

    /// <summary>Heat the laser sheds a second while it does not fire.</summary>
    public float CoolRate { get; init; } = 1f / 15f;

    /// <summary>The heat an overheated laser must cool to before it fires again, above 0 and below 1.</summary>
    public float UnlockHeat { get; init; } = 0.3f;

    /// <summary>Reads an optional laser entry; figures outside their bounds refuse the content.</summary>
    public static LaserSpec? Read(JsonElement block, JsonElement root)
    {
        if (!block.TryGetProperty("laser"u8, out var value)) return null;
        var name = value.GetProperty("quality"u8).GetString();
        var quality = name switch
        {
            "Common" => ModuleQuality.Common,
            "Uncommon" => ModuleQuality.Uncommon,
            "Rare" => ModuleQuality.Rare,
            "Epic" => ModuleQuality.Epic,
            "Legendary" => ModuleQuality.Legendary,
            "Alien Tech" => ModuleQuality.AlienTech,
            _ => throw new InvalidDataException("Unknown laser quality."),
        };
        var tier = root.GetProperty("laserTiers"u8).GetProperty(name!);
        float Figure(string key, string scale, float maximum)
        {
            var basis = value.GetProperty(key).GetSingle();
            var multiplier = tier.GetProperty(scale).GetSingle();
            var result = basis * multiplier;
            if (!float.IsFinite(basis) || basis <= 0 || !float.IsFinite(multiplier) || multiplier <= 0
                || !float.IsFinite(result) || result <= 0 || result > maximum)
                throw new InvalidDataException("Laser tuning is outside its finite positive bounds.");
            return result;
        }

        var gimbal = value.GetProperty("gimbalDegrees"u8).GetSingle();
        if (!float.IsFinite(gimbal) || gimbal <= 0 || gimbal >= 90)
            throw new InvalidDataException("A laser's gimbalDegrees is above 0 and below 90: it never aims back over its own face.");
        float Seconds(string key)
        {
            var seconds = value.GetProperty(key).GetSingle();
            if (!float.IsFinite(seconds) || seconds < 0.5f || seconds > 3600)
                throw new InvalidDataException($"A laser's {key} is from 0.5 to 3600 seconds.");
            return seconds;
        }

        var unlock = value.GetProperty("unlockHeat"u8).GetSingle();
        if (!float.IsFinite(unlock) || unlock <= 0 || unlock >= 1)
            throw new InvalidDataException("A laser's unlockHeat is above 0 and below 1: an overheated laser must cool before it fires again.");
        return new LaserSpec(Figure("range", "range", 1000), Figure("cutRadius", "cutRadius", 2), Figure("cutRate", "cutRate", 10),
            Figure("power", "power", 10000), gimbal, quality)
        {
            HeatRate = 1f / Seconds("heatSeconds"),
            CoolRate = 1f / Seconds("coolSeconds"),
            UnlockHeat = unlock,
        };
    }

    /// <summary>
    /// Whether a beam along <paramref name="direction"/> (unit, any frame) may leave a laser whose face points along
    /// <paramref name="outward"/> (unit, the same frame): within the gimbal limit. The one rule, for the server and
    /// for what the interface shows.
    /// </summary>
    public bool WithinGimbal(Vector3 outward, Vector3 direction) => Vector3.Dot(outward, direction) >= GimbalCosine;
}

/// <summary>
/// Action groups: the parts a player fires together, chosen per mount (<see cref="DesignMount.Group"/>) and per hull
/// (an override the server keeps). Groups are 1 to 10; 10 is the key <c>0</c>. Zero means no group: such a part never
/// fires. Only a part whose type <see cref="Groupable"/> says so may have one.
/// </summary>
public static class ActionGroups
{
    /// <summary>How many groups there are.</summary>
    public const int Count = 10;

    /// <summary>No group: the part does not fire.</summary>
    public const byte None = 0;

    /// <summary>The value of a per-hull override that is not set: the design's group stands.</summary>
    public const byte NotOverridden = 0xFF;

    /// <summary>A mask with every group's bit: bit <c>g - 1</c> is group <c>g</c>.</summary>
    public const ushort AllMask = (1 << Count) - 1;

    /// <summary>Whether a part of <paramref name="type"/> belongs in a group: lasers today, weapons later.</summary>
    public static bool Groupable(BlockType? type) => type?.Laser is not null;

    /// <summary>
    /// The group a part starts in when it is placed: group 1 for a groupable part, so a new laser fires without a
    /// further step, and none for everything else.
    /// </summary>
    public static byte Default(BlockType? type) => Groupable(type) ? (byte)1 : None;

    /// <summary>Whether <paramref name="group"/> is a group (1 to 10) or none.</summary>
    public static bool Valid(int group) => group is >= 0 and <= Count;

    /// <summary>Whether a mount of <paramref name="type"/> may carry <paramref name="group"/>: none always, a group only on a groupable part.</summary>
    public static bool Allowed(BlockType? type, int group) => group == None || (Valid(group) && Groupable(type));

    /// <summary>The bit of <paramref name="group"/> in a mask; zero for none.</summary>
    public static ushort Bit(int group) => group is >= 1 and <= Count ? (ushort)(1 << (group - 1)) : (ushort)0;

    /// <summary>The digit a group's key shows: 1 to 9, and 0 for group 10.</summary>
    public static int Digit(int group) => group % Count;

    /// <summary>
    /// The group a mount fires with on a hull: the hull's override when one is set, else the design's.
    /// </summary>
    public static int Effective(in DesignMount mount, byte overridden) => overridden == NotOverridden ? mount.Group : overridden;
}
