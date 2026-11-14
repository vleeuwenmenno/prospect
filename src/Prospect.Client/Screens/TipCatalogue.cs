using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Prospect.Client.Input;
using Prospect.Client.Settings;
using Prospect.Engine.IO;

namespace Prospect.Client.Screens;

/// <summary>
/// The moments that can bring up a tip. Code fires one with <see cref="TipBoard.Fire"/>; which tips it shows, what
/// they say and where they sit is data in <c>content/visuals/tips.json</c>. A tip names exactly one of these, and a
/// trigger with no tip in the file is refused when the file loads, so a firing site is never left silent.
/// </summary>
internal static class TipTriggers
{
    /// <summary>The galaxy map was opened.</summary>
    public const string MapOpened = "mapOpened";

    /// <summary>The command view was entered.</summary>
    public const string CommandViewOpened = "commandViewOpened";

    /// <summary>The ship editor opened on a ship.</summary>
    public const string EditorOpened = "editorOpened";

    /// <summary>The map's sector card was pinned on the player's own sector, where Scan sector is.</summary>
    public const string ScanAvailable = "scanAvailable";

    /// <summary>
    /// The ship being flown, which has a charting block, is in a sector its owner has not charted, in the flight or
    /// command view: fired each time it comes to be so, so the tip names the scan key.
    /// </summary>
    public const string UnchartedSector = "unchartedSector";

    /// <summary>The shield of the ship being flown went offline.</summary>
    public const string ShieldOffline = "shieldOffline";

    /// <summary>The battery of the ship being flown ran low.</summary>
    public const string EnergyLow = "energyLow";

    /// <summary>
    /// The player pressed the plain editor key in the drone, which opens the New ship dialog; fired each time by
    /// <c>ScreenHost.FireTip(TipTriggers.DroneEditHint)</c>, so the tip points to Ctrl plus the key for the drone itself.
    /// </summary>
    public const string DroneEditHint = "droneEditHint";

    /// <summary>Interface mode began: the pointer is free and every window that is not pinned appeared. Withdrawn when it ends.</summary>
    public const string InterfaceOpened = "interfaceOpened";

    /// <summary>The ship being flown has a gas scanner and a gas cloud is within its reach: fired each time it comes to be so.</summary>
    public const string GasScannerReady = "gasScannerReady";

    /// <summary>An asteroid the player has not scanned is targeted, in a ship with an asteroid scanner: fired each time one is.</summary>
    public const string AsteroidScannerReady = "asteroidScannerReady";

    /// <summary>The ship being flown has an asteroid locator, in the flight or command view: fired when it first comes to have one.</summary>
    public const string LocatorReady = "locatorReady";

    /// <summary>Every trigger, for validation.</summary>
    public static readonly string[] All = [MapOpened, CommandViewOpened, EditorOpened, ScanAvailable, UnchartedSector, ShieldOffline, EnergyLow, DroneEditHint, InterfaceOpened, GasScannerReady, AsteroidScannerReady, LocatorReady];
}

/// <summary>What a tip is placed against: the screen, a panel the editor or flight view draws, or a point.</summary>
internal enum TipRegion : byte
{
    /// <summary>The screen under the navigation bar, inside its margins; the side says which edge or corner.</summary>
    Screen,

    /// <summary>The pointer; the tip sits beside it and never under it.</summary>
    Cursor,

    /// <summary>The ship being targeted, where it is on screen; falls back to the screen when there is none.</summary>
    Target,

    /// <summary>
    /// The bottom cluster of the flight and command views: the pilot console and, over it, the module chips when the
    /// ship has any. A tip on its Top side sits above all of it; on a short screen it is pushed up, never down over it.
    /// </summary>
    Console,

    /// <summary>The editor's part list, its selection and figures panel, and its bottom bar.</summary>
    EditorParts,
    EditorDetails,
    EditorBar,

    /// <summary>The New ship dialog's panel; the tip is placed beside it while it is open.</summary>
    NewShipDialog,
}

/// <summary>
/// Which side a tip takes. Against the screen it is the edge or corner the tip sits inside; against a panel or a
/// point it is the side of that panel or point the tip sits outside, centred along it.
/// </summary>
internal enum TipSide : byte
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Center,
    Right,
    BottomLeft,
    Bottom,
    BottomRight,
}

