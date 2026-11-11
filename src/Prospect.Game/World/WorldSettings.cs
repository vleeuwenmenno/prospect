using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Prospect.Engine.Net;

namespace Prospect.Game.World;

/// <summary>What sort of value a world setting holds.</summary>
public enum SettingKind : byte
{
    /// <summary>A number between a minimum and a maximum that scales something.</summary>
    Multiplier,

    /// <summary>One of a few named options.</summary>
    Choice,

    /// <summary>On or off.</summary>
    Switch,
}

/// <summary>
/// One world setting as <c>content/rules/settings.json</c> describes it: what it is called, what values it can
/// take and what it starts at. A setting's value is always a single number: the multiplier itself, the position
/// of the chosen option, or 1 for on and 0 for off.
/// </summary>
public sealed class SettingDefinition
{
    internal SettingDefinition() { }

    /// <summary>Where this setting's value sits in a <see cref="WorldSettings"/>.</summary>
    public int Index { get; internal init; }

    /// <summary>The name used in files, on the command line and on the wire. Letters and digits only.</summary>
    public string Id { get; internal init; } = "";

    /// <summary>The name and the one-line explanation shown to a player.</summary>
    public string Name { get; internal init; } = "";
    public string Description { get; internal init; } = "";
    public SettingKind Kind { get; internal init; }

    /// <summary>The value a world has unless it is set otherwise.</summary>
    public float Default { get; internal init; }

    /// <summary>The range of a multiplier, and the distance between the values a slider offers.</summary>
    public float Minimum { get; internal init; }
    public float Maximum { get; internal init; }
    public float Step { get; internal init; }

    /// <summary>The names of a choice's options, in the order their positions count.</summary>
    public IReadOnlyList<string> Options => OptionNames;

    internal string[] OptionNames { get; init; } = [];

    /// <summary>The number each option of a choice stands for where the setting is applied.</summary>
    internal float[] OptionNumbers { get; init; } = [];

    /// <summary>
    /// Brings any number to the nearest value the setting can have. Every way a value comes in goes through
    /// here, a file, the command line or the network, so a stored value is always one the game can use.
    /// </summary>
    public float Clamp(float value)
    {
        if (float.IsNaN(value))
            return Default;

        return Kind switch
        {
            SettingKind.Multiplier => Math.Clamp(value, Minimum, Maximum),
            SettingKind.Choice => Math.Clamp(MathF.Round(value), 0f, OptionNames.Length - 1),
            _ => value >= 0.5f ? 1f : 0f,
        };
    }

    /// <summary>
    /// The number the game works with for a stored value: the multiplier, the number the chosen option stands
    /// for, or 1 and 0 for on and off.
    /// </summary>
    public float Number(float value)
    {
        var clean = Clamp(value);
        return Kind == SettingKind.Choice ? OptionNumbers[(int)clean] : clean;
    }

    /// <summary>
    /// Reads a value as a person writes it: a number, the name of an option, or on and off. The result may still
    /// be out of range; <see cref="Clamp"/> settles that.
    /// </summary>
    public bool TryParse(ReadOnlySpan<char> text, out float value)
    {
        text = text.Trim();
        value = 0;
        switch (Kind)
        {
            case SettingKind.Multiplier:
                return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

            case SettingKind.Choice:
                for (var i = 0; i < OptionNames.Length; i++)
                {
                    if (!text.Equals(OptionNames[i], StringComparison.OrdinalIgnoreCase))
                        continue;
                    value = i;
                    return true;
                }

                return false;

            default:
                if (text.Equals("on", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase) || text is "1")
                {
                    value = 1;
                    return true;
                }

                return text.Equals("off", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase) || text is "0";
        }
    }

    /// <summary>A stored value as text, for a log line or a label. Allocates: not for use every frame.</summary>
    public string Format(float value)
    {
        var clean = Clamp(value);
        return Kind switch
        {
            SettingKind.Multiplier => clean.ToString("0.##", CultureInfo.InvariantCulture),
            SettingKind.Choice => OptionNames[(int)clean],
            _ => clean != 0 ? "on" : "off",
        };
    }

    /// <summary>What a value for this setting has to be, for an error message.</summary>
    internal string Expects =>
        Kind switch
        {
            SettingKind.Multiplier => string.Create(CultureInfo.InvariantCulture, $"a number from {Minimum:0.##} to {Maximum:0.##}"),
            SettingKind.Choice => "one of " + string.Join(", ", OptionNames),
            _ => "on or off",
        };
}

