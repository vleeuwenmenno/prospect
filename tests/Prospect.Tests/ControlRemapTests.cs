using Brutal.GlfwApi;
using Prospect.Client.Input;
using Prospect.Client.Screens;
using Prospect.Client.Settings;
using Xunit;

namespace Prospect.Tests;

/// <summary>Changing controls: capture, clashes, reset, storage and the live map.</summary>
public sealed class ControlRemapTests
{
    private static readonly string Content = Path.Combine(AppContext.BaseDirectory, "content");
    private static readonly string Bindings = Path.Combine(Content, "input", "bindings.json");
    private static readonly string Catalogue = Path.Combine(Content, "input", "controls.json");

    private static InputBinding Key(GlfwKey key) => new(InputBinding.Source.Key, (int)key);

    private sealed class Rig
    {
        public readonly ActionMap Map = ActionMap.Load(Bindings);
        public readonly ClientSettings Settings = new();
        public readonly ControlsCatalog Catalog;
        public readonly ControlRemapper Remapper;

        public Rig()
        {
            Catalog = new ControlsCatalog(Catalogue, Map);
            Remapper = new ControlRemapper(Map, Catalog, Settings);
        }
    }

    [Fact]
    public void ReplacingAControlTakesEffectAtOnceInTheMapTheCatalogueAndTheSettings()
    {
        var rig = new Rig();
        var changes = 0;
        rig.Map.Changed += () => changes++;
        var before = rig.Map.Version;

        Assert.Null(rig.Remapper.Propose(ControlSlot.Of(InputAction.Boost), 0, Key(GlfwKey.J)));
        Assert.Equal(["J", "Pad LeftThumb"], rig.Map.Bindings(InputAction.Boost).ToArray().Select(b => b.DisplayName));
        Assert.Equal(1, changes);
        Assert.True(rig.Map.Version > before);
        rig.Catalog.Refresh();
        Assert.Contains(rig.Catalog.Rows, r => r.Action == "Boost" && r.Binding == "J / Pad LeftThumb");
        Assert.Equal(["Key:J", "Pad:LeftThumb"], rig.Settings.ControlOverrides["Boost"]);

        // The new key boosts and the old one no longer does.
        var devices = new InputDevices();
        devices.OnKey(GlfwKey.J, GlfwKeyAction.Press);
        Assert.True(rig.Map.Held(devices, InputAction.Boost, true));
        devices.OnKey(GlfwKey.J, GlfwKeyAction.Release);
        devices.OnKey(GlfwKey.Space, GlfwKeyAction.Press);
        Assert.False(rig.Map.Held(devices, InputAction.Boost, true));
        Assert.True(rig.Map.Pressed(devices, InputAction.Follow, true));
    }

    [Fact]
    public void AddingAndRemovingControlsKeepsTheLimitsAndTheLastKeyOfRequiredActions()
    {
        var rig = new Rig();
        var scan = ControlSlot.Of(InputAction.ScanSector);
        Assert.True(rig.Remapper.CanAdd(scan));
        Assert.Null(rig.Remapper.Propose(scan, -1, Key(GlfwKey.F7)));
        Assert.Null(rig.Remapper.Propose(scan, -1, Key(GlfwKey.F8)));
        Assert.Equal(4, rig.Remapper.Count(scan));
        Assert.False(rig.Remapper.CanAdd(scan));
        Assert.True(rig.Remapper.Remove(scan, 0));
        Assert.Equal(["Pad DpadLeft", "F7", "F8"], rig.Map.Bindings(InputAction.ScanSector).ToArray().Select(b => b.DisplayName));

        // Menu / back must keep one control, so nobody locks themselves out.
        var pause = ControlSlot.Of(InputAction.Pause);
        Assert.True(rig.Remapper.CanRemove(pause, 0));
        Assert.True(rig.Remapper.Remove(pause, 0));
        Assert.False(rig.Remapper.CanRemove(pause, 0));
        Assert.False(rig.Remapper.Remove(pause, 0));
        Assert.Equal(1, rig.Map.Bindings(InputAction.Pause).Length);
    }

