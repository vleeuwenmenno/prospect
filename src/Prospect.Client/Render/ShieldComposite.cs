using Prospect.Client.World;
using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>
/// How the shields are put into the scene and how the pilot's own shield rides through their own hyperspace
/// tunnel. Every shell is drawn in two passes, light then depth and lens mask; in both, the other hulls' shields go
/// first and the pilot's own last, whatever order they were added in. The mask then means the nearest shield seen
/// there: 0.375 where it is another hull's, 0.4375 where it is the pilot's own, and 0.5 kept wherever the pilot's
/// own hull is, since the mask only ever rises. On a frame where the tunnel covers the view at all, the scene is
/// copied once more just before the own shield, so what that shield added is the scene less that copy, with no
/// other shield's light or bend in it. The tone mapper tunnels the copy and adds the own shield's light back on
/// top; the shield fades its bend of the old world as the tunnel covers it.
/// </summary>
internal static class ShieldComposite
{
    /// <summary>The lens mask of the nearest shield where it is the pilot's own, between a hull's and the own hull's.</summary>
    public const float OwnShieldMask = 0.4375f;

    /// <summary>
    /// Writes the order the shells are drawn in, in both passes: the indices of the other hulls' shells, then the
    /// pilot's own. Returns how many come before the own ones. Draw <c>i</c> keeps instance <c>i</c>.
    /// </summary>
    public static int Order(ReadOnlySpan<ShieldDraw> draws, Span<int> order)
    {
        var count = 0;
        for (var i = 0; i < draws.Length; i++)
        {
            if (!draws[i].Own)
                order[count++] = i;
        }

        var others = count;
        for (var i = 0; i < draws.Length; i++)
        {
            if (draws[i].Own)
                order[count++] = i;
        }

        return others;
    }

    /// <summary>
    /// How much of the view the pilot's own hyperspace tunnel covers, exactly as the tone mapper reads it from the
    /// frame constants: the coverage while the own jump is hyperspace, otherwise none.
    /// </summary>
    public static float TunnelCover(in FrameConstants frame) =>
        (int)(frame.Ftl.X + 0.5f) == (int)FtlKind.Hyperspace ? frame.FtlUp.W : 0f;

    /// <summary>Whether this frame needs the copy before the own shield: an own shield is drawn and the tunnel covers.</summary>
    public static bool NeedsOwnCopy(in FrameConstants frame, bool ownDrawn) => ownDrawn && TunnelCover(frame) > 0f;
}
