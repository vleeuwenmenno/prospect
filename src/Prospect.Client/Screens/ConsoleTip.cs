using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>Where a console row's details go: measured first, then drawn, from the same lines.</summary>
internal interface IConsoleTipSink
{
    /// <summary>The row's name and its state in a word or a figure, in the state's colour.</summary>
    void Title(ReadOnlySpan<byte> title, ReadOnlySpan<byte> state, uint stateColor);

    /// <summary>One figure: what it is, and its value with its unit.</summary>
    void Line(ReadOnlySpan<byte> label, ReadOnlySpan<byte> value, uint valueColor);

    /// <summary>A sentence across the whole width, for what a figure cannot say.</summary>
    void Note(ReadOnlySpan<byte> text, uint color);
}

/// <summary>
/// The details a pilot console row shows while the pointer is on it: only figures the snapshot publishes for the
/// hull being flown, read again every frame. Nothing here is a forecast; a broken shield's wait is told as the rule
/// it follows, never as a countdown.
/// </summary>
internal static class ConsoleTip
{
    /// <summary>The space inside the box and between a label and its value, in unscaled pixels.</summary>
    public const float Pad = Theme.Space2;
    public const float Gap = Theme.Space3;

    /// <summary>The height of the title line and of every other line, in unscaled pixels.</summary>
    public const float TitleLine = 20f;
    public const float LineHeight = 16f;

    /// <summary>Writes a row's details into <paramref name="sink"/>.</summary>
    public static void Write<T>(ref T sink, ConsoleBar bar, in HullView own, in FlightParams flight)
        where T : struct, IConsoleTipSink
    {
        switch (bar)
        {
            case ConsoleBar.Shield:
                Shield(ref sink, own, flight);
                break;
            case ConsoleBar.Hull:
                Hull(ref sink, own, flight);
                break;
            case ConsoleBar.Energy:
                Energy(ref sink, own, flight);
                break;
            case ConsoleBar.Heat:
                Heat(ref sink, own, flight);
                break;
        }
    }

    /// <summary>The box a row's details need at <paramref name="scale"/>.</summary>
    public static float2 Measure(ImFontPtr mono, ImFontPtr sans, float scale, ConsoleBar bar, in HullView own, in FlightParams flight)
    {
        var measure = new Measurer(mono, sans);
        Write(ref measure, bar, own, flight);
        return measure.Size(scale);
    }

    /// <summary>Draws a row's details in <paramref name="rect"/>, as <see cref="Measure"/> sized them.</summary>
    public static void Draw(ImDrawListPtr draw, ImFontPtr mono, ImFontPtr sans, float scale, in HudRect rect, uint tone, ConsoleBar bar, in HullView own, in FlightParams flight)
    {
        var min = new float2(rect.Left, rect.Top);
        var max = new float2(rect.Right, rect.Bottom);
        Hud.Plate(draw, min, max, tone, true);
        draw.PushClipRect(min, max, true);
        var painter = new Painter(draw, mono, sans, scale, min, max.X);
        Write(ref painter, bar, own, flight);
        draw.PopClipRect();
    }

    private static void Shield<T>(ref T sink, in HullView own, in FlightParams flight)
        where T : struct, IConsoleTipSink
    {
        var capacity = flight.ShieldCapacity;
        if (!(capacity > 0f) || !float.IsFinite(capacity))
        {
            sink.Title("SHIELD"u8, "NONE"u8, Theme.TextMute);
            sink.Note("NO SHIELD EMITTER ON THIS HULL"u8, Theme.TextDim);
            return;
        }

        var mark = FlightConsole.RestartMark(flight);
        var thousands = FlightConsole.Thousands(capacity);
        if (own.ShieldOffline)
        {
            // Offline, the stored charge is building back but takes no hits: say so, and what brings it back.
            sink.Title("SHIELD"u8, "OFFLINE"u8, Theme.Bad);
            sink.Line("STORED"u8, ScreenHost.Points(own.Shield, capacity), Theme.Text);
            if (mark > 0f)
            {
                var threshold = capacity * mark;
                sink.Line("RESTARTS AT"u8, thousands ? Kit.F($"{threshold / 1000f:0}k ({mark * 100f:0} %)") : Kit.F($"{threshold:0} ({mark * 100f:0} %)"), Theme.Text);
                sink.Line("PROGRESS"u8, Kit.F($"{FlightConsole.RestartProgress(own.Shield, flight) * 100f:0} %"), Theme.Text);
            }

            sink.Line("RECHARGE"u8, Kit.F($"{flight.ShieldRecharge:0.#} /S"), Theme.Text);
            sink.Line("DELAY"u8, Kit.F($"{flight.ShieldDelay:0.#} S AFTER DAMAGE"), Theme.Text);
            sink.Line("RESTART DRAW"u8, Power(flight.ShieldRestartPower), FlightConsole.DrawColor);
            if (Starved(own, flight))
                sink.Note("BATTERY EMPTY: NOT CHARGING"u8, Theme.Bad);
            sink.Note("NO PROTECTION UNTIL IT RESTARTS"u8, Theme.Bad);
            return;
        }

        sink.Title("SHIELD"u8, "ONLINE"u8, Theme.Info);
        sink.Line("CHARGE"u8, ScreenHost.Points(own.Shield, capacity), Theme.Text);
        sink.Line("RECHARGE"u8, Kit.F($"{flight.ShieldRecharge:0.#} /S"), Theme.Text);
        sink.Line("DELAY"u8, Kit.F($"{flight.ShieldDelay:0.#} S AFTER DAMAGE"), Theme.Text);
        sink.Line("CHARGING DRAW"u8, Power(flight.ShieldPower), FlightConsole.DrawColor);
        if (mark > 0f)
            sink.Line("IF BROKEN"u8, Kit.F($"RESTARTS AT {mark * 100f:0} %"), Theme.TextDim);
        if (Starved(own, flight))
            sink.Note("BATTERY EMPTY: NOT CHARGING"u8, Theme.Bad);
    }

