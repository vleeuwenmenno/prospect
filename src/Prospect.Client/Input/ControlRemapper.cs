using Prospect.Client.Settings;

namespace Prospect.Client.Input;

/// <summary>Which kind of thing a <see cref="ControlSlot"/> is.</summary>
internal enum SlotKind : byte
{
    /// <summary>All the controls of one <see cref="InputAction"/>.</summary>
    Action,

    /// <summary>One key of an axis: its negative half or its positive half.</summary>
    AxisHalf,
}

/// <summary>One thing the player can rebind: an action, or one half of an axis.</summary>
internal readonly record struct ControlSlot(SlotKind Kind, int Index, bool Positive = false)
{
    public static ControlSlot Of(InputAction action) => new(SlotKind.Action, (int)action);

    public static ControlSlot Of(InputAxis axis, bool positive) => new(SlotKind.AxisHalf, (int)axis, positive);

    /// <summary>The name this slot is saved under in <see cref="ClientSettings.ControlOverrides"/>.</summary>
    public string Key => Kind == SlotKind.Action ? ((InputAction)Index).ToString() : $"{(InputAxis)Index}.{(Positive ? "Positive" : "Negative")}";
}

/// <summary>What the player chose when a control they captured was already in use.</summary>
internal enum ConflictChoice : byte
{
    /// <summary>The other action takes the control this one gave up.</summary>
    Swap,

    /// <summary>The other action loses the control and this one takes it.</summary>
    ClearOther,
}

/// <summary>A captured control that another action in an overlapping view already uses, waiting for a choice.</summary>
internal sealed class RemapProposal(ControlSlot slot, int index, InputBinding binding, ControlSlot[] conflicts)
{
    public ControlSlot Slot { get; } = slot;

    /// <summary>Which of the slot's controls is replaced, or -1 to add one.</summary>
    public int Index { get; } = index;

    public InputBinding Binding { get; } = binding;

    /// <summary>The slots that use <see cref="Binding"/> in a view the slot is also read in.</summary>
    public ControlSlot[] Conflicts { get; } = conflicts;
}

/// <summary>
/// The rules of changing controls, without any drawing: which controls clash, what a swap does, how a change is
/// stored in the client settings and put back. It edits the live <see cref="ActionMap"/>, so a change is in force at
/// once, and keeps <see cref="ClientSettings.ControlOverrides"/> equal to what differs from the defaults.
/// </summary>
internal sealed class ControlRemapper(ActionMap map, ControlsCatalog catalog, ClientSettings settings)
{
    /// <summary>Every slot, actions first then axis halves, in the order of their enums.</summary>
    public static IEnumerable<ControlSlot> AllSlots()
    {
        for (var i = 0; i < (int)InputAction.Count; i++)
            yield return new ControlSlot(SlotKind.Action, i);
        for (var i = 0; i < (int)InputAxis.Count; i++)
        {
            yield return new ControlSlot(SlotKind.AxisHalf, i, false);
            yield return new ControlSlot(SlotKind.AxisHalf, i, true);
        }
    }

    /// <summary>How many controls the slot holds now (an axis half holds at most one).</summary>
    public int Count(ControlSlot slot) =>
        slot.Kind == SlotKind.Action ? map.Bindings((InputAction)slot.Index).Length : map.AxisKey((InputAxis)slot.Index, slot.Positive) is null ? 0 : 1;

    /// <summary>The slot's control at <paramref name="index"/>.</summary>
    public InputBinding Get(ControlSlot slot, int index) =>
        slot.Kind == SlotKind.Action ? map.Bindings((InputAction)slot.Index)[index] : map.AxisKey((InputAxis)slot.Index, slot.Positive)!.Value;

    /// <summary>The views that read the slot.</summary>
    public ControlScope Scope(ControlSlot slot) => slot.Kind == SlotKind.Action ? catalog.ScopeOf((InputAction)slot.Index) : catalog.ScopeOf((InputAxis)slot.Index);

    /// <summary>The name of the slot, such as "Boost" or "Strafe left".</summary>
    public string Label(ControlSlot slot) => slot.Kind == SlotKind.Action ? catalog.LabelOf((InputAction)slot.Index) : catalog.LabelOf((InputAxis)slot.Index, slot.Positive);

    /// <summary>True when the slot is bound as the shipped file says.</summary>
    public bool IsDefault(ControlSlot slot) =>
        slot.Kind == SlotKind.Action
            ? map.IsDefault((InputAction)slot.Index)
            : map.AxisKey((InputAxis)slot.Index, slot.Positive) == map.DefaultAxisKey((InputAxis)slot.Index, slot.Positive);

    /// <summary>True when anything differs from the shipped bindings.</summary>
    public bool AnyChanged()
    {
        foreach (var slot in AllSlots())
            if (!IsDefault(slot))
                return true;
        return false;
    }

    /// <summary>True when the control at <paramref name="index"/> can be taken away: an action that must stay bound keeps its last one.</summary>
    public bool CanRemove(ControlSlot slot, int index) =>
        index >= 0 && index < Count(slot) && !(slot.Kind == SlotKind.Action && Count(slot) == 1 && catalog.IsRequired((InputAction)slot.Index));

    /// <summary>True when another control can be added: an axis half holds one, an action up to <see cref="ActionMap.MaxBindings"/>.</summary>
    public bool CanAdd(ControlSlot slot) => slot.Kind == SlotKind.Action && Count(slot) < ActionMap.MaxBindings;