    [Fact]
    public void AClashIsReportedChangesNothingAndCanBeSwappedOrCleared()
    {
        var rig = new Rig();
        var boost = ControlSlot.Of(InputAction.Boost);
        var dampeners = ControlSlot.Of(InputAction.ToggleDampeners);

        // Z is dampeners' key, and both are read in flight.
        var proposal = rig.Remapper.Propose(boost, 0, Key(GlfwKey.Z));
        Assert.NotNull(proposal);
        Assert.Equal([dampeners], proposal.Conflicts);
        Assert.Equal(["Space", "Pad LeftThumb"], rig.Map.Bindings(InputAction.Boost).ToArray().Select(b => b.DisplayName));
        Assert.Empty(rig.Settings.ControlOverrides);
        Assert.True(rig.Remapper.CanSwap(proposal));
        Assert.True(rig.Remapper.CanClearOthers(proposal));

        Assert.True(rig.Remapper.Resolve(proposal, ConflictChoice.Swap));
        Assert.Equal(["Z", "Pad LeftThumb"], rig.Map.Bindings(InputAction.Boost).ToArray().Select(b => b.DisplayName));
        Assert.Equal(["Space", "Pad X (Square)"], rig.Map.Bindings(InputAction.ToggleDampeners).ToArray().Select(b => b.DisplayName));
        Assert.Contains("Boost", rig.Settings.ControlOverrides.Keys);
        Assert.Contains("ToggleDampeners", rig.Settings.ControlOverrides.Keys);

        rig.Remapper.ResetAll();
        proposal = rig.Remapper.Propose(boost, 0, Key(GlfwKey.Z));
        Assert.NotNull(proposal);
        Assert.True(rig.Remapper.Resolve(proposal, ConflictChoice.ClearOther));
        Assert.Equal(["Z", "Pad LeftThumb"], rig.Map.Bindings(InputAction.Boost).ToArray().Select(b => b.DisplayName));
        Assert.Equal(["Pad X (Square)"], rig.Map.Bindings(InputAction.ToggleDampeners).ToArray().Select(b => b.DisplayName));
    }

    [Fact]
    public void AddingAControlOnlyOffersToClearAndTheSameKeyInAnotherViewIsNoClash()
    {
        var rig = new Rig();
        // Adding has no old control to hand over.
        var proposal = rig.Remapper.Propose(ControlSlot.Of(InputAction.ScanSector), -1, Key(GlfwKey.Z));
        Assert.NotNull(proposal);
        Assert.False(rig.Remapper.CanSwap(proposal));
        Assert.True(rig.Remapper.CanClearOthers(proposal));

        // The map layer keys are read in the map only: sharing a key with the flight view is no clash, as Space is
        // shared by boost and follow in the shipped file.
        Assert.Null(rig.Remapper.Propose(ControlSlot.Of(InputAction.MapTilt), 0, Key(GlfwKey.H)));
        Assert.Contains(Key(GlfwKey.Space), rig.Map.Bindings(InputAction.Boost).ToArray());
        Assert.Contains(Key(GlfwKey.Space), rig.Map.Bindings(InputAction.Follow).ToArray());
    }

    [Fact]
    public void ALastRequiredControlIsNeverTakenByAClash()
    {
        var rig = new Rig();
        rig.Remapper.Remove(ControlSlot.Of(InputAction.Pause), 1);
        var proposal = rig.Remapper.Propose(ControlSlot.Of(InputAction.ToggleControls), 0, Key(GlfwKey.Escape));
        Assert.NotNull(proposal);
        Assert.False(rig.Remapper.CanClearOthers(proposal));
        Assert.False(rig.Remapper.Resolve(proposal, ConflictChoice.ClearOther));
        Assert.True(rig.Remapper.CanSwap(proposal));
        Assert.True(rig.Remapper.Resolve(proposal, ConflictChoice.Swap));
        Assert.Equal([Key(GlfwKey.F1)], rig.Map.Bindings(InputAction.Pause).ToArray());
    }

