namespace Prospect.Client.Screens;

/// <summary>
/// Which tips belong to which surface of the game: the map's tips fire when the map opens, the command view's when
/// that view opens, the editor's when it opens, and each is taken back out of the stack when its surface closes, so a
/// tip about a screen that is gone never waits behind the next one and shows over it. No fonts and no drawing.
/// </summary>
internal sealed class TipSurfaces
{
    private bool _map;
    private bool _command;
    private bool _editor;
    private bool _scan;
    private bool _interface;

    /// <summary>
    /// Brings the stack in step with what is on screen this frame: a surface that just opened fires its trigger, one
    /// that just closed withdraws its tips. Called once a frame, before anything is laid out.
    /// </summary>
    public void Track(TipBoard tips, bool mapOpen, bool commandView, bool editorShown, bool scanAvailable, bool interfaceMode = false)
    {
        Edge(tips, mapOpen, ref _map, TipTriggers.MapOpened);
        Edge(tips, commandView, ref _command, TipTriggers.CommandViewOpened);
        Edge(tips, editorShown, ref _editor, TipTriggers.EditorOpened);
        Edge(tips, scanAvailable, ref _scan, TipTriggers.ScanAvailable);
        Edge(tips, interfaceMode, ref _interface, TipTriggers.InterfaceOpened);
    }

    /// <summary>Forgets what each surface last showed, so the next session fires them on their own changes.</summary>
    public void Reset() => _map = _command = _editor = _scan = _interface = false;

    private static void Edge(TipBoard tips, bool now, ref bool before, string trigger)
    {
        if (now && !before)
            tips.Fire(trigger);
        else if (!now && before)
            tips.Withdraw(trigger);
        before = now;
    }
}
