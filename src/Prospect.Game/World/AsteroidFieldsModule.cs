using System.Numerics;
using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>
/// Fills space with asteroid fields. A field is a thing in world space: a sector anchors none, one or several,
/// each of a kind from the content (a thin scatter, a cluster, a dense belt), centred anywhere in that sector
/// and free to reach into the sectors around it. Every asteroid gets a size from a table weighted toward the
/// tiny, and those that carry ore get it from the character of the sector they land in.
///
/// A sector's asteroids are the rocks of every field around it that land inside it. Each rock of a field is
/// drawn from a stream of its own, so every sector that looks at a field sees the same rocks in the same places
/// and exactly one sector takes each of them: a field runs through a sector wall without a seam, and a sector
/// is generated alone, from the plans of its neighbours and never from their contents.
///
/// Everything here is plain arithmetic in a fixed order, with no library function whose last bit differs between
/// machines: which asteroid sits where decides which one a collision names, so the server and every client must
/// build exactly the same list.
/// </summary>
public sealed class AsteroidFieldsModule : SectorModule
{
    public const string TypeName = "asteroidFields";

    /// <summary>The most fields one sector can anchor.</summary>
    public const int MaxFields = 8;

    /// <summary>
    /// The most fields whose rocks can land in one sector: those of the sector and of the 26 around it, with a
    /// field in a gas cloud for each. A rock's sort key keeps its field in eight bits.
    /// </summary>
    public const int MaxFieldsInReach = 27 * (MaxFields + 1);

    // One stream per decision, so that retuning sizes or ore never moves a rock. The contour and the key of a
    // sector's field number f use the stream f above these.
    private const uint PlanStream = 1;
    private const uint GasStream = 8;
    private const uint GasKeyStream = 9;
    private const uint BroadNoiseStream = 10;
    private const uint FineNoiseStream = 11;
    private const uint ContourStream = 16;
    private const uint KeyStream = 32;

    // And one per decision for every rock of a field, under the field's key.
    private const ulong SizeDraws = 0x1B873593A54FF53A;
    private const ulong PlaceDraws = 0x5851F42D4C957F2D;
    private const ulong LookDraws = 0x14057B7EF767814F;
    private const ulong OreDraws = 0x2545F4914F6CDD1D;

    private const int KnotsPerField = 4;

    /// <summary>
    /// A field's length is cut into this many pieces, each holding a set run of the field's rocks. A sector then
    /// looks only at the pieces that can reach it.
    /// </summary>
    private const int Segments = (KnotsPerField - 1) * PiecesPerSpan;
    private const int PiecesPerSpan = 8;

    // What a sector has to do with one piece of a field: nothing, take the rocks that land in it, or take all.
    private const ulong Apart = 0;
    private const ulong Straddles = 1;
    private const ulong Inside = 2;

    /// <summary>
    /// How far a field may reach from its centre along a sector axis, in sector edges. Its centre lies in the
    /// sector that anchors it, so every rock lands in that sector or in one of the 26 around it.
    /// </summary>
    private const float ReachLimit = 0.995f;

    /// <summary>Metres added around a piece of a field, so that rounding never puts a rock outside its box.</summary>
    private const float Slack = 1f;

    private readonly record struct Range(float Min, float Max)
    {
        public float At(float t) => Min + (Max - Min) * t;
    }

    /// <summary>
    /// A kind of field: how many asteroids it holds and how far it reaches from its centre, in metres. Its name in
    /// the content is a label for whoever edits the file; nothing in the game shows it.
    /// </summary>
    private readonly record struct FieldKind(float Weight, Range Asteroids, Range Length, Range Width, Range Height);

    /// <summary>A band of asteroid sizes, what an asteroid in it is called and how often an asteroid falls in it.</summary>
    private readonly record struct SizeClass(string Name, float Weight, Range Radius);

    /// <summary>One of the four transverse sections that define a field's continuous support.</summary>
    internal readonly struct RibbonKnot(float center, float width)
    {
        public readonly float Center = center;
        public readonly float Width = width;
    }

    /// <summary>One field: a bent ribbon with tapered, sparsely populated ends, in the frame of the sector that anchors it.</summary>
    internal struct Field
    {
        /// <summary>Relative to the centre of the sector that anchors the field.</summary>
        public Vector3 Center;

        /// <summary>Unit vector along the field's length, in the horizontal plane.</summary>
        public float AlongX;
        public float AlongZ;

        /// <summary>How far the field reaches from its centre along its length, across it and vertically.</summary>
        public float Length;
        public float Width;
        public float Height;

        /// <summary>How many rocks the field proposes, over all the sectors it reaches.</summary>
        public int Count;

        /// <summary>The largest asteroid the field takes, so that one rock cannot swallow the field.</summary>
        public float LargestRadius;

        /// <summary>Keys the stream of each of the field's rocks.</summary>
        public ulong Key;

        /// <summary>Whether the world's density noise thins the field. The home field is whole.</summary>
        public bool Thinned;

        /// <summary>Whether the field fills a gas cloud: a plain oval along the x axis, not a seeded ribbon.</summary>
        public bool FromGas;

        /// <summary>From the anchoring sector's frame to the frame of the sector being generated.</summary>
        public Vector3 Shift;

        /// <summary>The combined weight of the size classes the field may draw from.</summary>
        public float AllowedWeight;

        /// <summary>Two bits for each piece of the field: what the sector being generated has to do with it.</summary>
        public ulong Pieces;
    }

    private readonly FieldKind[] _kinds;
    private readonly SizeClass[] _sizes;
    private readonly float _kindWeight;
    private readonly float _emptySectorChance;
    private readonly float _voidFieldChance;
    private readonly float _densityGain;
    private readonly float _sparseFill;
    private readonly int _fieldsMin;
    private readonly int _fieldsMax;
    private readonly float _centerReach;
    private readonly float _centerFlatten;
    private readonly float _spacing;
    private readonly int _placementAttempts;
    private readonly float _largestShareOfField;
    private readonly Range _oreShare;
    private readonly Range _grade;
    private readonly float _gradeFromRichness;
    private readonly Field _home;
    private readonly float _outskirts;
    private readonly Range _ribbonWidth;
    private readonly float _bend;
    private readonly float _fade;
    private readonly float _jitter;

    /// <summary>The share of a field's rocks that lie before each of its pieces; one at the end.</summary>
    private readonly float[] _piecesBefore = new float[Segments + 1];

    // The world's density noise: a lattice of this many cells along a sector's edge, a broader one under it, and
    // how a value becomes the share of rocks kept. No cells means no thinning.
    private readonly int _thinCells;
    private readonly int _thinBroadCells;
    private readonly float _thinBroadShare;
    private readonly float _thinSparse;
    private readonly float _thinFrom;
    private readonly float _thinTo;
    private readonly float _thinMean = 1f;

    // Rocks in gas: how often a cloud holds a field, how dense it is and how much of the cloud it fills.
    private readonly float _gasChance;
    private readonly float _gasPerCubicKm;
    private readonly float _gasFill;
    private readonly int _gasMax;