    [Fact]
    public void AnAxisKeyCanBeRebounddClearedAndResetAndTheMapReadsIt()
    {
        var rig = new Rig();
        var left = ControlSlot.Of(InputAxis.Strafe, false);
        Assert.Equal(Key(GlfwKey.A), rig.Remapper.Get(left, 0));
        Assert.Null(rig.Remapper.Propose(left, 0, Key(GlfwKey.J)));
        Assert.Equal(["Key:J"], rig.Settings.ControlOverrides["Strafe.Negative"]);
        var devices = new InputDevices();
        devices.OnKey(GlfwKey.J, GlfwKeyAction.Press);
        Assert.Equal(-1f, rig.Map.Axis(devices, InputAxis.Strafe, true));
        devices.OnKey(GlfwKey.J, GlfwKeyAction.Release);
        devices.OnKey(GlfwKey.A, GlfwKeyAction.Press);
        Assert.Equal(0f, rig.Map.Axis(devices, InputAxis.Strafe, true));
        rig.Catalog.Refresh();
        Assert.Equal("J - D / Pad LeftX", rig.Catalog.Rows.Single(r => r.Kind == RowKind.Axis && r.Index == (int)InputAxis.Strafe).Binding);

        // Clearing leaves the other key and the stick alone.
        Assert.True(rig.Remapper.Remove(left, 0));
        Assert.Empty(rig.Settings.ControlOverrides["Strafe.Negative"]);
        Assert.Equal(0, rig.Remapper.Count(left));
        rig.Catalog.Refresh();
        Assert.Equal("unbound - D / Pad LeftX", rig.Catalog.Rows.Single(r => r.Kind == RowKind.Axis && r.Index == (int)InputAxis.Strafe).Binding);

        rig.Remapper.Reset(left);
        Assert.Equal(Key(GlfwKey.A), rig.Remapper.Get(left, 0));
        Assert.DoesNotContain("Strafe.Negative", rig.Settings.ControlOverrides.Keys);

        // Roll keeps its bumpers whatever its keys do, and an axis half clashes with an action in a shared view.
        Assert.Null(rig.Remapper.Propose(ControlSlot.Of(InputAxis.Roll, true), 0, Key(GlfwKey.U)));
        Assert.Contains(rig.Map.Bindings(InputAxis.Roll).ToArray(), b => b.DisplayName.Contains("Bumper"));
        var proposal = rig.Remapper.Propose(ControlSlot.Of(InputAxis.Roll, true), 0, Key(GlfwKey.H));
        Assert.NotNull(proposal);
        Assert.Equal([ControlSlot.Of(InputAction.Halt)], proposal.Conflicts);
    }

    [Fact]
    public void ResettingOneControlAndAllControlsPutsBackTheShippedBindingsAndEmptiesTheSettings()
    {
        var rig = new Rig();
        rig.Remapper.Propose(ControlSlot.Of(InputAction.Boost), 0, Key(GlfwKey.J));
        rig.Remapper.Propose(ControlSlot.Of(InputAction.MapTilt), 0, Key(GlfwKey.G));
        rig.Remapper.Propose(ControlSlot.Of(InputAxis.Yaw, true), 0, Key(GlfwKey.X));
        Assert.True(rig.Remapper.AnyChanged());
        Assert.Equal(3, rig.Settings.ControlOverrides.Count);

        rig.Remapper.Reset(ControlSlot.Of(InputAction.Boost));
        Assert.True(rig.Map.IsDefault(InputAction.Boost));
        Assert.DoesNotContain("Boost", rig.Settings.ControlOverrides.Keys);
        Assert.Equal(2, rig.Settings.ControlOverrides.Count);

        rig.Remapper.ResetAll();
        Assert.False(rig.Remapper.AnyChanged());
        Assert.Empty(rig.Settings.ControlOverrides);
        var fresh = ActionMap.Load(Bindings);
        foreach (var slot in ControlRemapper.AllSlots())
            Assert.Equal(slot.Kind == SlotKind.Action ? fresh.Bindings((InputAction)slot.Index).ToArray().Select(b => b.FileName) : [fresh.AxisKey((InputAxis)slot.Index, slot.Positive)?.FileName ?? ""],
                slot.Kind == SlotKind.Action ? rig.Map.Bindings((InputAction)slot.Index).ToArray().Select(b => b.FileName) : [rig.Map.AxisKey((InputAxis)slot.Index, slot.Positive)?.FileName ?? ""]);
    }

