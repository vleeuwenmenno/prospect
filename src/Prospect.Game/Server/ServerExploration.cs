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
        connection.Scan = new ScanView { State = ScanState.NoEquipment };
        if (!_hulls.IsAlive(connection.Hull) || !Equipped(connection.Hull.Index, true)) return;
        connection.ScanHull = connection.Hull.Packed;
        connection.Scan = new ScanView
        {
            State = ScanState.Scanning,
            Sector = Galaxy.SectorOf(_hulls.State(connection.Hull.Index).Position),
            StartedTick = _tick,
            CompletesTick = _tick + _content.Exploration.ScanTicks,
        };
    }

    private void TickExploration(long tick)
    {
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || connection.Scan.State != ScanState.Scanning) continue;
            ref var scan = ref connection.Scan;
            if (!_hulls.IsAlive(connection.Hull) || connection.Hull.Packed != connection.ScanHull
                || Galaxy.SectorOf(_hulls.State(connection.Hull.Index).Position) != scan.Sector
                || !Equipped(connection.Hull.Index, true))
            {
                scan.State = ScanState.Cancelled;
                continue;
            }
            if (tick < scan.CompletesTick) continue;
            scan.State = RecordChart(connection.PlayerId, scan.Sector, tick) ? ScanState.Completed : ScanState.ChartLimit;
        }
    }

    private void SendExploration(Connection connection)
    {
        if (!connection.CatalogueReady) return;
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
        if (connection.ExplorationSent && count == 0 && scan.State == previous.State && scan.Sector == previous.Sector
            && scan.StartedTick == previous.StartedTick && scan.CompletesTick == previous.CompletesTick)
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
