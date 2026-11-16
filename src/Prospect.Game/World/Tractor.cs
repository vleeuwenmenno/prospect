using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>One face-mounted tractor's range, acceleration, payload limit and active power cost.</summary>
public sealed record TractorSpec(float Range, float Acceleration, float MaxMass, float BasePower, float PowerPerTonne, ModuleQuality Quality = ModuleQuality.Common)
{
    /// <summary>Reads optional tractor tuning; invalid or unbounded figures refuse the content.</summary>
    public static TractorSpec? Read(JsonElement block, JsonElement root)
    {
        if (!block.TryGetProperty("tractor"u8, out var value)) return null;
        var name = value.GetProperty("quality"u8).GetString();
        var quality = name switch
        {
            "Common" => ModuleQuality.Common,
            "Uncommon" => ModuleQuality.Uncommon,
            "Rare" => ModuleQuality.Rare,
            "Epic" => ModuleQuality.Epic,
            "Legendary" => ModuleQuality.Legendary,
            "Alien Tech" => ModuleQuality.AlienTech,
            _ => throw new InvalidDataException("Unknown tractor quality.")
        };
        var tier = root.GetProperty("tractorTiers"u8).GetProperty(name!);
        float Figure(string key, string scale, float maximum, bool zero = false)
        {
            var basis = value.GetProperty(key).GetSingle();
            var multiplier = tier.GetProperty(scale).GetSingle();
            var result = basis * multiplier;
            if (!float.IsFinite(basis) || basis < 0 || (!zero && basis == 0)
                || !float.IsFinite(multiplier) || multiplier <= 0
                || !float.IsFinite(result) || result > maximum || result < 0 || (!zero && result == 0))
                throw new InvalidDataException("Tractor tuning is outside its finite positive bounds.");
            return result;
        }
        return new(Figure("range", "range", 1000), Figure("acceleration", "acceleration", 100),
            Figure("maxMass", "maxMass", 100000000), Figure("basePower", "power", 10000),
            Figure("powerPerTonne", "power", 10000, zero: true), quality);
    }

    /// <summary>Active draw in MW, including the loose container's shell mass.</summary>
    public float Power(double kilograms) => BasePower + (float)(kilograms / 1000) * PowerPerTonne;
}
