using Prospect.Client.Screens;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Xunit;

namespace Prospect.Tests;

/// <summary>The drone settings popup over the DRONES chip: its open and close rules and its buttons, without fonts.</summary>
public sealed class DronePopupTests
{
    private static BayView View(int stocked, int outside, bool auto = false)
    {
        var view = new BayView { Hull = 5, Count = 1, AutoPickup = auto };
        view.Bays[0] = new BayEntry { Capacity = 4, Stocked = (byte)stocked, Out = (byte)outside, Live = true, HatchClear = true };
        return view;
    }

    [Fact]
    public void AClickOnTheChipOpensThePopupAndAnotherClosesIt()
    {
        var popup = new DronePopup();
        popup.Follow(5, true);
        Assert.False(popup.IsOpen);
        popup.Toggle();
        Assert.True(popup.IsOpen);
        // The press that opened it is on the chip, so the outside-press rule leaves it be.
        popup.Press(true, false, true);
        Assert.True(popup.IsOpen);
        popup.Toggle();
        Assert.False(popup.IsOpen);
    }

    [Fact]
    public void APressOutsideClosesItButNotOneOnThePopup()
    {
        var popup = new DronePopup();
        popup.Follow(5, true);
        popup.Toggle();
        popup.Press(false, false, false);
        Assert.True(popup.IsOpen);
        popup.Press(true, true, false);
        Assert.True(popup.IsOpen);
        popup.Press(true, false, false);
        Assert.False(popup.IsOpen);
    }

    [Fact]
    public void ItBelongsToOneHullThatStillHasBays()
    {
        var popup = new DronePopup();
        popup.Follow(5, true);
        popup.Toggle();
        popup.Follow(5, true);
        Assert.True(popup.IsOpen);
        popup.Follow(6, true);
        Assert.False(popup.IsOpen);
        popup.Toggle();
        popup.Follow(6, false);
        Assert.False(popup.IsOpen);
    }

    [Fact]
    public void EscapeClosesItBeforeTheViewOrTheMenuButAfterTheCalloutsAndMenus()
    {
        var pilot = Prospect.Client.World.ViewMode.Pilot;
        var command = Prospect.Client.World.ViewMode.Command;
        Assert.Equal(ScreenHost.BackStep.DronePopup, ScreenHost.NextBackStep(false, false, false, false, false, true, pilot));
        Assert.Equal(ScreenHost.BackStep.DronePopup, ScreenHost.NextBackStep(false, false, false, false, false, true, command));
        Assert.Equal(ScreenHost.BackStep.CargoCallout, ScreenHost.NextBackStep(false, false, false, false, true, true, pilot));
        Assert.Equal(ScreenHost.BackStep.SessionMenu, ScreenHost.NextBackStep(false, true, false, false, false, true, pilot));
        Assert.Equal(ScreenHost.BackStep.OpenMenu, ScreenHost.NextBackStep(false, false, false, false, false, false, pilot));
    }

    [Fact]
    public void TheButtonRecallsWhileDronesAreOutAndLaunchesWhatIsInsideOtherwise()
    {
        var bridge = new PlayerInputBridge();
        DronePopup.LaunchOrRecall(View(stocked: 3, outside: 1), 5, bridge);
        Assert.True(bridge.ConsumeDroneCommand(out var recall));
        Assert.Equal(DroneCommand.Recall, recall.Command);

        DronePopup.LaunchOrRecall(View(stocked: 3, outside: 0), 5, bridge);
        Assert.True(bridge.ConsumeDroneCommand(out var launch));
        Assert.Equal(DroneCommand.Launch, launch.Command);
        Assert.Equal(3, launch.Count);
    }

    [Fact]
    public void TheCheckboxFlipsTheServersSettingNotACopyOfItsOwn()
    {
        var bridge = new PlayerInputBridge();
        DronePopup.ToggleAutoPickup(View(4, 0, auto: false), 5, bridge);
        Assert.True(bridge.ConsumeDroneSettings(out var on));
        Assert.Equal(new DroneSettingsMessage(5, true), on);
        DronePopup.ToggleAutoPickup(View(4, 0, auto: true), 5, bridge);
        Assert.True(bridge.ConsumeDroneSettings(out var off));
        Assert.Equal(new DroneSettingsMessage(5, false), off);
        Assert.False(bridge.ConsumeDroneSettings(out _));
    }

    [Fact]
    public void ThePopupAndItsTrackerTakeNoMemory()
    {
        var popup = new DronePopup();
        var tracker = new DroneTracker();
        var view = View(3, 1, auto: true);
        var bridge = new PlayerInputBridge();
        popup.Follow(5, true);
        var probe = new AllocationProbe();
        for (var i = 0; i < 100; i++)
        {
            while (probe.Measuring())
            {
                popup.Toggle(); popup.Press(true, false, false); popup.Follow(5, true);
                tracker.Update(view, 0.016f);
                _ = tracker.Latest;
                DronePopup.ToggleAutoPickup(view, 5, bridge);
                bridge.ConsumeDroneSettings(out _);
            }
        }
        Assert.Equal(0, probe.Bytes);
    }
}
