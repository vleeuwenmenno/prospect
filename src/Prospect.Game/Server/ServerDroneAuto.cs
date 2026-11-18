using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

/// <summary>
/// Auto pickup: a hull with the setting on sends its drones after loose containers by itself. It is the manual
/// fetch, not a second path: it picks a container and calls <see cref="StartFetch"/>, so the same refusals, claims,
/// pickup delay, range, carriers and tractor hand-over apply. A tick looks at a bounded, rotating window of the
/// 256-body pool and takes no memory.
/// </summary>
public sealed partial class ServerHost
{
    /// <summary>
    /// Once a tick for each hull with bays: when auto pickup is on, looks at the next
    /// <see cref="DroneRules.AutoPickupWindow"/> containers and starts a fetch for each the owner may take that is in
    /// range and in sight, that nobody (a tractor, other drones) has, that its own tractor cannot reach by itself and
    /// that a fetch was not refused for lately. Every free drone goes out, one fetch a body; a hull therefore brings
    /// several containers home at once, up to <see cref="DroneRules.AutoPickupMaxFetches"/> fetches, and the drones a
    /// fetch took are gone for the next. A refusal is told to the owner like a manual one, and the container is left
    /// alone for <see cref="DroneRules.AutoPickupRetrySeconds"/>, so a container no drone can lift never makes the
    /// setting nag every tick.
    /// </summary>
    private void TickAutoPickup(int index)
    {
        ref var page = ref _bayPages[index];
        if (!page.AutoPickup || _tick < page.AutoPausedUntil) return;
        var items = _containers.Items;
        if (items.IsEmpty) return;
        var hull = page.Hull;
        var info = _hulls.Info(hull.Index);
        if (info.Wreck || _hulls.State(hull.Index).Jump != JumpPhase.None) return;

        // What is left of the hull's fetches: a fetch that is over leaves its drones holding station, which the next
        // one takes where they are instead of launching from a bay.
        var rules = _content.Drones;
        var left = rules.AutoPickupMaxFetches - FetchesOf(hull, index);
        if (left <= 0) return;

        var range = (double)rules.FetchRangeMetres;
        var origin = _hulls.State(hull.Index).Position;
        var window = Math.Min(rules.AutoPickupWindow, items.Length);
        var start = page.AutoCursor % items.Length;
        var k = 0;
        for (; k < window && left > 0; k++)
        {
            var c = (start + k) % items.Length;
            ref readonly var item = ref items[c];
            if (item.TractorHull != 0 || item.FetchHull != 0 || !item.MayPickup(info.Owner, _tick)) continue;
            if (item.Position.RelativeTo(origin).LengthSquared() > range * range) continue;
            if (AutoSkipped(ref page, item.Id) || !Covered(info.Owner, item.Position)) continue;
            if (TractorSimulation.Reaches(_content, _field, hull.Index, item)) continue;
            // One drone a body at least: with none free there is nothing more to start this tick.
            if (FreeCarriers(hull, index, info.Owner) == 0) break;

            page.AutoCursor = (c + 1) % items.Length;
            var id = item.Id;
            var reply = StartFetch(info.Owner, hull, index, id, out var carriers);
            ReplyDrones(info.Owner, reply, carriers);
            if (reply == DroneReply.Fetching) { left--; continue; }
            ref var slot = ref page.AutoSkip[page.AutoSkipNext];
            slot.Id = id;
            slot.Until = _tick + rules.AutoPickupRetryTicks;
            page.AutoSkipNext = (page.AutoSkipNext + 1) % AutoSkipSlots;
        }
        if (k >= window) page.AutoCursor = (start + window) % items.Length;
    }

    private bool AutoSkipped(ref BayPage page, uint id)
    {
        for (var i = 0; i < AutoSkipSlots; i++)
            if (page.AutoSkip[i].Id == id && _tick < page.AutoSkip[i].Until) return true;
        return false;
    }
}
