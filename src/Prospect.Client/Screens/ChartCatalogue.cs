using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>How the Charts window orders the charts the player owns.</summary>
internal enum ChartSort : byte
{
    /// <summary>Nearest to the ship being flown first.</summary>
    Distance,

    /// <summary>Most recently surveyed first.</summary>
    Age,

    /// <summary>The sector's name, A to Z.</summary>
    Name,
}

/// <summary>What a chart says of its sector, as far as the Charts window shows it. Worked out once per chart.</summary>
internal readonly record struct ChartDetail(int StarClass, int Fields, int Clouds, bool Cold, bool Lightning);

/// <summary>
/// The Charts window's list: every chart the player owns, filtered by name and ordered by distance, age or name. It
/// is the player's knowledge, read from <see cref="ExplorationView.Charts"/>; nothing is stored anywhere in the
/// world. All its arrays are made once, sized to the content's chart limit, so rebuilding the order or looking a
/// row up takes no memory. The order is rebuilt only when something it depends on changed: the set of charts, the
/// sort, the filter, or the sector the ship being flown is in (distance is measured from that sector's centre, so
/// the order does not shuffle as the ship moves inside it). A row's star, fields and gas are worked out the first
/// time the row is asked for, a few a frame, since each reads the seed's generation.
/// <para>
/// Font-free: <c>ScreenHost</c> draws the rows, and the tests drive the list as it is.
/// </para>
/// </summary>
internal sealed class ChartCatalogue
{
    /// <summary>The most rows' details worked out in one frame.</summary>
    public const int DetailsPerFrame = 3;

    private readonly int _capacity;
    private readonly int _nameLength;
    private readonly int[] _order;
    private readonly double[] _distance;
    private readonly char[] _names;
    private readonly int[] _nameLengths;
    private readonly SectorCoord[] _nameSectors;
    private readonly bool[] _nameKnown;
    private readonly SectorCoord[] _detailSectors;
    private readonly bool[] _detailKnown;
    private readonly ChartDetail[] _details;
    private readonly ChartField[] _fieldScratch = new ChartField[AsteroidFieldsModule.MaxFieldsInReach];
    private readonly GasCloudPlacement[] _cloudScratch = new GasCloudPlacement[GasCloudModule.MaxPerSector];
    private readonly char[] _filter;
    private readonly Comparer _comparer;
    private ExplorationView? _view;
    private Galaxy? _galaxy;
    private int _filterLength;
    private int _count;
    private int _total;
    private uint _chartsSignature;
    private int _chartsCount = -1;
    private SectorCoord _from;
    private ChartSort _builtSort;
    private bool _builtDescending;
    private int _builtFilter = -1;
    private int _detailsLeft;

    /// <summary>The order the list is in; clicking the same one again turns it round.</summary>
    public ChartSort Sort { get; private set; } = ChartSort.Distance;

    /// <summary>True when the order is turned round: farthest, oldest or Z to A first.</summary>
    public bool Descending { get; private set; }

    /// <summary>How many times the order was rebuilt, for tests.</summary>
    public int Rebuilds { get; private set; }

    /// <summary>A list for at most <paramref name="capacity"/> charts, with names up to <paramref name="nameLength"/> characters.</summary>
    public ChartCatalogue(int capacity, int nameLength, int filterLength = 48)
    {
        _capacity = Math.Max(capacity, 1);
        _nameLength = Math.Max(nameLength, 8);
        _order = new int[_capacity];
        _distance = new double[_capacity];
        _names = new char[_capacity * _nameLength];
        _nameLengths = new int[_capacity];
        _nameSectors = new SectorCoord[_capacity];
        _nameKnown = new bool[_capacity];
        _detailSectors = new SectorCoord[_capacity];
        _detailKnown = new bool[_capacity];
        _details = new ChartDetail[_capacity];
        _filter = new char[filterLength];
        _comparer = new Comparer(this);
    }

    /// <summary>How many charts the player owns, whatever the filter.</summary>
    public int Total => _total;

    /// <summary>How many the filter keeps: the rows of the list.</summary>
    public int Count => _count;

    /// <summary>The index in <see cref="ExplorationView.Charts"/> of the chart in a row.</summary>
    public int ChartAt(int row) => _order[row];

    /// <summary>Picks an order; picking the one in use turns it round.</summary>
    public void Choose(ChartSort sort)
    {
        if (sort == Sort)
            Descending = !Descending;
        else
        {
            Sort = sort;
            Descending = false;
        }
    }

    /// <summary>Sets the name filter, as typed; a sector's name matches when it contains the text, ignoring case.</summary>
    public void SetFilter(ReadOnlySpan<char> text)
    {
        var length = Math.Min(text.Length, _filter.Length);
        text = text[..length].Trim();
        if (text.SequenceEqual(_filter.AsSpan(0, _filterLength)))
            return;
        text.CopyTo(_filter);
        _filterLength = text.Length;
    }

    /// <summary>The filter now in force.</summary>
    public ReadOnlySpan<char> Filter => _filter.AsSpan(0, _filterLength);

