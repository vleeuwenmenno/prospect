using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The course menu's wording and rows, shared by the command view's right-click menu, the map's course menu and
/// the pinned sector card. All three read one <see cref="JumpPlan"/> per frame (the server's own range and risk
/// function, per selected ship) and one set of rows, so they cannot say different things about the same jump.
/// </summary>
internal sealed partial class ScreenHost
{
    /// <summary>
    /// A figure, a word and a second figure in one line: <c>{lead}  {head}, {tail} {figure}</c> with the parts
    /// that are empty left out. The figures are built before this call, since a line cannot be built inside another.
    /// </summary>
    private static ReadOnlySpan<byte> Words(ReadOnlySpan<byte> lead, string head, string tail, ReadOnlySpan<byte> figure)
    {
        if (lead.IsEmpty)
            return figure.IsEmpty ? Kit.F($"{head}") : Kit.F($"{head} {tail} {figure}");
        if (tail.Length == 0)
            return Kit.F($"{head}  {lead} AWAY");
        return figure.IsEmpty ? Kit.F($"{head}  {lead}, {tail}") : Kit.F($"{head}  {lead}, {tail} {figure}");
    }

    /// <summary>The line about the jump itself: charted or blind, its distance against the drive's range, and the blind risk.</summary>
    private ReadOnlySpan<byte> JumpSummary(SessionSnapshot snapshot, in JumpPlan plan, out uint color)
    {
        var rules = snapshot.Content.Exploration;
        var figures = plan.Reference;
        color = Theme.TextDim;
        if (!plan.HasReference)
        {
            color = Theme.TextMute;
            return "NO SHIP SELECTED"u8;
        }

        switch (plan.Block)
        {
            case JumpBlock.NoDrive:
                color = Theme.Bad;
                return "NO WORKING JUMP DRIVE"u8;
            case JumpBlock.Busy:
                color = Theme.TextMute;
                return "SHIP IN TRANSIT, WAIT FOR ARRIVAL"u8;
            case JumpBlock.TooClose:
                color = Theme.Bad;
                return Words(Distance((float)figures.Distance), "TOO CLOSE", "MINIMUM", Distance((float)rules.JumpMinimum));
            case JumpBlock.TooFar:
                color = Theme.Bad;
                return Words(Distance((float)figures.Distance), "TOO FAR", "MAXIMUM", Distance(plan.MaxDistance));
            case JumpBlock.Invalid:
                color = Theme.Bad;
                return "NOT A VALID DESTINATION"u8;
            case JumpBlock.SameSector:
                color = Theme.TextMute;
                return Words(Distance((float)figures.Distance), "SAME SECTOR", "", default);
        }

        // A destination the drive can reach: what is known of it, and what a blind arrival could cost.
        if (figures.Charted)
        {
            color = Theme.Info;
            return Words(Distance((float)figures.Distance), "CHARTED", "NO RISK", default);
        }

        color = Theme.Accent;
        if (figures.Risk <= 0f)
            return Words(Distance((float)figures.Distance), "BLIND", "SAFE UP TO", Distance((float)rules.BlindSafeDistance));
        if (figures.Risk >= MapLook.CourseRiskDanger)
            color = Theme.Bad;
        return Kit.F($"BLIND  {figures.Risk * 100f:0}% CHANCE, {figures.MaximumDamageFraction * 100f:0}% DAMAGE");
    }

    /// <summary>The server's refusal of a plot or a jump start, in words.</summary>
    private ReadOnlySpan<byte> RefusalWords(SessionSnapshot snapshot, JumpRefusal refusal) =>
        refusal switch
        {
            JumpRefusal.NoDrive => "NO WORKING JUMP DRIVE"u8,
            JumpRefusal.Busy => "SHIP IN TRANSIT"u8,
            JumpRefusal.TooClose => Words(default, "TOO CLOSE,", "MINIMUM", Distance((float)snapshot.Content.Exploration.JumpMinimum)),
            JumpRefusal.TooFar => Words(default, "TOO FAR,", "MAXIMUM", Distance(snapshot.Flight.FtlMaxDistance)),
            JumpRefusal.InvalidTarget => "NOT A VALID DESTINATION"u8,
            _ => default,
        };

