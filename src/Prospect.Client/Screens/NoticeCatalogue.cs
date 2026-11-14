using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Engine.IO;

namespace Prospect.Client.Screens;

/// <summary>
/// The things that can post a notice. Code posts one with <see cref="NoticeBoard.Post"/>; what it says, its colour,
/// how long it stays and what a click does is data in <c>content/visuals/notices.json</c>. A kind with no entry in
/// the file is refused when the file loads, so a posting site is never left silent.
/// </summary>
internal static class NoticeKinds
{
    /// <summary>A gas scan finished: its results are in the Gas window.</summary>
    public const string GasResults = "gasResults";

    /// <summary>An asteroid scan finished: the analysis is on the target panel.</summary>
    public const string AsteroidResults = "asteroidResults";

    /// <summary>A locator pulse finished: its marks are drawn.</summary>
    public const string LocatorResults = "locatorResults";

    /// <summary>A module scan was refused or ended because the ship ran out of energy.</summary>
    public const string ModuleNoPower = "moduleNoPower";

    /// <summary>A module scan was cancelled before it finished.</summary>
    public const string ModuleCancelled = "moduleCancelled";

    /// <summary>The sector scan finished and charted the sector.</summary>
    public const string SectorCharted = "sectorCharted";

    /// <summary>A new ship was built in the New ship flow.</summary>
    public const string ShipBuilt = "shipBuilt";

    /// <summary>Every kind, for validation.</summary>
    public static readonly string[] All = [GasResults, AsteroidResults, LocatorResults, ModuleNoPower, ModuleCancelled, SectorCharted, ShipBuilt];
}

/// <summary>What a click on a notice's body does. Named in the file, like a tip's trigger; an unknown name is refused.</summary>
internal enum NoticeAction : byte
{
    /// <summary>The notice only informs.</summary>
    None,

    /// <summary>Opens the Gas window.</summary>
    OpenGasWindow,

    /// <summary>Opens the Charts window.</summary>
    OpenChartsWindow,
}

/// <summary>One kind of notice as the file defines it, with its text's key names resolved from the loaded bindings.</summary>
internal sealed class Notice
{
    public required string Id { get; init; }

    /// <summary>The text with each <c>{Action}</c> replaced by the key that action is bound to.</summary>
    public required string Text { get; init; }

    /// <summary>The text as the file has it, to resolve again after a remap.</summary>
    public string Template { get; init; } = "";

    /// <summary>The text as UTF-8, encoded once at load, so drawing allocates nothing.</summary>
    public byte[] TextBytes { get; init; } = [];

    /// <summary>The accent bar's colour as 0xRRGGBB.</summary>
    public uint Accent { get; init; }

    /// <summary>Seconds the notice stays on screen before it fades out, not counting time the pointer holds it.</summary>
    public float Seconds { get; init; }

    public NoticeAction Action { get; init; }
}

/// <summary>
/// The notices, loaded from <c>content/visuals/notices.json</c>: client data only. Each kind has a stable id, text
/// that may name an input action in braces (shown with the key bound to it now), an accent colour by name, a
/// duration (the file's default when it gives none) and an optional click action. A file with an unknown kind, action
/// or colour, a repeated id or a figure out of range is refused whole.
/// </summary>
internal sealed class NoticeCatalogue
{
    /// <summary>The most kinds a file may define.</summary>
    public const int MaxNotices = 64;

    /// <summary>The longest text, in characters.</summary>
    public const int MaxText = 160;

    /// <summary>The notice's width in unscaled pixels.</summary>
    public float Width { get; private init; }

    /// <summary>Seconds a notice fades in and out over.</summary>
    public float FadeSeconds { get; private init; }

    public Notice[] Notices { get; private init; } = [];

    /// <summary>The index of the kind <paramref name="id"/>, or -1 when the file has none.</summary>
    public int IndexOf(string id)
    {
        for (var i = 0; i < Notices.Length; i++)
            if (Notices[i].Id == id)
                return i;
        return -1;
    }

    /// <summary>The same notices with each key name resolved again from <paramref name="actions"/>, after a remap.</summary>
    public NoticeCatalogue Rebind(ActionMap actions)
    {
        var notices = new Notice[Notices.Length];
        for (var i = 0; i < notices.Length; i++)
        {
            var notice = Notices[i];
            var text = TipCatalogue.Resolve(notice.Template, actions, "notices", notice.Id);
            notices[i] = new Notice
            {
                Id = notice.Id,
                Text = text,
                Template = notice.Template,
                TextBytes = Encoding.UTF8.GetBytes(text),
                Accent = notice.Accent,
                Seconds = notice.Seconds,
                Action = notice.Action,
            };
        }

        return new NoticeCatalogue { Width = Width, FadeSeconds = FadeSeconds, Notices = notices };
    }

