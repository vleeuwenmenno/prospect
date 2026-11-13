using System.Security.Cryptography;
using System.Text;

namespace Prospect.Game.Persistence;

/// <summary>The separate private key's condition; none except Present can support hosting.</summary>
public enum WorldKeyStatus
{
    /// <summary>A protected matching private key was read.</summary>
    Present,
    /// <summary>No key exists. It is never regenerated on open.</summary>
    Missing,
    /// <summary>A valid P256 key belongs to another identity.</summary>
    Mismatch,
    /// <summary>The key cannot be read or is malformed.</summary>
    Unreadable,
    /// <summary>Protection is unsafe or unsupported on this platform.</summary>
    Unsafe,
}

/// <summary>Public immutable disk identity and the key condition at open, not a hosting authorization.</summary>
public sealed record WorldIdentity(Guid WorldId, string PublicKey, string Fingerprint, WorldKeyStatus KeyStatus)
{
    /// <summary>The only private key file. Database recovery copies do not contain it.</summary>
    public const string KeyFile = "world.key";

    internal const int MaxKeyBytes = 4096;
    internal const UnixFileMode SecretMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static ReadOnlySpan<byte> Prefix => [0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04];

    internal static bool ValidPublicKey(byte[] bytes)
    {
        if (bytes.Length != 91 || !bytes.AsSpan().StartsWith(Prefix))
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var consumed);
            return consumed == bytes.Length && key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(bytes);
        }
        catch (CryptographicException) { return false; }
    }

    internal static WorldIdentity Read(string folder, Guid worldId, byte[] publicKey)
    {
        var status = Status(folder, publicKey);
        return new(worldId, Convert.ToBase64String(publicKey), Convert.ToHexStringLower(SHA256.HashData(publicKey)), status);
    }

    private static WorldKeyStatus Status(string folder, byte[] expected)
    {
        if (!OperatingSystem.IsLinux())
            return WorldKeyStatus.Unsafe;
        var path = Path.Combine(folder, KeyFile);
        try
        {
            if (!SafeDirectory(folder) || new FileInfo(path).LinkTarget is not null)
                return WorldKeyStatus.Unsafe;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (File.GetUnixFileMode(file.SafeFileHandle) != SecretMode)
                return WorldKeyStatus.Unsafe;
            if (file.Length is < 1 or > MaxKeyBytes)
                return WorldKeyStatus.Unreadable;
            var bytes = new byte[(int)file.Length];
            try
            {
                file.ReadExactly(bytes);
                var chars = Encoding.ASCII.GetChars(bytes);
                try
                {
                    var text = chars.AsSpan().Trim();
                    if (!PemEncoding.TryFind(text, out var fields) || fields.Location.GetOffsetAndLength(text.Length) != (0, text.Length)
                        || !text[fields.Label].SequenceEqual("PRIVATE KEY"))
                        return WorldKeyStatus.Unreadable;
                    using var key = ECDsa.Create();
                    var der = new byte[fields.DecodedDataLength];
                    try
                    {
                        if (!Convert.TryFromBase64Chars(text[fields.Base64Data], der, out var written) || written != der.Length)
                            return WorldKeyStatus.Unreadable;
                        key.ImportPkcs8PrivateKey(der, out var consumed);
                        if (consumed != der.Length)
                            return WorldKeyStatus.Unreadable;
                    }
                    finally { CryptographicOperations.ZeroMemory(der); }
                    var actual = key.ExportSubjectPublicKeyInfo();
                    if (!ValidPublicKey(actual))
                        return WorldKeyStatus.Unreadable;
                    // A platform sign/verify also checks that the private scalar matches the imported point.
                    var signature = key.SignData(expected, HashAlgorithmName.SHA256);
                    if (!key.VerifyData(expected, signature, HashAlgorithmName.SHA256))
                        return WorldKeyStatus.Unreadable;
                    return actual.AsSpan().SequenceEqual(expected) ? WorldKeyStatus.Present : WorldKeyStatus.Mismatch;
                }
                finally { Array.Clear(chars); }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (FileNotFoundException) { return WorldKeyStatus.Missing; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or FormatException)
        {
            return WorldKeyStatus.Unreadable;
        }
    }

    internal static bool SafeDirectory(string folder)
    {
        if (!OperatingSystem.IsLinux())
            return false;
        // Ancestors must not allow replacement by another user. Sticky shared roots (such as /tmp) are allowed.
        for (var at = new DirectoryInfo(folder); at is not null; at = at.Parent)
        {
            if (at.LinkTarget is not null)
                return false;
            var mode = File.GetUnixFileMode(at.FullName);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 && (at.FullName == Path.GetFullPath(folder) || (mode & UnixFileMode.StickyBit) == 0))
                return false;
        }
        return true;
    }

    internal static byte[] CreateKey(string folder, string staging, Action acquired)
    {
        if (!OperatingSystem.IsLinux() || !SafeDirectory(folder))
            throw new IOException("World identity protection is unsupported or the folder permissions are unsafe.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var secret = key.ExportPkcs8PrivateKey();
        try
        {
            var pem = PemEncoding.Write("PRIVATE KEY", secret);
            try
            {
                var bytes = Encoding.ASCII.GetBytes(pem);
                try
                {
                    using var file = new FileStream(staging, new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        UnixCreateMode = SecretMode,
                    });
                    acquired();
                    file.Write(bytes);
                    file.Flush(flushToDisk: true);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { Array.Clear(pem); }
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
        return key.ExportSubjectPublicKeyInfo();
    }
}
