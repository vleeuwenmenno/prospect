using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>Debug: what the Fill tank button does now. To be removed with the button.</summary>
internal enum FillOffer : byte
{
    /// <summary>Nothing to show: the server does not allow it, no edit is open, or no tank is selected.</summary>
    None,

    /// <summary>The selected tanks are in the hull as it is; the button works.</summary>
    Ready,

    /// <summary>A selected tank is only in the draft: Apply first.</summary>
    ApplyFirst,

    /// <summary>More tanks are selected than one request carries.</summary>
    TooMany,

    /// <summary>A request is still out.</summary>
    Waiting,
}

/// <summary>
/// Debug: maps the tanks selected in the draft to the tanks of the edited hull's own design, for the Fill tank
/// button. Font-free so a test can drive it. To be removed with the button.
/// </summary>
internal static class EditorFill
{
    /// <summary>
    /// Matches each selected tank block of <paramref name="draft"/> to the tank of <paramref name="source"/> that is the
    /// same block of the same type and shape in the same cells, the way the server carries fuel into a rebuilt hull
    /// (which way a tank faces does not matter). The matched block indices of the hull's design go into
    /// <paramref name="blocks"/>. A selected tank with no match exists only in the draft: <see cref="FillOffer.ApplyFirst"/>.
    /// Blocks that are no tank are ignored; no tank selected is <see cref="FillOffer.None"/>.
    /// </summary>
    public static FillOffer Plan(BuildDraft draft, HullDesign source, ReadOnlySpan<int> selection, Span<int> blocks, out int count)
    {
        count = 0;
        var tanks = 0;
        var unmatched = false;
        var own = source.Tanks;
        foreach (var block in selection)
        {
            if ((uint)block >= (uint)draft.Boxes.Length || draft.Boxes[block].Type is not { } type || type.Tank == FuelKind.None)
                continue;
            tanks++;
            ref readonly var box = ref draft.Boxes[block];
            var found = false;
            for (var j = 0; j < own.Length && !found; j++)
            {
                ref readonly var old = ref source.Boxes[own[j].Block];
                if (old.At != box.At || old.Size != box.Size || old.Shape != box.Shape || !ReferenceEquals(old.Type, box.Type))
                    continue;
                found = true;
                if (count < blocks.Length)
                    blocks[count++] = own[j].Block;
            }

            unmatched |= !found;
        }

        if (tanks == 0)
            return FillOffer.None;
        if (tanks > EditorRequestMessage.MaxFill)
            return FillOffer.TooMany;
        return unmatched ? FillOffer.ApplyFirst : FillOffer.Ready;
    }
}
