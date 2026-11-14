using System.Runtime.CompilerServices;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// What a scan found an asteroid is made of, as the rows the target panel and the data sheet show: up to three ore
/// parts (main ore, then mix-ins) and the rock. Built from one <see cref="ModuleObservation"/> and nothing generated, by
/// value, so it takes no memory. A part with share zero is a trace: present, below what the scanner resolves, with
/// no figure and no mass. While a trace exists, the remainder is rock and traces together, so rock is only a ceiling
/// (<see cref="RockIsCeiling"/>, worded "UP TO x %"); with every share resolved it is a figure.
/// </summary>
internal struct RockComposition
{
    /// <summary>The most ore parts: a main ore and two mix-ins.</summary>
    public const int MaxParts = 3;

    private ResourceIds _resource;
    private Shares _share;

    /// <summary>How many ore parts there are; zero for bare rock.</summary>
    public int Count { get; private set; }

    /// <summary>The share of mass left once the resolved ore is taken: rock, plus any traces.</summary>
    public float Remainder { get; private set; }

    /// <summary>A named resource has no resolved share, so <see cref="Remainder"/> is a ceiling on the rock.</summary>
    public bool RockIsCeiling { get; private set; }

    /// <summary>The resource index of part <paramref name="part"/> (0 is the main ore).</summary>
    public readonly int ResourceOf(int part) => _resource[part];

    /// <summary>The resolved share of part <paramref name="part"/>; zero is a trace.</summary>
    public readonly float ShareOf(int part) => _share[part];

    /// <summary>Part <paramref name="part"/> is named but not resolved to a figure.</summary>
    public readonly bool IsTrace(int part) => _share[part] <= 0f;

    /// <summary>
    /// Reads a record. A resource index outside <paramref name="resourceCount"/> counts as absent, so a damaged record
    /// shows as bare rock instead of reading past the table.
    /// </summary>
    public static RockComposition From(in ModuleObservation record, int resourceCount)
    {
        var result = new RockComposition();
        if (record.Resource < 0 || record.Resource >= resourceCount)
        {
            result.Remainder = 1f;
            return result;
        }

        result.Add(record.Resource, record.Grade);
        if (record.Mix1 >= 0 && record.Mix1 < resourceCount)
        {
            result.Add(record.Mix1, record.Mix1Share);
            if (record.Mix2 >= 0 && record.Mix2 < resourceCount)
                result.Add(record.Mix2, record.Mix2Share);
        }

        var ore = 0f;
        for (var i = 0; i < result.Count; i++)
        {
            ore += Math.Max(0f, result._share[i]);
            result.RockIsCeiling |= result._share[i] <= 0f;
        }

        result.Remainder = Math.Clamp(1f - ore, 0f, 1f);
        return result;
    }

    /// <summary>Rows the readout lists below its bar, in unscaled lines: one per ore part and one for the rock.</summary>
    public readonly int Rows => Count + 1;

    private void Add(int resource, float share)
    {
        _resource[Count] = resource;
        _share[Count] = Math.Max(0f, share);
        Count++;
    }

    [InlineArray(MaxParts)]
    private struct ResourceIds { private int _first; }

    [InlineArray(MaxParts)]
    private struct Shares { private float _first; }
}
