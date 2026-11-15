using System.Text;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Prospect.Client.Designs;
using Prospect.Client.Input;
using Prospect.Client.World;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>Where the saved designs dialogs of the ship editor stand.</summary>
internal enum DesignStage : byte
{
    /// <summary>Not shown.</summary>
    Closed,

    /// <summary>Save: the player types a name.</summary>
    Naming,

    /// <summary>Save: the file is being written; the dialog cannot be left until it is.</summary>
    Saving,

    /// <summary>Save: a design of that name exists; asks before it is overwritten.</summary>
    ConfirmOverwrite,

    /// <summary>Load: the saved designs, or a note that they are being listed.</summary>
    Listing,

    /// <summary>Load: the picked file is being read and checked.</summary>
    Reading,

    /// <summary>Load: asks before the draft is replaced.</summary>
    ConfirmReplace,
}

/// <summary>
/// The rules of Save design and Load design in the ship editor, apart from fonts and drawing. Save asks for a name
/// (checked as a ship's name is, since it also becomes the design's name), writes the draft's blocks and parts through
/// <see cref="DesignShelf"/>, and asks again before it overwrites. Load lists the saved designs, marks the ones that
/// cannot be loaded (unreadable, or built on blocks of another size than the edited ship's), reads a picked one on a
/// worker, and asks before it replaces the draft, as one step to undo; the ship changes only on Apply, which the
/// server checks as ever. Runs on the render thread; the disk is the shelf's workers'. Takes no memory in a frame,
/// apart from the events themselves (a click, a result).
/// </summary>
internal sealed class DesignDialog
{
    /// <summary>The dialog panel's width and height in design pixels, before it is narrowed to fit the window.</summary>
    public const float PanelWidth = 560f;
    public const float PanelHeight = 440f;

    /// <summary>The field holds up to 32 characters, four bytes each at most, and the end mark.</summary>
    public const int NameBytes = NewShipDialog.NameBytes;

    private string _savingName = "";
    private DesignEntry? _picked;
    private BoxDesign? _loaded;

    /// <summary>Where the dialog stands.</summary>
    public DesignStage Stage { get; private set; }

    /// <summary>True while a dialog is shown: it takes the keyboard and the pointer.</summary>
    public bool Open => Stage != DesignStage.Closed;

    /// <summary>True while the file is being written: the dialog cannot be closed then.</summary>
    public bool Busy => Stage == DesignStage.Saving;

    /// <summary>One line in the dialog about what just went wrong (UTF-8, ready to draw), or empty.</summary>
    public byte[] Problem { get; private set; } = [];

    /// <summary>The entry the player picked, while it is read and while the replacement is confirmed.</summary>
    public DesignEntry? Picked => _picked;

    /// <summary>The name the draft is being saved under, for the overwrite question.</summary>
    public string SavingName => _savingName;

    /// <summary>Opens Save design, if there is a folder and the draft can be edited.</summary>
    public bool BeginSave(DesignShelf shelf, ShipEditor editor)
    {
        if (Open || !shelf.Available || !editor.CanEdit)
            return false;
        Problem = [];
        Stage = DesignStage.Naming;
        return true;
    }

    /// <summary>Opens Load design and asks for the list, if there is a folder and the draft can be edited.</summary>
    public bool BeginLoad(DesignShelf shelf, ShipEditor editor)
    {
        if (Open || !shelf.Available || !editor.CanEdit)
            return false;
        Problem = [];
        _picked = null;
        Stage = DesignStage.Listing;
        shelf.List();
        return true;
    }

    /// <summary>Closes the dialog whatever it was doing, for a session that ended; nothing under way is waited for.</summary>
    public void Reset(DesignShelf shelf)
    {
        shelf.Abandon();
        Stage = DesignStage.Closed;
        Problem = [];
        _picked = null;
        _loaded = null;
    }

    /// <summary>Whether a saved design's blocks are the size of the edited ship's, so that it can replace the draft.</summary>
    public static bool Fits(DesignEntry entry, ShipEditor editor) =>
        entry.State == DesignFileState.Readable && editor.Draft is { } draft && entry.BlockSize == draft.BlockSize;

    /// <summary>
    /// One step back: a question returns to what it asked about, the list closes, a read is given up. Does nothing
    /// while the file is written. True when the key was used.
    /// </summary>
    public bool Back(DesignShelf shelf)
    {
        switch (Stage)
        {
            case DesignStage.Closed:
                return false;
            case DesignStage.Saving:
                return true;
            case DesignStage.ConfirmOverwrite:
                Stage = DesignStage.Naming;
                Problem = [];
                return true;
            case DesignStage.Reading:
                shelf.Abandon();
                shelf.List();
                _picked = null;
                Stage = DesignStage.Listing;
                return true;
            case DesignStage.ConfirmReplace:
                _picked = null;
                _loaded = null;
                Stage = DesignStage.Listing;
                Problem = [];
                return true;
            default:
                Reset(shelf);
                return true;
        }
    }

    /// <summary>What is wrong with the typed name, as a ship's name is checked, which is also what the design is called.</summary>
    public static NameProblem Check(ReadOnlySpan<byte> utf8, Span<char> chars, out int length) => NewShipDialog.Check(utf8, chars, out length);

