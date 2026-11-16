using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>Incremental draft reach cache. Fixed storage; edits restart bounded work without allocating.</summary>
internal sealed class TractorPreview(int maxMounts)
{
    private const int Blocks = GeometryAdmission.MaxBlocks;
    private readonly byte[] _links = new byte[maxMounts * Blocks];
    private readonly bool[] _reached = new bool[GeometryAdmission.MaxBlocks];
    private readonly int[] _mounts = new int[maxMounts];
    private readonly int[] _racks = new int[GeometryAdmission.MaxBlocks];
    private BuildDraft? _draft;
    private int _version = -1, _mount, _rack, _mountCount, _rackCount;
    public bool Complete { get; private set; }
    public bool Supported { get; private set; }
    public int Unreached { get; private set; }
    public int Racks => _rackCount;
    public bool Reached(int rack) => (uint)rack < (uint)_reached.Length && _reached[rack];
    public bool Linked(int mount, int rack) => (uint)mount < (uint)maxMounts && (uint)rack < GeometryAdmission.MaxBlocks
        && _links[mount * Blocks + rack] != 0;
    public BlockFacing Face(int mount, int rack) => (BlockFacing)(_links[mount * Blocks + rack] - 1);

    public void Update(BuildDraft? draft, int budget)
    {
        if (!ReferenceEquals(_draft, draft) || draft is not null && _version != draft.Version)
        {
            _draft = draft; _version = draft?.Version ?? -1; _mount = _rack = _mountCount = _rackCount = 0;
            Array.Clear(_links); Array.Clear(_reached); Complete = false; Unreached = 0;
            Supported = draft is not null && draft.Boxes.Length <= GeometryAdmission.MaxBlocks && draft.Mounts.Length <= maxMounts;
            if (Supported)
            {
                for (var i = 0; i < draft!.Mounts.Length; i++) if (draft.Mounts[i].Type.Tractor is not null) _mounts[_mountCount++] = i;
                for (var i = 0; i < draft.Boxes.Length; i++) if (draft.Boxes[i].Type.Cargo > 0) _racks[_rackCount++] = i;
            }
        }
        if (draft is null || !Supported || Complete) return;
        while (_mount < _mountCount && _rackCount > 0 && budget-- > 0)
        {
            var mount = _mounts[_mount]; var rack = _racks[_rack];
            if (TractorReach.Rack(draft.Boxes, draft.Mounts, [], draft.BlockSize, mount, rack, out _, out var side))
            { _links[mount * Blocks + rack] = (byte)((int)side + 1); _reached[rack] = true; }
            if (++_rack == _rackCount) { _rack = 0; _mount++; }
        }
        if (_mount >= _mountCount || _rackCount == 0)
        {
            Complete = true;
            for (var i = 0; i < _rackCount; i++) if (!_reached[_racks[i]]) Unreached++;
        }
    }
}
