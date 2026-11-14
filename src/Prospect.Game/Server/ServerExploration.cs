using Prospect.Engine.Net;
using Prospect.Game.Persistence;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private readonly SectorChart[] _charts;
    private int _chartCount;

    internal ReadOnlySpan<SectorChart> Charts => _charts.AsSpan(0, _chartCount);

    private bool Equipped(int slot, bool charting)
    {
        var design = _content.Designs[_hulls.Info(slot).Design];
        var alive = _hulls.Blocks(slot);
        foreach (var b in charting ? design.ChartingBlocks : design.SensorBlocks)
            if (BlockSet.Has(alive, b)) return true;
        return false;
    }

    private void StartScan(Connection connection)
    {
        if (connection.Scan.State == ScanState.Scanning) return;

        // A refusal changes only the state: the cooldown from the last scan is kept, so a refused request never
        // shortens or lengthens it.
        var scan = new ScanView
        {
            State = ScanState.NoEquipment,
            Hull = connection.Hull.Packed,
            CooldownSector = connection.Scan.CooldownSector,
            CooldownUntilTick = connection.Scan.CooldownUntilTick,
        };
        connection.Scan = scan;
        if (!_hulls.IsAlive(connection.Hull) || !Equipped(connection.Hull.Index, true)) return;
        var sector = Galaxy.SectorOf(_hulls.State(connection.Hull.Index).Position);
        if (scan.CoolingDown(sector, _tick))
        {
            connection.Scan.State = ScanState.Cooldown;
            connection.Scan.Sector = sector;
            return;
        }

        scan.State = ScanState.Scanning;
        scan.Sector = sector;
        scan.StartedTick = _tick;
        scan.CompletesTick = _tick + _content.Exploration.ScanTicks;
        connection.Scan = scan;
    }

    /// <summary>
    /// The player's own request to stop the scan it started. Only the sender's running scan is touched: a scan is held
    /// per connection, so another player's cannot be reached, and with none running the request changes nothing, not
    /// even the state a client shows. The cooldown starts as for any other cancellation.
    /// </summary>
    private void CancelScan(Connection connection)
    {
        if (connection.Scan.State == ScanState.Scanning) EndScan(connection, ScanState.Cancelled, _tick);
    }

    /// <summary>
    /// Ends a running scan in <paramref name="state"/> and starts its sector's cooldown, whichever way it ended: a
    /// cancelled scan costs the wait too, so starting and cancelling is no way round it.
    /// </summary>
    private void EndScan(Connection connection, ScanState state, long tick)
    {
        ref var scan = ref connection.Scan;
        scan.State = state;
        scan.CooldownSector = scan.Sector;
        scan.CooldownUntilTick = tick + _content.Exploration.ScanCooldownTicks;
    }

    private void TickExploration(long tick)
    {
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || connection.Scan.State != ScanState.Scanning) continue;
            ref var scan = ref connection.Scan;
            // A scan belongs to the ship that began it: when another hull is controlled (normally ResetControl has
            // already ended it) it stops without a result, because there is nothing to tell the player about a ship
            // they no longer fly.
            if (!_hulls.IsAlive(connection.Hull) || connection.Hull.Packed != scan.Hull)
            {
                EndScan(connection, ScanState.Idle, tick);
                continue;
            }
            if (Galaxy.SectorOf(_hulls.State(connection.Hull.Index).Position) != scan.Sector
                || !Equipped(connection.Hull.Index, true))
            {
                EndScan(connection, ScanState.Cancelled, tick);
                continue;
            }
            if (tick < scan.CompletesTick) continue;
            EndScan(connection, RecordChart(connection.PlayerId, scan.Sector, tick) ? ScanState.Completed : ScanState.ChartLimit, tick);
        }
    }

    private void SendExploration(Connection connection)
    {
        if (!connection.CatalogueReady) return;
        var navigation = connection.Navigation;
        var sentNavigation = connection.SentNavigation;
        if (!connection.NavigationSent || navigation.Hull != sentNavigation.Hull || navigation.ResultHull != sentNavigation.ResultHull
            || navigation.HasPlot != sentNavigation.HasPlot || navigation.Target != sentNavigation.Target || navigation.Refusal != sentNavigation.Refusal)
        {
            var navigationWriter = new NetWriter(_sendBuffer);
            NavigationMessage.Write(ref navigationWriter, MessageId.Navigation, navigation);
            if (connection.Transport.Send(connection.Peer, NetChannel.Reliable, navigationWriter.Written))
            { connection.SentNavigation = navigation; connection.NavigationSent = true; }
        }
        Span<SectorChart> charts = stackalloc SectorChart[ExplorationMessage.MaxCharts];
        var count = 0;
        var cursor = connection.ChartCursor;
        var visits = 0;
        while (cursor < _chartCount && count < charts.Length && visits++ < _content.Exploration.ChartsPerOwner)
        {
            var chart = _charts[cursor++];
            if (chart.Owner == connection.PlayerId && (!connection.SentCharts.TryGetValue(chart.Sector, out var sent) || sent != chart.RecordedTick)) charts[count++] = chart;
        }
        var scan = connection.Scan;
        var previous = connection.SentScan;
        if (connection.ExplorationSent && count == 0 && scan.State == previous.State && scan.Hull == previous.Hull && scan.Sector == previous.Sector
            && scan.StartedTick == previous.StartedTick && scan.CompletesTick == previous.CompletesTick
            && scan.CooldownSector == previous.CooldownSector && scan.CooldownUntilTick == previous.CooldownUntilTick)
        { connection.ChartCursor = cursor; return; }
        var writer = new NetWriter(_sendBuffer);
        ExplorationMessage.Write(ref writer, scan, charts[..count]);
        if (connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written))
        {
            connection.ChartCursor = cursor; connection.SentScan = scan; connection.ExplorationSent = true;
            foreach (var chart in charts[..count]) connection.SentCharts[chart.Sector] = chart.RecordedTick;
        }
    }

    internal bool RecordChart(uint owner, SectorCoord sector, long tick)
    {
        var owned = 0;
        for (var i = 0; i < _chartCount; i++)
        {
            if (_charts[i].Owner != owner) continue;
            owned++;
            if (_charts[i].Sector != sector) continue;
            _charts[i] = new(owner, sector, tick);
            foreach (var c in _connections)
                if (c.PlayerId == owner) c.ChartCursor = Math.Min(c.ChartCursor, i);
            _touched = true;
            return true;
        }
        if (!HasRoomForChart(owned)) return false;
        _charts[_chartCount++] = new(owner, sector, tick);
        _touched = true;
        return true;
    }

    private bool HasRoomForChart(int owned)
    {
        if (owned < _content.Exploration.ChartsPerOwner && _chartCount < _charts.Length) return true;
        if (_save is not null) Volatile.Write(ref _refusedGrowth, _refusedGrowth + 1);
        return false;
    }

    private void RestoreCharts(WorldSnapshot world)
    {
        if ((uint)world.ChartCount > (uint)world.Charts.Length || world.ChartCount > _charts.Length)
            throw new InvalidDataException("Too many saved charts.");
        var seen = new HashSet<(uint, SectorCoord)>();
        var counts = new Dictionary<uint, int>();
        foreach (var chart in world.Charts.AsSpan(0, world.ChartCount))
        {
            if (OwnerSlot(chart.Owner) < 0 || chart.RecordedTick < 0 || chart.RecordedTick >= world.NextWorldTick
                || !seen.Add((chart.Owner, chart.Sector)))
                throw new InvalidDataException("Invalid saved chart.");
            counts.TryGetValue(chart.Owner, out var count);
            if (++count > _content.Exploration.ChartsPerOwner) throw new InvalidDataException("Owner chart limit exceeded.");
            counts[chart.Owner] = count;
        }
        world.Charts.AsSpan(0, world.ChartCount).CopyTo(_charts);
        _chartCount = world.ChartCount;
    }
}