    /// <summary>
    /// Writes the draft as a design called <paramref name="name"/>. False, with nothing written, when the name does not
    /// pass <see cref="NewShipDialog.Check(ReadOnlySpan{char})"/>, there is no draft or a save is already running.
    /// With <paramref name="overwrite"/> an existing design of that name is replaced.
    /// </summary>
    public bool Submit(ReadOnlySpan<char> name, DesignShelf shelf, ShipEditor editor, bool overwrite)
    {
        if (Stage is not (DesignStage.Naming or DesignStage.ConfirmOverwrite) || NewShipDialog.Check(name) != NameProblem.None || editor.Draft is not { } draft)
            return false;
        var text = name.Trim().ToString();
        if (!shelf.Save(text, draft.BlockSize, draft.Class, draft.Color, draft.Boxes.ToArray(), draft.Mounts.ToArray(), overwrite))
            return false;
        _savingName = text;
        Problem = [];
        Stage = DesignStage.Saving;
        return true;
    }

    /// <summary>Starts reading a listed design, if it can be loaded. False when it cannot: it is unreadable or does not fit.</summary>
    public bool Pick(DesignEntry entry, DesignShelf shelf, ShipEditor editor)
    {
        if (Stage != DesignStage.Listing || !Fits(entry, editor) || shelf.Reading)
            return false;
        _picked = entry;
        Problem = [];
        Stage = DesignStage.Reading;
        shelf.Read(entry.File);
        return true;
    }

    /// <summary>
    /// Puts the picked design in the draft as one step to undo and closes the dialog, with a notice. False when the draft
    /// refuses it: the dialog goes back to the list with the reason.
    /// </summary>
    public bool Replace(DesignShelf shelf, ShipEditor editor, NoticeBoard board)
    {
        if (Stage != DesignStage.ConfirmReplace || _loaded is not { } design)
            return false;
        var result = editor.ReplaceDraft(design);
        if (result == DraftEdit.Done)
        {
            board.Post(NoticeKinds.DesignLoaded);
            Reset(shelf);
            return true;
        }

        Problem = ScreenHost.EditText(result).ToArray();
        _loaded = null;
        _picked = null;
        Stage = DesignStage.Listing;
        return false;
    }

    /// <summary>
    /// Follows the workers, once a frame: a save's end (a notice, a question or a reason), a read's end (the question
    /// or a reason), and the editor going away under the dialog.
    /// </summary>
    public void Update(DesignShelf shelf, ShipEditor editor, NoticeBoard board)
    {
        shelf.Update();
        if (shelf.TakeSave() is { } saved)
            Saved(saved, shelf, board);
        if (Stage is DesignStage.Closed or DesignStage.Saving)
            return;
        // An edit that ended, or a hull that turned out damaged, takes the dialog away: there is nothing to save or replace.
        if (!editor.CanEdit)
        {
            Reset(shelf);
            return;
        }

        if (Stage == DesignStage.Reading && shelf.TakeRead() is { } read)
        {
            if (read.Design is { } design && _picked is not null && editor.Draft is { } draft && design.BlockSize == draft.BlockSize)
            {
                _loaded = design;
                Stage = DesignStage.ConfirmReplace;
            }
            else
            {
                Problem = Encoding.UTF8.GetBytes(read.Design is null ? read.Message : "Its blocks are not the size of your ship's.");
                _picked = null;
                Stage = DesignStage.Listing;
            }
        }
    }

    private void Saved(DesignSaveResult result, DesignShelf shelf, NoticeBoard board)
    {
        switch (result.Status)
        {
            case DesignSaveStatus.Saved:
                board.Post(NoticeKinds.DesignSaved, Compose("Design saved as ", _savingName, "."));
                Reset(shelf);
                break;
            case DesignSaveStatus.Exists:
                Stage = DesignStage.ConfirmOverwrite;
                break;
            default:
                // A failure the player can act on is told in the dialog, which stays open for another try.
                Problem = Encoding.UTF8.GetBytes(result.Message);
                board.Post(NoticeKinds.DesignSaveFailed, Encoding.UTF8.GetBytes(result.Message));
                Stage = DesignStage.Naming;
                break;
        }
    }

    /// <summary>
    /// Enter and Escape, read in the shortcut phase before flight. Enter saves (Naming), overwrites (ConfirmOverwrite)
    /// or replaces the draft (ConfirmReplace); Escape goes one step back. Every other key is the name field's. True
    /// when a key was used.
    /// </summary>
    public bool HandleKeys(ActionMap actions, InputDevices devices, DesignShelf shelf, ShipEditor editor, NoticeBoard board, ImInputString name, bool typing, bool pointerOwned)
    {
        if (!Open)
            return false;
        var used = false;
        if (devices.KeyPressed(GlfwKey.Enter) || devices.KeyPressed(GlfwKey.KpEnter))
        {
            used = true;
            switch (Stage)
            {
                case DesignStage.Naming:
                case DesignStage.ConfirmOverwrite:
                    Span<char> chars = stackalloc char[NameBytes];
                    var overwrite = Stage == DesignStage.ConfirmOverwrite;
                    if (overwrite)
                        Submit(_savingName, shelf, editor, true);
                    else if (Check(name.ValueSpan, chars, out var length) == NameProblem.None)
                        Submit(chars[..length], shelf, editor, false);
                    break;
                case DesignStage.ConfirmReplace:
                    Replace(shelf, editor, board);
                    break;
            }
        }

        if (actions.InterfaceShortcutPressed(devices, InputAction.Pause, typing, pointerOwned))
            used |= Back(shelf);
        return used;
    }

    /// <summary>The notice text <paramref name="before"/> + name + <paramref name="after"/> as UTF-8, cut to what a notice holds.</summary>
    public static byte[] Compose(string before, string name, string after)
    {
        var shown = name.Length > DesignStore.MaxShownChars ? name[..DesignStore.MaxShownChars] : name;
        return Encoding.UTF8.GetBytes(before + shown + after);
    }
}
