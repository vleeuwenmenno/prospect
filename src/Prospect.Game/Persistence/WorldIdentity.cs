using System.Runtime.InteropServices;
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
public sealed partial record WorldIdentity(Guid WorldId, string PublicKey, string Fingerprint, WorldKeyStatus KeyStatus)
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
            if (!OwnedFile(file))
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or FormatException or EntryPointNotFoundException)
        {
            return WorldKeyStatus.Unreadable;
        }
    }

    internal static bool SafeDirectory(string folder)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try { return SafePath(Path.GetFullPath(folder), true, 0); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EntryPointNotFoundException)
        { return false; }
    }

    private static bool SafePath(string path, bool leaf, int links)
    {
        if (links > 40) return false;
        var directory = new DirectoryInfo(path);
        if (directory.Parent is { } parent && !SafePath(parent.FullName, false, links)) return false;
        // AT_SYMLINK_NOFOLLOW checks the link owner too: a sticky parent alone cannot protect another user's link.
        if (Statx(-100, path, 0x100, 0x0b, out var stat) != 0 || (stat.Mask & 0x0b) != 0x0b
            || (stat.Uid != GetEuid() && stat.Uid != 0)) return false;
        // Validate both the link's containing path and the target's containing path.
        if (directory.ResolveLinkTarget(false) is { } target)
            return SafePath(target.FullName, leaf, links + 1);
        if ((stat.Mode & 0xf000) != 0x4000) return false;
        var mode = (UnixFileMode)(stat.Mode & 0xfff);
        return (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0
            || (!leaf && (mode & UnixFileMode.StickyBit) != 0);
    }

    private static bool OwnedFile(FileStream file)
    {
        // AT_EMPTY_PATH checks the opened inode, not a path another writer could replace.
        return Statx(file.SafeFileHandle.DangerousGetHandle().ToInt32(), "", 0x1000, 0x0f, out var stat) == 0
            && (stat.Mask & 0x0f) == 0x0f && stat.Uid == GetEuid() && stat.Links == 1
            && stat.Mode == (0x8000 | (int)SecretMode);
    }

    // Linux statx has a fixed ABI on all architectures; unused fields still need their reserved space.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileStat
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint Links;
        [FieldOffset(20)] internal uint Uid;
        [FieldOffset(28)] internal ushort Mode;
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out FileStat stat);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEuid();

    internal static byte[] CreateKey(string folder, string staging, Action acquired)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // Without proven protection retain only the public identity. Private play needs no signing key.
        if (!OperatingSystem.IsLinux() || !SafeDirectory(folder)) return key.ExportSubjectPublicKeyInfo();
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
                    if (!OwnedFile(file)) return key.ExportSubjectPublicKeyInfo();
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
