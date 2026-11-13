using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prospect.Engine.Net;

namespace Prospect.Game.World;

/// <summary>
/// What a game's shared content comes to, in sixteen bytes: two games with the same identity fly the same blocks
/// under the same rules and generate the same galaxy. A server compares a client's identity before admission.
/// Protocol 23 supplies the immutable design index mapping separately through the server catalogue.
///
/// It is the first sixteen bytes of a SHA-256 over <see cref="EvaluatorVersion"/> and every content file the
/// shared simulation reads, in the order <see cref="GameContent.Load(string)"/> reads them. Sixteen bytes are
/// plenty for what it guards against, which is an accident and not an attack: a stale checkout, a retuned file,
/// a mod on one side. It is no proof of anything, since a client that wants to lie can send any bytes it likes,
/// so more bytes would only make every hello longer.
/// </summary>
public readonly struct ContentIdentity : IEquatable<ContentIdentity>
{
    /// <summary>The bytes an identity takes in a message.</summary>
    public const int WireSize = 16;

    /// <summary>
    /// The version of the arithmetic that turns blocks into figures: <see cref="BoxHullSums"/>, the size rules,
    /// <see cref="FlightRules.Under"/> and what a design derives from them. Bump it when that arithmetic changes
    /// what comes out while the content files stay as they are, so that two builds which would evaluate the same
    /// files differently do not pass for the same content.
    /// </summary>
    public const uint EvaluatorVersion = 4;

    private readonly ulong _low;
    private readonly ulong _high;

    private ContentIdentity(ulong low, ulong high)
    {
        _low = low;
        _high = high;
    }

    /// <summary>The identity whose bytes are the first <see cref="WireSize"/> of <paramref name="digest"/>.</summary>
    internal static ContentIdentity FromDigest(ReadOnlySpan<byte> digest) =>
        new(BinaryPrimitives.ReadUInt64LittleEndian(digest), BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]));

    /// <summary>Writes the identity's <see cref="WireSize"/> bytes.</summary>
    public void Write(ref NetWriter writer)
    {
        writer.WriteUInt64(_low);
        writer.WriteUInt64(_high);
    }

    /// <summary>Reads an identity. False when the message ends before it does. Allocates nothing.</summary>
    public static bool TryRead(ref NetReader reader, out ContentIdentity identity)
    {
        var low = reader.ReadUInt64();
        var high = reader.ReadUInt64();
        identity = new ContentIdentity(low, high);
        return reader.IsValid;
    }

    /// <inheritdoc/>
    public bool Equals(ContentIdentity other) => _low == other._low && _high == other._high;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ContentIdentity other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_low, _high);

    /// <summary>The identity's bytes in hexadecimal, for a log. Allocates; never call it per connection.</summary>
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[WireSize];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, _low);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], _high);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>True when both are the identity of the same content.</summary>
    public static bool operator ==(ContentIdentity left, ContentIdentity right) => left.Equals(right);

    /// <summary>True when the two are identities of different content.</summary>
    public static bool operator !=(ContentIdentity left, ContentIdentity right) => !left.Equals(right);
}

/// <summary>
/// Works out a <see cref="ContentIdentity"/> from content files, added in a fixed order while they are loaded.
///
/// A file counts for what it says, not for its bytes: it is parsed and written out again in one fixed form, so
/// line endings, indentation and how a string is escaped make no difference, and a checkout on another system
/// or a reformatted file is the same content. Everything else does make a difference: a value, a name, an added
/// or removed entry, and the order of entries in an array or of properties in an object.
///
/// A number counts as it is written, so <c>1</c> and <c>1.0</c> are different content. The loaders read some
/// numbers as single precision, some as double and some as whole numbers, and two spellings that one of those
/// takes for the same value another can tell apart. Erring this way refuses a join that would have worked; the
/// other way would admit one that does not.
/// </summary>
internal sealed class ContentIdentityBuilder
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly ArrayBufferWriter<byte> _canonical = new(4096);

    /// <summary>Starts an identity. The evaluator's version is part of it from the first byte.</summary>
    public ContentIdentityBuilder(uint evaluatorVersion = ContentIdentity.EvaluatorVersion)
    {
        _hash.AppendData("PROSPECT content identity v1"u8);
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, evaluatorVersion);
        _hash.AppendData(version);
    }

    /// <summary>
    /// Adds the JSON file at <paramref name="path"/> under <paramref name="name"/>, which is how the game
    /// refers to it whatever directory it was found in. Returns the bytes it read, for the loader to parse:
    /// the file is read once, so the identity describes exactly what was loaded even if the file is saved
    /// again a moment later.
    /// </summary>
    public byte[] Add(string name, string path)
    {
        var json = File.ReadAllBytes(path);
        Add(name, json);
        return json;
    }

    /// <summary>Adds one file's JSON under <paramref name="name"/>.</summary>
    public void Add(string name, ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        _canonical.Clear();
        Canonical(document.RootElement, _canonical);

        // Each file goes in as its name and its content, both with their length in front. The lengths are what
        // keeps two different sets of files from running together into the same bytes.
        Span<byte> length = stackalloc byte[4];
        var nameBytes = Encoding.UTF8.GetBytes(name);
        BinaryPrimitives.WriteInt32LittleEndian(length, nameBytes.Length);
        _hash.AppendData(length);
        _hash.AppendData(nameBytes);
        BinaryPrimitives.WriteInt32LittleEndian(length, _canonical.WrittenCount);
        _hash.AppendData(length);
        _hash.AppendData(_canonical.WrittenSpan);
    }

    /// <summary>The identity of everything added so far.</summary>
    public ContentIdentity Build()
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        _hash.GetCurrentHash(digest);
        return ContentIdentity.FromDigest(digest);
    }

    /// <summary>
    /// Writes a JSON value in the one form the identity is taken over: a tag for its kind, then a count and the
    /// members for an object or an array, or a length and the bytes for a string or a number. It is not JSON and
    /// depends on no JSON writer, so no library's choice of what to escape can change an identity.
    /// </summary>
    private static void Canonical(JsonElement element, ArrayBufferWriter<byte> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Tag(into, (byte)'o', element.GetPropertyCount());
                foreach (var property in element.EnumerateObject())
                {
                    Text(into, property.Name);
                    Canonical(property.Value, into);
                }

                break;

            case JsonValueKind.Array:
                Tag(into, (byte)'a', element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                    Canonical(item, into);
                break;

            case JsonValueKind.String:
                into.GetSpan(1)[0] = (byte)'s';
                into.Advance(1);
                Text(into, element.GetString()!);
                break;

            case JsonValueKind.Number:
                into.GetSpan(1)[0] = (byte)'n';
                into.Advance(1);
                Text(into, element.GetRawText());
                break;

            default:
                into.GetSpan(1)[0] = element.ValueKind switch
                {
                    JsonValueKind.True => (byte)'t',
                    JsonValueKind.False => (byte)'f',
                    _ => (byte)'z',
                };
                into.Advance(1);
                break;
        }
    }

    private static void Tag(ArrayBufferWriter<byte> into, byte tag, int count)
    {
        var span = into.GetSpan(5);
        span[0] = tag;
        BinaryPrimitives.WriteInt32LittleEndian(span[1..], count);
        into.Advance(5);
    }

    private static void Text(ArrayBufferWriter<byte> into, string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        var span = into.GetSpan(4 + bytes);
        BinaryPrimitives.WriteInt32LittleEndian(span, bytes);
        Encoding.UTF8.GetBytes(text, span[4..]);
        into.Advance(4 + bytes);
    }
}