    private static void Hull<T>(ref T sink, in HullView own, in FlightParams flight)
        where T : struct, IConsoleTipSink
    {
        var hull = FlightConsole.Fraction(own.Hull, flight.HullPoints);
        sink.Title("HULL"u8, Kit.F($"{hull * 100f:0} %"), hull < 0.3f ? Theme.Bad : Theme.Good);
        sink.Line("POINTS"u8, ScreenHost.Points(own.Hull, flight.HullPoints), Theme.Text);
        sink.Line("MASS"u8, Kit.F($"{FlightConsole.Shown(flight.Mass, float.MaxValue):0} T"), Theme.Text);
        if (flight.CollisionSafeSpeed > 0f)
            sink.Line("SAFE IMPACT"u8, Kit.F($"BELOW {flight.CollisionSafeSpeed:0} M/S"), Theme.Text);
    }

    private static void Energy<T>(ref T sink, in HullView own, in FlightParams flight)
        where T : struct, IConsoleTipSink
    {
        if (!(flight.EnergyStorage > 0f))
        {
            sink.Title("ENERGY"u8, "NONE"u8, Theme.TextMute);
            sink.Note("NO BATTERY ON THIS HULL"u8, Theme.TextDim);
            sink.Line("GENERATION"u8, Power(flight.PowerGenerated), Theme.Text);
            return;
        }

        var energy = FlightConsole.Fraction(own.Energy, flight.EnergyStorage);
        sink.Title("ENERGY"u8, Kit.F($"{energy * 100f:0} %"), energy < 0.15f ? Theme.Bad : Theme.Accent);
        sink.Line("STORED"u8, ScreenHost.Energy(own.Energy, flight.EnergyStorage), Theme.Text);
        var rate = float.IsFinite(own.EnergyRate) ? own.EnergyRate : 0f;
        if (MathF.Abs(rate) < FlightConsole.SteadyRate)
            sink.Line("NET FLOW"u8, "STEADY"u8, Theme.TextMute);
        else
            sink.Line("NET FLOW"u8, Power(rate, true), rate < 0f ? FlightConsole.DrawColor : FlightConsole.ChargeColor);
        sink.Line("GENERATION"u8, Power(flight.PowerGenerated), Theme.Text);
        sink.Line("BASE DRAW"u8, Power(flight.IdlePower), Theme.Text);
        sink.Line("BOOST FLOOR"u8, FlightConsole.Thousands(flight.EnergyStorage) ? Kit.F($"{flight.BoostEnergyFloor / 1000f:0.0} GJ") : Kit.F($"{flight.BoostEnergyFloor:0} MJ"), Theme.Text);
    }

    private static void Heat<T>(ref T sink, in HullView own, in FlightParams flight)
        where T : struct, IConsoleTipSink
    {
        var heat = FlightConsole.Fraction(own.Heat, 1f);
        var starved = FlightConsole.BoostStarved(own, flight);
        ReadOnlySpan<byte> state = own.Boosting ? "BOOSTING"u8 : own.BoostLocked ? "OVERHEATED"u8 : starved ? "NO ENERGY"u8 : "READY"u8;
        var stateColor = own.Boosting ? Theme.Accent : own.BoostLocked || starved ? Theme.Bad : Theme.Good;
        sink.Title("BOOST HEAT"u8, state, stateColor);
        sink.Line("HEAT"u8, Kit.F($"{heat * 100f:0} %"), FlightConsole.HeatColor(heat));
        if (own.BoostLocked)
            sink.Line("UNLOCKS AT"u8, Kit.F($"{FlightModel.BoostUnlockHeat * 100f:0} %"), Theme.Text);
        else if (starved)
            sink.Line("NEEDS STORED"u8, FlightConsole.Thousands(flight.EnergyStorage) ? Kit.F($"{flight.BoostEnergyFloor / 1000f:0.0} GJ") : Kit.F($"{flight.BoostEnergyFloor:0} MJ"), Theme.Text);
        sink.Line("HEATING"u8, Kit.F($"{flight.HeatRate * 100f:0.#} %/S"), Theme.Text);
        sink.Line("COOLING"u8, Kit.F($"{flight.CoolRate * 100f:0.#} %/S"), Theme.Text);
        sink.Line("BOOST DRAW"u8, Power(flight.ThrustPower * flight.BoostPowerMultiplier), FlightConsole.DrawColor);
    }

