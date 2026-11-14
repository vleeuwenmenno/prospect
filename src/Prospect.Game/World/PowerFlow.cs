namespace Prospect.Game.World;

/// <summary>
/// Where a hull's power went in one tick, in megawatts: what the generators made and what each kind of use took.
/// <see cref="FlightModel.Step"/> fills it from the same arithmetic that moves the battery, so the pilot's
/// readout and the simulation cannot disagree. It is gross: a full battery clips the surplus it would store,
/// but the flow here still shows it, which is why a shield recharging on a hull with spare generation is visible
/// at all. Active module draw uses the same shared spending rule on the server and in prediction.
/// Not simulation state: nothing is sent or saved.
/// </summary>
public struct PowerFlow
{
    /// <summary>Generator output.</summary>
    public float Generated;

    /// <summary>Always-on draw: computer core, gyros.</summary>
    public float Idle;

    /// <summary>Thrust actually supplied, boost included.</summary>
    public float Thrust;

    /// <summary>Shield points restored this tick, as power; restart power while a broken shield charges back.</summary>
    public float Shield;

    /// <summary>What the jump drive took from the battery this tick.</summary>
    public float Ftl;

    /// <summary>Additional active scanner/locator draw after ordinary flight and shield spending.</summary>
    public float Modules;

    /// <summary>Everything drawn.</summary>
    public readonly float Drawn => Idle + Thrust + Shield + Ftl + Modules;

    /// <summary>Generation less everything drawn: positive is surplus, negative comes out of the battery.</summary>
    public readonly float Net => Generated - Drawn;

    /// <summary>Moves each figure a share of the way to <paramref name="sample"/>, so a readout does not flicker.</summary>
    public void Blend(in PowerFlow sample, float share)
    {
        Generated += (sample.Generated - Generated) * share;
        Idle += (sample.Idle - Idle) * share;
        Thrust += (sample.Thrust - Thrust) * share;
        Shield += (sample.Shield - Shield) * share;
        Ftl += (sample.Ftl - Ftl) * share;
        Modules += (sample.Modules - Modules) * share;
    }
}
