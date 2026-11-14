using Brutal.Numerics;
using Prospect.Client.Screens;
using Prospect.Client.Ui;
using Prospect.Game.Protocol;
using Xunit;

namespace Prospect.Tests;

/// <summary>The New ship dialog's rules apart from drawing: its name check, its panel and where its tip goes. The keys and the panel's content, with Dear ImGui, are in <see cref="FrameOrderTests"/>.</summary>
public sealed class NewShipDialogTests
{
    [Theory]
    [InlineData("", (int)NameProblem.Empty)]
    [InlineData("   ", (int)NameProblem.Empty)]
    [InlineData("Scout", (int)NameProblem.None)]
    [InlineData("  Scout  ", (int)NameProblem.None)]
    [InlineData("12345678901234567890123456789012", (int)NameProblem.None)]
    [InlineData("123456789012345678901234567890123", (int)NameProblem.TooLong)]
    [InlineData("tab\there", (int)NameProblem.BadCharacter)]
    [InlineData("line\nbreak", (int)NameProblem.BadCharacter)]
    public void ANameIsCheckedWithTheServersRules(string name, int expected) =>
        Assert.Equal((NameProblem)expected, NewShipDialog.Check(name));

    [Fact]
    public void ACharacterThatTakesTwoUnitsCountsOnceAndAnUnpairedHalfIsRefused()
    {
        // 32 emoji are 64 UTF-16 units and still 32 characters; a 33rd is too long.
        var thirtyTwo = string.Concat(Enumerable.Repeat("\U0001F680", 32));
        Assert.Equal(NameProblem.None, NewShipDialog.Check(thirtyTwo));
        Assert.Equal(NameProblem.TooLong, NewShipDialog.Check(thirtyTwo + "\U0001F680"));
        Assert.Equal(NameProblem.BadCharacter, NewShipDialog.Check("half\ud83d"));
    }

    [Fact]
    public void TheFieldsBytesAreDecodedAndCheckedWithoutBeingLongerThanItsBuffer()
    {
        Span<char> chars = stackalloc char[NewShipDialog.NameBytes];
        Assert.Equal(NameProblem.None, NewShipDialog.Check("Scout"u8, chars, out var length));
        Assert.Equal("Scout", new string(chars[..length]));
        Assert.Equal(NameProblem.Empty, NewShipDialog.Check(default, chars, out _));
        Assert.Equal(NameProblem.TooLong, NewShipDialog.Check(Enumerable.Repeat((byte)'a', 200).ToArray(), chars, out _));
    }

    [Theory]
    [InlineData(1280f, 720f, 1f)]
    [InlineData(1280f, 720f, 2f)]
    [InlineData(1655f, 688f, 2f)]
    [InlineData(800f, 480f, 2f)]
    public void ThePanelStaysOnScreenUnderTheBarAndTheTipFitsBesideItWhereTheDataPutsIt(float width, float height, float scale)
    {
        var display = new float2(width, height);
        var panel = NewShipDialog.Panel(display, scale);
        Assert.True(panel.Left >= 0 && panel.Right <= width, $"{panel}");
        Assert.True(panel.Top >= HudLayout.TopBarBottom(scale) && panel.Bottom <= height, $"{panel}");

        var catalogue = TipCatalogue.Load(Path.Combine(AppContext.BaseDirectory, "content", "visuals", "tips.json"), Prospect.Client.Input.ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json")));
        var tip = catalogue.Tips.Single(t => t.Id == "drone-edit");
        Assert.Equal(TipRegion.NewShipDialog, tip.Anchor.Region);
        var size = new float2(MathF.Round(320 * scale), MathF.Round(110 * scale));
        var at = TipPlacement.Place(tip.Anchor, true, panel, size, display, scale);
        var rect = new HudRect(at.X, at.Y, at.X + size.X, at.Y + size.Y);
        Assert.True(rect.Left >= 0 && rect.Right <= width && rect.Bottom <= height, $"{rect}");
        // Where the screen has room the tip sits clear of the dialog; at a doubled scale in a short window it cannot, and stays on screen.
        if (scale == 1f && height >= 688)
            Assert.False(rect.Overlaps(panel), $"tip {rect} over dialog {panel}");
    }

    [Fact]
    public void TheDialogOpensOnlyOnceCancelsOnlyWhileNamingAndResets()
    {
        var dialog = new NewShipDialog();
        Assert.False(dialog.Open);
        dialog.Begin();
        Assert.Equal(NewShipStage.Naming, dialog.Stage);
        dialog.BeginRefused(EditorReason.DuplicateName);
        Assert.Equal(EditorReason.DuplicateName, dialog.Refusal);
        Assert.True(dialog.Cancel());
        Assert.False(dialog.Open);
        Assert.Equal(EditorReason.None, dialog.Refusal);
        Assert.False(dialog.Cancel());
    }
}
