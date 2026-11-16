using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Resource codes are content: capitals and digits, one to four, each its own.</summary>
public sealed class ResourceCodeTests
{
    private const string Minimal = "{ \"id\": \"{ID}\", \"name\": \"X\", \"color\": [1,1,1], \"weight\": 1, \"coreBias\": 0, \"presence\": 1, \"partners\": [] }";

    private static string Galaxy(params string[] resources)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "content", "galaxy", "galaxy.json");
        var text = File.ReadAllText(path);
        var start = text.IndexOf("\"resources\": [", StringComparison.Ordinal);
        var end = text.IndexOf("],\n  \"sectorCharacter\"", StringComparison.Ordinal);
        return text[..start] + "\"resources\": [" + string.Join(",", resources) + text[end..];
    }

    private static Galaxy Load(string json)
    {
        var folder = Directory.CreateTempSubdirectory("codes");
        try
        {
            var path = Path.Combine(folder.FullName, "galaxy.json");
            File.WriteAllText(path, json);
            return Prospect.Game.World.Galaxy.Load(path);
        }
        finally { folder.Delete(true); }
    }

    // The home sector names iron and copper, so those two are always there; only their codes vary.
    private static string Pair(string? iron, string? copper)
    {
        string One(string id, string? code) => Minimal.Replace("{ID}", id).Replace("\"name\"", (code is null ? "" : $"\"code\": \"{code}\", ") + "\"name\"");
        return Galaxy(One("iron", iron), One("copper", copper));
    }

    [Fact]
    public void ACodeIsOneToFourCapitalsOrDigitsAndUnique()
    {
        Assert.Equal(["AB", "C9"], Load(Pair("AB", "C9")).Resources.Select(r => r.Code));
        Assert.Throws<InvalidDataException>(() => Load(Pair("ab", "C9")));
        Assert.Throws<InvalidDataException>(() => Load(Pair("ABCDE", "C9")));
        Assert.Throws<InvalidDataException>(() => Load(Pair("", "C9")));
        Assert.Throws<InvalidDataException>(() => Load(Pair("AB", "AB")));
    }

    [Fact]
    public void ContentThatNamesNoCodeGetsTheFirstTwoLettersOfTheId()
    {
        Assert.Equal(["IR", "CO"], Load(Pair(null, null)).Resources.Select(r => r.Code));
    }
}