    /// <summary>The jump row's second line: who does what if it is chosen, or why it cannot be.</summary>
    private ReadOnlySpan<byte> JumpRowDetail(SessionSnapshot snapshot, WorldView world, in JumpPlan plan)
    {
        if (plan.Ready > 0)
        {
            if (plan.PlotsOnly)
                return Kit.F($"YOU PLOT, THEN AIM AND {world.BoostKeyName}");
            if (plan.Mixed)
                return Kit.F($"YOU PLOT, {plan.ReadyRemote} {(plan.ReadyRemote == 1 ? "SHIP JUMPS" : "SHIPS JUMP")} ALONE");
            return plan.ReadyRemote == 1 ? "SHIP JUMPS BY ITSELF"u8 : "SHIPS JUMP BY THEMSELVES"u8;
        }

        var rules = snapshot.Content.Exploration;
        return plan.Block switch
        {
            JumpBlock.NoShips => "NO SHIPS SELECTED"u8,
            JumpBlock.NoDrive => "NO WORKING JUMP DRIVE"u8,
            JumpBlock.Busy => "SHIP IN TRANSIT"u8,
            JumpBlock.SameSector => "ALREADY IN THIS SECTOR"u8,
            JumpBlock.TooClose => Words(default, "TOO CLOSE:", "MINIMUM", Distance((float)rules.JumpMinimum)),
            JumpBlock.TooFar => Words(default, "TOO FAR:", "MAXIMUM", Distance(plan.MaxDistance)),
            _ => "NOT A VALID DESTINATION"u8,
        };
    }

    /// <summary>
    /// The two rows every course menu has, and the pinned card with them: move the selection here, or plot or
    /// order the jump. A jump row is a plot ("Plot jump") when only the ship being flown can go, since its pilot
    /// starts the jump with NAV and boost, and an order ("Jump here") when ships nobody is flying can go, since
    /// they jump by themselves. Out of range, too close or without a drive, the row says why and takes no click.
    /// </summary>
    private bool DrawCourseRows(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, Double3 target, float2 min, float2 rowSize)
    {
        var plan = world.PlanJump(target);
        var session = _app.Session;
        var acted = false;
        var row = MathF.Round(MapCourseMenu.RowHeight * Kit.Scale);
        var first = min + new float2(Kit.Px(6), Kit.Px(Theme.Space1));
        var ships = plan.Ships;
        var none = ships == 0;
        var moveCount = ships == 1 ? "1 ship"u8 : Kit.F($"{ships} ships");
        if (MapMenuItem(draw, first, rowSize, "Move here"u8, none ? "NO SHIPS SELECTED"u8 : "UNDER OWN ENGINES"u8, moveCount, !none) && session is not null)
        {
            world.OrderMoveSelected(session.Client.Input, target);
            acted = true;
        }

        var canJump = plan.Ready > 0;
        var plots = plan.IsPlot(world.JumpPlotsOwnOnly);
        var jumpCount = ships == 1 ? Kit.F($"{plan.Ready} of 1 ship") : Kit.F($"{plan.Ready} of {ships} ships");
        if (MapMenuItem(draw, first + new float2(0, row), rowSize, plots ? "Plot jump"u8 : "Jump here"u8, JumpRowDetail(snapshot, world, plan), jumpCount, canJump) && session is not null)
        {
            world.OrderJump(session.Client.Input, target);
            acted = true;
        }

        return acted;
    }

