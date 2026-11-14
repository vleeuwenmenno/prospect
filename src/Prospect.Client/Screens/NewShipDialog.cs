using System.Text;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.Protocol;

namespace Prospect.Client.Screens;

/// <summary>Why a typed ship name cannot be sent, found on the client before the server is asked.</summary>
internal enum NameProblem : byte
{
    /// <summary>The name can be sent. The server may still refuse it, for example as a repeat.</summary>
    None,

    /// <summary>Nothing but blanks, or nothing at all.</summary>
    Empty,

    /// <summary>More than <see cref="ShipName.MaxChars"/> characters.</summary>
    TooLong,

    /// <summary>A control, formatting or unassigned character, or text that is not valid Unicode.</summary>
    BadCharacter,
}

/// <summary>Where the New ship dialog stands.</summary>
internal enum NewShipStage : byte
{
    /// <summary>Not shown.</summary>
    Closed,

    /// <summary>The player is typing a name. A refusal of the last try, if any, is shown under the field.</summary>
    Naming,

    /// <summary>The name was sent and the server has not answered.</summary>
    Waiting,

    /// <summary>The server made the ship; the editor is waiting for it to appear before it opens.</summary>
    Building,
}

/// <summary>
/// The rules of the New ship dialog apart from fonts and drawing: opening it from the drone, checking the name as
/// it is typed, sending it, following the server's answer, and handing the new hull to the ship editor. A refusal is
/// kept for the dialog to show and the player may type another name; a created ship closes the dialog once the
/// editor has opened on it. Runs on the render thread and reads only <see cref="SessionSnapshot"/>.
/// </summary>
internal sealed class NewShipDialog
{
    /// <summary>The panel's width and height in design pixels, before it is narrowed to fit the window.</summary>
    public const float PanelWidth = 460f;
    public const float PanelHeight = 316f;

    private uint _sentSerial;

    /// <summary>Where the dialog stands.</summary>
    public NewShipStage Stage { get; private set; }

    /// <summary>True while the dialog is shown and takes the keyboard and the pointer.</summary>
    public bool Open => Stage != NewShipStage.Closed;

    /// <summary>True while the server is working: the dialog cannot be closed then, or a ship could appear unclaimed.</summary>
    public bool Busy => Stage is NewShipStage.Waiting or NewShipStage.Building;

    /// <summary>What the server last refused the name for, shown under the field until the next try; none when there is nothing to say.</summary>
    public EditorReason Refusal { get; private set; }

    /// <summary>Opens the dialog on an empty refusal. Does nothing when it is already open.</summary>
    public void Begin()
    {
        if (Stage != NewShipStage.Closed)
            return;
        Stage = NewShipStage.Naming;
        Refusal = EditorReason.None;
    }

    /// <summary>Opens the dialog showing a refusal, for the capture that shows what one looks like.</summary>
    public void BeginRefused(EditorReason reason)
    {
        Stage = NewShipStage.Naming;
        Refusal = reason;
    }

    /// <summary>Closes the dialog when the server is not working on a request. True when it closed.</summary>
    public bool Cancel()
    {
        if (Stage != NewShipStage.Naming)
            return false;
        Stage = NewShipStage.Closed;
        Refusal = EditorReason.None;
        return true;
    }

    /// <summary>The field holds up to 32 characters, four bytes each at most, and the end mark.</summary>
    public const int NameBytes = ShipName.MaxChars * 4 + 1;

    /// <summary>
    /// The editor key and Escape, read in the shortcut phase before flight, with nothing else open. Open, the
    /// dialog takes Enter (builds when the name is good) and Escape (closes it, or is only swallowed while the
    /// server works); every other key is the name field's. Closed, the editor key in the escape drone opens the
    /// dialog and fires the drone-edit tip every time, and with Ctrl held asks to edit the drone itself; in a ship,
    /// and with an edit open, the key is the ship editor's as ever. True when a key was used.
    /// </summary>
    public bool HandleKeys(ActionMap actions, InputDevices devices, SessionSnapshot snapshot, ShipEditor editor, PlayerInputBridge bridge, TipBoard tips, ImInputString name, bool typing, bool pointerOwned)
    {
        if (Open)
        {
            var used = false;
            if (Stage == NewShipStage.Naming && (devices.KeyPressed(GlfwKey.Enter) || devices.KeyPressed(GlfwKey.KpEnter)))
            {
                Span<char> chars = stackalloc char[NameBytes];
                if (Check(name.ValueSpan, chars, out var length) == NameProblem.None)
                    Submit(chars[..length], snapshot, bridge);
                used = true;
            }

            if (actions.InterfaceShortcutPressed(devices, InputAction.Pause, typing, pointerOwned))
            {
                Cancel();
                used = true;
            }

            return used;
        }

        ref var own = ref snapshot.Hulls[snapshot.OwnHull];
        if (snapshot.OwnIsDrone && editor.Stage == EditorStage.Closed && actions.InterfaceShortcutPressed(devices, InputAction.ToggleEditor, typing, pointerOwned))
        {
            if (devices.KeyDown(GlfwKey.LeftControl) || devices.KeyDown(GlfwKey.RightControl))
            {
                editor.Request(own, bridge);
            }
            else
            {
                name.Clear();
                Begin();
                tips.Fire(TipTriggers.DroneEditHint);
            }

            return true;
        }

        return editor.HandleKeys(actions, devices, own, bridge, typing, pointerOwned);
    }

