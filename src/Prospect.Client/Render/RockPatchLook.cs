using System.Text.Json;
using Prospect.Engine.IO;

namespace Prospect.Client.Render;

/// <summary>
/// Client-only figures for drawing scraped rocks, from <c>content/visuals/rock_edits.json</c>: when a rock's patch is
/// drawn instead of its plain facets, and the fixed memory patches are kept in.
/// </summary>
internal sealed class RockPatchLook
{
    /// <summary>A rock's patch is drawn while one pixel at its surface covers no more than this many metres; beyond it the plain facets are.</summary>
    public float DrawMetresPerPixel { get; set; } = 0.5f;

    /// <summary>A patch already drawn is kept out to this many times <see cref="DrawMetresPerPixel"/>, so it does not flicker at the limit.</summary>
    public float KeepFactor { get; set; } = 1.25f;

    /// <summary>Rocks with a patch in memory at once.</summary>
    public int MaxPatches { get; set; } = 32;

    /// <summary>Vertices of all patches together: the size of the one vertex buffer they share, 24 bytes a vertex.</summary>
    public int ArenaVertices { get; set; } = 2_000_000;

    /// <summary>Vertices of one patch at most; a rock that needs more is drawn as plain facets.</summary>
    public int MaxPatchVertices { get; set; } = 600_000;

    /// <summary>Unreached facets between two reached ones that the patch draws too, to split the base mesh into fewer runs.</summary>
    public int MergeGap { get; set; } = 12;

    /// <summary>Frames a patch may go undrawn before its memory is given back.</summary>
    public int IdleFrames { get; set; } = 600;

    /// <summary>Reads and validates the file.</summary>
    public static RockPatchLook Load()
    {
        var value = JsonSerializer.Deserialize<RockPatchLook>(File.ReadAllText(AppPaths.Content("visuals", "rock_edits.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (!(value.DrawMetresPerPixel > 0 && value.DrawMetresPerPixel <= 100 && value.KeepFactor >= 1 && value.KeepFactor <= 4
            && value.MaxPatches is >= 1 and <= SceneRenderer.RockPatchDraws && value.ArenaVertices is >= 30_000 and <= 16_000_000
            && value.MaxPatchVertices >= 3000 && value.MaxPatchVertices <= value.ArenaVertices && value.MergeGap is >= 0 and <= 4096
            && value.IdleFrames is >= 2 and <= 1_000_000))
            throw new InvalidDataException("Invalid rock patch visual tuning.");
        return value;
    }
}