    private AsteroidFieldsModule? _baseRules;
    private float _amount = 1, _size = 1, _fieldSize = 1, _frequency = 1, _spacingScale = 1, _oreShareScale = 1, _gradeScale = 1;
    private int _countMin;
    private Range _radiusLimits = new(0, float.MaxValue), _gradeLimits = new(0, 1);
    private Range[] _materialGrades = [];


    /// <summary>Applies settings once from the original content, leaving earlier worlds untouched.</summary>
    public AsteroidFieldsModule Under(WorldSettingCatalog catalog, in WorldSettings settings, Resource[] resources)
    {
        var clean = catalog.Sanitize(settings);
        var original = _baseRules ?? this;
        var copy = (AsteroidFieldsModule)original.MemberwiseClone();
        copy._baseRules = original;
        copy._amount = original._amount * catalog.Number(clean, "asteroidAmount");
        copy._size = original._size * catalog.Number(clean, "asteroidSize");
        copy._fieldSize = original._fieldSize * catalog.Number(clean, "asteroidFieldSize");
        copy._frequency = original._frequency * catalog.Number(clean, "asteroidFrequency");
        copy._spacingScale = original._spacingScale * catalog.Number(clean, "asteroidSpacing");
        copy._oreShareScale = original._oreShareScale * catalog.Number(clean, "asteroidOreShare");
        copy._gradeScale = original._gradeScale * catalog.Number(clean, "asteroidOreGrade");
        copy.MaxPerSector = Math.Min(original.MaxPerSector, (int)catalog.Number(clean, "asteroidCap"));
        copy._countMin = (int)catalog.Number(clean, "asteroidCountMin");
        copy._radiusLimits = new(catalog.Number(clean, "asteroidRadiusMin"), catalog.Number(clean, "asteroidRadiusMax"));
        copy._gradeLimits = new(catalog.Number(clean, "oreGradeMin"), catalog.Number(clean, "oreGradeMax"));
        copy._materialGrades = new Range[resources.Length];
        for (var i = 0; i < resources.Length; i++)
            copy._materialGrades[i] = new(catalog.Number(clean, resources[i].Id + "GradeMin"), catalog.Number(clean, resources[i].Id + "GradeMax"));
        copy.SmallestRadius = Math.Clamp(original.SmallestRadius * catalog.Number(clean, "asteroidSize"), copy._radiusLimits.Min, copy._radiusLimits.Max);
        copy.LargestRadius = Math.Clamp(original.LargestRadius * catalog.Number(clean, "asteroidSize"), copy._radiusLimits.Min, copy._radiusLimits.Max);
        return copy;
    }

