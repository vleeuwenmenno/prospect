using System.Text;
using System.Text.Json;
using Prospect.Engine.IO;

namespace Prospect.Client.Input;

/// <summary>The views a control is read in; two controls only clash when some view reads both.</summary>
[Flags]
internal enum ControlScope
{
    None = 0,
    Pilot = 1,
    Command = 2,
    Map = 4,
    Editor = 8,
    All = Pilot | Command | Map | Editor,
}

/// <summary>What a catalogue row stands for.</summary>
internal enum RowKind : byte
{
    /// <summary>Written down for the player, not remappable: a mouse gesture or a fixed key.</summary>
    Fixed,

    /// <summary>An <see cref="InputAction"/>; <see cref="ControlsCatalog.Row.Index"/> is its number.</summary>
    Action,

    /// <summary>An <see cref="InputAxis"/>; <see cref="ControlsCatalog.Row.Index"/> is its number.</summary>
    Axis,
}

/// <summary>
/// Every control the game has, grouped into the tabs of the Controls screens. The groups, labels and scopes are data
/// (<c>content/input/controls.json</c>); the keys shown are read from the live <see cref="ActionMap"/>, so they follow
/// the player's remaps. The file must name every action and every axis exactly once, or loading is refused.
/// </summary>
internal sealed class ControlsCatalog
{
    /// <summary>One line of the screens: what it does and what it is bound to now. <c>Extra</c> is what an axis has beyond its first pair of buttons.</summary>
    public readonly record struct Row(
        string Category,
        string Action,
        string Binding,
        RowKind Kind = RowKind.Fixed,
        int Index = -1,
        ControlScope Scope = ControlScope.None,
        string NegativeLabel = "",
        string PositiveLabel = "",
        bool Required = false,
        string Extra = "")
    {
        public bool Matches(string query) => Category.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Action.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Binding.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One tab and its rows, in the order the file lists them.</summary>
    public sealed class Tab(string title)
    {
        public string Title { get; } = title;
        public Row[] Rows { get; internal set; } = [];
    }

    private sealed record Entry(int Tab, RowKind Kind, int Index, string Label, string Template, ControlScope Scope, string Negative, string Positive, bool Required);

    private const int MaxText = 240;

    private readonly ActionMap _map;
    private readonly Entry[] _entries;
    private readonly int[] _actionEntry = new int[(int)InputAction.Count];
    private readonly int[] _axisEntry = new int[(int)InputAxis.Count];
    private int _version = -1;

    /// <summary>The tabs, in the order the file lists them.</summary>
    public Tab[] Tabs { get; }

    /// <summary>Every row of every tab, in tab order.</summary>
    public Row[] Rows { get; private set; } = [];

    /// <summary>Reads the shipped file.</summary>
    public ControlsCatalog(ActionMap map) : this(AppPaths.Content("input", "controls.json"), map) { }

    /// <summary>Reads and checks the file at <paramref name="path"/>; anything missing, repeated or unknown is refused.</summary>
    public ControlsCatalog(string path, ActionMap map)
    {
        _map = map;
        Array.Fill(_actionEntry, -1);
        Array.Fill(_axisEntry, -1);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var tabNames = new List<string>();
        foreach (var tab in root.GetProperty("tabs"u8).EnumerateArray())
        {
            var title = tab.GetString() ?? "";
            if (title.Length is 0 or > 48 || tabNames.Contains(title))
                throw new InvalidDataException($"{path}: a tab needs a distinct title of 1 to 48 characters");
            tabNames.Add(title);
        }

        if (tabNames.Count is 0 or > 12)
            throw new InvalidDataException($"{path}: 1 to 12 tabs");
        Tabs = [.. tabNames.Select(name => new Tab(name))];

        var entries = new List<Entry>();
        foreach (var row in root.GetProperty("rows"u8).EnumerateArray())
        {
            var tabName = Text(row, "tab"u8);
            var tab = tabNames.IndexOf(tabName);
            if (tab < 0)
                throw new InvalidDataException($"{path}: unknown tab \"{tabName}\"");
            var label = Text(row, "label"u8);
            if (label.Length is 0 or > MaxText)
                throw new InvalidDataException($"{path}: a label is 1 to {MaxText} characters");
            var required = row.TryGetProperty("required"u8, out var flag) && flag.GetBoolean();
            var scope = ControlScope.None;
            if (row.TryGetProperty("scope"u8, out var scopes))
                foreach (var name in scopes.EnumerateArray())
                    scope |= Enum.TryParse<ControlScope>(name.GetString(), out var one) && one != ControlScope.None
                        ? one
                        : throw new InvalidDataException($"{path}: \"{label}\" has an unknown scope \"{name.GetString()}\"");
            if (row.TryGetProperty("action"u8, out var action))
            {
                if (!Enum.TryParse<InputAction>(action.GetString(), out var parsed) || parsed == InputAction.Count)
                    throw new InvalidDataException($"{path}: unknown action \"{action.GetString()}\"");
                if (_actionEntry[(int)parsed] >= 0)
                    throw new InvalidDataException($"{path}: action {parsed} is listed twice");
                if (scope == ControlScope.None)
                    throw new InvalidDataException($"{path}: action {parsed} needs a scope");
                _actionEntry[(int)parsed] = entries.Count;
                entries.Add(new Entry(tab, RowKind.Action, (int)parsed, label, "", scope, "", "", required));
            }
            else if (row.TryGetProperty("axis"u8, out var axis))
            {
                if (!Enum.TryParse<InputAxis>(axis.GetString(), out var parsed) || parsed == InputAxis.Count)
                    throw new InvalidDataException($"{path}: unknown axis \"{axis.GetString()}\"");
                if (_axisEntry[(int)parsed] >= 0)
                    throw new InvalidDataException($"{path}: axis {parsed} is listed twice");
                if (scope == ControlScope.None)
                    throw new InvalidDataException($"{path}: axis {parsed} needs a scope");
                var negative = Text(row, "negative"u8);
                var positive = Text(row, "positive"u8);
                if (negative.Length is 0 or > MaxText || positive.Length is 0 or > MaxText)
                    throw new InvalidDataException($"{path}: axis {parsed} needs a negative and a positive label");
                _axisEntry[(int)parsed] = entries.Count;
                entries.Add(new Entry(tab, RowKind.Axis, (int)parsed, label, "", scope, negative, positive, required));
            }
            else
            {
                var template = Text(row, "binding"u8);
                if (template.Length is 0 or > MaxText * 2)
                    throw new InvalidDataException($"{path}: \"{label}\" needs a binding text");
                entries.Add(new Entry(tab, RowKind.Fixed, -1, label, template, ControlScope.None, "", "", false));
            }
        }

        for (var i = 0; i < _actionEntry.Length; i++)
            if (_actionEntry[i] < 0)
                throw new InvalidDataException($"{path}: action {(InputAction)i} is in no tab; add it to the file");
        for (var i = 0; i < _axisEntry.Length; i++)
            if (_axisEntry[i] < 0)
                throw new InvalidDataException($"{path}: axis {(InputAxis)i} is in no tab; add it to the file");
        _entries = [.. entries];
        Refresh();
    }

    private static string Text(JsonElement element, ReadOnlySpan<byte> name) =>
        element.TryGetProperty(name, out var value) && value.GetString() is { } text ? text : "";

    /// <summary>The scope the file gives an action: the views that read it.</summary>
    public ControlScope ScopeOf(InputAction action) => _entries[_actionEntry[(int)action]].Scope;

    /// <summary>The scope the file gives an axis.</summary>
    public ControlScope ScopeOf(InputAxis axis) => _entries[_axisEntry[(int)axis]].Scope;

    /// <summary>The label the file gives an action.</summary>
    public string LabelOf(InputAction action) => _entries[_actionEntry[(int)action]].Label;

    /// <summary>The label of one half of an axis, such as "Strafe left".</summary>
    public string LabelOf(InputAxis axis, bool positive)
    {
        var entry = _entries[_axisEntry[(int)axis]];
        return positive ? entry.Positive : entry.Negative;
    }

    /// <summary>The tab an action is listed under.</summary>
    public string TabOf(InputAction action) => Tabs[_entries[_actionEntry[(int)action]].Tab].Title;

    /// <summary>The tab an axis is listed under.</summary>
    public string TabOf(InputAxis axis) => Tabs[_entries[_axisEntry[(int)axis]].Tab].Title;

    /// <summary>True when an action may never be left with no control, so the player cannot lock themselves out.</summary>
    public bool IsRequired(InputAction action) => _entries[_actionEntry[(int)action]].Required;

    /// <summary>Rebuilds the rows when a binding changed since they were built; a no-op otherwise.</summary>
    public void Refresh()
    {
        if (_version == _map.Version)
            return;
        _version = _map.Version;
        var perTab = new List<Row>[Tabs.Length];
        for (var i = 0; i < perTab.Length; i++)
            perTab[i] = [];
        foreach (var entry in _entries)
        {
            var title = Tabs[entry.Tab].Title;
            perTab[entry.Tab].Add(entry.Kind switch
            {
                RowKind.Action => new Row(title, entry.Label, Describe(_map.Bindings((InputAction)entry.Index)), RowKind.Action, entry.Index, entry.Scope, Required: entry.Required),
                RowKind.Axis => new Row(title, entry.Label, Describe(_map.Bindings((InputAxis)entry.Index)), RowKind.Axis, entry.Index, entry.Scope, entry.Negative, entry.Positive, Extra: Fixed(_map.Bindings((InputAxis)entry.Index))),
                _ => new Row(title, entry.Label, Resolve(entry.Template)),
            });
        }

        var all = new List<Row>();
        for (var i = 0; i < Tabs.Length; i++)
        {
            Tabs[i].Rows = [.. perTab[i]];
            all.AddRange(perTab[i]);
        }

        Rows = [.. all];
    }

    private static string Describe(ReadOnlySpan<InputBinding> bindings)
    {
        if (bindings.IsEmpty)
            return "Unbound";
        var names = new List<string>(bindings.Length);
        foreach (var binding in bindings)
            names.Add(binding.DisplayName);
        return string.Join(" / ", names);
    }

    /// <summary>What an axis is bound to beyond its first pair of buttons (sticks, triggers, a second pair): fixed, so shown, not edited.</summary>
    private static string Fixed(ReadOnlySpan<AxisBinding> bindings)
    {
        var names = new List<string>(bindings.Length);
        var first = true;
        foreach (var binding in bindings)
        {
            if (binding.IsPair && first)
            {
                first = false;
                continue;
            }

            names.Add(binding.DisplayName);
        }

        return string.Join(" / ", names);
    }

    private static string Describe(ReadOnlySpan<AxisBinding> bindings)
    {
        var names = new List<string>(bindings.Length);
        foreach (var binding in bindings)
            if (!binding.IsEmptyPair)
                names.Add(binding.DisplayName);
        return names.Count == 0 ? "Unbound" : string.Join(" / ", names);
    }

    /// <summary>Fills <c>{Action}</c> in a fixed row's text with what the action is bound to now, and <c>{EditorLeave}</c> with the editor's leave keys.</summary>
    private string Resolve(string template)
    {
        if (template.IndexOf('{') < 0)
            return template;
        var result = new StringBuilder(template.Length + 16);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                result.Append(template[i]);
                continue;
            }

            var end = template.IndexOf('}', i + 1);
            if (end < 0)
                throw new InvalidDataException($"controls.json: \"{template}\" has an opening brace with no closing one");
            var name = template.Substring(i + 1, end - i - 1);
            if (name == "EditorLeave")
            {
                var leave = new List<string>();
                foreach (var binding in _map.Bindings(InputAction.ToggleEditor))
                    leave.Add(binding.DisplayName);
                foreach (var binding in _map.Bindings(InputAction.Pause))
                    if (binding.Device == InputBinding.Source.Key)
                        leave.Add(binding.DisplayName);
                result.Append(leave.Count == 0 ? "Cancel button" : string.Join(" or ", leave));
            }
            else if (Enum.TryParse<InputAction>(name, out var action) && action != InputAction.Count)
            {
                var names = new List<string>();
                foreach (var binding in _map.Bindings(action))
                    names.Add(binding.DisplayName);
                result.Append(names.Count == 0 ? "(unbound)" : string.Join(" or ", names));
            }
            else
                throw new InvalidDataException($"controls.json: \"{template}\" names an unknown action \"{name}\"");
            i = end;
        }

        return result.ToString();
    }
}
