using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>Shared bounds for block recovery; storage is reserved before packets are processed.</summary>
public sealed record BlockTransferRules(int MaxBytes, int Transfers, int HeldBytes, int RequestTicks, int TimeoutTicks, int BytesPerTick)
{
    /// <summary>Whole encoded catalogue budget, including repeated entries.</summary>
    public int CatalogueBytes { get; init; } = 196608;
    /// <summary>Conservative decoded block budget; each canonical block needs at least 27 bytes.</summary>
    public int CatalogueBoxes { get; init; } = 8192;
    /// <summary>Fixed admission deadline including request spacing, headers, and bounded retry slack.</summary>
    public long AdmissionTicks => TimeoutTicks + 256L * (RequestTicks + 1) + 4L * (CatalogueBytes + 9500) / Math.Min(BytesPerTick - 24, 1000);
    /// <summary>Per-transfer deadline includes all slots competing for the outgoing budget.</summary>
    public long TransferTicks => TimeoutTicks + 4L * Transfers * MaxBytes / Math.Min(BytesPerTick - 24, 1000);
    /// <summary>Checks aggregate allocations before a catalogue is decoded.</summary>
    public bool AllowsCatalogue(long bytes) => bytes <= CatalogueBytes && bytes / 27 <= CatalogueBoxes;

    /// <summary>Loads and validates the bounded bulk lane configuration.</summary>
    public static BlockTransferRules Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var r = doc.RootElement;
        var result = new BlockTransferRules(r.GetProperty("maxBytes"u8).GetInt32(), r.GetProperty("transfers"u8).GetInt32(),
            r.GetProperty("heldBytes"u8).GetInt32(), r.GetProperty("requestTicks"u8).GetInt32(),
            r.GetProperty("timeoutTicks"u8).GetInt32(), r.GetProperty("bytesPerTick"u8).GetInt32())
        {
            CatalogueBytes = r.GetProperty("catalogueBytes"u8).GetInt32(),
            CatalogueBoxes = r.GetProperty("catalogueBoxes"u8).GetInt32()
        };
        if (result.MaxBytes is < 168 or > 8388608 || result.MaxBytes % 8 != 0 || result.Transfers is < 1 or > 8
            || result.HeldBytes < (long)result.MaxBytes * result.Transfers || result.HeldBytes > 67108864
            || result.RequestTicks is < 1 or > 3600 || result.TimeoutTicks < result.RequestTicks || result.TimeoutTicks > 36000
            || result.BytesPerTick is < 64 or > 8192
            || result.CatalogueBytes is < 1024 or > 1048576 || result.CatalogueBoxes is < 1 or > 16384)
            throw new InvalidDataException("Invalid block transfer limits.");
        return result;
    }
}