    private AsteroidFieldsModule(string id, JsonElement json, string path)
        : base(id)
    {
        MaxPerSector = json.GetProperty("maxPerSector"u8).GetInt32();
        _emptySectorChance = json.GetProperty("emptySectorChance"u8).GetSingle();
        _voidFieldChance = json.GetProperty("voidFieldChance"u8).GetSingle();
        _densityGain = json.GetProperty("densityGain"u8).GetSingle();
        _sparseFill = json.GetProperty("sparseFill"u8).GetSingle();
        var fieldsPerSector = json.GetProperty("fieldsPerSector"u8);
        _fieldsMin = fieldsPerSector[0].GetInt32();
        _fieldsMax = fieldsPerSector[1].GetInt32();
        var centers = json.GetProperty("fieldCenters"u8);
        _centerReach = centers.GetProperty("reach"u8).GetSingle();
        _centerFlatten = centers.GetProperty("flatten"u8).GetSingle();
        ArrivalClearance = json.GetProperty("arrivalClearance"u8).GetSingle();
        _spacing = json.GetProperty("spacing"u8).GetSingle();
        _placementAttempts = json.GetProperty("placementAttempts"u8).GetInt32();
        _largestShareOfField = json.GetProperty("largestShareOfField"u8).GetSingle();

        var distribution = json.GetProperty("distribution"u8);
        _outskirts = distribution.GetProperty("outskirts"u8).GetSingle();
        _ribbonWidth = ReadRange(distribution.GetProperty("width"u8));
        _bend = distribution.GetProperty("bend"u8).GetSingle();
        _fade = distribution.TryGetProperty("fade"u8, out var fade) ? fade.GetSingle() : 0f;
        _jitter = distribution.TryGetProperty("jitter"u8, out var jitter) ? jitter.GetSingle() : 6f;
        if (!float.IsFinite(_bend) || _bend < 0 || _bend > 1 || !float.IsFinite(_outskirts) || _outskirts < 0 || _outskirts > 1 ||
            !float.IsFinite(_ribbonWidth.Min) || !float.IsFinite(_ribbonWidth.Max) ||
            _ribbonWidth.Min <= 0 || _ribbonWidth.Max > 1 || _ribbonWidth.Min > _ribbonWidth.Max ||
            !float.IsFinite(_fade) || _fade < 0 || _fade > 1 || !float.IsFinite(_jitter) || _jitter < 0 || _jitter > 64)
            throw new InvalidDataException($"{path}: {id}: invalid field distribution");

        // A field's centre must lie in the sector that anchors it: a sector looks for fields among its
        // neighbours' and no further.
        if (!float.IsFinite(_centerReach) || _centerReach < 0 || _centerReach > 1 || !float.IsFinite(_centerFlatten) || _centerFlatten < 0 || _centerFlatten > 1)
            throw new InvalidDataException($"{path}: {id}: fieldCenters reach and flatten must lie between 0 and 1");

        // Rocks gather toward the middle of a field's length: the outskirts are spread evenly along it, the
        // rest as the mean of two even draws. Each piece of the length holds its share of the field's rocks.
        for (var piece = 0; piece < Segments; piece++)
        {
            var along = piece / (float)Segments;
            var gathered = along <= 0.5f ? 2f * along * along : 1f - 2f * (1f - along) * (1f - along);
            _piecesBefore[piece] = _outskirts * along + (1f - _outskirts) * gathered;
        }
        _piecesBefore[Segments] = 1f;

        var kinds = new List<FieldKind>();
        foreach (var kind in json.GetProperty("kinds"u8).EnumerateArray())
        {
            kinds.Add(
                new FieldKind(
                    kind.GetProperty("weight"u8).GetSingle(),
                    ReadRange(kind.GetProperty("asteroids"u8)),
                    ReadRange(kind.GetProperty("length"u8)),
                    ReadRange(kind.GetProperty("width"u8)),
                    ReadRange(kind.GetProperty("height"u8))
                )
            );
            _kindWeight += kinds[^1].Weight;
        }

        var sizes = new List<SizeClass>();
        foreach (var size in json.GetProperty("sizes"u8).EnumerateArray())
        {
            sizes.Add(new SizeClass(size.GetProperty("name"u8).GetString()!, size.GetProperty("weight"u8).GetSingle(), ReadRange(size.GetProperty("radius"u8))));
            if (sizes.Count > 1 && sizes[^1].Radius.Min < sizes[^2].Radius.Min)
                throw new InvalidDataException($"{path}: {id}: size classes must be listed smallest first");
            SmallestRadius = sizes.Count == 1 ? sizes[0].Radius.Min : Math.Min(SmallestRadius, sizes[^1].Radius.Min);
            LargestRadius = Math.Max(LargestRadius, sizes[^1].Radius.Max);
        }

        var ore = json.GetProperty("ore"u8);
        _oreShare = ReadRange(ore.GetProperty("share"u8));
        _grade = ReadRange(ore.GetProperty("grade"u8));
        _gradeFromRichness = ore.GetProperty("gradeFromRichness"u8).GetSingle();

        var home = json.GetProperty("home"u8);
        var offset = home.GetProperty("offset"u8);
        _home = new Field
        {
            Center = new Vector3(offset[0].GetSingle(), offset[1].GetSingle(), offset[2].GetSingle()),
            AlongX = 1f,
            Length = home.GetProperty("length"u8).GetSingle(),
            Width = home.GetProperty("width"u8).GetSingle(),
            Height = home.GetProperty("height"u8).GetSingle(),
            Count = home.GetProperty("asteroids"u8).GetInt32(),
        };

        _kinds = [.. kinds];
        _sizes = [.. sizes];

        // Optional scales rebase live content; the frozen fixture keeps its original scale.
        if (json.TryGetProperty("baseline"u8, out var baseline))
        {
            _amount = Baseline("amount");
            _size = Baseline("size");
            _fieldSize = Baseline("fieldSize");
            _frequency = Baseline("frequency");
            _spacingScale = Baseline("spacing");
            _oreShareScale = Baseline("oreShare");
            _gradeScale = Baseline("oreGrade");
            SmallestRadius *= _size;
            LargestRadius *= _size;

            float Baseline(string name)
            {
                var value = baseline.GetProperty(name).GetSingle();
                if (!float.IsFinite(value) || value <= 0 || value > 5)
                    throw new InvalidDataException($"{path}: {id}: baseline {name} must be positive and at most 5");
                return value;
            }
        }

        // Optional: content without it has fields of even density.
        if (json.TryGetProperty("density"u8, out var density))
        {
            _thinCells = density.GetProperty("cells"u8).GetInt32();
            _thinBroadCells = density.GetProperty("broadCells"u8).GetInt32();
            _thinBroadShare = density.GetProperty("broadShare"u8).GetSingle();
            _thinSparse = density.GetProperty("sparse"u8).GetSingle();
            _thinFrom = density.GetProperty("from"u8).GetSingle();
            _thinTo = density.GetProperty("to"u8).GetSingle();
            if (_thinCells is < 2 or > 32 || _thinBroadCells < 1 || _thinBroadCells > Math.Min(_thinCells, 8))
                throw new InvalidDataException($"{path}: {id}: density cells must lie between 2 and 32, broadCells between 1 and 8 and no more than cells");
            if (!float.IsFinite(_thinBroadShare) || _thinBroadShare < 0 || _thinBroadShare > 1 || !float.IsFinite(_thinSparse) || _thinSparse < 0 || _thinSparse > 1 ||
                !float.IsFinite(_thinFrom) || !float.IsFinite(_thinTo) || _thinFrom < 0 || _thinTo > 1 || _thinFrom >= _thinTo)
                throw new InvalidDataException($"{path}: {id}: density broadShare, sparse, from and to must lie between 0 and 1, with from below to");
            _thinMean = MeanKept();
        }

        // Optional: content without it never puts rocks in gas.
        if (json.TryGetProperty("inGas"u8, out var inGas))
        {
            _gasChance = inGas.GetProperty("chance"u8).GetSingle();
            _gasPerCubicKm = inGas.GetProperty("perCubicKm"u8).GetSingle();
            _gasFill = inGas.GetProperty("fill"u8).GetSingle();
            _gasMax = inGas.GetProperty("max"u8).GetInt32();
            if (!float.IsFinite(_gasChance) || _gasChance < 0 || _gasChance > 1 || !float.IsFinite(_gasPerCubicKm) || _gasPerCubicKm < 0 || _gasPerCubicKm > 1000 ||
                !float.IsFinite(_gasFill) || _gasFill <= 0 || _gasFill > 1 || _gasMax < 1)
                throw new InvalidDataException($"{path}: {id}: inGas needs a chance from 0 to 1, perCubicKm from 0 to 1000, a fill above 0 up to 1 and a max of at least 1");
        }

        // An asteroid travels as a 16-bit index into its sector's list.
        if (MaxPerSector is < 1 or > ushort.MaxValue)
            throw new InvalidDataException($"{path}: {id}: maxPerSector must be between 1 and {ushort.MaxValue}");
        if (_kinds.Length == 0 || _sizes.Length == 0 || _kindWeight <= 0)
            throw new InvalidDataException($"{path}: {id}: needs at least one field kind and one size class");
        if (_fieldsMin < 1 || _fieldsMax < _fieldsMin || _fieldsMax > MaxFields)
            throw new InvalidDataException($"{path}: {id}: fieldsPerSector must lie between 1 and {MaxFields}");
        if (_placementAttempts < 1 || _spacing < 1f)
            throw new InvalidDataException($"{path}: {id}: placementAttempts and spacing must be at least 1");
    }

    /// <summary>The most asteroids one sector can hold.</summary>
    public int MaxPerSector { get; private set; }

    /// <summary>
    /// Whether a summary's counts are estimates. They are: a summary weighs the fields that reach a sector
    /// without placing a rock, and generation then thins, crowds out and caps them.
    /// </summary>
    public bool EstimatesCounts => true;

    /// <summary>Radius of the smallest and of the largest asteroid the size table allows, in metres.</summary>
    public float SmallestRadius { get; private set; }
    public float LargestRadius { get; private set; }

    /// <summary>
    /// How far every asteroid's surface stays from the centre of its sector, in metres. Ships arrive there: new
    /// players in the home sector, and anything that jumps to a sector.
    /// </summary>
    public float ArrivalClearance { get; }

    /// <summary>What an asteroid of a radius is called: the name of the size class it falls in.</summary>
    public string SizeName(float radius)
    {
        var name = _sizes[0].Name;
        foreach (var size in _sizes)
        {
            if (radius >= size.Radius.Min * _size)
                name = size.Name;
        }

        return name;
    }

    internal static AsteroidFieldsModule Read(string id, JsonElement json, string path) => new(id, json, path);

    private static Range ReadRange(JsonElement pair) => new(pair[0].GetSingle(), pair[1].GetSingle());