    /// <summary>The other slots that use <paramref name="binding"/> in a view <paramref name="slot"/> is also read in.</summary>
    public ControlSlot[] ConflictsOf(ControlSlot slot, InputBinding binding)
    {
        var scope = Scope(slot);
        var found = new List<ControlSlot>();
        foreach (var other in AllSlots())
        {
            if (other == slot || (Scope(other) & scope) == 0)
                continue;
            for (var i = 0; i < Count(other); i++)
                if (Get(other, i) == binding)
                {
                    found.Add(other);
                    break;
                }
        }

        return [.. found];
    }

    /// <summary>
    /// Binds a captured control. With no clash it is applied and null is returned; with one, nothing changes and the
    /// proposal comes back for the player to resolve with <see cref="Resolve"/>.
    /// </summary>
    public RemapProposal? Propose(ControlSlot slot, int index, InputBinding binding)
    {
        var conflicts = ConflictsOf(slot, binding);
        if (conflicts.Length > 0)
            return new RemapProposal(slot, index, binding, conflicts);
        Place(slot, index, binding);
        Persist();
        return null;
    }

    /// <summary>True when a swap is possible for the proposal: one other slot, and this one gave up a control to hand over.</summary>
    public bool CanSwap(RemapProposal proposal) => proposal.Conflicts.Length == 1 && proposal.Index >= 0 && proposal.Index < Count(proposal.Slot);

    /// <summary>True when every other slot can give the control up: an action that must stay bound keeps its last one.</summary>
    public bool CanClearOthers(RemapProposal proposal)
    {
        foreach (var other in proposal.Conflicts)
            if (!CanRemove(other, IndexOf(other, proposal.Binding)))
                return false;
        return true;
    }

    /// <summary>Applies a proposal the player resolved; false, with nothing changed, when the choice is not allowed.</summary>
    public bool Resolve(RemapProposal proposal, ConflictChoice choice)
    {
        if (choice == ConflictChoice.Swap)
        {
            if (!CanSwap(proposal))
                return false;
            var given = Get(proposal.Slot, proposal.Index);
            var other = proposal.Conflicts[0];
            var at = IndexOf(other, proposal.Binding);
            Place(proposal.Slot, proposal.Index, proposal.Binding);
            if (at >= 0)
                Place(other, at, given);
        }
        else
        {
            if (!CanClearOthers(proposal))
                return false;
            foreach (var other in proposal.Conflicts)
                Take(other, IndexOf(other, proposal.Binding));
            Place(proposal.Slot, proposal.Index, proposal.Binding);
        }

        Persist();
        return true;
    }

    private int IndexOf(ControlSlot slot, InputBinding binding)
    {
        for (var i = 0; i < Count(slot); i++)
            if (Get(slot, i) == binding)
                return i;
        return -1;
    }

    /// <summary>Takes one control from a slot; false when a required action would be left bare.</summary>
    private bool Take(ControlSlot slot, int index)
    {
        if (!CanRemove(slot, index))
            return false;
        if (slot.Kind == SlotKind.AxisHalf)
        {
            map.SetAxisKey((InputAxis)slot.Index, slot.Positive, null);
            return true;
        }

        var action = (InputAction)slot.Index;
        var list = map.Bindings(action).ToArray().ToList();
        list.RemoveAt(index);
        map.SetBindings(action, [.. list]);
        return true;
    }

    private void Place(ControlSlot slot, int index, InputBinding binding)
    {
        if (slot.Kind == SlotKind.AxisHalf)
        {
            map.SetAxisKey((InputAxis)slot.Index, slot.Positive, binding);
            return;
        }

        var action = (InputAction)slot.Index;
        var list = map.Bindings(action).ToArray().ToList();
        if (index >= 0 && index < list.Count)
            list[index] = binding;
        else
            list.Add(binding);
        map.SetBindings(action, [.. list]);
    }

    /// <summary>Takes one control away from a slot and stores the change; false when it is not allowed.</summary>
    public bool Remove(ControlSlot slot, int index)
    {
        if (!Take(slot, index))
            return false;
        Persist();
        return true;
    }

    /// <summary>Puts one slot back as the shipped file has it.</summary>
    public void Reset(ControlSlot slot)
    {
        if (slot.Kind == SlotKind.Action)
            map.SetBindings((InputAction)slot.Index, map.DefaultBindings((InputAction)slot.Index));
        else
            map.SetAxisKey((InputAxis)slot.Index, slot.Positive, map.DefaultAxisKey((InputAxis)slot.Index, slot.Positive));
        Persist();
    }

    /// <summary>Puts every control back as the shipped file has it.</summary>
    public void ResetAll()
    {
        map.ResetAll();
        Persist();
    }

    /// <summary>
    /// Makes the settings hold exactly the slots that differ from the defaults, leaving names this version does not
    /// know alone. The caller saves the settings.
    /// </summary>
    public void Persist()
    {
        var overrides = settings.ControlOverrides;
        foreach (var slot in AllSlots())
        {
            var key = slot.Key;
            if (IsDefault(slot))
            {
                overrides.Remove(key);
                continue;
            }

            var list = new List<string>(Count(slot));
            for (var i = 0; i < Count(slot); i++)
                list.Add(Get(slot, i).FileName);
            overrides[key] = list;
        }
    }
}
