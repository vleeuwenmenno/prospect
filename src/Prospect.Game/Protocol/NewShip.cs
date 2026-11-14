using System.Runtime.CompilerServices;
using Prospect.Engine.Net;

namespace Prospect.Game.Protocol;

public enum NewShipStatus : byte { None, Pending, Created, Refused }
public readonly record struct NewShipView(NewShipStatus Status, EditorReason Reason, uint Hull, uint Serial);

/// <summary>Bounded UTF-16 name; no text allocation while receiving or refusing a request.</summary>
[InlineArray(64)]
public struct ShipNameText { private char _first; }

public struct ShipName
{
    public const int MaxChars = 32;
    public const int MaxUnits = MaxChars * 2;
    public ShipNameText Text;
    public byte Length;
    public bool Set(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        if (text.Length is < 1 or > MaxUnits) return false;
        var left = text;
        var count = 0;
        while (!left.IsEmpty)
        {
            if (System.Text.Rune.DecodeFromUtf16(left, out var rune, out var consumed) != System.Buffers.OperationStatus.Done
                || ++count > MaxChars || System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control
                    or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.OtherNotAssigned
                    or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator) return false;
            left = left[consumed..];
        }
        text.CopyTo(Text);
        Length = (byte)text.Length;
        return true;
    }
    public readonly void Write(ref NetWriter writer)
    {
        writer.WriteByte(Length);
        for (var i = 0; i < Length; i++) writer.WriteUInt16(Text[i]);
    }
    public static bool Read(ref NetReader reader, out ShipName name)
    {
        name = default;
        var count = reader.ReadByte();
        if (count > MaxUnits || reader.Remaining != count * 2) return false;
        Span<char> text = stackalloc char[MaxUnits];
        for (var i = 0; i < count; i++) text[i] = (char)reader.ReadUInt16();
        // Invalid printable text remains an empty name and receives an explicit refusal.
        name.Set(text[..count]);
        return reader.IsValid;
    }
}
