using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>The independent equipment families. Unknown families are refused until their server behavior exists.</summary>
public enum ModuleFamily : byte { None, GasScanner, AsteroidScanner, AsteroidLocator }

/// <summary>Ordered equipment quality, independent of mark and variant.</summary>
public enum ModuleQuality : byte { Common, Uncommon, Rare, Epic, Legendary, AlienTech }

/// <summary>Evaluated equipment figures from one block type; duplicates and resizing do not improve them.</summary>
public readonly record struct ModuleSpec(ModuleFamily Family, ModuleQuality Quality, int Mark, string Variant,
    double Range, double Accuracy, double ScanSeconds, double PowerDraw, double CooldownSeconds)
{
    /// <summary>Decodes one module using the catalogue's content-owned tier multipliers.</summary>
    internal static ModuleSpec Read(JsonElement block, JsonElement root)
    {
        if (!block.TryGetProperty("module"u8, out var module)) return default;
        var family = module.GetProperty("family"u8).GetString() switch
        {
            "gasScanner" => ModuleFamily.GasScanner,
            "asteroidScanner" => ModuleFamily.AsteroidScanner,
            "asteroidLocator" => ModuleFamily.AsteroidLocator,
            _ => throw new InvalidDataException("Unknown module family.")
        };
        var name = module.GetProperty("quality"u8).GetString();
        var quality = name switch
        {
            "Common" => ModuleQuality.Common,
            "Uncommon" => ModuleQuality.Uncommon,
            "Rare" => ModuleQuality.Rare,
            "Epic" => ModuleQuality.Epic,
            "Legendary" => ModuleQuality.Legendary,
            "Alien Tech" => ModuleQuality.AlienTech,
            _ => throw new InvalidDataException("Unknown module quality.")
        };
        var mark = module.GetProperty("mark"u8).GetInt32();
        var variant = module.GetProperty("variant"u8).GetString();
        if (mark is < 1 or > 255 || string.IsNullOrWhiteSpace(variant) || variant.Length > 32)
            throw new InvalidDataException("Invalid module mark or variant.");
        var tier = root.GetProperty("moduleTiers"u8).GetProperty(name!);
        double Figure(string key)
        {
            var value = module.GetProperty(key).GetDouble();
            var multiplier = tier.GetProperty(key).GetDouble();
            var result = value * multiplier;
            if (!double.IsFinite(value) || value <= 0 || !double.IsFinite(multiplier) || multiplier <= 0
                || !double.IsFinite(result) || result <= 0 || result > 1000000)
                throw new InvalidDataException("Invalid module figure.");
            return result;
        }
        var accuracy = Figure("accuracy");
        if (accuracy > 1) throw new InvalidDataException("Module accuracy exceeds one.");
        return new(family, quality, mark, variant!, Figure("range"), accuracy, Figure("scanSeconds"),
            Figure("powerDraw"), Figure("cooldownSeconds"));
    }
}

/// <summary>The chosen live block of one module family and its content-derived figures. Block is one-based; zero means absent.</summary>
public readonly record struct ModuleEquipment(int Block, ModuleQuality Quality, int Mark, double Range,
    double Accuracy, double ScanSeconds, double PowerDraw, double CooldownSeconds)
{
    /// <summary>
    /// Field-wise equality written by hand: the synthesized one goes through <c>EqualityComparer&lt;T&gt;.Default</c>,
    /// whose first use for each field type allocates its comparer, and the server compares equipment on the tick.
    /// </summary>
    public bool Equals(ModuleEquipment other) =>
        Block == other.Block && Quality == other.Quality && Mark == other.Mark && Range == other.Range
        && Accuracy == other.Accuracy && ScanSeconds == other.ScanSeconds && PowerDraw == other.PowerDraw
        && CooldownSeconds == other.CooldownSeconds;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Block, (int)Quality, Mark, Range, Accuracy, HashCode.Combine(ScanSeconds, PowerDraw, CooldownSeconds));

    /// <summary>Copies the selected block's evaluated specification without allocating.</summary>
    internal static ModuleEquipment Of(int block, in ModuleSpec spec) => block == 0 ? default :
        new(block, spec.Quality, spec.Mark, spec.Range, spec.Accuracy, spec.ScanSeconds, spec.PowerDraw, spec.CooldownSeconds);
}