    /// <summary>
    /// The menu's head: what the point is, how far from the ship the figures are about, and the jump line. The
    /// map's menu and the command view's draw it the same way.
    /// </summary>
    private void DrawCourseHeader(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, Galaxy galaxy, Double3 target, in JumpPlan plan, float2 min, float2 size)
    {
        var sector = galaxy.SectorOf(target);
        var known = Charted(sector);
        var text = min + new float2(Kit.Px(Theme.Space4), Kit.Px(Theme.Space3));
        var inner = size.X - Kit.Px(Theme.Space4) * 2;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, text, Theme.Accent, "SET COURSE"u8);
        var coords = Kit.F($"{sector.X} : {sector.Z}   LAYER {sector.Y:+0;-0;0}");
        var coordsWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, coords).X;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(min.X + size.X - Kit.Px(Theme.Space4) - coordsWidth, text.Y), Theme.TextMute, coords);
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontBody, text + new float2(0, Kit.Px(20)), known ? Theme.Text : Theme.TextDim, known ? MapName(galaxy, sector) : "Uncharted sector"u8, inner);
        var range = plan.HasReference ? (float)plan.Reference.Distance : target.RelativeTo(world.OwnPosition).Length();
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(46)), Theme.TextDim, Kit.F($"DISTANCE   {range / 1000f:0.0} KM"));
        var summary = JumpSummary(snapshot, plan, out var color);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(64)), color, summary, inner);
        var divider = min.Y + Kit.Px(MapCourseMenu.HeaderHeight) - 0.5f;
        draw.AddLine(new float2(min.X + Kit.Px(6), divider), new float2(min.X + size.X - Kit.Px(6), divider), Kit.Color(Theme.Line));
    }

    /// <summary>
    /// The command view's menu, opened by a right click on a point of the plane: where it is, the jump line,
    /// then Move here and the jump row. A click on a row acts and closes it; Escape, or a click anywhere else,
    /// closes it without ordering anything (the world and <c>HandleBack</c> do that).
    /// </summary>
    private void DrawCommandCourseMenu(SessionSnapshot snapshot, WorldView world, float alpha)
    {
        var menu = world.CourseMenu;
        var galaxy = world.Galaxy;
        if (!menu.IsOpen || galaxy is null)
            return;
        var target = menu.Target;
        var size = MapCourseMenu.Size(Kit.Scale);
        var placed = MapCourseMenu.Place(menu.At, size, _ui.DisplaySize, Kit.Scale);
        var since = world.Time - menu.OpenedAt;
        var appear = MapCourseMenu.Appear(since);
        var min = MapCourseMenu.Shown(placed, since, Kit.Scale);
        MapCourseMenu.BeginFree("##coursemenu"u8, min, size);
        var previousFade = Kit.Fade;
        Kit.Fade = alpha * appear;
        var draw = ImGui.GetWindowDrawList();
        Kit.PanelFrame(draw, min, min + size);
        var plan = world.PlanJump(target);
        DrawCourseHeader(draw, snapshot, world, galaxy, target, plan, min, size);
        var acted = DrawCourseRows(draw, snapshot, world, target, new float2(min.X, min.Y + MathF.Round(MapCourseMenu.HeaderHeight * Kit.Scale)), MapCourseMenu.RowSize(size, Kit.Scale));
        MapCourseMenu.End();
        Kit.Fade = previousFade;
        if (acted)
            menu.Close();
    }

    /// <summary>The point the open course menu is about, marked in the command view, with how far it is.</summary>
    private void DrawCourseMenuTarget(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world)
    {
        var menu = world.CourseMenu;
        if (!menu.IsOpen || !world.Project(menu.Target, out var screen))
            return;
        DrawTargetMarker(draw, screen, Theme.Accent);
    }

    /// <summary>
    /// The server's refusal, as a line under the command view's status while it is news: the plot or the jump
    /// the player's own ship was refused, with the reason.
    /// </summary>
    private void DrawCommandRefusal(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float2 top)
    {
        if (world.NavRefusal == JumpRefusal.None || world.Time - world.NavRefusalAt >= MapLook.CourseRefusalSeconds)
            return;
        var reason = RefusalWords(snapshot, world.NavRefusal);
        var words = Kit.F($"JUMP REFUSED: {reason}");
        var size = Kit.Measure(_ui.Mono, Theme.FontCaption, words);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, top + new float2(-size.X * 0.5f, Kit.Px(38)), Theme.Bad, words);
    }
}
