using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>Bounded, ordered residency shared by requests and slot protection.</summary>
internal sealed class SectorRequests
{
    private readonly SectorCoord[] _coordinates = new SectorCoord[102];
    private int _count;
    private SectorCoord _camera;
    private SectorCoord? _focus;
    internal ReadOnlySpan<SectorCoord> Coordinates => _coordinates.AsSpan(0, _count);
    internal bool Contains(SectorCoord coord) => Coordinates.Contains(coord);
    internal bool Update(SectorCoord camera, SectorCoord? focus)
    {
        if (_count != 0 && _camera == camera && _focus == focus) return false;
        _camera = camera;
        _focus = focus;
        _count = 0;
        if (focus is { } subject) Add(subject);
        Add(camera);
        if (focus is { } neighborhood) Block(neighborhood, 1, 1);
        Block(camera, 2, 1);
        return true;
    }
    private void Add(SectorCoord coord)
    {
        if (!Contains(coord)) _coordinates[_count++] = coord;
    }
    private void Block(SectorCoord center, int horizontal, int vertical)
    {
        // Near shells are queued first; integer traversal breaks ties reproducibly.
        for (var shell = 1; shell <= horizontal; shell++)
            for (var x = -shell; x <= shell; x++)
                for (var y = -vertical; y <= vertical; y++)
                    for (var z = -shell; z <= shell; z++)
                        if (Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) == shell)
                            Add(center.Offset(x, y, z));
    }
}
