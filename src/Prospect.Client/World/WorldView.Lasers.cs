using System.Numerics;
using Prospect.Client.Input;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

// Action groups and firing in the pilot view. The number keys toggle groups; a group exists only while a part of the
// flown hull is in it. While any group is on, the align button (the left mouse button) fires every group that is on,
// aimed along the view (the captured pointer is the reticle); with all off it aligns as it always has. The intent
// goes to the server through PlayerInputBridge whenever it changes; the server decides what fires and what it cuts.
internal sealed partial class WorldView
{
    // Smallest change of the aim worth telling the server: about 0.06 degrees, and a centimetre of camera.
    private const float AimTurnCosine = 0.9999995f, AimShiftMetres = 0.01f;

    private uint _aimHull;
    private ushort _aimSent;
    private Vector3 _aimOffset, _aimDirection;

    /// <summary>The action groups that are on (bit <c>g - 1</c> for group <c>g</c>), always among <see cref="ActionGroupsPresent"/>.</summary>
    public ushort ActionGroupsOn { get; private set; }

    /// <summary>The groups the flown hull has a part in, as far as this client knows (its design and the server's overrides).</summary>
    public ushort ActionGroupsPresent { get; private set; }

    /// <summary>Whether the pilot is firing this frame: a group is on and the fire button is held where it may fire.</summary>
    public bool LasersFiring { get; private set; }

    /// <summary>Scripted runs (<c>--fire-group</c>, <c>--fire-at</c>): the group to turn on and hold firing, once the run reaches its time.</summary>
    internal int ScriptedFireGroup { get; set; }

    /// <summary>The groups the flown hull has parts in: one bit a group, from <see cref="SessionSnapshot.GroupOf"/>.</summary>
    internal static ushort GroupsOf(SessionSnapshot snapshot, uint hull, HullDesign design)
    {
        var mask = 0;
        var parts = Math.Min(design.Lasers.Length, Prospect.Game.Protocol.ActionGroupsMessage.MaxParts);
        for (var part = 0; part < parts; part++) mask |= ActionGroups.Bit(snapshot.GroupOf(hull, design, part));
        return (ushort)mask;
    }

    // The number keys, then whether the left button fires or aligns. Returns whether the align button is the
    // pilot's to align with this frame (all groups off).
    private bool UpdateFiring(SessionSnapshot snapshot, PlayerInputBridge bridge, bool keyboard, bool fireBlocked)
    {
        var own = snapshot.OwnHull >= 0 ? snapshot.Hulls[snapshot.OwnHull] : default;
        var present = own.Id != 0 ? GroupsOf(snapshot, own.Id, snapshot.Content.Designs[own.Design]) : (ushort)0;
        if (own.Id != _aimHull) ActionGroupsOn = 0;
        ActionGroupsPresent = present;
        var on = ActionGroupsOn & present;
        for (var g = 1; g <= ActionGroups.Count; g++)
        {
            var bit = ActionGroups.Bit(g);
            if ((present & bit) != 0 && _actions.Pressed(_devices, InputAction.ActionGroup1 + (g - 1), keyboard)) on ^= bit;
        }

        var held = _actions.Held(_devices, InputAction.AlignToView, keyboard);
        if (ScriptedFireGroup != 0 && (present & ActionGroups.Bit(ScriptedFireGroup)) != 0)
        {
            on |= ActionGroups.Bit(ScriptedFireGroup);
            held = true;
            fireBlocked = false;
        }

        ActionGroupsOn = (ushort)on;
        LasersFiring = on != 0 && held && !fireBlocked;
        SendAim(bridge, own.Id, LasersFiring ? ActionGroupsOn : (ushort)0);
        return on == 0;
    }

    // Leaves every group off and stops firing: another view, the editor, a surface over the world.
    private void StopFiring(PlayerInputBridge bridge)
    {
        LasersFiring = false;
        SendAim(bridge, _aimHull, 0);
    }

    private void SendAim(PlayerInputBridge bridge, uint hull, ushort groups)
    {
        var offset = CameraPosition.RelativeTo(OwnPosition);
        var direction = _lookDirection;
        var changed = hull != _aimHull || groups != _aimSent;
        if (!changed && groups != 0)
            changed = Vector3.Dot(direction, _aimDirection) < AimTurnCosine || Vector3.DistanceSquared(offset, _aimOffset) > AimShiftMetres * AimShiftMetres;
        if (!changed) return;
        if (hull != 0 && (groups != 0 || _aimSent != 0 || hull != _aimHull)) bridge.AimLasers(hull, groups, offset, direction);
        _aimHull = hull;
        _aimSent = groups;
        _aimOffset = offset;
        _aimDirection = direction;
    }
}