/// <summary>Where a tip goes: its region, the side of it, and a shift in unscaled pixels.</summary>
internal readonly record struct TipAnchor(TipRegion Region, TipSide Side, float OffsetX, float OffsetY);

/// <summary>One tip as the file defines it, with its text's key names resolved from the loaded bindings.</summary>
internal sealed class Tip
{
    public required string Id { get; init; }
    public required string Trigger { get; init; }
    public required string Title { get; init; }

    /// <summary>The text with each <c>{Action}</c> replaced by the key that action is bound to.</summary>
    public required string Text { get; init; }

    /// <summary>The text as the file has it, with its <c>{Action}</c> placeholders, to resolve again after a remap.</summary>
    public string Template { get; init; } = "";
    public required TipAnchor Anchor { get; init; }

    /// <summary>Title and text as UTF-8, encoded once at load, so drawing allocates nothing.</summary>
    public byte[] TitleBytes { get; init; } = [];
    public byte[] TextBytes { get; init; } = [];
}

/// <summary>The words of the Settings screen's tips section, from the same file as the tips.</summary>
internal sealed class TipSettingsText
{
    public required byte[] Caption { get; init; }
    public required byte[] Description { get; init; }
    public required byte[] Reset { get; init; }
    public required byte[] Confirm { get; init; }
    public required byte[] ConfirmYes { get; init; }
    public required byte[] ConfirmNo { get; init; }
    public required byte[] Done { get; init; }

    /// <summary>The panel positions part of the same section: what it is, its button and the line shown after it.</summary>
    public required byte[] PanelsDescription { get; init; }
    public required byte[] PanelsReset { get; init; }
    public required byte[] PanelsDone { get; init; }
}

/// <summary>
/// The in-game tips, loaded from <c>content/visuals/tips.json</c>: client data only, nothing the server decides.
/// Each tip has a stable id (what "Don't show this again" saves), the trigger that brings it up, a title, text and
/// an anchor. Text may name an input action in braces, <c>{ToggleMap}</c>, and is shown with the key that action is
/// bound to now, so a rebound key is never taught wrong. A file with an unknown action or trigger, a repeated id or
/// a figure out of range is refused whole.
/// </summary>
internal sealed class TipCatalogue
{
    /// <summary>The most tips a file may define.</summary>
    public const int MaxTips = 64;

    /// <summary>The longest id, title and text, in characters.</summary>
    public const int MaxTitle = 48;
    public const int MaxText = 480;

    /// <summary>The panel's width in unscaled pixels.</summary>
    public float PanelWidth { get; private init; }

    public byte[] Next { get; private init; } = [];
    public byte[] Close { get; private init; } = [];
    public byte[] DontShowAgain { get; private init; } = [];

    /// <summary>The line shown in place of the buttons while the pointer is hidden for flight.</summary>
    public byte[] CapturedHint { get; private init; } = [];

    private string _capturedHintTemplate = "";

    public TipSettingsText Settings { get; private init; } = null!;
    public Tip[] Tips { get; private init; } = [];

    /// <summary>
    /// The same tips with each key name resolved again from <paramref name="actions"/>, for after the player remapped a
    /// control. Nothing is read from disk.
    /// </summary>
    public TipCatalogue Rebind(ActionMap actions)
    {
        var tips = new Tip[Tips.Length];
        for (var i = 0; i < tips.Length; i++)
        {
            var tip = Tips[i];
            var text = Resolve(tip.Template, actions, "tips", tip.Id);
            tips[i] = new Tip
            {
                Id = tip.Id,
                Trigger = tip.Trigger,
                Title = tip.Title,
                Text = text,
                Template = tip.Template,
                Anchor = tip.Anchor,
                TitleBytes = tip.TitleBytes,
                TextBytes = Encoding.UTF8.GetBytes(text),
            };
        }

        return new TipCatalogue
        {
            PanelWidth = PanelWidth,
            Next = Next,
            Close = Close,
            DontShowAgain = DontShowAgain,
            CapturedHint = Encoding.UTF8.GetBytes(Resolve(_capturedHintTemplate, actions, "tips", "capturedHint")),
            _capturedHintTemplate = _capturedHintTemplate,
            Settings = Settings,
            Tips = tips,
        };
    }

