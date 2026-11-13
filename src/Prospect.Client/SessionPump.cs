using Brutal.Numerics;
using Prospect.Engine.Diagnostics;

namespace Prospect.Client;

/// <summary>What the main loop does with a frame.</summary>
internal enum FrameStep : byte
{
    /// <summary>Draw it.</summary>
    Draw,

    /// <summary>There is nothing to draw into (the window is minimised): skip drawing, and come back.</summary>
    Skip,

    /// <summary>Quitting is done: leave the loop.</summary>
    Exit,
}

/// <summary>
/// The start of every frame of the main loop, before anything is drawn: a quit that was asked for, the end of the
/// loop once quitting is done, polling, and moving the play session on. The session moves on whether or not the
/// frame can be drawn, so a minimised window still saves, closes, opens and quits. Main thread only; takes no
/// memory in a steady frame.
/// </summary>
internal sealed class SessionPump(SessionLifecycle lifecycle, WorldShelf worlds, bool scripted, Action entered)
{
    private bool _busy;
    private int _lingering;

    /// <summary>
    /// Begins a frame. A quit that <paramref name="quitAsked"/> carries is handed to the lifecycle (and cleared);
    /// once quitting is done this returns <see cref="FrameStep.Exit"/> before polling. Otherwise
    /// <paramref name="poll"/> polls input and returns the framebuffer's size, the session is moved on, and the
    /// frame is drawn unless that size is zero.
    /// </summary>
    public FrameStep Next(ref bool quitAsked, Func<int2> poll)
    {
        if (quitAsked)
        {
            quitAsked = false;
            lifecycle.Quit();
        }

        if (lifecycle.QuitReady)
            return FrameStep.Exit;

        var framebuffer = poll();
        Advance();
        return framebuffer.X == 0 || framebuffer.Y == 0 ? FrameStep.Skip : FrameStep.Draw;
    }

    /// <summary>
    /// Moves the play session on: a fault on a session thread starts its close, a closed session goes, an opened
    /// one is shown, and the list of worlds is read again once nothing holds one.
    /// </summary>
    public void Advance()
    {
        if (lifecycle.Session?.Fault is { } fault)
        {
            Logs.App.Error($"The session stopped unexpectedly: {fault}");
            lifecycle.Fail("The game stopped unexpectedly.");
        }

        if (lifecycle.Update())
            entered();
        var busy = lifecycle.Busy;
        var lingering = lifecycle.LingeringCount;
        if (((_busy && !busy) || lingering < _lingering) && lifecycle.Session is null && !busy && !lifecycle.Quitting)
            worlds.Refresh();
        _busy = busy;
        _lingering = lingering;
        worlds.Update();

        // A scripted run reads no report that holds back its own end: it is logged, and the run ends. A report of
        // a leave the run asked for (--open leave) stays on screen to be captured.
        if (scripted && lifecycle.Report is { WasQuitting: true })
            lifecycle.Acknowledge(quit: true);
    }
}
