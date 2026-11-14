using Prospect.Client.Settings;

namespace Prospect.Client.Screens;

/// <summary>
/// What is on screen of the in-game tips: the tips waiting to be read, which one is shown, and which the player has
/// hidden for good. No fonts and no drawing, so the rules can be tested without either.
/// <list type="bullet">
/// <item>A trigger fires every tip that names it, in file order, except those already hidden or already waiting.
/// Tips fired together or one after another stack: the panel shows one at a time with a counter.</item>
/// <item><see cref="Next"/> goes to the following tip and wraps. <see cref="Close"/> closes the whole stack.</item>
/// <item>"Don't show this again" is ticked for the tip shown. When that tip is left, by Next or Close, it is saved
/// as hidden and never fires again on this client. A tip closed without it fires again with its next trigger.</item>
/// <item>Hidden tips live in <see cref="ClientSettings.HiddenTips"/>; <see cref="Reset"/> empties them.</item>
/// </list>
/// Nothing here allocates once built, so it may run in the frame loop.
/// </summary>
internal sealed class TipBoard
{
    private readonly TipCatalogue _catalogue;
    private readonly ClientSettings _settings;
    private readonly int[] _queue;
    private int _count;
    private int _index;
    private bool _changed;

    public TipBoard(TipCatalogue catalogue, ClientSettings settings)
    {
        _catalogue = catalogue;
        _settings = settings;
        _queue = new int[catalogue.Tips.Length];
    }

    /// <summary>
    /// False while tips are off, as in a scripted capture or benchmark that did not ask for them: firing then does
    /// nothing, so a run's pictures and timings are not changed by a hint over them.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True while a stack is waiting to be read.</summary>
    public bool Active => _count > 0;

    /// <summary>How many tips are in the stack.</summary>
    public int Count => _count;

    /// <summary>The position of the tip shown, from zero.</summary>
    public int Index => _index;

    /// <summary>The tip shown; only valid while <see cref="Active"/>.</summary>
    public Tip Current => _catalogue.Tips[_queue[_index]];

    /// <summary>True when "Don't show this again" is ticked for the tip shown.</summary>
    public bool DontShowAgain { get; set; }

    /// <summary>Brings up every tip for <paramref name="trigger"/> that is not hidden or already waiting. Returns how many were added.</summary>
    public int Fire(string trigger)
    {
        if (!Enabled)
            return 0;
        var added = 0;
        var tips = _catalogue.Tips;
        for (var i = 0; i < tips.Length; i++)
        {
            if (tips[i].Trigger != trigger || IsHidden(tips[i].Id) || Waiting(i))
                continue;
            _queue[_count++] = i;
            added++;
        }

        return added;
    }

    /// <summary>
    /// Takes the tips of <paramref name="trigger"/> out of the stack because what they were about is gone from the
    /// screen: a map tip has no place in the command view. Nothing is hidden for good, so the tips come again with
    /// their next trigger, except that the one shown with "Don't show this again" ticked is hidden, as leaving it
    /// by Next would. Returns how many were taken out.
    /// </summary>
    public int Withdraw(string trigger)
    {
        var kept = 0;
        var removed = 0;
        var atShown = 0;
        var shownGone = false;
        var tips = _catalogue.Tips;
        for (var i = 0; i < _count; i++)
        {
            if (i == _index)
                atShown = kept;
            if (tips[_queue[i]].Trigger == trigger)
            {
                removed++;
                if (i == _index)
                {
                    shownGone = true;
                    if (DontShowAgain)
                        Hide(tips[_queue[i]].Id);
                }

                continue;
            }

            _queue[kept++] = _queue[i];
        }

        if (removed == 0)
            return 0;
        _count = kept;
        // The tip shown stays shown when it stays; when it went, the next one that stayed takes its place.
        _index = atShown >= kept ? 0 : atShown;
        if (shownGone)
            DontShowAgain = false;
        return removed;
    }

    /// <summary>True when the player hid the tip for good.</summary>
    public bool IsHidden(string id)
    {
        foreach (var hidden in _settings.HiddenTips)
            if (hidden == id)
                return true;
        return false;
    }

    /// <summary>True when the tip at <paramref name="tip"/> in the catalogue is in the stack.</summary>
    public bool Waiting(int tip)
    {
        for (var i = 0; i < _count; i++)
            if (_queue[i] == tip)
                return true;
        return false;
    }

    /// <summary>
    /// Leaves the tip shown for the next, wrapping after the last. A tip ticked "Don't show this again" is hidden
    /// and leaves the stack; the one after it is shown next.
    /// </summary>
    public void Next()
    {
        if (_count == 0)
            return;
        if (DontShowAgain)
        {
            Hide(_catalogue.Tips[_queue[_index]].Id);
            DontShowAgain = false;
            Array.Copy(_queue, _index + 1, _queue, _index, _count - _index - 1);
            _count--;
        }
        else
        {
            _index++;
        }

        if (_index >= _count)
            _index = 0;
    }

    /// <summary>Closes the stack. The tip shown, if ticked, is hidden for good; every other fires again.</summary>
    public void Close()
    {
        if (_count > 0 && DontShowAgain)
            Hide(_catalogue.Tips[_queue[_index]].Id);
        Clear();
    }

    /// <summary>
    /// Escape: closes the stack when one is <paramref name="shown"/> and Escape was <paramref name="pressed"/>, and
    /// says so, so the caller keeps that press from every other surface. Nothing else Escape does comes before this.
    /// </summary>
    public bool Escape(bool shown, bool pressed)
    {
        if (!shown || !pressed || _count == 0)
            return false;
        Close();
        return true;
    }

    /// <summary>Drops the stack and the tick without hiding anything: a new session starts with nothing waiting.</summary>
    public void Clear()
    {
        _count = _index = 0;
        DontShowAgain = false;
    }

    /// <summary>Shows every tip again: forgets which were hidden. A stack already waiting stays.</summary>
    public void Reset()
    {
        if (_settings.HiddenTips.Count == 0)
            return;
        _settings.HiddenTips.Clear();
        _changed = true;
    }

    /// <summary>True once after the hidden list changed, so the caller can save the settings.</summary>
    public bool TakeChanged()
    {
        var changed = _changed;
        _changed = false;
        return changed;
    }

    private void Hide(string id)
    {
        if (IsHidden(id) || _settings.HiddenTips.Count >= ClientSettings.MaxHiddenTips)
            return;
        _settings.HiddenTips.Add(id);
        _changed = true;
    }
}
