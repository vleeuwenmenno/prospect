using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Prospect.Game.Protocol;

/// <summary>
/// How a client proves it knows a server's password without sending it.
///
/// <list type="number">
/// <item>The server makes a random <b>salt</b> of 16 bytes when it starts, and derives its <b>key</b> once:
/// PBKDF2-HMAC-SHA256 of the password's UTF-8 bytes with that salt, 200,000 rounds, 32 bytes out.</item>
/// <item>It answers every hello it does not refuse outright with the salt and a <b>challenge</b>: 32 random
/// bytes made for that one connection. A hello of another version or with other content
/// (<see cref="World.ContentIdentity"/>) is refused without one.</item>
/// <item>The client derives the same key from the password it was given and the server's salt, and sends the
/// <b>proof</b>: HMAC-SHA256, keyed with the key, of the text <c>PROSPECT password proof v1</c>, the protocol
/// version (four bytes, little-endian), the salt and the challenge, in that order.</item>
/// <item>The server computes the same and compares in constant time.</item>
/// </list>
///
/// The password never crosses the network. A proof answers one challenge of one server run: the challenge is
/// new for every connection, and the salt is new for every server, so a proof that was overheard opens nothing
/// else. This protects the password and nothing more: game packets are neither encrypted nor signed, someone on
/// the path can read and alter them, and someone who relays a live handshake gets in with it. A weak password
/// can be guessed offline by anyone who overhears one handshake; the rounds only make each guess slower.
///
/// The proof covers the version, the salt and the challenge, and nothing else of the hello: not the name and not
/// the content identity. Neither is secret, and the server has compared the identity before it challenges.
/// </summary>
public static class Passwords
{
    /// <summary>The longest password, in UTF-16 units.</summary>
    public const int MaxChars = 64;

    public const int SaltSize = 16;
    public const int ChallengeSize = 32;
    public const int KeySize = 32;
    public const int ProofSize = 32;

    /// <summary>
    /// Rounds of PBKDF2. Part of the protocol: both ends must use the same number, and a server cannot make a
    /// client work harder by asking for more.
    /// </summary>
    public const int Rounds = 200_000;

    private const int MaxPasswordBytes = MaxChars * 3;

    private static ReadOnlySpan<byte> Label => "PROSPECT password proof v1"u8;

    /// <summary>True for a password a server can have: 1 to <see cref="MaxChars"/> characters, none of them a control character.</summary>
    public static bool IsUsable(ReadOnlySpan<char> password)
    {
        if (password.IsEmpty || password.Length > MaxChars)
            return false;
        foreach (var c in password)
        {
            if (char.IsControl(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Derives the key from a password and a server's salt into <paramref name="key"/>
    /// (<see cref="KeySize"/> bytes). Takes tens of milliseconds by design; never call it per connection attempt
    /// on a server. The password must be <see cref="IsUsable"/>.
    /// </summary>
    public static void DeriveKey(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt, Span<byte> key)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(password.Length, MaxChars);
        Span<byte> utf8 = stackalloc byte[MaxPasswordBytes];
        var length = Encoding.UTF8.GetBytes(password, utf8);
        Rfc2898DeriveBytes.Pbkdf2(utf8[..length], salt[..SaltSize], key[..KeySize], Rounds, HashAlgorithmName.SHA256);
        CryptographicOperations.ZeroMemory(utf8);
    }

    /// <summary>Computes the proof for one challenge into <paramref name="proof"/> (<see cref="ProofSize"/> bytes).</summary>
    public static void ComputeProof(ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> challenge, Span<byte> proof) =>
        ComputeProof(key, salt, challenge, proof, ProtocolInfo.Version);

    /// <summary>
    /// The proof as a build of protocol <paramref name="version"/> computes it. The game only ever asks for its
    /// own version; this is here so a test can hold the scheme to bytes worked out elsewhere.
    /// </summary>
    internal static void ComputeProof(ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> challenge, Span<byte> proof, uint version)
    {
        Span<byte> message = stackalloc byte[Label.Length + 4 + SaltSize + ChallengeSize];
        Label.CopyTo(message);
        BinaryPrimitives.WriteUInt32LittleEndian(message[Label.Length..], version);
        salt[..SaltSize].CopyTo(message[(Label.Length + 4)..]);
        challenge[..ChallengeSize].CopyTo(message[(Label.Length + 4 + SaltSize)..]);
        HMACSHA256.HashData(key[..KeySize], message, proof[..ProofSize]);
    }

    /// <summary>
    /// True when <paramref name="proof"/> is the proof for this key, salt and challenge. The comparison takes
    /// the same time whatever the proof is.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof)
    {
        Span<byte> expected = stackalloc byte[ProofSize];
        ComputeProof(key, salt, challenge, expected);
        return CryptographicOperations.FixedTimeEquals(expected, proof);
    }
}
