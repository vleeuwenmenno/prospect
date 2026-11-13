using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.Persistence;
using Prospect.Game.Protocol;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    /// <summary>Why the last Load World attempt failed, shown under the list until the next one.</summary>
    private string _loadError = "";

    /// <summary>Why Continue failed, shown under the main menu until the next choice.</summary>
    private string _menuError = "";

    /// <summary>Opens the list of saved worlds and reads it again.</summary>
    public void OpenLoadWorld()
    {
        _loadError = "";
        Show(Screen.LoadWorld);
    }

    /// <summary>Shows why a world from the list did not open, on the list.</summary>
    public void ShowLoadError(string error)
    {
        Show(Screen.LoadWorld);
        _loadError = error;
    }

    /// <summary>Shows why Continue did not open the last world, on the main menu.</summary>
    public void ShowMenuError(string error)
    {
        Show(Screen.MainMenu);
        _menuError = error;
    }

    /// <summary>The list of saved worlds: each one with when it was saved, and why it cannot be opened when it cannot.</summary>
    private void DrawLoadWorld(float fade)
    {
        BeginRoot("##loadworld"u8);
        var worlds = _app.Worlds;
        var width = Math.Min(680, _ui.DisplaySize.X / Kit.Scale - 32);
        var size = new float2(Kit.Px(width), Math.Min(Kit.Px(900), _ui.DisplaySize.Y * 0.92f - Kit.Px(72)));
        var min = new float2(Math.Min(Kit.Px(96), Math.Max(Kit.Px(8), (_ui.DisplaySize.X - size.X) * 0.5f)), _ui.DisplaySize.Y * 0.06f + Slide(fade));
        BeginPanel("##loadworldpanel"u8, min, size);

        if (Compact)
        {
            Kit.Caption("LOCAL GAME  /  LOAD WORLD"u8, Theme.Accent);
            Kit.Space(Theme.Space3);
        }
        else
        {
            Kit.Caption("LOCAL GAME"u8, Theme.Accent);
            Kit.Space(Theme.Space1);
            Kit.Heading("Load World"u8);
            Kit.Space(Theme.Space2);
            Kit.Body("Worlds save themselves while you play and when you leave."u8);
            Kit.Space(Theme.Space4);
        }

        var inner = ImGui.GetContentRegionAvail().X;
        var errorHeight = 0f;
        var error = _loadError.Length > 0 ? _loadError : worlds.List.Error ?? "";
        if (error.Length > 0)
        {
            ImGui.PushFont(_ui.Sans, Theme.FontSmall);
            errorHeight = ImGui.CalcTextSize(error, false, inner).Y + Kit.Px(Theme.Space2);
            ImGui.PopFont();
        }

        var footer = Kit.Px(Theme.ControlHeight + Theme.Space4) + errorHeight;
        var listHeight = Math.Max(Kit.Px(60), ImGui.GetContentRegionAvail().Y - footer);
        ImGui.BeginChild("##worldlist"u8, new float2(0, listHeight), ImGuiChildFlags.NavFlattened, ImGuiWindowFlags.NoBackground);
        if (worlds.Library is null)
            Kit.Paragraph(_ui.Sans, Theme.FontBody, Theme.TextDim, "Saved worlds are off for this run."u8, inner / Kit.Scale);
        else if (worlds.Loading)
            Kit.Paragraph(_ui.Sans, Theme.FontBody, Theme.TextDim, "Reading saved worlds..."u8, inner / Kit.Scale);
        else if (worlds.List.Worlds.Count == 0)
            Kit.Paragraph(_ui.Sans, Theme.FontBody, Theme.TextDim, "No saved worlds yet. Start one with New Game."u8, inner / Kit.Scale);
        else
        {
            var list = worlds.List.Worlds;
            for (var i = 0; i < list.Count; i++)
            {
                if (WorldRow(list[i], i, list[i].FolderId == worlds.List.LastWorld))
                {
                    _loadError = "";
                    _app.LoadWorld(list[i]);
                }
            }
        }

        ImGui.EndChild();

        Kit.Space(Theme.Space4);
        if (error.Length > 0)
        {
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Bad, error, inner / Kit.Scale);
            Kit.Space(Theme.Space2);
        }

        if (Kit.Button("Back"u8, ButtonKind.Secondary, 120) || BackPressed())
            Show(Screen.MainMenu);
        ImGui.SameLine(0, Kit.Px(Theme.Space3));
        if (Kit.Button("Refresh"u8, ButtonKind.Secondary, 140, worlds.Library is not null))
        {
            _loadError = "";
            worlds.Refresh();
        }

        EndPanel();
        DrawHints(back: true);
        EndRoot();
    }

    /// <summary>One saved world in the list. Returns true when a world that can be opened was chosen.</summary>
    private bool WorldRow(WorldEntry world, int index, bool last)
    {
        Kit.PushId((ulong)index);
        var draw = ImGui.GetWindowDrawList();
        var position = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var hasReason = world.Reason.Length > 0;
        var size = new float2(width, Kit.Px(hasReason ? 74 : 54));
        if (!world.Ready)
            ImGui.BeginDisabled();
        var pressed = ImGui.InvisibleButton("##world"u8, size);
        var hot = world.Ready && Kit.IsHot();
        var t = Kit.Animate(ImGui.GetItemID(), hot ? 1f : 0f);
        if (!world.Ready)
            ImGui.EndDisabled();

        if (t > 0.01f)
        {
            draw.AddRectFilled(position, position + size, Kit.Color(Theme.PanelRaised, 0.9f * t));
            draw.AddRectFilled(position, position + new float2(Kit.Px(3), size.Y), Kit.Color(Theme.Accent, t));
        }

        var pad = Kit.Px(Theme.Space3);
        var savedWidth = world.SavedText.Length > 0 ? Kit.Measure(_ui.Mono, Theme.FontCaption, world.SavedText).X : 0f;
        var nameWidth = size.X - pad * 3 - savedWidth;
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontBody, position + new float2(pad, Kit.Px(8)), world.Ready ? Theme.Text : Theme.TextDim, world.Name, nameWidth);
        if (savedWidth > 0)
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, position + new float2(size.X - pad - savedWidth, Kit.Px(12)), Theme.TextDim, world.SavedText);

        var status = world.Availability switch
        {
            WorldAvailability.Ready => last ? "READY  /  PLAYED LAST"u8 : "READY"u8,
            WorldAvailability.Busy => "IN USE"u8,
            WorldAvailability.Incompatible => "OTHER VERSION"u8,
            WorldAvailability.Damaged => "DAMAGED"u8,
            _ => "UNREADABLE"u8,
        };
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, position + new float2(pad, Kit.Px(32)), world.Ready ? Theme.Good : Theme.Bad, status);
        if (hasReason)
        {
            // A world this game let go of without waiting is in use by this game, not another one.
            var reason = world.Availability == WorldAvailability.Busy && _app.Lifecycle.IsReleasing(world.FolderId) ? "Still being released by this game. Try again in a moment." : world.Reason;
            Kit.ClippedText(draw, _ui.Sans, Theme.FontSmall, position + new float2(pad, Kit.Px(50)), world.Ready ? Theme.TextMute : Theme.Bad, reason, size.X - pad * 2);
        }
        draw.AddLine(position + new float2(0, size.Y - 0.5f), position + new float2(size.X, size.Y - 0.5f), Kit.Color(Theme.Line, 0.6f));
        Kit.PopId();
        return pressed && world.Ready;
    }

    /// <summary>
    /// What the session's lifecycle is doing while it works in the background: a world being opened, or the
    /// session being saved and closed. Nothing else is drawn meanwhile, so nothing can be asked of a session that
    /// is on its way in or out.
    /// </summary>
    private void DrawLifecycle(float fade)
    {
        var lifecycle = _app.Lifecycle;
        BeginRoot("##lifecycle"u8);
        var draw = ImGui.GetWindowDrawList();
        var center = _ui.DisplaySize * 0.5f + new float2(0, Slide(fade));
        Kit.Spinner(draw, center - new float2(0, Kit.Px(80)), Kit.Px(22));

        var close = lifecycle.ClosingStatus;
        var saving = lifecycle.ClosingSave.State != SaveState.Off;
        var caption = lifecycle.Phase switch
        {
            SessionPhase.Opening when lifecycle.Cancelling => "CANCELLING"u8,
            SessionPhase.Opening => "OPENING"u8,
            SessionPhase.Waiting when lifecycle.Quitting => "QUITTING"u8,
            SessionPhase.Waiting => "WAITING"u8,
            _ when lifecycle.Quitting => saving ? "SAVING AND QUITTING"u8 : "QUITTING"u8,
            _ => saving ? "SAVING"u8 : "CLOSING"u8,
        };
        var captionSize = Kit.Measure(_ui.SansMedium, Theme.FontCaption, caption);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, center - new float2(captionSize.X * 0.5f, Kit.Px(34)), Theme.Accent, caption);

        var maxWidth = Math.Min(_ui.DisplaySize.X - Kit.Px(48), Kit.Px(640));
        var title = lifecycle.Activity;
        var titleWidth = Math.Min(Kit.Measure(_ui.Sans, Theme.FontHeading, title).X, maxWidth);
        Kit.ClippedText(draw, _ui.Sans, Theme.FontHeading, center - new float2(titleWidth * 0.5f, Kit.Px(10)), Theme.Text, title, maxWidth);

        var detail = lifecycle.Phase switch
        {
            SessionPhase.Opening when lifecycle.Cancelling => "Closing what was opened, so it is left as it was."u8,
            SessionPhase.Opening => "Reading the world and starting it."u8,
            SessionPhase.Waiting => "Waiting for a world this game stopped waiting for to be released."u8,
            SessionPhase.Releasing => "Releasing the world."u8,
            _ when close.Phase == ClosePhase.Freezing => "Waiting for the save in progress to finish."u8,
            _ when saving => "Writing the world to disk."u8,
            _ => "Closing the connection."u8,
        };
        var detailSize = Kit.Measure(_ui.Sans, Theme.FontSmall, detail);
        Kit.Text(draw, _ui.Sans, Theme.FontSmall, center + new float2(-detailSize.X * 0.5f, Kit.Px(28)), Theme.TextDim, detail);

        var waiting = lifecycle.Phase == SessionPhase.Waiting;
        if ((lifecycle.Phase == SessionPhase.Opening && !lifecycle.Cancelling) || (waiting && !lifecycle.Quitting && !lifecycle.Patient))
        {
            ImGui.SetCursorScreenPos(center + new float2(-Kit.Px(70), Kit.Px(64)));
            if (Kit.Button("Cancel"u8, ButtonKind.Secondary, 140) || BackPressed())
                _app.LeaveSession();
        }
        else if (lifecycle.Patient)
        {
            // At the bound of work let go of without waiting, nothing more is let go of; quitting still is.
            var stop = lifecycle.CanStopWaiting;
            var late = lifecycle.Phase switch
            {
                _ when !stop => "Earlier worlds are still being closed and released. Wait, or quit the game."u8,
                SessionPhase.Waiting when lifecycle.Quitting => "It is still not done. If you quit now, what it was saving may not finish."u8,
                SessionPhase.Waiting => "It is still not released. Opening anyway may find it in use."u8,
                SessionPhase.Opening => "It does not stop. If you stop waiting, what it opens is closed again by itself."u8,
                _ => "This is taking longer than it should. The world keeps its last completed save if you stop waiting."u8,
            };
            var lateSize = Kit.Measure(_ui.Sans, Theme.FontSmall, late);
            Kit.Text(draw, _ui.Sans, Theme.FontSmall, center + new float2(-Math.Min(lateSize.X, maxWidth) * 0.5f, Kit.Px(56)), Theme.Accent, late);
            ImGui.SetCursorScreenPos(center + new float2(-Kit.Px(90), Kit.Px(90)));
            if (!stop)
            {
                if (Kit.Button("Quit Game"u8, ButtonKind.Secondary, 180))
                    _app.Quit();
            }
            else if (Kit.Button(waiting && lifecycle.Quitting ? "Quit Anyway"u8 : waiting ? "Open Anyway"u8 : "Stop Waiting"u8, ButtonKind.Secondary, 180))
            {
                lifecycle.StopWaiting();
            }

            if ((waiting || lifecycle.Phase == SessionPhase.Opening) && !lifecycle.Quitting)
            {
                ImGui.SameLine(0, Kit.Px(Theme.Space3));
                if (Kit.Button("Cancel"u8, ButtonKind.Secondary, 140) || BackPressed())
                    _app.LeaveSession();
            }
        }

        EndRoot();
    }

    /// <summary>
    /// How a session that just closed went, when the player needs to know: a final save that failed or could not
    /// be confirmed, one that was saved without its tidy-up or its recovery copy, or a session that stopped by
    /// itself. Quitting waits on this until the player has read it.
    /// </summary>
    private void DrawCloseReport(CloseReport report, float fade)
    {
        BeginRoot("##closereport"u8);
        var size = new float2(Math.Min(Kit.Px(600), _ui.DisplaySize.X - Kit.Px(24)), Math.Min(Kit.Px(360), _ui.DisplaySize.Y - Kit.Px(24)));
        var min = (_ui.DisplaySize - size) * 0.5f + new float2(0, Slide(fade));
        BeginPanel("##closereportpanel"u8, min, size);
        var status = report.Status;
        var width = ImGui.GetContentRegionAvail().X / Kit.Scale;

        // The headline claims only what is certain: an unknown or abandoned final save is unconfirmed, not lost.
        var headline = SessionLifecycle.Headline(report);
        Kit.Caption(
            headline switch
            {
                CloseHeadline.Stopped => "THE GAME STOPPED"u8,
                CloseHeadline.SavedWithWarning => "SAVED, WITH A WARNING"u8,
                CloseHeadline.Unconfirmed => "SAVE UNCONFIRMED"u8,
                _ => "NOT SAVED"u8,
            },
            headline == CloseHeadline.SavedWithWarning ? Theme.Accent : Theme.Bad
        );
        Kit.Space(Theme.Space1);
        var at = ImGui.GetCursorScreenPos();
        Kit.ClippedText(ImGui.GetWindowDrawList(), _ui.SansBold, Theme.FontHeading, at, Theme.Text, report.Title, ImGui.GetContentRegionAvail().X);
        ImGui.Dummy(new float2(ImGui.GetContentRegionAvail().X, Kit.Px(Theme.FontHeading) * 1.3f));
        Kit.Space(Theme.Space2);
        if (report.Late)
        {
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextDim, "You had stopped waiting for this world. It opened afterwards and was closed again."u8, width);
            Kit.Space(Theme.Space2);
        }

        if (report.Fault is { } fault)
        {
            Kit.Paragraph(_ui.Sans, Theme.FontBody, Theme.Text, fault, width);
            Kit.Space(Theme.Space2);
        }

        Kit.Paragraph(
            _ui.Sans,
            Theme.FontBody,
            Theme.TextDim,
            status.FinalCommit switch
            {
                FinalCommitOutcome.Committed when report.Fault is not null => "The world was saved as it was when it stopped."u8,
                FinalCommitOutcome.Committed => "The world is saved."u8,
                FinalCommitOutcome.Failed => "The last save failed. The world goes on from its last completed save; what happened after it is lost."u8,
                FinalCommitOutcome.Unknown =>
                    "The game could not tell whether the last save was written. Next time the world goes on from its last completed save or from the moment it closed."u8,
                FinalCommitOutcome.SkippedAfterFault => "The world was not saved as it stopped. It goes on from its last completed save."u8,
                FinalCommitOutcome.Abandoned => "Closing was stopped before the last save was known to be complete. The world goes on from its last completed save or from the moment it closed."u8,
                FinalCommitOutcome.NotPersisted => "This game was not saved."u8,
                _ => "The world was not saved as it closed. It goes on from its last completed save."u8,
            },
            width
        );

        if (report.ReleaseFailed)
        {
            Kit.Space(Theme.Space2);
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Bad, "Its files could not be confirmed released. Restart the game before opening this world again."u8, width);
        }
        else if (_app.Lifecycle.LingeringCount > 0)
        {
            Kit.Space(Theme.Space2);
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Accent, "The world is still being released in the background. It can be opened again once that has finished."u8, width);
        }

        if (status.Checkpoint == CheckpointOutcome.Failed)
        {
            Kit.Space(Theme.Space2);
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Accent, "Its files were not tidied up after the save. It opens normally; play and leave it once more before copying its folder."u8, width);
        }

        if (status.Backup == BackupOutcome.Failed)
        {
            Kit.Space(Theme.Space2);
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Accent, "No fresh recovery copy could be made. The save itself is not affected; check the disk has room."u8, width);
        }

        if (_app.Lifecycle.PendingReports > 1)
        {
            Kit.Space(Theme.Space2);
            Kit.Label(_ui.Sans, Theme.FontSmall, Theme.TextDim, Kit.F($"{_app.Lifecycle.PendingReports - 1} more to read after this."));
        }

        Kit.Space(Theme.Space5);
        if (report.WasQuitting)
        {
            if (Kit.Button("Quit"u8, ButtonKind.Primary, 140))
                _app.Lifecycle.Acknowledge(quit: true);
            ImGui.SameLine(0, Kit.Px(Theme.Space3));
            if (Kit.Button("Back to Menu"u8, ButtonKind.Secondary, 200) || BackPressed())
                _app.Lifecycle.Acknowledge();
        }
        else if (Kit.Button("OK"u8, ButtonKind.Primary, 140) || BackPressed())
        {
            _app.Lifecycle.Acknowledge();
        }

        EndPanel();
        EndRoot();
    }

    /// <summary>
    /// How the world this process runs is being saved, for the in-game menu: saved how long ago, saving now, or
    /// failing, and for a game that is not saved, that it is not. Draws nothing for a remote session.
    /// </summary>
    private void DrawSaveStatus(GameSession session, ServerVisibility visibility)
    {
        if (!session.IsLocal)
            return;
        Kit.Space(Theme.Space1);
        if (!session.IsPersistent)
        {
            Kit.Paragraph(
                _ui.Sans,
                Theme.FontSmall,
                Theme.Accent,
                visibility != ServerVisibility.Private
                    ? "Not saved: a hosted game is not saved yet."u8
                    : "Not saved: saved worlds are off for this run."u8,
                ImGui.GetContentRegionAvail().X / Kit.Scale
            );
            return;
        }

        var save = session.Save;
        if (save.State == SaveState.Halted)
        {
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Bad, "Saving has stopped. The world plays on; leaving keeps its last completed save."u8, ImGui.GetContentRegionAvail().X / Kit.Scale);
        }
        else if (save.FailedAttempts > 0)
        {
            Kit.Label(_ui.Sans, Theme.FontSmall, Theme.Bad, Kit.F($"The last save failed ({save.FailedAttempts}x). Trying again."));
        }
        else if (save.State is SaveState.Capturing or SaveState.Writing)
        {
            Kit.Label(_ui.Sans, Theme.FontSmall, Theme.TextDim, "Saving..."u8);
        }
        else if (save.SavedAtUnixMs > 0)
        {
            var seconds = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - save.SavedAtUnixMs) / 1000);
            if (seconds < 60)
                Kit.Label(_ui.Sans, Theme.FontSmall, Theme.Good, Kit.F($"Saved {seconds} s ago. Saved again as you play."));
            else
                Kit.Label(_ui.Sans, Theme.FontSmall, Theme.Good, Kit.F($"Saved {seconds / 60} min ago. Saved again as you play."));
        }
        else
        {
            Kit.Label(_ui.Sans, Theme.FontSmall, Theme.Good, Kit.F($"Saved automatically every {_app.Settings.AutosaveSeconds} s and when you leave."));
        }

        if (save.RefusedGrowth > 0)
        {
            Kit.Space(Theme.Space1);
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Accent, "The world is as large as a save can hold; it no longer grows."u8, ImGui.GetContentRegionAvail().X / Kit.Scale);
        }
    }

    /// <summary>A warning over the world while its saves fail, so a failure is never silent.</summary>
    private void DrawSaveWarning()
    {
        if (_app.Session is not { IsPersistent: true } session)
            return;
        var save = session.Save;
        var halted = save.State == SaveState.Halted;
        if (!halted && save.FailedAttempts == 0)
            return;
        var text = halted ? "SAVING HAS STOPPED  /  THE LAST COMPLETED SAVE IS KEPT"u8 : "SAVE FAILED  /  TRYING AGAIN"u8;
        var draw = ImGui.GetForegroundDrawList();
        var size = Kit.Measure(_ui.SansMedium, Theme.FontCaption, text);
        var at = new float2((_ui.DisplaySize.X - size.X) * 0.5f, HudTop + Kit.Px(Theme.Space2));
        var pad = Kit.Px(Theme.Space2);
        draw.AddRectFilled(at - new float2(pad, pad * 0.5f), at + size + new float2(pad, pad * 0.5f), Kit.Opaque(Theme.Void, 0.85f));
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, at, Theme.Bad, text);
    }
}