    [Fact]
    public void RemapsSurviveTheSettingsFileAndReachAFreshMapAtStartup()
    {
        var rig = new Rig();
        rig.Remapper.Propose(ControlSlot.Of(InputAction.Boost), 0, Key(GlfwKey.J));
        rig.Remapper.Propose(ControlSlot.Of(InputAction.ScanSector), 1, new InputBinding(InputBinding.Source.PadButton, (int)GlfwGamepadButton.RightBumper));
        rig.Remapper.Propose(ControlSlot.Of(InputAction.MapTilt), 0, new InputBinding(InputBinding.Source.MouseButton, (int)GlfwMouseButton.Middle));
        rig.Remapper.Propose(ControlSlot.Of(InputAxis.Pitch, true), 0, Key(GlfwKey.I));
        rig.Remapper.Remove(ControlSlot.Of(InputAction.Halt), 1);
        rig.Settings.ControlOverrides["FromANewerBuild"] = ["Key:F20"];

        var path = Path.GetTempFileName();
        try
        {
            rig.Settings.Save(path);
            var loaded = ClientSettings.Load(path);
            var map = ActionMap.Load(Bindings);
            map.ApplyOverrides(loaded.ControlOverrides);
            foreach (var slot in ControlRemapper.AllSlots())
            {
                if (slot.Kind == SlotKind.Action)
                    Assert.Equal(rig.Map.Bindings((InputAction)slot.Index).ToArray(), map.Bindings((InputAction)slot.Index).ToArray());
                else
                    Assert.Equal(rig.Map.AxisKey((InputAxis)slot.Index, slot.Positive), map.AxisKey((InputAxis)slot.Index, slot.Positive));
            }

            Assert.Equal(["Key:H", "Pad:B"], loaded.ControlOverrides["Halt"]);
            Assert.Equal(["Mouse:Middle"], loaded.ControlOverrides["MapTilt"]);
            // A name this build does not know is kept for the build that does, and not applied.
            Assert.Equal(["Key:F20"], loaded.ControlOverrides["FromANewerBuild"]);

            // Changing one back and saving again drops it; the unknown name stays.
            var again = new ControlRemapper(map, new ControlsCatalog(Catalogue, map), loaded);
            again.Reset(ControlSlot.Of(InputAction.Boost));
            Assert.DoesNotContain("Boost", loaded.ControlOverrides.Keys);
            Assert.Contains("FromANewerBuild", loaded.ControlOverrides.Keys);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AHandEditedSettingsFileCannotBreakTheMapOrGrowWithoutLimit()
    {
        var map = ActionMap.Load(Bindings);
        map.ApplyOverrides(new Dictionary<string, List<string>>
        {
            ["Boost"] = ["Key:NotAKey"],
            ["Strafe.Negative"] = ["Key:J", "Key:K"],
            ["Strafe.Sideways"] = ["Key:J"],
            ["Halt"] = ["Key:H", "Key:H", "Key:G", "Key:F", "Key:E", "Key:D"],
        });
        Assert.True(map.IsDefault(InputAction.Boost));
        Assert.Equal(Key(GlfwKey.A), map.AxisKey(InputAxis.Strafe, false));
        Assert.Equal(ActionMap.MaxBindings, map.Bindings(InputAction.Halt).Length);
        Assert.Equal(map.Bindings(InputAction.Halt).Length, map.Bindings(InputAction.Halt).ToArray().Distinct().Count());

        var settings = new ClientSettings();
        for (var i = 0; i < 300; i++)
            settings.ControlOverrides[$"Name{i}"] = Enumerable.Repeat("Key:A", 9).Concat([new string('x', 200)]).ToList();
        settings.ControlOverrides[new string('n', 100)] = ["Key:A"];
        var path = Path.GetTempFileName();
        try
        {
            settings.Save(path);
            var loaded = ClientSettings.Load(path);
            Assert.True(loaded.ControlOverrides.Count <= ClientSettings.MaxControlOverrides);
            Assert.All(loaded.ControlOverrides, pair =>
            {
                Assert.True(pair.Key.Length <= ClientSettings.MaxControlNameLength);
                Assert.True(pair.Value.Count <= ClientSettings.MaxControlOverrideBindings);
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RemappedControlsReachTheTipsAndTheCatalogueText()
    {
        var rig = new Rig();
        var tips = TipCatalogue.Load(Path.Combine(Content, "visuals", "tips.json"), rig.Map);
        var scan = tips.Tips.First(t => t.Template.Contains("{ScanSector}"));
        Assert.Contains("K", scan.Text);
        rig.Remapper.Propose(ControlSlot.Of(InputAction.ScanSector), 0, Key(GlfwKey.J));
        var rebound = tips.Rebind(rig.Map);
        Assert.Equal(tips.Tips.Length, rebound.Tips.Length);
        var after = rebound.Tips.First(t => t.Id == scan.Id);
        Assert.Contains(" J ", after.Text.Replace(".", " ").Replace(",", " ") + " ");
        Assert.DoesNotContain("press K ", after.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(after.TextBytes), after.Text);
    }

    [Fact]
    public void CapturingATakesTheFirstPressEscapeAndALeftClickCancelAndTheMapIsHeldUntilTheFrameEnds()
    {
        var map = ActionMap.Load(Bindings);
        var devices = new InputDevices();
        var capture = new ControlCapture(map);
        Assert.Equal(CaptureResult.Waiting, capture.Update(devices));

        capture.Begin(ControlSlot.Of(InputAction.Boost), 0);
        Assert.True(map.Suspended);
        Assert.Equal(CaptureResult.Waiting, capture.Update(devices));
        devices.OnKey(GlfwKey.Z, GlfwKeyAction.Press);
        // The map reads nothing while a capture waits, not even the key that is down.
        Assert.False(map.Pressed(devices, InputAction.ToggleDampeners, true));
        Assert.False(map.Held(devices, InputAction.ToggleDampeners, true));
        Assert.False(map.InterfaceShortcutPressed(devices, InputAction.ToggleDampeners, false, false));
        devices.OnKey(GlfwKey.W, GlfwKeyAction.Press);
        Assert.Equal(0f, map.Axis(devices, InputAxis.Forward, true));

        Assert.Equal(CaptureResult.Captured, capture.Update(devices));
        Assert.Equal(Key(GlfwKey.W), capture.Binding);
        Assert.False(capture.Active);
        Assert.True(capture.EndedThisFrame);
        Assert.True(map.Suspended);
        capture.EndFrame();
        Assert.False(map.Suspended);
        Assert.False(capture.EndedThisFrame);

        // Escape cancels and is the page's business no more than this frame's.
        devices.BeginFrame();
        capture.Begin(ControlSlot.Of(InputAction.Boost), -1);
        devices.OnKey(GlfwKey.Escape, GlfwKeyAction.Press);
        devices.OnKey(GlfwKey.A, GlfwKeyAction.Press);
        Assert.Equal(CaptureResult.Cancelled, capture.Update(devices));
        Assert.False(capture.EndedByClick);
        Assert.False(map.Pressed(devices, InputAction.Pause, true));
        capture.EndFrame();
        Assert.False(map.Suspended);

        // A left click cancels (the interface needs it); the other mouse buttons and the gamepad bind.
        devices.BeginFrame();
        capture.Begin(ControlSlot.Of(InputAction.Boost), 0);
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
        Assert.Equal(CaptureResult.Cancelled, capture.Update(devices));
        Assert.True(capture.EndedByClick);
        capture.EndFrame();

        devices.BeginFrame();
        capture.Begin(ControlSlot.Of(InputAction.Boost), 0);
        devices.OnMouseButton(GlfwMouseButton.Right, GlfwButtonAction.Press);
        Assert.Equal(CaptureResult.Captured, capture.Update(devices));
        Assert.Equal(new InputBinding(InputBinding.Source.MouseButton, (int)GlfwMouseButton.Right), capture.Binding);
        capture.EndFrame();

        // Leaving the page mid-capture lets the map answer at once.
        capture.Begin(ControlSlot.Of(InputAction.Boost), 0);
        capture.Cancel();
        Assert.False(map.Suspended);
        Assert.False(capture.Active);
    }

    [Fact]
    public void AnEdgeOnlyLastsOneFrameSoTheClickThatOpensACaptureIsNotTheOneItTakes()
    {
        var map = ActionMap.Load(Bindings);
        var devices = new InputDevices();
        var capture = new ControlCapture(map);
        // The click that opened it was polled in the frame before; the next frame starts with no edges.
        devices.OnMouseButton(GlfwMouseButton.Left, GlfwButtonAction.Press);
        capture.Begin(ControlSlot.Of(InputAction.Boost), 0);
        devices.BeginFrame();
        Assert.Equal(CaptureResult.Waiting, capture.Update(devices));
        Assert.False(devices.TryFirstPress(out _));
    }
}
