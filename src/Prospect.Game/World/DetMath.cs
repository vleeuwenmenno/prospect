namespace Prospect.Game.World;

/// <summary>
/// Exponential, logarithm and trigonometry built from additions, multiplications, divisions and square roots only.
/// Those are exact to the last bit on every machine; the system's own <c>exp</c> or <c>cos</c> are not, and differ
/// between operating systems. World generation decides discrete things from these values (is this sector empty,
/// does this rock carry ore), so it uses these, and the server and every client arrive at the same galaxy.
/// Accurate to better than one part in a million, which is all generation needs.
/// </summary>
public static class DetMath
{
    private const double Ln2 = 0.6931471805599453;
    private const double InvLn2 = 1.4426950408889634;
    private const double Pi = 3.141592653589793;
    private const double HalfPi = 1.5707963267948966;
    private const double TwoPi = 6.283185307179586;
    private const double Sqrt2 = 1.4142135623730951;

    // The series, lowest power first: e^r, sin(r) / r in r squared, and atan(t) / t in t squared.
    private static readonly double[] ExpTerms =
    [
        1.0,
        1.0,
        1.0 / 2.0,
        1.0 / 6.0,
        1.0 / 24.0,
        1.0 / 120.0,
        1.0 / 720.0,
        1.0 / 5040.0,
        1.0 / 40320.0,
        1.0 / 362880.0,
        1.0 / 3628800.0,
        1.0 / 39916800.0,
    ];

    private static readonly double[] SinTerms =
    [
        1.0,
        -1.0 / 6.0,
        1.0 / 120.0,
        -1.0 / 5040.0,
        1.0 / 362880.0,
        -1.0 / 39916800.0,
        1.0 / 6227020800.0,
        -1.0 / 1307674368000.0,
        1.0 / 355687428096000.0,
    ];

    private static readonly double[] AtanTerms = [1.0, -1.0 / 3.0, 1.0 / 5.0, -1.0 / 7.0, 1.0 / 9.0, -1.0 / 11.0, 1.0 / 13.0, -1.0 / 15.0, 1.0 / 17.0];

    /// <summary>Evaluates a polynomial from its highest term down, one multiplication and one addition per term.</summary>
    private static double Horner(double[] terms, double x)
    {
        var sum = terms[^1];
        for (var i = terms.Length - 2; i >= 0; i--)
            sum = terms[i] + x * sum;
        return sum;
    }

    public static float Exp(float x) => (float)Exp((double)x);

    public static float Log(float x) => (float)Log((double)x);

    public static float Cos(float x) => (float)Sin(x + HalfPi);

    public static float Sin(float x) => (float)Sin((double)x);

    public static float Atan2(float y, float x) => (float)Atan2((double)y, x);

    public static double Exp(double x)
    {
        // Written so that a value that is not a number falls out as zero as well.
        if (!(x > -700.0))
            return 0.0;
        if (x > 700.0)
            return double.PositiveInfinity;

        // e^x = 2^k * e^r with r within half of ln 2 of zero, where a short series is exact enough.
        var k = Math.Floor(x * InvLn2 + 0.5);
        var r = x - k * Ln2;
        var series = Horner(ExpTerms, r);
        return series * BitConverter.Int64BitsToDouble((long)((int)k + 1023) << 52);
    }

    public static double Log(double x)
    {
        if (!(x > 0.0))
            return double.NegativeInfinity;

        // x = m * 2^e with m near one; then ln m = 2 atanh((m - 1) / (m + 1)), a series in a small number.
        var bits = BitConverter.DoubleToInt64Bits(x);
        var e = (int)(bits >> 52 & 0x7FF) - 1023;
        var m = BitConverter.Int64BitsToDouble(bits & 0x000FFFFFFFFFFFFF | 0x3FF0000000000000);
        if (m > Sqrt2)
        {
            m *= 0.5;
            e++;
        }

        var s = (m - 1.0) / (m + 1.0);
        var s2 = s * s;
        var series = s * (2.0 + s2 * (2.0 / 3.0 + s2 * (2.0 / 5.0 + s2 * (2.0 / 7.0 + s2 * (2.0 / 9.0 + s2 * (2.0 / 11.0 + s2 * (2.0 / 13.0)))))));
        return e * Ln2 + series;
    }

    public static double Sin(double x)
    {
        // Fold the angle into a quarter turn either side of zero.
        var r = x - Math.Floor(x / TwoPi + 0.5) * TwoPi;
        if (r > HalfPi)
            r = Pi - r;
        else if (r < -HalfPi)
            r = -Pi - r;

        return r * Horner(SinTerms, r * r);
    }

    public static double Atan2(double y, double x)
    {
        var ax = Math.Abs(x);
        var ay = Math.Abs(y);
        if (ax == 0.0 && ay == 0.0)
            return 0.0;

        // The angle of the smaller over the larger lies within an eighth of a turn. Halving it twice leaves a
        // tangent small enough for a short series.
        var t = ax > ay ? ay / ax : ax / ay;
        t /= 1.0 + Math.Sqrt(1.0 + t * t);
        t /= 1.0 + Math.Sqrt(1.0 + t * t);
        var angle = 4.0 * t * Horner(AtanTerms, t * t);

        if (ay > ax)
            angle = HalfPi - angle;
        if (x < 0.0)
            angle = Pi - angle;
        return y < 0.0 ? -angle : angle;
    }
}
