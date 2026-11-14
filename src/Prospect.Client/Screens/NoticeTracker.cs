using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// Turns the changes the snapshot reports into notices, once each: a module scan that finished, was cancelled or ran
/// out of energy, the sector scan that charted, a ship the New ship flow built. It reads only what the snapshot says
/// and posts only for the player's own hull, as the module chips do. The first snapshot after <see cref="Reset"/> is
/// taken as it is without posting, so a state left over from before is not news. No fonts, no allocation.
/// </summary>
internal sealed class NoticeTracker
{
    private bool _primed;
    private uint _moduleSerial;
    private ModuleScanState _moduleState;
    private ScanState _sectorState;
    private uint _newShipSerial;
    private uint _fuelHull;
    private FuelLevel _nitrogen;
    private FuelLevel _hydrazine;
    private FuelLevel _bipropellant;

    /// <summary>Forgets what was last seen: a new game starts fresh.</summary>
    public void Reset() => _primed = false;

    /// <summary>
    /// Follows one frame's scans and new-ship answer, posting to <paramref name="board"/>. <paramref name="ownHull"/> is
    /// the packed id of the hull flown, zero when none.
    /// </summary>
    public void Update(NoticeBoard board, in ModuleScanView module, in ScanView sector, in NewShipView newShip, uint ownHull, in FuelFlow fuel = default)
    {
        var moduleChanged = module.Serial != _moduleSerial || module.State != _moduleState;
        var sectorChanged = sector.State != _sectorState;
        var shipChanged = newShip.Serial != _newShipSerial;
        var primed = _primed;
        _moduleSerial = module.Serial;
        _moduleState = module.State;
        _sectorState = sector.State;
        _newShipSerial = newShip.Serial;
        _primed = true;
        var fuelPrimed = primed && ownHull != 0 && ownHull == _fuelHull;
        _fuelHull = ownHull;
        TrackFuel(board, fuel, fuelPrimed);
        if (!primed)
            return;

        // A module's result belongs to the hull that ran it; a state left over from another hull is not news.
        if (moduleChanged && ownHull != 0 && module.Hull == ownHull)
        {
            var kind = module.State switch
            {
                ModuleScanState.Completed => module.Family switch
                {
                    ModuleFamily.GasScanner => NoticeKinds.GasResults,
                    ModuleFamily.AsteroidScanner => NoticeKinds.AsteroidResults,
                    ModuleFamily.AsteroidLocator => NoticeKinds.LocatorResults,
                    _ => null,
                },
                ModuleScanState.NoPower => NoticeKinds.ModuleNoPower,
                ModuleScanState.Cancelled => NoticeKinds.ModuleCancelled,
                _ => null,
            };
            if (kind is not null && module.Family is >= ModuleFamily.GasScanner and <= ModuleFamily.AsteroidLocator)
                board.Post(kind);
        }

        if (sectorChanged && sector.State == ScanState.Completed && ownHull != 0 && sector.Hull == ownHull)
            board.Post(NoticeKinds.SectorCharted);

        if (shipChanged && newShip.Status == NewShipStatus.Created && newShip.Hull != 0)
            board.Post(NoticeKinds.ShipBuilt);
    }

    /// <summary>
    /// Posts when an own tank drops under the low mark or runs empty, once for each crossing: a tank that stays low
    /// posts nothing more, one that is filled past the mark may post again. A hull seen for the first time, such as
    /// a new ship, is taken as it is.
    /// </summary>
    private void TrackFuel(NoticeBoard board, in FuelFlow fuel, bool primed)
    {
        Step(board, FuelKind.Nitrogen, ref _nitrogen, ConsoleFuel.Level(fuel, FuelKind.Nitrogen), primed, NoticeKinds.FuelLowNitrogen, NoticeKinds.FuelEmptyNitrogen);
        Step(board, FuelKind.Hydrazine, ref _hydrazine, ConsoleFuel.Level(fuel, FuelKind.Hydrazine), primed, NoticeKinds.FuelLowHydrazine, NoticeKinds.FuelEmptyHydrazine);
        Step(board, FuelKind.Bipropellant, ref _bipropellant, ConsoleFuel.Level(fuel, FuelKind.Bipropellant), primed, NoticeKinds.FuelLowBipropellant, NoticeKinds.FuelEmptyBipropellant);
    }

    private static void Step(NoticeBoard board, FuelKind kind, ref FuelLevel seen, FuelLevel now, bool primed, string low, string empty)
    {
        var before = seen;
        seen = now;
        if (!primed || before == FuelLevel.None || now == before)
            return;
        if (now == FuelLevel.Empty)
            board.Post(empty);
        else if (now == FuelLevel.Low && before == FuelLevel.Ok)
            board.Post(low);
    }
}