    /// <summary>Closes the dialog whatever it was doing, for a session that ended.</summary>
    public void Reset()
    {
        Stage = NewShipStage.Closed;
        Refusal = EditorReason.None;
    }

    /// <summary>
    /// What is wrong with <paramref name="name"/> as typed, using the rules the server uses (<see cref="ShipName"/>):
    /// trimmed, one to <see cref="ShipName.MaxChars"/> characters, nothing unprintable. Allocates nothing.
    /// </summary>
    public static NameProblem Check(ReadOnlySpan<char> name)
    {
        name = name.Trim();
        if (name.IsEmpty)
            return NameProblem.Empty;
        // A name far past the limit is too long whatever it holds; the bound keeps the check itself bounded.
        if (name.Length > ShipName.MaxUnits)
            return NameProblem.TooLong;
        var count = 0;
        foreach (var _ in name.EnumerateRunes())
            count++;
        if (count > ShipName.MaxChars)
            return NameProblem.TooLong;
        ShipName probe = default;
        return probe.Set(name) ? NameProblem.None : NameProblem.BadCharacter;
    }

    /// <summary>
    /// Decodes the field's UTF-8 into <paramref name="chars"/> and checks it. A field longer than the buffer holds
    /// is too long.
    /// </summary>
    public static NameProblem Check(ReadOnlySpan<byte> utf8, Span<char> chars, out int length)
    {
        length = 0;
        if (utf8.Length == 0)
            return NameProblem.Empty;
        if (Encoding.UTF8.GetMaxCharCount(utf8.Length) > chars.Length && Encoding.UTF8.GetCharCount(utf8) > chars.Length)
            return NameProblem.TooLong;
        length = Encoding.UTF8.GetChars(utf8, chars);
        return Check(chars[..length]);
    }

    /// <summary>
    /// Sends the name to the server when it passes <see cref="Check(ReadOnlySpan{char})"/>. False, with nothing sent,
    /// when it does not or when a request is already out.
    /// </summary>
    public bool Submit(ReadOnlySpan<char> name, SessionSnapshot snapshot, PlayerInputBridge bridge)
    {
        if (Stage != NewShipStage.Naming || Check(name) != NameProblem.None || !bridge.RequestNewShip(name))
            return false;
        _sentSerial = snapshot.NewShip.Serial;
        Refusal = EditorReason.None;
        Stage = NewShipStage.Waiting;
        return true;
    }

    /// <summary>
    /// Follows the server's answer, once a frame. A refusal goes back to naming with the reason; a created ship is
    /// handed to the editor, which opens on it once the hull is in the snapshot, and the dialog closes then.
    /// </summary>
    public void Update(SessionSnapshot snapshot, ShipEditor editor, uint pilot)
    {
        // Leaving the drone, or losing it, takes the question away: only a drone may ask.
        if (Stage == NewShipStage.Naming && !snapshot.OwnIsDrone)
        {
            Reset();
            return;
        }

        switch (Stage)
        {
            case NewShipStage.Waiting:
                var answer = snapshot.NewShip;
                if (answer.Serial == _sentSerial || answer.Status is NewShipStatus.None or NewShipStatus.Pending)
                    break;
                if (answer.Status == NewShipStatus.Created && answer.Hull != 0)
                {
                    editor.AwaitCreated(pilot, answer.Hull);
                    Stage = NewShipStage.Building;
                }
                else
                {
                    Refusal = answer.Reason == EditorReason.None ? EditorReason.Unavailable : answer.Reason;
                    Stage = NewShipStage.Naming;
                }

                break;
            case NewShipStage.Building:
                // The editor opened on the new ship, or gave up waiting for it and says why in its own notice.
                if (editor.Stage != EditorStage.Requesting)
                    Reset();
                break;
        }
    }

    /// <summary>The dialog's panel, centred a little above the middle so a tip fits under it, for a window and an interface scale.</summary>
    public static HudRect Panel(float2 display, float scale)
    {
        float Px(float design) => MathF.Round(design * scale);
        var margin = Px(FloatingWindow.Margin);
        var top = HudLayout.TopBarBottom(scale) + margin;
        var width = Math.Min(Px(PanelWidth), Math.Max(0f, display.X - margin * 2));
        var height = Math.Min(Px(PanelHeight), Math.Max(0f, display.Y - top - margin));
        var left = MathF.Round((display.X - width) * 0.5f);
        var at = Math.Max(top, MathF.Round(display.Y * 0.40f - height * 0.5f));
        return new HudRect(left, at, left + width, at + height);
    }
}