/// <summary>
/// The settings of one world: a value for every setting in the <see cref="WorldSettingCatalog"/> it was made
/// from. A plain block of numbers, so it is cheap to copy, needs no memory of its own and is safe to send. The
/// server owns a world's settings for as long as it runs; a client is given them on joining.
/// </summary>
public struct WorldSettings : IEquatable<WorldSettings>
{
    /// <summary>The most settings a world can have.</summary>
    public const int Capacity = 32;

    [InlineArray(Capacity)]
    private struct Slots
    {
        private float _first;
    }

    private Slots _values;

    /// <summary>The stored value of a setting: the multiplier, the position of the option, or 1 and 0.</summary>
    public readonly float Get(SettingDefinition setting) => _values[setting.Index];

    /// <summary>Gives a setting a value, brought into the range the setting allows.</summary>
    public void Set(SettingDefinition setting, float value) => _values[setting.Index] = setting.Clamp(value);

    public readonly bool Equals(WorldSettings other)
    {
        ReadOnlySpan<float> mine = _values;
        ReadOnlySpan<float> theirs = other._values;
        return mine.SequenceEqual(theirs);
    }

    public override readonly bool Equals(object? obj) => obj is WorldSettings other && Equals(other);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        ReadOnlySpan<float> values = _values;
        foreach (var value in values)
            hash.Add(value);
        return hash.ToHashCode();
    }

    public static bool operator ==(WorldSettings left, WorldSettings right) => left.Equals(right);

    public static bool operator !=(WorldSettings left, WorldSettings right) => !left.Equals(right);
}

/// <summary>
/// Every world setting the game has, loaded from <c>content/rules/settings.json</c>. It is the one place values
/// come in through, from a file, the command line or the network: an id it does not know is ignored and a value
/// out of range is brought into range, so a <see cref="WorldSettings"/> that came through here is always usable.
/// </summary>
public sealed class WorldSettingCatalog
{
    /// <summary>The longest id a setting may have, in characters.</summary>
    public const int MaxIdLength = 32;

    /// <summary>The most options a choice may have.</summary>
    public const int MaxOptions = 16;

    /// <summary>The most bytes a world's settings take on the wire: a count, then an id and a value for each.</summary>
    public const int MaxWireSize = 1 + WorldSettings.Capacity * (1 + MaxIdLength + 4);

    private readonly SettingDefinition[] _definitions;
    private readonly string _source;

    private WorldSettingCatalog(SettingDefinition[] definitions, string source)
    {
        _definitions = definitions;
        _source = source;
        var defaults = default(WorldSettings);
        foreach (var definition in definitions)
            defaults.Set(definition, definition.Default);
        Defaults = defaults;
    }

    /// <summary>Every setting, in the order the file lists them.</summary>
    public ReadOnlySpan<SettingDefinition> Definitions => _definitions;

    /// <summary>The settings of a world nobody has changed anything about.</summary>
    public WorldSettings Defaults { get; }

    /// <summary>The setting with an id, or null if there is none. Upper and lower case are not told apart.</summary>
    public SettingDefinition? Find(ReadOnlySpan<char> id)
    {
        foreach (var definition in _definitions)
        {
            if (id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase))
                return definition;
        }

