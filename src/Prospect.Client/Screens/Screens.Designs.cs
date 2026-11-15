using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Designs;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;

namespace Prospect.Client.Screens;

/// <summary>
/// The wiring of the ship editor's Save design and Load design dialogs: the window around <see cref="DesignPanel"/>,
/// the keys of <see cref="DesignDialog"/> and the capture hook. The rules are the dialog's, the disk work the
/// shelf's (<see cref="App.Designs"/>).
/// </summary>
internal sealed partial class ScreenHost
{
    private readonly DesignDialog _designs = new();
    private readonly ImInputString _designName = new(DesignDialog.NameBytes);
    private string? _designView;

    /// <summary>True while a saved designs dialog is open over the editor: it takes the keyboard and the pointer.</summary>
    internal bool DesignsOpen => _designs.Open;

    /// <summary>
    /// The capture hook for the editor's dialogs, once the editor has opened: <c>edit-save</c> shows the Save design
    /// question with a name typed, <c>edit-load</c> the list of saved designs, and <c>edit-load-confirm</c> picks the
    /// first one that fits and shows the question before the draft is replaced.
    /// </summary>
    internal void PreviewDesigns(string view) => _designView = view;

    /// <summary>Opens Save design: the name field starts with the ship's own name.</summary>
    private void OpenSaveDesign(ShipEditor editor)
    {
        if (editor.Draft is not { } draft || !_designs.BeginSave(_app.Designs, editor))
            return;
        _designName.SetValue(draft.Name);
    }

    /// <summary>Follows the designs workers and the capture hook, once a frame while in the game.</summary>
    private void UpdateDesigns()
    {
        var editor = _app.World.Editor;
        _designs.Update(_app.Designs, editor, _notices);
        if (_designView is not { } view || !editor.CanEdit)
            return;
        switch (view)
        {
            case "edit-save":
                _designName.SetValue("Scout Mk II");
                _designs.BeginSave(_app.Designs, editor);
                _designView = null;
                break;
            case "edit-load":
                _designs.BeginLoad(_app.Designs, editor);
                _designView = null;
                break;
            default:
                // Wait for the list, then pick the first design that fits, as a click would.
                if (_designs.Stage == DesignStage.Closed)
                    _designs.BeginLoad(_app.Designs, editor);
                else if (_designs.Stage == DesignStage.Listing && _app.Designs.Listed)
                {
                    foreach (var entry in _app.Designs.Entries)
                        if (DesignDialog.Fits(entry, editor))
                        {
                            _designs.Pick(entry, _app.Designs, editor);
                            break;
                        }

                    if (_designs.Stage == DesignStage.Listing)
                        _designView = null;
                }
                else if (_designs.Stage == DesignStage.ConfirmReplace)
                    _designView = null;
                break;
        }
    }

    /// <summary>The dialog over the editor, dimmed behind, in a window of its own that takes the pointer and the keyboard.</summary>
    private void DrawDesigns()
    {
        if (!_designs.Open)
            return;
        var panel = DesignPanel.Panel(_ui.DisplaySize, Kit.Scale);
        DrawDim(0.55f);
        BeginRoot("##designs"u8);
        BeginPanel("##designspanel"u8, new float2(panel.Left, panel.Top), new float2(panel.Width, panel.Height));
        DesignPanel.DrawBody(_designs, _app.Designs, _designName, _app.World.Editor, _notices, _ui);
        EndPanel();
        EndRoot();
    }
}