    /// <summary>Reads the shipped file and resolves key names from <paramref name="actions"/>.</summary>
    public static TipCatalogue Load(ActionMap actions) => Load(AppPaths.Content("visuals", "tips.json"), actions);

    /// <summary>Reads and checks the file; anything out of range, unknown or repeated is refused.</summary>
    public static TipCatalogue Load(string path, ActionMap actions)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        TipFile file;
        try
        {
            file = JsonSerializer.Deserialize<TipFile>(File.ReadAllText(path), options) ?? throw new InvalidDataException($"{path}: empty");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }

        if (!(file.PanelWidth is >= 240 and <= 480))
            throw new InvalidDataException($"{path}: panelWidth is 240 to 480");
        if (file.Tips is not { Length: > 0 and <= MaxTips })
            throw new InvalidDataException($"{path}: 1 to {MaxTips} tips");
        var words = new[] { file.Next, file.Close, file.DontShowAgain, file.CapturedHint };
        foreach (var word in words)
            if (string.IsNullOrWhiteSpace(word) || word.Length > 96)
                throw new InvalidDataException($"{path}: next, close, dontShowAgain and capturedHint are 1 to 96 characters");
        if (file.Settings is null)
            throw new InvalidDataException($"{path}: missing settings");

        var tips = new Tip[file.Tips.Length];
        for (var i = 0; i < tips.Length; i++)
        {
            var raw = file.Tips[i];
            if (string.IsNullOrWhiteSpace(raw.Id) || raw.Id.Length > ClientSettings.MaxTipIdLength || !raw.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                throw new InvalidDataException($"{path}: a tip id is 1 to {ClientSettings.MaxTipIdLength} letters, digits and dashes");
            for (var j = 0; j < i; j++)
                if (tips[j].Id == raw.Id)
                    throw new InvalidDataException($"{path}: tip \"{raw.Id}\" is defined twice");
            if (Array.IndexOf(TipTriggers.All, raw.Trigger) < 0)
                throw new InvalidDataException($"{path}: tip \"{raw.Id}\" names an unknown trigger \"{raw.Trigger}\"");
            if (string.IsNullOrWhiteSpace(raw.Title) || raw.Title.Length > MaxTitle)
                throw new InvalidDataException($"{path}: tip \"{raw.Id}\" needs a title of 1 to {MaxTitle} characters");
            if (string.IsNullOrWhiteSpace(raw.Text) || raw.Text.Length > MaxText)
                throw new InvalidDataException($"{path}: tip \"{raw.Id}\" needs text of 1 to {MaxText} characters");
            if (raw.Anchor is null || raw.Anchor.Offset is not { Length: 2 } offset || !float.IsFinite(offset[0]) || !float.IsFinite(offset[1])
                || Math.Abs(offset[0]) > 1000 || Math.Abs(offset[1]) > 1000)
                throw new InvalidDataException($"{path}: tip \"{raw.Id}\" needs an anchor with an offset of two figures within 1000");
            var text = Resolve(raw.Text, actions, path, raw.Id);
            tips[i] = new Tip
            {
                Id = raw.Id,
                Trigger = raw.Trigger,
                Title = raw.Title,
                Text = text,
                Template = raw.Text,
                Anchor = new TipAnchor(raw.Anchor.Region, raw.Anchor.Side, offset[0], offset[1]),
                TitleBytes = Encoding.UTF8.GetBytes(raw.Title),
                TextBytes = Encoding.UTF8.GetBytes(text),
            };
        }

        foreach (var trigger in TipTriggers.All)
            if (!tips.Any(tip => tip.Trigger == trigger))
                throw new InvalidDataException($"{path}: no tip for the trigger \"{trigger}\"");

        var s = file.Settings;
        foreach (var word in new[] { s.Caption, s.Description, s.Reset, s.Confirm, s.ConfirmYes, s.ConfirmNo, s.Done, s.PanelsDescription, s.PanelsReset, s.PanelsDone })
            if (string.IsNullOrWhiteSpace(word) || word.Length > 240)
                throw new InvalidDataException($"{path}: every settings line is 1 to 240 characters");

        static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
        return new TipCatalogue
        {
            PanelWidth = file.PanelWidth,
            Next = Bytes(file.Next),
            Close = Bytes(file.Close),
            DontShowAgain = Bytes(file.DontShowAgain),
            CapturedHint = Bytes(Resolve(file.CapturedHint, actions, path, "capturedHint")),
            _capturedHintTemplate = file.CapturedHint,
            Settings = new TipSettingsText
            {
                Caption = Bytes(s.Caption),
                Description = Bytes(s.Description),
                Reset = Bytes(s.Reset),
                Confirm = Bytes(s.Confirm),
                ConfirmYes = Bytes(s.ConfirmYes),
                ConfirmNo = Bytes(s.ConfirmNo),
                Done = Bytes(s.Done),
                PanelsDescription = Bytes(Resolve(s.PanelsDescription, actions, path, "panelsDescription")),
                PanelsReset = Bytes(s.PanelsReset),
                PanelsDone = Bytes(s.PanelsDone),
            },
            Tips = tips,
        };
    }

