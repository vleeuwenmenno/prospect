using System.Globalization;
using System.Text;

namespace Prospect.Game.Protocol;

/// <summary>
/// The rules for a server's name, the same ones the listing server applies (docs/listing-server.md), so a name
/// the game accepts is a name the list accepts. A name is shown to every player who browses or joins, so nothing
/// that can reorder, hide or break text survives cleaning.
/// </summary>
public static class ServerNames
{
    /// <summary>The longest name, in Unicode characters, after cleaning.</summary>
    public const int MaxLength = 48;

    /// <summary>The most UTF-16 units a name takes: every character outside the basic plane takes two.</summary>
    public const int MaxChars = MaxLength * 2;

    /// <summary>The most bytes a name takes on the wire, in UTF-8.</summary>
    public const int MaxBytes = MaxLength * 4;

    /// <summary>The name of a server whose host chose none.</summary>
    public const string Default = "PROSPECT server";

    /// <summary>What a name from the network that cleans to nothing is shown as.</summary>
    public const string Unnamed = "Unnamed server";

    /// <summary>
    /// Checks a name its host chose. True, with the cleaned name, when 1 to <see cref="MaxLength"/> characters
    /// are left after cleaning; false when nothing is left or it is too long.
    /// </summary>
    public static bool TryNormalize(ReadOnlySpan<char> raw, out string name)
    {
        Span<char> clean = stackalloc char[MaxChars];
        var length = Clean(raw, clean, out var truncated);
        name = length > 0 && !truncated ? clean[..length].ToString() : "";
        return name.Length > 0;
    }

    /// <summary>
    /// Cleans a name that came from somewhere untrusted (the network, the list): cut to <see cref="MaxLength"/>
    /// characters, and <see cref="Unnamed"/> when nothing is left.
    /// </summary>
    public static string Clean(ReadOnlySpan<char> raw)
    {
        Span<char> clean = stackalloc char[MaxChars];
        var length = Clean(raw, clean, out _);
        return length > 0 ? clean[..length].ToString() : Unnamed;
    }

    /// <summary>
    /// Removes control, formatting (bidirectional overrides, zero-width characters), private-use and unassigned
    /// characters and broken surrogates, turns every run of whitespace into one space and trims the ends.
    /// Stops after <see cref="MaxLength"/> characters and says so in <paramref name="truncated"/>. Returns the
    /// number of UTF-16 units written to <paramref name="destination"/>, which must hold <see cref="MaxChars"/>.
    /// </summary>
    public static int Clean(ReadOnlySpan<char> raw, Span<char> destination, out bool truncated)
    {
        truncated = false;
        var length = 0;
        var count = 0;
        var pendingSpace = false;
        foreach (var rune in raw.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (
                rune == Rune.ReplacementChar
                || category
                    is UnicodeCategory.Control
                        or UnicodeCategory.Format
                        or UnicodeCategory.Surrogate
                        or UnicodeCategory.PrivateUse
                        or UnicodeCategory.OtherNotAssigned
            )
                continue;

            if (Rune.IsWhiteSpace(rune) || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                pendingSpace = count > 0;
                continue;
            }

            // A space only goes in once something follows it, so the end never needs trimming.
            var needed = pendingSpace ? 2 : 1;
            if (count + needed > MaxLength)
            {
                truncated = true;
                break;
            }

            if (pendingSpace)
            {
                destination[length++] = ' ';
                pendingSpace = false;
            }

            length += rune.EncodeToUtf16(destination[length..]);
            count += needed;
        }

        return length;
    }
}