    /// <summary>Reads the shipped file and resolves key names from <paramref name="actions"/>.</summary>
    public static NoticeCatalogue Load(ActionMap actions) => Load(AppPaths.Content("visuals", "notices.json"), actions);

    /// <summary>Reads and checks the file; anything out of range, unknown or repeated is refused.</summary>
    public static NoticeCatalogue Load(string path, ActionMap actions)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        NoticeFile file;
        try
        {
            file = JsonSerializer.Deserialize<NoticeFile>(File.ReadAllText(path), options) ?? throw new InvalidDataException($"{path}: empty");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }

        if (!(file.Width is >= 200 and <= 480))
            throw new InvalidDataException($"{path}: width is 200 to 480");
        if (!(file.DefaultSeconds is >= 1 and <= 60))
            throw new InvalidDataException($"{path}: defaultSeconds is 1 to 60");
        if (!(file.FadeSeconds is >= 0.05f and <= 2))
            throw new InvalidDataException($"{path}: fadeSeconds is 0.05 to 2");
        if (file.Notices is not { Length: > 0 and <= MaxNotices })
            throw new InvalidDataException($"{path}: 1 to {MaxNotices} notices");

        var notices = new Notice[file.Notices.Length];
        for (var i = 0; i < notices.Length; i++)
        {
            var raw = file.Notices[i];
            if (string.IsNullOrWhiteSpace(raw.Id) || raw.Id.Length > 48 || !raw.Id.All(char.IsAsciiLetterOrDigit))
                throw new InvalidDataException($"{path}: a notice id is 1 to 48 letters and digits");
            for (var j = 0; j < i; j++)
                if (notices[j].Id == raw.Id)
                    throw new InvalidDataException($"{path}: notice \"{raw.Id}\" is defined twice");
            if (Array.IndexOf(NoticeKinds.All, raw.Id) < 0)
                throw new InvalidDataException($"{path}: notice \"{raw.Id}\" is not a kind the game posts");
            if (string.IsNullOrWhiteSpace(raw.Text) || raw.Text.Length > MaxText)
                throw new InvalidDataException($"{path}: notice \"{raw.Id}\" needs text of 1 to {MaxText} characters");
            if (!Enum.IsDefined(raw.Action))
                throw new InvalidDataException($"{path}: notice \"{raw.Id}\" names an unknown action");
            var seconds = raw.Seconds ?? file.DefaultSeconds;
            if (!(seconds is >= 1 and <= 60))
                throw new InvalidDataException($"{path}: notice \"{raw.Id}\" seconds is 1 to 60");
            var text = TipCatalogue.Resolve(raw.Text, actions, path, raw.Id);
            notices[i] = new Notice
            {
                Id = raw.Id,
                Text = text,
                Template = raw.Text,
                TextBytes = Encoding.UTF8.GetBytes(text),
                Accent = Accent(path, raw.Id, raw.Accent),
                Seconds = seconds,
                Action = raw.Action,
            };
        }

        foreach (var kind in NoticeKinds.All)
            if (!notices.Any(n => n.Id == kind))
                throw new InvalidDataException($"{path}: no notice for the kind \"{kind}\"");

        return new NoticeCatalogue { Width = file.Width, FadeSeconds = file.FadeSeconds, Notices = notices };
    }

    private static uint Accent(string path, string id, string name) => name switch
    {
        "accent" => Theme.Accent,
        "info" => Theme.Info,
        "good" => Theme.Good,
        "bad" => Theme.Bad,
        "dim" => Theme.TextDim,
        _ => throw new InvalidDataException($"{path}: notice \"{id}\" names an unknown accent \"{name}\" (accent, info, good, bad or dim)"),
    };

    private sealed class NoticeFile
    {
        public float Width { get; init; }
        public float DefaultSeconds { get; init; }
        public float FadeSeconds { get; init; }
        public NoticeEntry[]? Notices { get; init; }
    }

    private sealed class NoticeEntry
    {
        public string Id { get; init; } = "";
        public string Text { get; init; } = "";
        public string Accent { get; init; } = "accent";
        public float? Seconds { get; init; }
        public NoticeAction Action { get; init; }
    }
}
