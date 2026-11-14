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

    /// <summary>Forgets what was last seen: a new game starts fresh.</summary>
    public void Reset() => _primed = false;

    /// <summary>
    /// Follows one frame's scans and new-ship answer, posting to <paramref name="board"/>. <paramref name="ownHull"/> is
    /// the packed id of the hull flown, zero when none.
    /// </summary>
    public void Update(NoticeBoard board, in ModuleScanView module, in ScanView sector, in NewShipView newShip, uint ownHull)
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
}
