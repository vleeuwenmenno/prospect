using Brutal.Numerics;
using Prospect.Client.Settings;

namespace Prospect.Client.Screens;

/// <summary>
/// Keeps where the interface windows are, which are open and which are pinned in <see cref="ClientSettings.Windows"/>,
/// so they come back as the player left them. Positions are saved in unscaled pixels. A change is written once the
/// player lets go of it (a drag is not saved at every step), and never allocates while nothing changes. The write is
/// <paramref name="save"/>, so tests round-trip it through a settings file of their own. Font-free.
/// </summary>
internal sealed class WindowLayout(ClientSettings settings, SocialPanes panes, Action save)
{
    private readonly int[] _last = new int[SocialPanes.Count];
    private bool _restored;

    /// <summary>
    /// Applies the saved state to the windows: open, pinned and placed. The navigator and the card have no open
    /// state to keep (the interface mode and the selection decide them), only a place. Call once, when the scale is known.
    /// </summary>
    public void Restore(float scale)
    {
        _restored = true;
        for (var i = 0; i < SocialPanes.Count; i++)
        {
            var pane = (SocialPane)i;
            var window = panes[pane];
            if (!settings.Windows.TryGetValue(SocialPanes.NameOf(pane), out var state))
            {
                _last[i] = Signature(pane, window);
                continue;
            }

            if (pane is not (SocialPane.Navigator or SocialPane.Detail))
            {
                window.Open = state.Open;
                window.Pinned = state.Pinned && window.Open;
            }

            if (state.Placed)
            {
                window.Min = new float2(MathF.Round(state.X * scale), MathF.Round(state.Y * scale));
                window.Placed = true;
            }

            _last[i] = Signature(pane, window);
        }
    }

    /// <summary>
    /// Notes a change in what is open, pinned or placed and, when the player is not holding a drag, writes it to the
    /// settings. Cheap when nothing changed. Returns true on the frame it saved.
    /// </summary>
    public bool Track(float scale, bool dragging)
    {
        if (!_restored || dragging)
            return false;
        var changed = false;
        for (var i = 0; i < SocialPanes.Count; i++)
        {
            var signature = Signature((SocialPane)i, panes[(SocialPane)i]);
            if (signature == _last[i])
                continue;
            _last[i] = signature;
            changed = true;
        }

        if (!changed)
            return false;
        Capture(scale);
        save();
        return true;
    }

    /// <summary>Copies the windows' state into the settings, unsaved.</summary>
    public void Capture(float scale)
    {
        for (var i = 0; i < SocialPanes.Count; i++)
        {
            var pane = (SocialPane)i;
            var window = panes[pane];
            var name = SocialPanes.NameOf(pane);
            if (!settings.Windows.TryGetValue(name, out var state))
            {
                if (settings.Windows.Count >= ClientSettings.MaxWindows)
                    continue;
                state = new WindowState();
                settings.Windows[name] = state;
            }

            state.Open = window.Open;
            state.Pinned = window.Pinned;
            state.Placed = window.Placed;
            if (window.Placed && scale > 0f)
            {
                state.X = window.Min.X / scale;
                state.Y = window.Min.Y / scale;
            }
        }
    }

    // The navigator and the card are not saved open or closed, so opening one is no change worth a write.
    private static int Signature(SocialPane pane, FloatingWindow window) =>
        HashCode.Combine(pane is SocialPane.Navigator or SocialPane.Detail || window.Open, window.Pinned, window.Placed, (int)MathF.Round(window.Min.X), (int)MathF.Round(window.Min.Y));
}
