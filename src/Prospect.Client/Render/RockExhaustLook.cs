namespace Prospect.Client.Render;

/// <summary>Surface response of one propulsion kind, in metres and seconds.</summary>
internal sealed class RockExhaustLook
{
    public float DustRate { get; init; }
    public float DustSeconds { get; init; }
    public float DustSpeed { get; init; }
    public float DustLift { get; init; }
    public float DustSize { get; init; }
    public float Glow { get; init; }
    public float Scorch { get; init; }
    public float Shimmer { get; init; }
    public float HotSeconds { get; init; }
    public float ScorchHoldSeconds { get; init; }
    public float FadeSeconds { get; init; }
    public float Spread { get; init; }
    public float Bias { get; init; }

    public void Check(string kind)
    {
        if (!(DustRate is >= 0 and <= 2000) || !(DustSeconds is > 0 and <= 2) || !(DustSpeed is > 0 and <= 100)
            || !(DustLift is > 0 and <= 20) || !(DustSize is > 0 and <= 1) || !(Glow is >= 0 and <= 10)
            || !(Scorch is >= 0 and <= 1) || !(Shimmer is >= 0 and <= 1) || !(HotSeconds is > 0 and <= 5)
            || !(ScorchHoldSeconds >= 0 && ScorchHoldSeconds < FadeSeconds)
            || !(FadeSeconds is > 0 and <= 60) || !(Spread is > 0 and <= 5) || !(Bias is > 0 and <= 0.1f)
            || (kind == "ion" && (DustRate != 0 || Scorch != 0 || Glow != 0))
            || (kind == "puff" && (Scorch != 0 || Glow != 0 || Shimmer != 0)))
            throw new InvalidDataException("Invalid rock exhaust response");
    }
}
