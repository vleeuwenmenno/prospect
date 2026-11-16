using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>One face-mounted tractor's range, acceleration, payload limit and active power cost.</summary>
public sealed record TractorSpec(float Range, float Acceleration, float MaxMass, float BasePower, float PowerPerTonne)
{
    /// <summary>Reads optional tractor tuning; invalid or unbounded figures refuse the content.</summary>
    public static TractorSpec? Read(JsonElement block)
    {
        if (!block.TryGetProperty("tractor"u8, out var value)) return null;
        var result = new TractorSpec(value.GetProperty("range"u8).GetSingle(), value.GetProperty("acceleration"u8).GetSingle(),
            value.GetProperty("maxMass"u8).GetSingle(), value.GetProperty("basePower"u8).GetSingle(), value.GetProperty("powerPerTonne"u8).GetSingle());
        if (!(result.Range is > 0 and <= 1000) || !(result.Acceleration is > 0 and <= 100)
            || !(result.MaxMass is > 0 and <= 100000000) || !(result.BasePower is > 0 and <= 10000)
            || !(result.PowerPerTonne is >= 0 and <= 10000))
            throw new InvalidDataException("Tractor tuning is outside its finite positive bounds.");
        return result;
    }

    /// <summary>Active draw in MW, including the loose container's shell mass.</summary>
    public float Power(double kilograms) => BasePower + (float)(kilograms / 1000) * PowerPerTonne;
}