    /// <summary>True when an unfilled shield cannot charge because the battery is flat.</summary>
    private static bool Starved(in HullView own, in FlightParams flight) => own.Energy <= 0f && own.Shield < flight.ShieldCapacity;

    /// <summary>Megawatts, or gigawatts for the largest figures; signed when it is a flow.</summary>
    private static ReadOnlySpan<byte> Power(float megawatts, bool signed = false)
    {
        var shown = float.IsFinite(megawatts) ? megawatts : 0f;
        if (MathF.Abs(shown) >= 1000f)
            return signed ? Kit.F($"{shown / 1000f:+0.0;-0.0} GW") : Kit.F($"{shown / 1000f:0.0} GW");
        return signed ? Kit.F($"{shown:+0.0;-0.0} MW") : Kit.F($"{shown:0.#} MW");
    }

    /// <summary>Takes the widest of each kind of line, to size the box.</summary>
    private struct Measurer(ImFontPtr mono, ImFontPtr sans) : IConsoleTipSink
    {
        private float _title;
        private float _label;
        private float _value;
        private float _note;
        private int _lines;

        public void Title(ReadOnlySpan<byte> title, ReadOnlySpan<byte> state, uint stateColor) =>
            _title = Kit.Measure(sans, Theme.FontSmall, title).X + Kit.Measure(sans, Theme.FontCaption, state).X;

        public void Line(ReadOnlySpan<byte> label, ReadOnlySpan<byte> value, uint valueColor)
        {
            _label = Math.Max(_label, Kit.Measure(sans, Theme.FontCaption, label).X);
            _value = Math.Max(_value, Kit.Measure(mono, Theme.FontCaption, value).X);
            _lines++;
        }

        public void Note(ReadOnlySpan<byte> text, uint color)
        {
            _note = Math.Max(_note, Kit.Measure(sans, Theme.FontCaption, text).X);
            _lines++;
        }

        public readonly float2 Size(float scale)
        {
            float Px(float design) => MathF.Round(design * scale);
            var inner = Math.Max(Math.Max(_title, _label + _value) + Px(Gap), _note);
            return new float2(MathF.Ceiling(inner) + 2 * Px(Pad), 2 * Px(Pad) + Px(TitleLine) + _lines * Px(LineHeight));
        }
    }

    /// <summary>Draws the lines down the box: labels at the left, values and the title's state at the right.</summary>
    private struct Painter(ImDrawListPtr draw, ImFontPtr mono, ImFontPtr sans, float scale, float2 min, float right) : IConsoleTipSink
    {
        private float _y = min.Y + MathF.Round(Pad * scale);
        private readonly float _left = min.X + MathF.Round(Pad * scale);
        private readonly float _right = right - MathF.Round(Pad * scale);

        public void Title(ReadOnlySpan<byte> title, ReadOnlySpan<byte> state, uint stateColor)
        {
            Kit.Text(draw, sans, Theme.FontSmall, new float2(_left, _y), Theme.Text, title);
            var size = Kit.Measure(sans, Theme.FontCaption, state);
            Kit.Text(draw, sans, Theme.FontCaption, new float2(_right - size.X, _y + MathF.Round(2 * scale)), stateColor, state);
            _y += MathF.Round(TitleLine * scale);
        }

        public void Line(ReadOnlySpan<byte> label, ReadOnlySpan<byte> value, uint valueColor)
        {
            Kit.Text(draw, sans, Theme.FontCaption, new float2(_left, _y), Theme.TextDim, label);
            var size = Kit.Measure(mono, Theme.FontCaption, value);
            Kit.Text(draw, mono, Theme.FontCaption, new float2(_right - size.X, _y), valueColor, value);
            _y += MathF.Round(LineHeight * scale);
        }

        public void Note(ReadOnlySpan<byte> text, uint color)
        {
            Kit.Text(draw, sans, Theme.FontCaption, new float2(_left, _y), color, text);
            _y += MathF.Round(LineHeight * scale);
        }
    }
}
