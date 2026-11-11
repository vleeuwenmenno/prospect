using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Prospect.Listing.Accounts;

internal static class AccountCrypto
{
    internal static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string Random() => Encode(RandomNumberGenerator.GetBytes(32));
    internal static byte[] Hash(string value) => SHA256.HashData(Encoding.ASCII.GetBytes(value));
    internal static string Challenge(string verifier) => Encode(Hash(verifier));
    internal static bool Canonical(string? value)
    {
        if (value is not { Length: 43 } || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return false;
        try { return Encode(Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=")) == value; }
        catch (FormatException) { return false; }
    }
    internal static bool Equal(byte[] left, byte[] right) => CryptographicOperations.FixedTimeEquals(left, right);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        MaxDepth = 8,
    };
    internal static string Name(string? raw, string id)
    {
        var clean = Announcement.SanitizeName(raw ?? "");
        var result = new StringBuilder();
        foreach (var rune in clean.EnumerateRunes())
        {
            if (result.Length + rune.Utf16SequenceLength > 24) break;
            result.Append(rune.ToString());
        }
        return result.Length == 0 ? "Pilot-" + id[..4].ToUpperInvariant() : result.ToString().Trim();
    }
}

internal sealed class AccountFailure(int status) : Exception
{
    internal int Status { get; } = status;
}
internal sealed record Account(string Id, string Name, string FriendCode);
internal sealed record Friend(string Id, string Name, DateTimeOffset Since);
