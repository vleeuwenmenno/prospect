using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>Eight named gas shares in catalogue order; no heap storage in a sample.</summary>
[InlineArray(8)]
public struct GasShares
{
    private float _first;
}

/// <summary>A local generated mixture. Shares sum to one; purity and density are separate normalized figures.</summary>
public struct GasSample
{
    public GasShares Shares;
    public float Purity;
    public float Density;
    /// <summary>One of eight octants in the cloud's anchor frame.</summary>
    public byte Region;
}

/// <summary>Content-owned gases and kind biases; sampling never consumes a placement stream or allocates.</summary>
public sealed class GasComposition
{
    /// <summary>The fixed wire and save capacity.</summary>
    public const int Capacity = 8;
    private readonly string[] _names;
    private readonly double[][] _weights;
    private readonly double _purityMin, _purityMax;
    private static readonly ulong Stream = SectorRandom.Key("gas-composition-v1");

    private GasComposition(string[] names, double[][] weights, double min, double max)
    { _names = names; _weights = weights; _purityMin = min; _purityMax = max; }

    /// <summary>Stable names in the sample's share order.</summary>
    public IReadOnlyList<string> Names => _names;

    internal static GasComposition Read(JsonElement root)
    {
        var names = new List<string>();
        foreach (var item in root.GetProperty("gases"u8).EnumerateArray())
        {
            var name = item.GetString();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 40 || names.Contains(name))
                throw new InvalidDataException("Invalid gas name.");
            names.Add(name);
        }
        if (names.Count != Capacity) throw new InvalidDataException("Gas composition requires eight named gases.");
        var weights = new double[3][];
        string[] kinds = ["normal", "cold", "lightning"];
        for (var kind = 0; kind < kinds.Length; kind++)
        {
            var array = root.GetProperty(kinds[kind]);
            if (array.GetArrayLength() != Capacity) throw new InvalidDataException("Invalid gas weights.");
            weights[kind] = new double[Capacity];
            var total = 0.0;
            for (var i = 0; i < Capacity; i++)
            {
                var weight = array[i].GetDouble();
                if (!double.IsFinite(weight) || weight < 0 || weight > 1000) throw new InvalidDataException("Invalid gas weight.");
                total += weights[kind][i] = weight;
            }
            if (total == 0) throw new InvalidDataException("An empty gas mix is invalid.");
        }
        var min = root.GetProperty("purityMin"u8).GetDouble();
        var max = root.GetProperty("purityMax"u8).GetDouble();
        if (!double.IsFinite(min) || !double.IsFinite(max) || min < 0 || max > 1 || min > max)
            throw new InvalidDataException("Invalid gas purity range.");
        return new(names.ToArray(), weights, min, max);
    }

    /// <summary>Samples the cloud's local octant at a world position. Outside samples have zero envelope density.</summary>
    public GasSample Sample(Galaxy galaxy, in GasCloudPlacement placement, Double3 position)
    {
        var delta = position - GasCloudQuery.CenterOf(galaxy, placement);
        var cloud = placement.Cloud;
        var region = (byte)((delta.X >= 0 ? 1 : 0) | (delta.Y >= 0 ? 2 : 0) | (delta.Z >= 0 ? 4 : 0));
        var random = new SectorRandom(galaxy.Seed, placement.Anchor,
            Stream ^ BitConverter.SingleToUInt32Bits(cloud.Seed) ^ ((ulong)region << 32));
        var weights = _weights[(int)cloud.Kind];
        Span<double> values = stackalloc double[Capacity];
        var total = 0.0;
        for (var i = 0; i < Capacity; i++) total += values[i] = weights[i] * (0.5 + random.NextSingle());
        var result = new GasSample { Region = region, Purity = (float)(_purityMin + (_purityMax - _purityMin) * random.NextSingle()) };
        for (var i = 0; i < Capacity; i++) result.Shares[i] = (float)(values[i] / total);
        var fill = galaxy.GasClouds!.Shape.X;
        var x = delta.X / (cloud.HalfSize.X * (double)fill);
        var y = delta.Y / (cloud.HalfSize.Y * (double)fill);
        var z = delta.Z / (cloud.HalfSize.Z * (double)fill);
        result.Density = (float)Math.Clamp(1 - (x * x + y * y + z * z), 0, 1);
        return result;
    }
}