    /// <summary>
    /// Brings the list up to date with the snapshot's charts and the sector the ship being flown is in, and gives
    /// this frame its budget for rows' details. Cheap when nothing changed.
    /// </summary>
    public void Update(ExplorationView view, Galaxy galaxy, SectorCoord from)
    {
        if (!ReferenceEquals(galaxy, _galaxy))
        {
            _galaxy = galaxy;
            Array.Clear(_nameKnown);
            Array.Clear(_detailKnown);
            _chartsCount = -1;
        }

        _view = view;
        _detailsLeft = DetailsPerFrame;
        var count = Math.Min(view.ChartCount, _capacity);
        var signature = 0u;
        for (var i = 0; i < count; i++)
        {
            var sector = view.Charts[i].Sector;
            signature = signature * 31u + (uint)(sector.X * 73856093 ^ sector.Y * 19349663 ^ sector.Z * 83492791) + (uint)view.Charts[i].RecordedTick;
        }

        var filterHash = Hash(_filter.AsSpan(0, _filterLength));
        if (count == _chartsCount && signature == _chartsSignature && from == _from && Sort == _builtSort
            && Descending == _builtDescending && filterHash == _builtFilter)
            return;

        _chartsCount = count;
        _chartsSignature = signature;
        _from = from;
        _builtSort = Sort;
        _builtDescending = Descending;
        _builtFilter = filterHash;
        Rebuild(view, galaxy, count);
    }

    private static int Hash(ReadOnlySpan<char> text)
    {
        var hash = 17;
        foreach (var c in text)
            hash = hash * 31 + char.ToLowerInvariant(c);
        return hash;
    }

    private void Rebuild(ExplorationView view, Galaxy galaxy, int count)
    {
        Rebuilds++;
        _total = count;
        var filter = _filter.AsSpan(0, _filterLength);
        var kept = 0;
        var centre = galaxy.CenterOf(_from);
        for (var i = 0; i < count; i++)
        {
            var sector = view.Charts[i].Sector;
            EnsureName(galaxy, i, sector);
            if (filter.Length > 0 && !NameOf(i).Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var d = galaxy.CenterOf(sector).RelativeTo(centre);
            _distance[i] = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
            _order[kept++] = i;
        }

        _count = kept;
        Array.Sort(_order, 0, kept, _comparer);
    }

    private void EnsureName(Galaxy galaxy, int index, SectorCoord sector)
    {
        if (_nameKnown[index] && _nameSectors[index] == sector)
            return;
        var span = _names.AsSpan(index * _nameLength, _nameLength);
        Span<char> scratch = stackalloc char[Math.Max(galaxy.MaxNameLength, 8)];
        var length = Math.Min(galaxy.NameOf(sector, scratch), _nameLength);
        scratch[..length].CopyTo(span);
        _nameLengths[index] = length;
        _nameSectors[index] = sector;
        _nameKnown[index] = true;
    }

    /// <summary>The name of the chart at <paramref name="index"/> in <see cref="ExplorationView.Charts"/>, as worked out for the list.</summary>
    public ReadOnlySpan<char> NameOf(int index) => _names.AsSpan(index * _nameLength, _nameLengths[index]);

    /// <summary>
    /// What a chart says of its sector. False while the row's turn has not come: only
    /// <see cref="DetailsPerFrame"/> new rows are worked out in a frame, and the row is drawn without them until then.
    /// </summary>
    public bool TryDetail(int index, out ChartDetail detail)
    {
        detail = default;
        if (_view is null || _galaxy is null || index >= _chartsCount)
            return false;
        var sector = _view.Charts[index].Sector;
        if (_detailKnown[index] && _detailSectors[index] == sector)
        {
            detail = _details[index];
            return true;
        }

        if (_detailsLeft <= 0)
            return false;
        _detailsLeft--;
        var summary = _galaxy.Summarize(sector);
        var fields = _galaxy.AsteroidFields?.ChartFields(_galaxy, sector, _fieldScratch) ?? 0;
        var clouds = _galaxy.GasClouds?.Intersecting(_galaxy, sector, _cloudScratch) ?? 0;
        var cold = false;
        var lightning = false;
        for (var i = 0; i < clouds; i++)
        {
            cold |= _cloudScratch[i].Cloud.Kind == GasCloudKind.Cold;
            lightning |= _cloudScratch[i].Cloud.Kind == GasCloudKind.Lightning;
        }

        _details[index] = detail = new ChartDetail(summary.StarClass, fields, clouds, cold, lightning);
        _detailSectors[index] = sector;
        _detailKnown[index] = true;
        return true;
    }

    /// <summary>Seconds since the chart was recorded, by the server's clock.</summary>
    public float AgeSeconds(int index, long serverTick) =>
        _view is null ? 0f : Math.Max(0, serverTick - _view.Charts[index].RecordedTick) / (float)Prospect.Engine.Time.TickLoop.SimulationHz;

    /// <summary>Metres from the ship being flown to the middle of the chart's sector.</summary>
    public static double MetersTo(Galaxy galaxy, SectorCoord sector, Double3 from) => galaxy.CenterOf(sector).RelativeTo(from).Length();

    private sealed class Comparer(ChartCatalogue list) : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            var result = list.Sort switch
            {
                ChartSort.Age => list._view!.Charts[b].RecordedTick.CompareTo(list._view.Charts[a].RecordedTick),
                ChartSort.Name => list.NameOf(a).CompareTo(list.NameOf(b), StringComparison.OrdinalIgnoreCase),
                _ => list._distance[a].CompareTo(list._distance[b]),
            };
            if (result == 0 && list.Sort != ChartSort.Name)
                result = list.NameOf(a).CompareTo(list.NameOf(b), StringComparison.OrdinalIgnoreCase);
            if (result == 0)
            {
                var x = list._view!.Charts[a].Sector;
                var y = list._view.Charts[b].Sector;
                result = x.Y != y.Y ? x.Y.CompareTo(y.Y) : x.Z != y.Z ? x.Z.CompareTo(y.Z) : x.X.CompareTo(y.X);
            }

            return list.Descending ? -result : result;
        }
    }
}
