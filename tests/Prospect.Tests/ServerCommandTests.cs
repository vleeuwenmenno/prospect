using Prospect.Game.Protocol;
using Prospect.Game.World;
using Prospect.Server;
using Xunit;

namespace Prospect.Tests;

public sealed class ServerCommandTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpNeedsNoSettingsOrContent(string help)
    {
        var command = new ServerCommand();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Null(command.Parse(["--settings", "/missing/settings.json", "--password", "secret-marker", help], output, error, out var code));
        Assert.Equal(0, code);
        Assert.Contains("--max-players", output.ToString());
        Assert.Contains("PROSPECT_PASSWORD", output.ToString());
        Assert.DoesNotContain("secret-marker", output.ToString() + error);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--unknown")]
    [InlineData("--port", "0")]
    [InlineData("--port", "65536")]
    [InlineData("--port", "secret-marker")]
    [InlineData("--max-players", "0")]
    [InlineData("--max-players", "257")]
    [InlineData("--seed", "-1")]
    [InlineData("--seed", "18446744073709551616")]
    [InlineData("--visibility", "private")]
    [InlineData("--password", "secret-marker", "--password", "other-secret")]
    [InlineData("--password=secret-marker", "unexpected-secret")]
    public void BadArgumentsAreSafe(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Null(new ServerCommand().Parse(args, output, error, out var code));
        Assert.Equal(2, code);
        Assert.DoesNotContain("secret", output.ToString() + error);
    }

    [Theory]
    [InlineData(null, null, "file-password")]
    [InlineData("", null, "file-password")]
    [InlineData("env-password", null, "env-password")]
    [InlineData("env-password", "", null)]
    [InlineData("env-password", "cli-password", "cli-password")]
    public void FileAndPasswordPrecedence(string? environment, string? cli, string? expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"server":{"name":"File server","port":32123,"maxPlayers":23,"password":"file-password","listing":"off"}}""");
            var args = new List<string> { "--settings", path, "--seed", "0" };
            if (cli is not null)
                args.AddRange(["--password", cli]);
            var command = new ServerCommand();
            var result = command.Parse(args.ToArray(), TextWriter.Null, TextWriter.Null, out var code);
            Assert.Equal(0, code);
            Assert.NotNull(result);
            var config = command.BuildConfig(result, GameContent.Load(), environment, _ => { });
            Assert.Equal(expected, config.Password);
            Assert.Equal("File server", config.Name);
            Assert.Equal(32123, config.Port);
            Assert.Equal(23, config.MaxPlayers);
            Assert.Equal(0UL, config.GalaxySeed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExplicitOptionsOverrideServerFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"server":{"name":"File server","visibility":"public","port":32123,"maxPlayers":23,"listing":"https://example.com"}}""");
            var command = new ServerCommand();
            var result = command.Parse(
                ["--settings", path, "--name", "CLI server", "--visibility", "unlisted", "--port", "1", "--max-players", "1", "--listing", "off"],
                TextWriter.Null, TextWriter.Null, out var code);
            Assert.Equal(0, code);
            Assert.NotNull(result);
            var config = command.BuildConfig(result, GameContent.Load(), null, _ => { });
            Assert.Equal("CLI server", config.Name);
            Assert.Equal(ServerVisibility.Unlisted, config.Visibility);
            Assert.Equal(1, config.Port);
            Assert.Equal(1, config.MaxPlayers);
            Assert.Null(config.Listing);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RepeatAssignmentsKeepOrderAndBoundsAreInclusive()
    {
        var content = GameContent.Load();
        var setting = content.SettingCatalog.Definitions.ToArray().First(d => d.Kind == SettingKind.Multiplier);
        var command = new ServerCommand();
        var result = command.Parse(["--port", "65535", "--max-players", "256", "--seed", "18446744073709551615", "--listing", "off",
            "--set", $"{setting.Id}=0", "--set", $"{setting.Id}=999999"], TextWriter.Null, TextWriter.Null, out var code);
        Assert.Equal(0, code);
        Assert.NotNull(result);
        var config = command.BuildConfig(result, content, null, _ => { });
        Assert.Equal(ushort.MaxValue, config.Port);
        Assert.Equal(256, config.MaxPlayers);
        Assert.Equal(ulong.MaxValue, config.GalaxySeed);
        Assert.Equal(setting.Maximum, config.Settings!.Value.Get(setting));
    }
}