        return null;
    }

    /// <summary>
    /// The number the game works with for a setting it applies. A setting the game applies has to be in the
    /// file: without it the content is broken, and this says so.
    /// </summary>
    public float Number(in WorldSettings settings, string id)
    {
        var definition = Find(id) ?? throw new InvalidDataException($"{_source}: the game applies the setting \"{id}\", which is not defined");
        return definition.Number(settings.Get(definition));
    }

    /// <summary>Brings every value into the range its setting allows, and clears whatever belongs to no setting.</summary>
    public WorldSettings Sanitize(in WorldSettings settings)
    {
        var clean = default(WorldSettings);
        foreach (var definition in _definitions)
            clean.Set(definition, settings.Get(definition));
        return clean;
    }

    /// <summary>
    /// Works out a world's settings from what an operator or a player asked for: the defaults, then a file of
    /// values if there is one, then assignments of the form <c>id=value</c>, later ones winning. An id nobody
    /// knows is ignored and a value out of range is brought into range, both with a warning. A file that cannot
    /// be read or a value that is not of the setting's kind throws, with a message that says what is wrong.
    /// </summary>
    public WorldSettings Build(string? valuesFile, IReadOnlyList<string> assignments, Action<string>? warn = null)
    {
        var settings = Defaults;
        if (valuesFile is not null)
            ReadFile(ref settings, valuesFile, warn);
        foreach (var assignment in assignments)
            Assign(ref settings, assignment, warn);
        return settings;
    }

    /// <summary>Applies one <c>id=value</c> assignment, as given with <c>--set</c> on a command line.</summary>
    public void Assign(ref WorldSettings settings, ReadOnlySpan<char> assignment, Action<string>? warn = null)
    {
        var split = assignment.IndexOf('=');
        if (split <= 0)
            throw new InvalidDataException($"World setting \"{assignment}\": expected id=value");

        var id = assignment[..split].Trim();
        var text = assignment[(split + 1)..];
        var definition = Find(id);
        if (definition is null)
        {
            warn?.Invoke($"Unknown world setting \"{id}\" ignored");
            return;
        }

        if (!definition.TryParse(text, out var value))
            throw new InvalidDataException($"World setting {definition.Id}: \"{text}\" is not {definition.Expects}");
        Store(ref settings, definition, value, warn);
    }

    /// <summary>
    /// Applies a file of values: one JSON object with a setting's id as each name, and a number, the name of an
    /// option, or true or false as its value.
    /// </summary>
    public void ReadFile(ref WorldSettings settings, string path, Action<string>? warn = null)
    {
        using var document = ParseJson(File.ReadAllBytes(path), path);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: expected one object with a setting's id and its value on each line");

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var definition = Find(entry.Name);
            if (definition is null)
            {
                warn?.Invoke($"{path}: unknown world setting \"{entry.Name}\" ignored");
                continue;
            }

            var value = entry.Value;
            var ok = definition.Kind switch
            {
                SettingKind.Multiplier => value.ValueKind == JsonValueKind.Number,
                SettingKind.Choice => value.ValueKind == JsonValueKind.String,
                _ => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            };
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            if (!ok || !definition.TryParse(text, out var number))
                throw new InvalidDataException($"{path}: {definition.Id} is {value.GetRawText()}, which is not {definition.Expects}");
            Store(ref settings, definition, number, warn);
        }
    }

    private static void Store(ref WorldSettings settings, SettingDefinition definition, float value, Action<string>? warn)
    {
        settings.Set(definition, value);
        if (settings.Get(definition) != value)
            warn?.Invoke($"World setting {definition.Id}: {value.ToString("0.###", CultureInfo.InvariantCulture)} is not {definition.Expects}; using {definition.Format(value)}");
    }

    /// <summary>Writes a world's settings as the welcome message carries them: each id with its value.</summary>
    public void Write(ref NetWriter writer, in WorldSettings settings)
    {
        writer.WriteByte((byte)_definitions.Length);
        foreach (var definition in _definitions)
        {
            writer.WriteString(definition.Id);
            writer.WriteSingle(settings.Get(definition));
        }
    }

    /// <summary>
    /// Reads a world's settings from the network. It starts from the defaults, so whatever the message leaves out
    /// or gets wrong still has a usable value; an id this game does not know is skipped. It never throws: a
    /// message that ends early clears the reader's <see cref="NetReader.IsValid"/>, as every read does.
    /// </summary>
    public WorldSettings Read(ref NetReader reader)
    {
        var settings = Defaults;
        Span<char> id = stackalloc char[MaxIdLength];
        var count = reader.ReadByte();
        for (var i = 0; i < count && reader.IsValid; i++)
        {
            var length = reader.ReadString(id, MaxIdLength);
            var value = reader.ReadSingle();
            if (reader.IsValid && Find(id[..length]) is { } definition)
                settings.Set(definition, value);
        }

        return settings;
    }

    public static WorldSettingCatalog Load(string path) => Parse(File.ReadAllBytes(path), path);

    /// <summary>Reads the list of settings. <paramref name="source"/> names where it came from in error messages.</summary>
    public static WorldSettingCatalog Parse(ReadOnlyMemory<byte> json, string source)
    {
        using var document = ParseJson(json, source);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("settings"u8, out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{source}: needs a \"settings\" list");
        if (list.GetArrayLength() > WorldSettings.Capacity)
            throw new InvalidDataException($"{source}: {list.GetArrayLength()} settings; a world can have at most {WorldSettings.Capacity}");

        var definitions = new SettingDefinition[list.GetArrayLength()];
        var count = 0;
        foreach (var entry in list.EnumerateArray())
        {
            definitions[count] = ParseDefinition(entry, count, source, definitions.AsSpan(0, count));
            count++;
        }

        return new WorldSettingCatalog(definitions, source);
    }

    private static JsonDocument ParseJson(ReadOnlyMemory<byte> json, string source)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{source}: not valid JSON: {e.Message}", e);
        }
    }

    private static SettingDefinition ParseDefinition(JsonElement entry, int index, string source, ReadOnlySpan<SettingDefinition> earlier)
    {
        var where = $"{source}: setting {index + 1}";
        if (entry.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{where} must be an object");

        var id = RequiredText(entry, "id", where);
        if (id.Length > MaxIdLength || id.AsSpan().ContainsAnyExcept(IdCharacters))
            throw new InvalidDataException($"{where}: the id \"{id}\" must be 1 to {MaxIdLength} letters and digits");

        where = $"{source}: setting \"{id}\"";
        foreach (var other in earlier)
        {
            if (other.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{where} is defined twice");
        }

        var name = RequiredText(entry, "name", where);
        var description = RequiredText(entry, "description", where);
        switch (RequiredText(entry, "kind", where))
        {
            case "multiplier":
                {
                    var minimum = RequiredNumber(entry, "minimum", where);
                    var maximum = RequiredNumber(entry, "maximum", where);
                    var step = RequiredNumber(entry, "step", where);
                    var initial = RequiredNumber(entry, "default", where);
                    if (minimum > maximum || initial < minimum || initial > maximum || step <= 0)
                        throw new InvalidDataException($"{where}: needs minimum <= default <= maximum and a step above 0");

                    return new SettingDefinition
                    {
                        Index = index,
                        Id = id,
                        Name = name,
                        Description = description,
                        Kind = SettingKind.Multiplier,
                        Default = initial,
                        Minimum = minimum,
                        Maximum = maximum,
                        Step = step,
                    };
                }

            case "choice":
                {
                    if (!entry.TryGetProperty("options"u8, out var options) || options.ValueKind != JsonValueKind.Array || options.GetArrayLength() is 0 or > MaxOptions)
                        throw new InvalidDataException($"{where}: needs a list of 1 to {MaxOptions} options");

                    var names = new string[options.GetArrayLength()];
                    var numbers = new float[names.Length];
                    var count = 0;
                    foreach (var option in options.EnumerateArray())
                    {
                        if (option.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException($"{where}: option {count + 1} must be an object with a name");

                        // An option stands for its own position unless the file gives it a number to stand for.
                        names[count] = RequiredText(option, "name", $"{where}, option {count + 1}");
                        numbers[count] = option.TryGetProperty("value"u8, out _) ? RequiredNumber(option, "value", $"{where}, option \"{names[count]}\"") : count;
                        if (Array.FindIndex(names, 0, count, n => n.Equals(names[count], StringComparison.OrdinalIgnoreCase)) >= 0)
                            throw new InvalidDataException($"{where}: the option \"{names[count]}\" is listed twice");
                        count++;
                    }

                    var chosen = RequiredText(entry, "default", where);
                    var initial = Array.FindIndex(names, n => n.Equals(chosen, StringComparison.OrdinalIgnoreCase));
                    if (initial < 0)
                        throw new InvalidDataException($"{where}: the default \"{chosen}\" is not one of its options");

                    return new SettingDefinition
                    {
                        Index = index,
                        Id = id,
                        Name = name,
                        Description = description,
                        Kind = SettingKind.Choice,
                        Default = initial,
                        OptionNames = names,
                        OptionNumbers = numbers,
                    };
                }

            case "switch":
                {
                    if (!entry.TryGetProperty("default"u8, out var initial) || initial.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException($"{where}: needs a default of true or false");

                    return new SettingDefinition
                    {
                        Index = index,
                        Id = id,
                        Name = name,
                        Description = description,
                        Kind = SettingKind.Switch,
                        Default = initial.GetBoolean() ? 1f : 0f,
                    };
                }

            case var kind:
                throw new InvalidDataException($"{where}: unknown kind \"{kind}\"; expected multiplier, choice or switch");
        }
    }

    private static readonly System.Buffers.SearchValues<char> IdCharacters = System.Buffers.SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
    );

    private static string RequiredText(JsonElement entry, string name, string where)
    {
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"{where}: needs \"{name}\", a piece of text");
        return value.GetString()!;
    }

    private static float RequiredNumber(JsonElement entry, string name, string where)
    {
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var number) || !float.IsFinite(number))
            throw new InvalidDataException($"{where}: needs \"{name}\", a number");
        return number;
    }
}
