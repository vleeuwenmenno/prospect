using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The wiring of the ship editor's "Discard changes?" question: every way out of an edit that would lose the draft
/// asks it first (<see cref="ShipEditor.Leave"/>), and the in-game menu's Leave and Quit do too while an edit is open.
/// The rule and its keys are the editor's (<see cref="ShipEditor.WouldLoseChanges"/>, <see cref="ShipEditor.HandleKeys"/>);
/// this is the window and what happens after Discard.
/// </summary>
internal sealed partial class ScreenHost
{
    private DiscardThen _discardThen;
    private bool _discardWasOpen;

    /// <summary>True while the question is up over the editor: it takes the keyboard and the pointer.</summary>
    internal bool DiscardOpen => _app.World.Editor.ConfirmDiscard;

    /// <summary>
    /// Asks before the menu's Leave or Quit lets go of an open edit that has changes. True when the question is up
    /// (the menu closes and nothing else happens yet), false when there is nothing to lose and the caller goes on.
    /// </summary>
    private bool AskDiscardBefore(DiscardThen then)
    {
        if (!_app.World.Editor.AskDiscard())
            return false;
        _discardThen = then;
        _sessionMenuOpen = false;
        return true;
    }

    /// <summary>
    /// A close request from the window itself, its close button or Alt+F4, reads like the menu's Quit: with an open
    /// edit that has changes it puts the "Discard changes?" question up and the window stays open, so only Discard
    /// ends the game. True when the question is now up; false when there is nothing to lose and the window closes
    /// as it always did. Font-free, so the frame order tests drive the rule with the real editor.
    /// </summary>
    internal static bool CloseAsksFirst(ShipEditor editor) => editor.AskDiscard();

    /// <summary>
    /// Remembers that the question a close request put up is answered by quitting, as the menu's Quit is, and that
    /// the in-game menu it came over is no longer the surface on show.
    /// </summary>
    internal void ExpectQuitOnDiscard()
    {
        _discardThen = DiscardThen.Quit;
        _sessionMenuOpen = false;
    }

    /// <summary>Throws the draft away and does what the question stood in front of.</summary>
    private void Discard()
    {
        var then = _discardThen;
        _discardThen = DiscardThen.Cancel;
        if (_app.Session?.Client.Input is { } bridge)
            _app.World.Editor.Cancel(bridge);
        switch (then)
        {
            case DiscardThen.Leave:
                _app.LeaveSession();
                break;
            case DiscardThen.Quit:
                _app.Quit();
                break;
        }
    }

    /// <summary>Forgets what the question was for once it is gone, however it went: kept, answered or ended under it.</summary>
    private void UpdateDiscard()
    {
        if (_app.World.Editor.ConfirmDiscard)
            return;
        _discardThen = DiscardThen.Cancel;
        _discardWasOpen = false;
    }

    /// <summary>The question over the editor, dimmed behind, in a window of its own that takes the pointer and the keyboard.</summary>
    private void DrawDiscard()
    {
        if (!_app.World.Editor.ConfirmDiscard)
            return;
        var panel = DiscardPanel.Panel(_ui.DisplaySize, Kit.Scale);
        // A text field of the part list may still hold the caret when the question comes up; it lets go.
        if (!_discardWasOpen)
        {
            ImGui.Internal.ClearActiveID();
            _discardWasOpen = true;
        }

        DrawDim(0.55f);
        BeginRoot("##discard"u8);
        BeginPanel("##discardpanel"u8, new float2(panel.Left, panel.Top), new float2(panel.Width, panel.Height));
        var answer = DiscardPanel.DrawBody(_ui);
        EndPanel();
        EndRoot();
        switch (answer)
        {
            case DiscardAnswer.Keep:
                _app.World.Editor.KeepEditing();
                break;
            case DiscardAnswer.Discard:
                Discard();
                break;
        }
    }
}
