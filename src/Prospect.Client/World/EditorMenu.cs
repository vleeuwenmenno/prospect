using Brutal.Numerics;

namespace Prospect.Client.World;

/// <summary>Which flyout the editor's context menu shows beside its rows.</summary>
internal enum EditorMenuSub : byte
{
    /// <summary>No flyout.</summary>
    None,

    /// <summary>The fuels a selected tank can take.</summary>
    Fuel,

    /// <summary>The axes a selected tank can be cut along.</summary>
    Split,
}

/// <summary>
/// The context menu the ship editor opens on a right click on a placed block or a mounted part: where it was opened
/// on the screen, what was clicked and which flyout is out. It holds no choices of its own: every row's state comes
/// from <see cref="ShipEditor"/> each frame and every row's action goes through it, so what the menu shows and what
/// a row does cannot drift apart. The window is drawn by the screens.
/// </summary>
internal sealed class EditorMenu
{
    /// <summary>True while the menu is open.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Where the pointer was when the menu was opened, in screen pixels.</summary>
    public float2 At { get; private set; }

    /// <summary>The block clicked, or -1 when the click was on a mounted part.</summary>
    public int Block { get; private set; } = -1;

    /// <summary>The mounted part clicked, or -1 when the click was on a block.</summary>
    public int Mount { get; private set; } = -1;

    /// <summary>The flyout that is out.</summary>
    public EditorMenuSub Sub { get; set; }

    /// <summary>Seconds the editor had run, at the frame the menu opened; the screens ease it in from this.</summary>
    public float OpenedAt { get; private set; }

    /// <summary>Opens the menu at <paramref name="at"/> for a block or a mounted part, replacing any open one.</summary>
    public void Open(float2 at, int block, int mount, float now)
    {
        IsOpen = true;
        At = at;
        Block = block;
        Mount = mount;
        Sub = EditorMenuSub.None;
        OpenedAt = now;
    }

    /// <summary>Closes the menu. True when it was open.</summary>
    public bool Close()
    {
        var was = IsOpen;
        IsOpen = false;
        Sub = EditorMenuSub.None;
        return was;
    }
}
