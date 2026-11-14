using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Client;

internal sealed partial class ClientWorld
{
    private readonly ulong[] _visible = new ulong[BlockSet.WordCount(HullStore.MaxHulls)];
    private readonly Dictionary<uint, SensorContact> _contacts = new(HullStore.MaxHulls);
    private readonly uint[] _contactSlots = new uint[HullStore.MaxHulls];
    private bool _contactsComplete = true;
    private long _visibilityTick = -1;
    private SectorChart[] _charts = [];
    private int _chartCount;
    private ScanView _scan;

    public void OnExploration(ref NetReader reader)
    {
        Span<SectorChart> charts = stackalloc SectorChart[ExplorationMessage.MaxCharts];
        if (!ExplorationMessage.Read(ref reader, charts, out var scan, out var count)) return;
        for (var i = 0; i < count; i++) if (charts[i].Owner != _playerId) return;
        _scan = scan;
        foreach (var chart in charts[..count])
        {
            var index = 0;
            while (index < _chartCount && _charts[index].Sector != chart.Sector) index++;
            if (index == _charts.Length) continue;
            if (index == _chartCount) _chartCount++;
            if (_charts[index].RecordedTick <= chart.RecordedTick) _charts[index] = chart;
        }
    }

    public void OnVisibility(ref NetReader reader)
    {
        var tick = reader.ReadInt64();
        var words = reader.ReadUInt16();
        if (!reader.IsValid || tick < 0 || tick <= _visibilityTick || words > _visible.Length || reader.Remaining != words * sizeof(ulong)) return;
        _visibilityTick = tick;
        for (var i = 0; i < words; i++) _visible[i] = reader.ReadUInt64();
        _visible.AsSpan(words).Clear();
        foreach (var pair in _contacts)
        {
            var contact = pair.Value;
            if (!contact.Live || BlockSet.Has(_visible, (int)(contact.Hull & 65535))) continue;
            contact.Live = false;
            contact.LostTick = tick;
            _contacts[pair.Key] = contact;
            ClearHeld(contact.Hull);
        }
        for (var i = 0; i < _remotes.Length; i++)
            if (_remotes[i].Used && !BlockSet.Has(_visible, (int)(_remotes[i].Id & 65535))) Forget(ref _remotes[i]);
    }

    private bool Visible(uint id, long tick) => _visibilityTick < 0 || (tick >= _visibilityTick && BlockSet.Has(_visible, (int)(id & 65535)));

    private bool Observe(in HullRecord record, long tick)
    {
        var slot = (int)(record.Id & 65535);
        if (_contacts.TryGetValue(_contactSlots[slot], out var latest) && latest.ObservedTick > tick) return false;
        if (_contactSlots[slot] is var prior && prior != 0 && prior != record.Id && _contacts.TryGetValue(prior, out var old) && old.Live)
        {
            old.Live = false;
            old.LostTick = tick;
            _contacts[prior] = old;
        }
        if (_contacts.TryGetValue(record.Id, out var contact))
        {
            if (contact.ObservedTick > tick) return false;
        }
        else if (_contacts.Count >= HullStore.MaxHulls)
        {
            _contactsComplete = false;
            return true;
        }
        _contactSlots[slot] = record.Id;
        _contacts[record.Id] = new SensorContact
        {
            Hull = record.Id,
            Owner = record.Owner,
            Design = record.Design,
            Position = record.State.Position,
            ObservedTick = tick,
            Live = true
        };
        return true;
    }

    public void CopyExploration(ExplorationView view, long tick)
    {
        view.Scan = _scan;
        _charts.AsSpan(0, _chartCount).CopyTo(view.Charts);
        view.ChartCount = _chartCount;
        view.ContactCount = 0;
        view.ContactsComplete = _contactsComplete;
        foreach (var pair in _contacts)
        {
            var contact = pair.Value;
            if (contact.Live && tick - contact.ObservedTick > _content.Exploration.ContactTicks)
            { contact.Live = false; contact.LostTick = contact.ObservedTick; _contacts[pair.Key] = contact; }
            if (!contact.Live && _content.Designs[contact.Design].Class != HullClass.Station
                && tick - contact.LostTick >= _content.Exploration.ContactTicks)
            { _contacts.Remove(pair.Key); continue; }
            view.Contacts[view.ContactCount++] = contact;
        }
    }
}
