using Prospect.Game.World;

namespace Prospect.Game.Server;

/// <summary>Preallocated mailboxes keep generation and its allocations off the simulation thread.</summary>
internal sealed class ModuleSurveyWorker : IDisposable
{
    internal sealed class Slot
    {
        // 0 free, 1 queued, 2 worker-owned, 3 ready. Release/acquire transitions transfer the whole mailbox.
        internal int State;
        internal ModuleScanRequest Request;
        internal Double3 Origin;
        internal double Range;
        internal uint Owner;
        internal readonly ModuleObservation[] Results = new ModuleObservation[ModuleScanRules.PulseResults];
        internal int Count;
        internal bool Partial;
        internal ModuleScanState Failure;
    }

    internal readonly Slot[] Slots;
    private readonly Galaxy _galaxy;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private bool _stop;

    internal ModuleSurveyWorker(Galaxy galaxy, int connections)
    {
        _galaxy = galaxy;
        Slots = new Slot[connections];
        for (var i = 0; i < connections; i++) Slots[i] = new();
        _thread = new Thread(Run) { IsBackground = true, Name = "Module surveys" };
        _thread.Start();
    }

    internal bool Start(int index, ModuleScanRequest request, Double3 origin, double range, uint owner)
    {
        var slot = Slots[index];
        if (Volatile.Read(ref slot.State) != 0) return false;
        slot.Request = request; slot.Origin = origin; slot.Range = range; slot.Owner = owner;
        Volatile.Write(ref slot.State, 1);
        _wake.Set();
        return true;
    }

    private void Run()
    {
        var sector = new SectorContent();
        while (!Volatile.Read(ref _stop))
        {
            _wake.WaitOne();
            foreach (var slot in Slots)
            {
                if (Volatile.Read(ref _stop)) return;
                if (Interlocked.CompareExchange(ref slot.State, 2, 1) != 1) continue;
                slot.Count = 0; slot.Partial = false; slot.Failure = ModuleScanState.Idle;
                try { Prepare(slot, sector); }
                catch (Exception) { slot.Count = 0; slot.Failure = ModuleScanState.Unavailable; }
                Volatile.Write(ref slot.State, 3);
            }
        }
    }

    private void Prepare(Slot slot, SectorContent sector)
    {
        var request = slot.Request;
        if (request.Family == ModuleFamily.GasScanner)
        {
            if (_galaxy.GasClouds is not { } gas || _galaxy.GasComposition is not { } mixtures
                || !gas.TryGetCloud(_galaxy, request.Sector, out var cloud))
            { slot.Failure = ModuleScanState.InvalidTarget; return; }
            var placement = new GasCloudPlacement(request.Sector, cloud);
            var center = GasCloudQuery.CenterOf(_galaxy, placement);
            var d = slot.Origin - center;
            var x = d.X / (cloud.HalfSize.X * (double)gas.Shape.X);
            var y = d.Y / (cloud.HalfSize.Y * (double)gas.Shape.X);
            var z = d.Z / (cloud.HalfSize.Z * (double)gas.Shape.X);
            var length = Math.Sqrt(x * x + y * y + z * z);
            var position = length > 0.95 ? center + d * (0.95 / length) : slot.Origin;
            var sample = mixtures.Sample(_galaxy, placement, position);
            slot.Results[slot.Count++] = new ModuleObservation
            {
                Owner = slot.Owner,
                Family = request.Family,
                Sector = request.Sector,
                Index = sample.Region,
                Position = position,
                Resource = -1,
                Gas = sample
            };
            return;
        }
        if (request.Family == ModuleFamily.AsteroidScanner)
        {
            _galaxy.Generate(request.Sector, sector);
            if ((uint)request.Index >= (uint)sector.AsteroidCount)
            { slot.Failure = ModuleScanState.InvalidTarget; return; }
            Add(slot, request.Sector, request.Index, sector.Asteroids[request.Index]);
            return;
        }
        if (request.Family != ModuleFamily.AsteroidLocator || slot.Range > _galaxy.SectorSize)
        { slot.Failure = ModuleScanState.OutOfRange; return; }
        var origin = _galaxy.SectorOf(slot.Origin);
        for (var y = -1; y <= 1; y++)
            for (var z = -1; z <= 1; z++)
                for (var x = -1; x <= 1; x++)
                {
                    if (Volatile.Read(ref _stop)) return;
                    var coord = origin.Offset(x, y, z);
                    _galaxy.Generate(coord, sector);
                    for (var i = 0; i < sector.AsteroidCount; i++)
                    {
                        var rock = sector.Asteroids[i];
                        var position = _galaxy.CenterOf(coord) + rock.LocalPosition;
                        if (!ModuleScanRules.InRange(slot.Origin, position, 0, slot.Range)) continue;
                        if (slot.Count == slot.Results.Length) { slot.Partial = true; return; }
                        Add(slot, coord, i, rock);
                    }
                }
    }

    private void Add(Slot slot, SectorCoord sector, int index, in Asteroid rock)
    {
        var solid = rock.Radius * RockField.SolidFraction;
        slot.Results[slot.Count++] = new ModuleObservation
        {
            Owner = slot.Owner,
            Family = slot.Request.Family,
            Sector = sector,
            Index = index,
            Position = _galaxy.CenterOf(sector) + rock.LocalPosition,
            Radius = rock.Radius,
            Mass = _galaxy.AsteroidDensity * 4.18879f * solid * solid * solid,
            Resource = rock.Resource,
            Grade = rock.Grade
        };
    }

    public void Dispose()
    {
        Volatile.Write(ref _stop, true); _wake.Set(); _thread.Join(); _wake.Dispose();
    }
}