    /// <summary>Replaces each <c>{Action}</c> in <paramref name="template"/> with the key the action is bound to.</summary>
    public static string Resolve(string template, ActionMap actions, string path = "tips", string id = "")
    {
        var result = new StringBuilder(template.Length + 16);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '}')
                throw new InvalidDataException($"{path}: \"{id}\" has a closing brace with no opening one");
            if (c != '{')
            {
                result.Append(c);
                continue;
            }

            var end = template.IndexOf('}', i + 1);
            if (end < 0)
                throw new InvalidDataException($"{path}: \"{id}\" has an opening brace with no closing one");
            var name = template.AsSpan(i + 1, end - i - 1);
            if (!Enum.TryParse<InputAction>(name, out var action) || action == InputAction.Count)
                throw new InvalidDataException($"{path}: \"{id}\" names an unknown action \"{name}\"");
            result.Append(KeyNames(actions, action));
            i = end;
        }

        return result.ToString();
    }

    /// <summary>
    /// What an action is bound to, for a sentence: its keyboard keys (the first two) spaced and in capitals, else
    /// its first other control, else a plain statement that it is unbound.
    /// </summary>
    public static string KeyNames(ActionMap actions, InputAction action)
    {
        var names = new List<string>(2);
        foreach (var binding in actions.Bindings(action))
        {
            if (binding.Device == InputBinding.Source.Key && names.Count < 2)
                names.Add(Spaced(binding.DisplayName).ToUpperInvariant());
        }

        if (names.Count == 0)
        {
            var any = actions.Bindings(action);
            if (any.IsEmpty)
                return "(unbound)";
            names.Add(any[0].DisplayName.ToUpperInvariant());
        }

        return string.Join(" or ", names);
    }

    /// <summary>"PageUp" as "Page Up".</summary>
    private static string Spaced(string name)
    {
        var result = new StringBuilder(name.Length + 2);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                result.Append(' ');
            result.Append(name[i]);
        }

        return result.ToString();
    }

    private sealed class TipFile
    {
        public float PanelWidth { get; init; }
        public string Next { get; init; } = "";
        public string Close { get; init; } = "";
        public string DontShowAgain { get; init; } = "";
        public string CapturedHint { get; init; } = "";
        public SettingsFile? Settings { get; init; }
        public TipEntry[]? Tips { get; init; }
    }

    private sealed class SettingsFile
    {
        public string Caption { get; init; } = "";
        public string Description { get; init; } = "";
        public string Reset { get; init; } = "";
        public string Confirm { get; init; } = "";
        public string ConfirmYes { get; init; } = "";
        public string ConfirmNo { get; init; } = "";
        public string Done { get; init; } = "";
        public string PanelsDescription { get; init; } = "";
        public string PanelsReset { get; init; } = "";
        public string PanelsDone { get; init; } = "";
    }

    private sealed class TipEntry
    {
        public string Id { get; init; } = "";
        public string Trigger { get; init; } = "";
        public string Title { get; init; } = "";
        public string Text { get; init; } = "";
        public AnchorEntry? Anchor { get; init; }
    }

    private sealed class AnchorEntry
    {
        public TipRegion Region { get; init; }
        public TipSide Side { get; init; }
        public float[]? Offset { get; init; }
    }
}