    /// <summary>
    /// Estimates what generation will make of a sector: for every field around it, the rocks of the pieces that
    /// reach the sector, by how much of each piece's box lies inside it. No rock is placed. A sector the
    /// estimate calls empty is empty; one it gives rocks can still come out without any.
    /// </summary>
    public override void Summarize(in SectorContext sector, ref SectorSummary summary)
    {
        summary.AsteroidCount = 0;
        summary.FieldCount = 0;
        if (_amount == 0 || MaxPerSector == 0)
            return;

        Span<Field> anchored = stackalloc Field[MaxFields + 1];
        Span<RibbonKnot> contour = stackalloc RibbonKnot[KnotsPerField];
        Span<float> layers = stackalloc float[3];
        var galaxy = sector.Galaxy;
        var size = (float)galaxy.SectorSize;
        for (var dy = -1; dy <= 1; dy++)
            layers[dy + 1] = galaxy.Shape.LayerFalloff(sector.Coord.Y + dy);

        var total = 0L;
        var fields = 0;
        for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                // The same product as Galaxy.MatterAt, with the costly half shared by a column of sectors.
                var column = galaxy.Shape.Density(sector.Coord.X + dx, sector.Coord.Z + dz);
                for (var dy = -1; dy <= 1; dy++)
                {
                    var anchor = new SectorContext(galaxy, sector.Coord.Offset(dx, dy, dz), column * layers[dy + 1]);
                    var count = Anchored(anchor, anchored);
                    var shift = new Vector3(dx * size, dy * size, dz * size);
                    for (var f = 0; f < count; f++)
                    {
                        ref var field = ref anchored[f];
                        if (!Reaches(anchor, ref field, f, shift, contour, out var expected))
                            continue;
                        if (field.Thinned)
                            expected *= _thinMean;
                        total += Math.Max(1, (int)(expected + 0.5f));
                        fields++;
                    }
                }
            }

