using Brutal.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// The context menu the command view opens on a right click: where it was opened on the screen and the point in
/// the world it is about. It holds no choices of its own. What its rows offer comes from
/// <see cref="WorldView.PlanJump"/> each frame, and a row's action goes through <c>WorldView</c>, so what the
/// menu shows and what a row does cannot drift apart. The window is drawn by the screens.
/// </summary>
internal sealed class CourseMenu
{
    /// <summary>True while the menu is open.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Where the pointer was when the menu was opened, in screen pixels.</summary>
    public float2 At { get; private set; }

    /// <summary>The point in the world the rows are about.</summary>
    public Double3 Target { get; private set; }

    /// <summary>Seconds the view has run, at the frame the menu opened; the screens ease it in from this.</summary>
    public float OpenedAt { get; private set; }

    /// <summary>Opens the menu for <paramref name="target"/>, replacing any open one.</summary>
    public void Open(float2 at, Double3 target, float now)
    {
        IsOpen = true;
        At = at;
        Target = target;
        OpenedAt = now;
    }

    /// <summary>Closes the menu. True when it was open.</summary>
    public bool Close()
    {
        var was = IsOpen;
        IsOpen = false;
        return was;
    }
}
