using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Prospect.Client.Ui;

/// <summary>
/// Formats interpolated strings for the interface as UTF-8 into a per-frame arena, without allocating. Use through
/// <see cref="Kit.F"/>: <c>Kit.F($"TICK {tick}")</c>. The result is valid until the end of the frame. Render
/// thread only.
/// </summary>
[InterpolatedStringHandler]
internal ref struct UiText
{
    private static readonly byte[] Arena = new byte[256 * 1024];
    private static int _used;
    private readonly int _start;

    public UiText(int literalLength, int formattedCount)
    {
        _start = _used;
    }

    public readonly ReadOnlySpan<byte> Span => Arena.AsSpan(_start, _used - _start);

    /// <summary>Discards everything formatted during the previous frame.</summary>
    public static void ResetArena() => _used = 0;

    public readonly void AppendLiteral(string value) => AppendFormatted(value);

    public readonly void AppendFormatted(string? value)
    {
        if (value is null || Encoding.UTF8.GetMaxByteCount(value.Length) > Arena.Length - _used)
            return;
        _used += Encoding.UTF8.GetBytes(value, Arena.AsSpan(_used));
    }

    /// <summary>Appends UTF-16 text, such as a name held inline in a snapshot, as UTF-8.</summary>
    public readonly void AppendFormatted(ReadOnlySpan<char> utf16)
    {
        if (Encoding.UTF8.GetMaxByteCount(utf16.Length) > Arena.Length - _used)
            return;
        _used += Encoding.UTF8.GetBytes(utf16, Arena.AsSpan(_used));
    }

    public readonly void AppendFormatted(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > Arena.Length - _used)
            return;
        utf8.CopyTo(Arena.AsSpan(_used));
        _used += utf8.Length;
    }

    public readonly void AppendFormatted<T>(T value, string? format = null)
        where T : IUtf8SpanFormattable
    {
        if (value.TryFormat(Arena.AsSpan(_used), out var written, format, CultureInfo.InvariantCulture))
            _used += written;
    }
}