        summary.AsteroidCount = (int)Math.Min(total, MaxPerSector);
        summary.FieldCount = fields;
    }

    public override void Generate(in SectorContext sector, SectorContent content)
    {
        content.AsteroidCount = 0;
        content.Summary.AsteroidCount = 0;
        content.Summary.FieldCount = 0;
        content.Rocks.Reset(0, sector.Galaxy.SectorSize);
        if (_amount == 0 || MaxPerSector == 0)
            return;

        if (content.Fields.Length == 0)
        {
            content.Fields = new Field[MaxFieldsInReach];
            content.Knots = new RibbonKnot[MaxFieldsInReach * KnotsPerField];
        }

        var fields = content.Fields.AsSpan();
        var knots = content.Knots.AsSpan();
        var fieldCount = Gather(sector, fields, knots, out var candidates);
        if (candidates == 0)
            return;
        var most = Math.Min(candidates, MaxPerSector);
        content.Reserve(most, candidates);
        content.Rocks.Reset(most, sector.Galaxy.SectorSize);

        // Sizes first, for every rock that lands in the sector, then sorted largest first. The big ones are then
        // placed while the sector is empty and the small ones fill in around them. The order is also what makes
        // distant sectors cheap to draw: the loop over a sector stops at the first asteroid too small to see.
        //
        // A key is the radius (inverted, so that ascending keys run from large to small), then the field, the
        // piece of it and the rock's number in the field. No two keys are equal, so the result does not depend
        // on how the sort breaks ties.
        var half = sector.HalfSize;
        var keys = content.SortKeys.AsSpan(0, candidates);
        var drawn = 0;
        for (var f = 0; f < fieldCount; f++)
        {
            ref readonly var field = ref fields[f];
            var contour = knots.Slice(f * KnotsPerField, KnotsPerField);
            for (var piece = 0; piece < Segments; piece++)
            {
                var state = field.Pieces >> piece * 2 & 3;
                if (state == Apart)
                    continue;
                var last = PieceStart(field.Count, piece + 1);
                for (var i = PieceStart(field.Count, piece); i < last; i++)
                {
                    // A rock belongs to the sector its first position lies in, and to no other: the position is
                    // worked out in the field's own frame, the same numbers whichever sector asks.
                    if (state == Straddles)
                    {
                        var place = SectorRandom.Numbered(field.Key ^ PlaceDraws, (uint)i);
                        place.NextSingle();
                        if (!Holds(PointIn(ref place, field, contour, piece) + field.Shift, half))
                            continue;
                    }

                    var sizes = SectorRandom.Numbered(field.Key ^ SizeDraws, (uint)i);
                    var radius = DrawRadius(ref sizes, field.LargestRadius, field.AllowedWeight);
                    keys[drawn++] = ((ulong)~BitConverter.SingleToUInt32Bits(radius) << 32) | ((ulong)f << 24) | ((ulong)piece << 19) | (uint)i;
                }
            }
        }

        keys = keys[..drawn];
        keys.Sort();

        var asteroids = content.Asteroids;
        var character = content.Summary.Character;
        var oreShare = character.Resources == 0 ? 0f : Math.Min(1f, _oreShare.At(character.Richness) * _oreShareScale);
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        var placed = 0;
        var thinningReady = false;
        Span<ulong> occupied = stackalloc ulong[(MaxFieldsInReach + 63) / 64];
        occupied.Clear();
        for (var k = 0; k < keys.Length && placed < MaxPerSector; k++)
        {
            // Largest first, so the first rock below the least radius ends the list. A sector over its cap
            // likewise loses its smallest.
            var radius = BitConverter.UInt32BitsToSingle(~(uint)(keys[k] >> 32));
            if (radius < _radiusLimits.Min)
                break;
            var f = (int)(keys[k] >> 24 & 0xFF);
            var piece = (int)(keys[k] >> 19 & 0x1F);
            var number = (uint)(keys[k] & 0x7FFFF);
            ref readonly var field = ref fields[f];

            var place = SectorRandom.Numbered(field.Key ^ PlaceDraws, number);
            var kept = place.NextSingle();
            var origin = PointIn(ref place, field, knots.Slice(f * KnotsPerField, KnotsPerField), piece) + field.Shift;
            if (field.Thinned)
            {
                // The world's density noise: each rock is kept or not where it first lands, by its own draw, so
                // that both sides of a sector wall thin a field by the same measure.
                if (!thinningReady)
                {
                    BuildThinning(sector, content);
                    thinningReady = true;
                }

                if (kept >= Kept(content.Thinning, origin, half))
                    continue;
            }

            var position = Settle(ref place, field, origin, radius, half, content, placed);
            if (!float.IsFinite(position.X))
                continue;

            var look = SectorRandom.Numbered(field.Key ^ LookDraws, number);
            var rotation = Rotation(ref look);
            var shape = look.NextInt(Galaxy.AsteroidShapes);

            // What the rock is made of. Three numbers are drawn whether it turns out to carry ore or not.
            var ore = SectorRandom.Numbered(field.Key ^ OreDraws, number);
            var roll = ore.NextSingle();
            var pick = ore.NextSingle();
            var quality = ore.NextSingle();
            var resource = -1;
            var grade = 0f;
            if (roll < oreShare)
            {
                resource = character.Pick(pick);
                grade = Math.Min(1f, _grade.At(_gradeFromRichness * character.Richness + (1f - _gradeFromRichness) * quality * quality) * _gradeScale);
                grade = Math.Clamp(grade, _gradeLimits.Min, _gradeLimits.Max);
                if (_materialGrades.Length != 0)
                    grade = Math.Clamp(grade, _materialGrades[resource].Min, _materialGrades[resource].Max);
            }

            occupied[f >> 6] |= 1UL << (f & 63);
            asteroids[placed] = new Asteroid
            {
                LocalPosition = position,
                Radius = radius,
                Rotation = rotation,
                Shape = shape,
                Resource = resource,
                Grade = grade,
            };
            content.Rocks.Add(placed++, position, radius);
            low = Vector3.Min(low, position - new Vector3(radius));
            high = Vector3.Max(high, position + new Vector3(radius));
        }

        content.AsteroidCount = placed;
        content.Summary.AsteroidCount = placed;
        if (placed == 0)
            return;
        foreach (var bits in occupied)
            content.Summary.FieldCount += BitOperations.PopCount(bits);
        content.BoundsCenter = (low + high) * 0.5f;
        content.BoundsRadius = ((high - low) * 0.5f).Length();
    }

    /// <summary>
    /// How many rocks the fields a sector anchors propose, over all the sectors they reach, and how many fields
    /// those are. For tests and tools: generation never needs the total.
    /// </summary>
    internal int Proposed(Galaxy galaxy, SectorCoord anchor, out int fields)
    {
        Span<Field> anchored = stackalloc Field[MaxFields + 1];
        fields = Anchored(new SectorContext(galaxy, anchor, galaxy.MatterAt(anchor)), anchored);
        var total = 0;
        for (var f = 0; f < fields; f++)
            total += anchored[f].Count;
        return total;
    }

    /// <summary>
    /// Collects the fields with rocks for a sector: those anchored in it and in the 26 sectors around it whose
    /// pieces reach it. Returns their number; <paramref name="candidates"/> is the most rocks they can land in
    /// the sector.
    /// </summary>
    public int ChartFields(Galaxy galaxy, SectorCoord coord, Span<ChartField> destination)
    {
        Span<Field> fields = stackalloc Field[MaxFieldsInReach];
        Span<RibbonKnot> knots = stackalloc RibbonKnot[MaxFieldsInReach * KnotsPerField];
        var summary = galaxy.Summarize(coord);
        var sector = new SectorContext(galaxy, coord, summary.Density);
        var count = Gather(sector, fields, knots, out _);
        if (destination.Length < count) throw new ArgumentException("Chart field buffer is too small.", nameof(destination));
        for (var i = 0; i < count; i++)
        {
            ref var field = ref fields[i];
            destination[i] = new ChartField(galaxy.CenterOf(coord) + field.Shift + field.Center,
                new Vector3(field.Length, field.Height, field.Width), new Vector3(field.AlongX, 0, field.AlongZ));
        }
        return count;
    }

    private int Gather(in SectorContext sector, Span<Field> fields, Span<RibbonKnot> knots, out int candidates)
    {
        Span<Field> anchored = stackalloc Field[MaxFields + 1];
        Span<float> layers = stackalloc float[3];
        var galaxy = sector.Galaxy;
        var size = (float)galaxy.SectorSize;
        for (var dy = -1; dy <= 1; dy++)
            layers[dy + 1] = galaxy.Shape.LayerFalloff(sector.Coord.Y + dy);

        candidates = 0;
        var gathered = 0;
        for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                // The same product as Galaxy.MatterAt, with the costly half shared by a column of sectors.
                var column = galaxy.Shape.Density(sector.Coord.X + dx, sector.Coord.Z + dz);
                for (var dy = -1; dy <= 1; dy++)
                {
                    var anchor = new SectorContext(galaxy, sector.Coord.Offset(dx, dy, dz), column * layers[dy + 1]);
                    var count = Anchored(anchor, anchored);
                    var shift = new Vector3(dx * size, dy * size, dz * size);
                    for (var f = 0; f < count; f++)
                    {
                        ref var field = ref anchored[f];
                        var contour = knots.Slice(gathered * KnotsPerField, KnotsPerField);
                        if (!Reaches(anchor, ref field, f, shift, contour, out _))
                            continue;

                        var landing = 0;
                        for (var piece = 0; piece < Segments; piece++)
                        {
                            if ((field.Pieces >> piece * 2 & 3) != Apart)
                                landing += PieceStart(field.Count, piece + 1) - PieceStart(field.Count, piece);
                        }

                        field.AllowedWeight = AllowedWeight(field.LargestRadius);
                        fields[gathered++] = field;
                        candidates += landing;
                    }
                }
            }

        return gathered;
    }

    /// <summary>
    /// The fields a sector anchors, sized: those of its plan and, where a gas cloud centred in it holds rocks,
    /// one more in the cloud. Cheap, and the same whichever sector asks.
    /// </summary>
    private int Anchored(in SectorContext anchor, Span<Field> fields)
    {
        var count = Plan(anchor, fields, out _);
        var limit = anchor.HalfSize * 2f * ReachLimit;
        for (var f = 0; f < count; f++)
        {
            ref var field = ref fields[f];
            field.Length *= _fieldSize;
            field.Width *= _fieldSize;
            field.Height *= _fieldSize;

            // No rock lands further than one sector from the sector that anchors its field.
            var reach = MathF.Sqrt(field.Length * field.Length + field.Width * field.Width);
            if (reach > limit)
            {
                var fit = limit / reach;
                field.Length *= fit;
                field.Width *= fit;
            }

            field.Height = Math.Min(field.Height, Math.Min(field.Width, limit));
            field.LargestRadius = Math.Min(_largestShareOfField * field.Width, field.Height);

            // The starter field is whole: the world's density noise must not thin it away from arrival.
            field.Thinned = _thinCells != 0 && !anchor.IsHome;
        }

        if (_gasChance > 0 && anchor.Galaxy.GasClouds is { } gas && gas.TryGetCloud(anchor.Galaxy, anchor.Coord, out var cloud))
        {
            // Some clouds hold rocks: a field that fills the cloud's middle, as dense as the content says.
            var random = Stream(anchor, GasStream);
            if (random.NextSingle() < _gasChance * Math.Min(1f, _frequency))
            {
                ref var field = ref fields[count];
                field = new Field
                {
                    Center = cloud.Center,
                    AlongX = 1f,
                    Length = cloud.HalfSize.X * _gasFill,
                    Width = cloud.HalfSize.Z * _gasFill,
                    Height = cloud.HalfSize.Y * _gasFill,
                    Thinned = _thinCells != 0,
                    FromGas = true,
                };
                var cubicKm = 4.18879f * (field.Length * 0.001f) * (field.Width * 0.001f) * (field.Height * 0.001f);
                field.Count = Math.Min((int)(_gasPerCubicKm * cubicKm * _amount), Math.Min(_gasMax, MaxPerSector));
                field.LargestRadius = Math.Min(_largestShareOfField * field.Width, field.Height);
                if (field.Count > 0)
                    count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Readies a field for the sector being generated and says whether any piece of it reaches that sector.
    /// Writes the field's contour to <paramref name="contour"/>, marks each piece as apart from the sector,
    /// straddling its walls or inside it, and estimates the rocks that land in the sector.
    /// </summary>
    private bool Reaches(in SectorContext anchor, ref Field field, int number, Vector3 shift, Span<RibbonKnot> contour, out float expected)
    {
        expected = 0;
        field.Shift = shift;
        field.Pieces = 0;
        var half = anchor.HalfSize;
        var home = anchor.IsHome && number == 0 && !field.FromGas;

        // Most fields around a sector come nowhere near it. The home field's centre is not settled yet.
        var around = MathF.Sqrt(field.Length * field.Length + field.Width * field.Width) + Slack;
        var middle = field.Center + shift;
        if (!home && (MathF.Abs(middle.X) > half + around || MathF.Abs(middle.Z) > half + around || MathF.Abs(middle.Y) > half + field.Height + Slack))
            return false;

        if (field.FromGas)
        {
            // A cloud's rocks fill a plain oval: a straight ribbon of full width.
            for (var k = 0; k < KnotsPerField; k++)
                contour[k] = new RibbonKnot(0f, 1f);
            field.Key = Stream(anchor, GasKeyStream).NextUInt64();
        }
        else
        {
            // Four seeded sections, then fitted to their own transverse support: a ribbon fills its width.
            var draws = Stream(anchor, ContourStream + (uint)number);
            var lowZ = float.MaxValue;
            var highZ = float.MinValue;
            for (var k = 0; k < KnotsPerField; k++)
            {
                var width = _ribbonWidth.At(draws.NextSingle());
                var centerZ = draws.NextSigned() * _bend;
                contour[k] = new RibbonKnot(centerZ, width);
                lowZ = Math.Min(lowZ, centerZ - width);
                highZ = Math.Max(highZ, centerZ + width);
            }

            var mid = (lowZ + highZ) * 0.5f;
            var extent = (highZ - lowZ) * 0.5f;
            for (var k = 0; k < KnotsPerField; k++)
                contour[k] = new RibbonKnot((contour[k].Center - mid) / extent, contour[k].Width / extent);

            if (home)
            {
                // The starter field's middle stays beside arrival even when its seeded ends bend away.
                var bend = (contour[1].Center + contour[2].Center) * 0.5f;
                var room = Math.Max(0f, half - field.Width);
                field.Center.Z = Math.Clamp(_home.Center.Z - bend * field.Width, -room, room);
            }

            field.Key = Stream(anchor, KeyStream + (uint)number).NextUInt64();
        }

        // A box around each piece of the ribbon, in the frame of the sector being generated. Smoothstep is
        // monotone and the taper peaks nearest zero, so these are bounds, not sampled guesses.
        middle = field.Center + shift;
        var reaches = false;
        for (var piece = 0; piece < Segments; piece++)
        {
            var span = piece / PiecesPerSpan;
            var u = (piece - span * PiecesPerSpan) / (float)PiecesPerSpan;
            var v = (piece - span * PiecesPerSpan + 1) / (float)PiecesPerSpan;
            var x0 = (span + u) * (2f / (KnotsPerField - 1)) - 1f;
            var x1 = (span + v) * (2f / (KnotsPerField - 1)) - 1f;
            u = u * u * (3f - 2f * u);
            v = v * v * (3f - 2f * v);
            var a = contour[span];
            var b = contour[span + 1];
            var c0 = a.Center + (b.Center - a.Center) * u;
            var c1 = a.Center + (b.Center - a.Center) * v;
            var width = Math.Max(a.Width + (b.Width - a.Width) * u, a.Width + (b.Width - a.Width) * v);
            var near = x0 <= 0 && x1 >= 0 ? 0 : Math.Min(Math.Abs(x0), Math.Abs(x1));
            var near2 = near * near;
            width *= 1f - near2 * near2;
            var z0 = (Math.Min(c0, c1) - width) * field.Width;
            var z1 = (Math.Max(c0, c1) + width) * field.Width;
            x0 *= field.Length;
            x1 *= field.Length;
            var lowX = middle.X + Math.Min(field.AlongX * x0, field.AlongX * x1) + Math.Min(-field.AlongZ * z0, -field.AlongZ * z1) - Slack;
            var highX = middle.X + Math.Max(field.AlongX * x0, field.AlongX * x1) + Math.Max(-field.AlongZ * z0, -field.AlongZ * z1) + Slack;
            var lowZ = middle.Z + Math.Min(field.AlongZ * x0, field.AlongZ * x1) + Math.Min(field.AlongX * z0, field.AlongX * z1) - Slack;
            var highZ = middle.Z + Math.Max(field.AlongZ * x0, field.AlongZ * x1) + Math.Max(field.AlongX * z0, field.AlongX * z1) + Slack;
            var lowY = middle.Y - field.Height - Slack;
            var highY = middle.Y + field.Height + Slack;
            var rocks = PieceStart(field.Count, piece + 1) - PieceStart(field.Count, piece);
            if (rocks == 0 || lowX >= half || highX < -half || lowY >= half || highY < -half || lowZ >= half || highZ < -half)
                continue;

            reaches = true;
            if (lowX >= -half && highX < half && lowY >= -half && highY < half && lowZ >= -half && highZ < half)
            {
                field.Pieces |= Inside << piece * 2;
                expected += rocks;
                continue;
            }

            // How much of the piece's box lies in the sector stands in for how many of its rocks do.
            field.Pieces |= Straddles << piece * 2;
            var share = (Math.Min(highX, half) - Math.Max(lowX, -half)) / (highX - lowX);
            share *= (Math.Min(highY, half) - Math.Max(lowY, -half)) / (highY - lowY);
            share *= (Math.Min(highZ, half) - Math.Max(lowZ, -half)) / (highZ - lowZ);
            expected += rocks * share;
        }

        return reaches;
    }

    /// <summary>Whether a position relative to a sector's centre lies in that sector: each wall belongs to one side.</summary>
    private static bool Holds(Vector3 position, float half) =>
        position.X >= -half && position.X < half && position.Y >= -half && position.Y < half && position.Z >= -half && position.Z < half;

    /// <summary>The number of a field's first rock in a piece of its length.</summary>
    private int PieceStart(int count, int piece) => piece >= Segments ? count : (int)(count * _piecesBefore[piece]);

    /// <summary>
    /// Decides the fields a sector anchors: how many, of which kind, where, how large and with how many asteroids
    /// each. Returns the number of fields. Cheap, so a sector can ask it of every sector around it.
    /// </summary>
    private int Plan(in SectorContext sector, Span<Field> fields, out int total)
    {
        total = 0;
        if (_amount == 0 || MaxPerSector == 0)
            return 0;
        if (sector.IsHome)
        {
            // The home sector always has the same field to work, beside the arrival point.
            fields[0] = _home;
            fields[0].Count = total = Math.Min(Math.Max((int)(_home.Count * _amount), _frequency == 0 ? 0 : _countMin), MaxPerSector);
            return 1;
        }

        // Rock follows the galaxy's spiral: most sectors in an arm anchor a field, almost none in the void do.
        total = 0;
        var random = Stream(sector, PlanStream);
        var fill = Math.Min(1f, sector.Density * _densityGain);
        if (random.NextSingle() >= Math.Min(1f, (_voidFieldChance + (1f - _emptySectorChance) * fill) * _frequency))
            return 0;

        var wanted = _fieldsMin + random.NextInt(_fieldsMax - _fieldsMin + 1);
        var abundance = _sparseFill + (1f - _sparseFill) * fill;
        var half = sector.HalfSize;
        var count = 0;
        for (var i = 0; i < wanted && total < MaxPerSector; i++)
        {
            var pick = random.NextSingle() * _kindWeight;
            var kind = _kinds[^1];
            foreach (var candidate in _kinds)
            {
                pick -= candidate.Weight;
                if (pick < 0)
                {
                    kind = candidate;
                    break;
                }
            }

            // Squaring the roll makes full fields the exception within each kind.
            ref var field = ref fields[count++];
            field = default;
            var share = random.NextSingle();
            field.Count = Math.Min(Math.Max(1, (int)(kind.Asteroids.At(share * share) * abundance * _amount)), MaxPerSector - total);
            field.Length = kind.Length.At(random.NextSingle());
            field.Width = Math.Min(kind.Width.At(random.NextSingle()), field.Length);
            field.Height = Math.Min(kind.Height.At(random.NextSingle()), field.Width);
            Heading(ref random, out field.AlongX, out field.AlongZ);

            // Anywhere in the sector, so that fields sit where they fall and not around sector centres. Fields
            // may overlap. Only actual asteroid surfaces reserve space.
            field.Center = new Vector3(
                random.NextSigned() * half * _centerReach,
                random.NextSigned() * half * _centerReach * _centerFlatten,
                random.NextSigned() * half * _centerReach
            );
            total += field.Count;
        }

        if (count > 0 && total < _countMin)
        {
            var extra = Math.Min(_countMin, MaxPerSector) - total;
            fields[0].Count += extra;
            total += extra;
        }
        return count;
    }

    /// <summary>A random horizontal direction, without trigonometry: a point in the unit disc, pushed out to its rim.</summary>
    private static void Heading(ref SectorRandom random, out float x, out float z)
    {
        for (var i = 0; i < 16; i++)
        {
            x = random.NextSigned();
            z = random.NextSigned();
            var lengthSquared = x * x + z * z;
            if (lengthSquared is > 0.01f and <= 1f)
            {
                var length = MathF.Sqrt(lengthSquared);
                x /= length;
                z /= length;
                return;
            }
        }

        x = 1f;
        z = 0f;
    }

    /// <summary>The combined weight of the size classes a field may draw from. The smallest class is always allowed.</summary>
    private float AllowedWeight(float limit)
    {
        var weight = 0f;
        for (var i = 0; i < _sizes.Length; i++)
        {
            if (i == 0 || _sizes[i].Radius.Min * _size <= limit)
                weight += _sizes[i].Weight;
        }

        return weight;
    }

    /// <summary>
    /// Draws an asteroid's radius: a class by weight, then a size within it leaning toward the class's small end.
    /// Classes that start above <paramref name="limit"/> are left out and the result is capped at it.
    /// </summary>
    private float DrawRadius(ref SectorRandom random, float limit, float allowedWeight)
    {
        var pick = random.NextSingle() * allowedWeight;
        var within = random.NextSingle();
        var chosen = 0;
        for (var i = 0; i < _sizes.Length; i++)
        {
            if (i != 0 && _sizes[i].Radius.Min * _size > limit)
                continue;
            chosen = i;
            pick -= _sizes[i].Weight;
            if (pick < 0)
                break;
        }

        return Math.Min(Math.Clamp(_sizes[chosen].Radius.At(within * within) * _size, _radiusLimits.Min, _radiusLimits.Max), limit);
    }

    /// <summary>
    /// Finds a place for an asteroid at or near where it first lands: inside its sector's walls, clear of the
    /// sector's arrival point and, where a few tries can manage it, clear of the asteroids already placed. The
    /// tries stay near the first one, so a crowded rock never moves to another part of its field: a field's
    /// density then hangs on the place alone and is the same on both sides of a sector wall. Exhausted attempts
    /// omit the asteroid.
    /// </summary>
    private Vector3 Settle(ref SectorRandom random, in Field field, Vector3 origin, float radius, float half, SectorContent content, int placed)
    {
        var wall = half - radius;
        var clear = radius + ArrivalClearance;
        var reach = radius * Math.Max(1f, _spacing * _spacingScale);
        var drift = Math.Min(_jitter * reach, field.Height);
        var position = origin;
        for (var attempt = 0; attempt < _placementAttempts; attempt++)
        {
            // Fields are flat, so a rock looks for room sideways more than up and down.
            if (attempt > 0)
                position = new Vector3(origin.X + random.NextSigned() * drift, origin.Y + random.NextSigned() * drift * 0.5f, origin.Z + random.NextSigned() * drift);
            if (Math.Abs(position.X) > wall || Math.Abs(position.Y) > wall || Math.Abs(position.Z) > wall)
                continue;
            if (position.X * position.X + position.Y * position.Y + position.Z * position.Z < clear * clear)
                continue;
            if (!Overlaps(content, placed, position, reach))
                return position;
        }

        return new Vector3(float.NaN);
    }

    /// <summary>True if a sphere would come within reach of an asteroid already placed.</summary>
    internal static bool Overlaps(SectorContent content, int placed, Vector3 position, float reach) =>
        content.Rocks.PlacementOverlaps(content.Asteroids, placed, position, reach);

    /// <summary>
    /// Where a rock of a piece of a field first lands, in the frame of the sector that anchors the field: a
    /// point of a bent, variable-width ribbon with tapered ends, thinning toward its edge.
    /// </summary>
    private Vector3 PointIn(ref SectorRandom random, in Field field, ReadOnlySpan<RibbonKnot> knots, int piece)
    {
        // A draw just under one can round up to the piece's far end, which is the last knot for the last piece.
        var t = (piece + random.NextSingle()) * (1f / PiecesPerSpan);
        var k = Math.Min((int)t, KnotsPerField - 2);
        var fraction = t - k;
        fraction = fraction * fraction * (3f - 2f * fraction);
        var a = knots[k];
        var b = knots[k + 1];
        var centerZ = a.Center + (b.Center - a.Center) * fraction;
        var width = a.Width + (b.Width - a.Width) * fraction;
        var x = t * (2f / (KnotsPerField - 1)) - 1f;
        var x2 = x * x;
        var taper = 1f - x2 * x2;
        float y, z, squared;
        var tries = 0;
        do
        {
            y = random.NextSigned();
            z = random.NextSigned();
            squared = y * y + z * z;
        } while (squared > 1f && ++tries < 8);
        // The usual path needs no roots. The bounded rejection fallback still stays inside the disc.
        if (squared > 1f)
        {
            var inverse = 1f / MathF.Sqrt(squared);
            y *= inverse;
            z *= inverse;
            squared = 1f;
        }

        // A share of the rocks is drawn toward the ribbon's spine, so a field fades out toward its edge
        // rather than ending at it. The squared distance of an even point is even; the lesser of two even
        // numbers falls off in a straight line, which is a density that fades to nothing at the rim.
        var fades = random.NextSingle() < _fade;
        var inner = random.NextSingle();
        if (fades && inner < squared)
        {
            var pull = MathF.Sqrt(inner / squared);
            y *= pull;
            z *= pull;
        }

        y *= (1f - x2 * x2 * x2 * x2);
        z = centerZ + z * width * taper;
        return new Vector3(
            field.Center.X + field.AlongX * (x * field.Length) - field.AlongZ * (z * field.Width),
            field.Center.Y + y * field.Height,
            field.Center.Z + field.AlongZ * (x * field.Length) + field.AlongX * (z * field.Width)
        );
    }

    /// <summary>
    /// Fills a sector's lattice with the share of rocks the world's density noise keeps at each point: from
    /// almost none to all of them. Two lattices in world space, a broad one and a fine one, whose points on a
    /// sector's wall are the neighbour's too, so the noise runs through the wall unbroken.
    /// </summary>
    private void BuildThinning(in SectorContext sector, SectorContent content)
    {
        var cells = _thinCells;
        var side = cells + 1;
        if (content.Thinning.Length < side * side * side)
            content.Thinning = new float[side * side * side];

        var broadCells = _thinBroadCells;
        var broadSide = broadCells + 1;
        Span<float> broad = stackalloc float[broadSide * broadSide * broadSide];
        var broadKey = GalaxyKey(sector.Galaxy, BroadNoiseStream);
        var fineKey = GalaxyKey(sector.Galaxy, FineNoiseStream);
        var coord = sector.Coord;
        for (var z = 0; z < broadSide; z++)
            for (var y = 0; y < broadSide; y++)
                for (var x = 0; x < broadSide; x++)
                    broad[(z * broadSide + y) * broadSide + x] = SectorRandom.Lattice(broadKey, coord.X * broadCells + x, coord.Y * broadCells + y, coord.Z * broadCells + z);

        // A fine point on a sector's wall is a broad point too, to the last bit: the product is whole there.
        var kept = content.Thinning;
        var index = 0;
        for (var z = 0; z < side; z++)
        {
            Cell(z * broadCells / (float)cells, broadCells, out var bz, out var fz);
            for (var y = 0; y < side; y++)
            {
                Cell(y * broadCells / (float)cells, broadCells, out var by, out var fy);
                for (var x = 0; x < side; x++)
                {
                    Cell(x * broadCells / (float)cells, broadCells, out var bx, out var fx);
                    var at = (bz * broadSide + by) * broadSide + bx;
                    var near = Blend(Blend(broad[at], broad[at + 1], fx), Blend(broad[at + broadSide], broad[at + broadSide + 1], fx), fy);
                    at += broadSide * broadSide;
                    var far = Blend(Blend(broad[at], broad[at + 1], fx), Blend(broad[at + broadSide], broad[at + broadSide + 1], fx), fy);
                    var fine = SectorRandom.Lattice(fineKey, coord.X * cells + x, coord.Y * cells + y, coord.Z * cells + z);
                    kept[index++] = KeptAt(_thinBroadShare * Blend(near, far, fz) + (1f - _thinBroadShare) * fine);
                }
            }
        }

        // The cell a lattice coordinate lies in and how far along it, eased so the broad noise has no creases.
        static void Cell(float coordinate, int cells, out int cell, out float fraction)
        {
            cell = Math.Min((int)coordinate, cells - 1);
            fraction = coordinate - cell;
            fraction = fraction * fraction * (3f - 2f * fraction);
        }

        static float Blend(float a, float b, float t) => a + (b - a) * t;
    }

    /// <summary>The share of rocks kept where the density noise has a value: few below the band, all above it.</summary>
    private float KeptAt(float noise)
    {
        var t = Math.Clamp((noise - _thinFrom) / (_thinTo - _thinFrom), 0f, 1f);
        return _thinSparse + (1f - _thinSparse) * (t * t * (3f - 2f * t));
    }

    /// <summary>The share of rocks kept at a position in a sector, between the points of its lattice.</summary>
    private float Kept(float[] lattice, Vector3 position, float half)
    {
        var cells = _thinCells;
        var side = cells + 1;
        var scale = cells / (half * 2f);
        var x = (position.X + half) * scale;
        var y = (position.Y + half) * scale;
        var z = (position.Z + half) * scale;
        var cx = Math.Clamp((int)x, 0, cells - 1);
        var cy = Math.Clamp((int)y, 0, cells - 1);
        var cz = Math.Clamp((int)z, 0, cells - 1);
        var fx = x - cx;
        var fy = y - cy;
        var fz = z - cz;
        var at = (cz * side + cy) * side + cx;
        var near = lattice[at] + (lattice[at + 1] - lattice[at]) * fx;
        var nearUp = lattice[at + side] + (lattice[at + side + 1] - lattice[at + side]) * fx;
        at += side * side;
        var far = lattice[at] + (lattice[at + 1] - lattice[at]) * fx;
        var farUp = lattice[at + side] + (lattice[at + side + 1] - lattice[at + side]) * fx;
        near += (nearUp - near) * fy;
        far += (farUp - far) * fy;
        return near + (far - near) * fz;
    }

    /// <summary>
    /// The share of rocks the density noise keeps on average, for the map's estimate: the mean over many
    /// lattice points, each with a broad value blended from eight draws and a fine one of its own.
    /// </summary>
    private float MeanKept()
    {
        const int samples = 4096;
        var random = new SectorRandom(0x6A09E667F3BCC908, default, SectorRandom.Key("mean-kept"));
        var sum = 0.0;
        Span<float> corners = stackalloc float[8];
        for (var i = 0; i < samples; i++)
        {
            for (var c = 0; c < corners.Length; c++)
                corners[c] = random.NextSingle();
            var fx = Ease(random.NextSingle());
            var fy = Ease(random.NextSingle());
            var fz = Ease(random.NextSingle());
            var near = Blend(Blend(corners[0], corners[1], fx), Blend(corners[2], corners[3], fx), fy);
            var far = Blend(Blend(corners[4], corners[5], fx), Blend(corners[6], corners[7], fx), fy);
            sum += KeptAt(_thinBroadShare * Blend(near, far, fz) + (1f - _thinBroadShare) * random.NextSingle());
        }

        return (float)(sum / samples);

        static float Ease(float t) => t * t * (3f - 2f * t);
        static float Blend(float a, float b, float t) => a + (b - a) * t;
    }

    /// <summary>A random orientation, normalised by hand so that it is the same to the last bit everywhere.</summary>
    private static Quaternion Rotation(ref SectorRandom random)
    {
        var x = random.NextSigned();
        var y = random.NextSigned();
        var z = random.NextSigned();
        var w = random.NextSigned();
        var lengthSquared = x * x + y * y + z * z + w * w;
        if (lengthSquared < 1e-4f)
            return Quaternion.Identity;
        var length = MathF.Sqrt(lengthSquared);
        return new Quaternion(x / length, y / length, z / length, w / length);
    }
}
