using System.Numerics;
using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>
/// Fills a sector with asteroid fields. A sector gets none, one or several fields, each of a kind from the
/// content (a thin scatter, a cluster, a dense belt); every asteroid gets a size from a table weighted toward the
/// tiny, and those that carry ore get it from the sector's character.
///
/// Everything here is plain arithmetic in a fixed order, with no library function whose last bit differs between
/// machines: which asteroid sits where decides which one a collision names, so the server and every client must
/// build exactly the same list.
/// </summary>
public sealed class AsteroidFieldsModule : SectorModule
{
    public const string TypeName = "asteroidFields";

    /// <summary>The most fields one sector can have.</summary>
    public const int MaxFields = 8;

    // One stream per decision, so that retuning sizes or ore never moves a rock.
    private const uint PlanStream = 1;
    private const uint SizeStream = 2;
    private const uint PlaceStream = 3;
    private const uint LookStream = 4;
    private const uint OreStream = 5;

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

    /// <summary>One field of a sector: an ellipsoid lying flat, turned to some heading.</summary>
    private struct Field
    {
        public Vector3 Center;

        /// <summary>Unit vector along the field's length, in the horizontal plane.</summary>
        public float AlongX;
        public float AlongZ;

        /// <summary>How far the field reaches from its centre along its length, across it and vertically.</summary>
        public float Length;
        public float Width;
        public float Height;
        public int Count;

        /// <summary>The largest asteroid the field takes, so that one rock cannot swallow the field.</summary>
        public float LargestRadius;
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

    private AsteroidFieldsModule? _baseRules;
    private float _amount = 1, _size = 1, _fieldSize = 1, _frequency = 1, _spacingScale = 1, _oreShareScale = 1, _gradeScale = 1;
    private bool _boundedPlacement;
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
        copy._boundedPlacement = original._boundedPlacement || copy._amount != 1 || copy._size != 1 || copy._fieldSize != 1 || copy._spacingScale != 1 || copy._countMin != 0 || copy._radiusLimits.Min > original.SmallestRadius * catalog.Number(clean, "asteroidSize") || copy._radiusLimits.Max < original.LargestRadius * catalog.Number(clean, "asteroidSize");
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
        _home.LargestRadius = _largestShareOfField * _home.Width;

        _kinds = [.. kinds];
        _sizes = [.. sizes];

        // Optional scales rebase live content while old fixtures keep their original arithmetic.
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
            _boundedPlacement = true;

            float Baseline(string name)
            {
                var value = baseline.GetProperty(name).GetSingle();
                if (!float.IsFinite(value) || value <= 0 || value > 5)
                    throw new InvalidDataException($"{path}: {id}: baseline {name} must be positive and at most 5");
                return value;
            }
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

    /// <summary>Whether planned counts are upper bounds because crowded placements can be omitted.</summary>
    public bool EstimatesCounts => _boundedPlacement;

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

    public override void Summarize(in SectorContext sector, ref SectorSummary summary)
    {
        Span<Field> fields = stackalloc Field[MaxFields];
        summary.FieldCount = Plan(sector, fields, out var total);
        summary.AsteroidCount = total;
    }

    public override void Generate(in SectorContext sector, SectorContent content)
    {
        Span<Field> fields = stackalloc Field[MaxFields];
        var fieldCount = Plan(sector, fields, out var total);
        content.AsteroidCount = total;
        content.Rocks.Reset(total, sector.Galaxy.SectorSize);
        if (total == 0)
            return;
        content.Reserve(total);

        // Sizes first, for every asteroid of every field, then sorted largest first. The big ones are then placed
        // while the field is empty and the small ones fill in around them. The order is also what makes distant
        // sectors cheap to draw: the loop over a sector stops at the first asteroid too small to see.
        //
        // A key is the radius (inverted, so that ascending keys run from large to small), then the field, then
        // the order drawn. No two keys are equal, so the result does not depend on how the sort breaks ties.
        var keys = content.SortKeys.AsSpan(0, total);
        var sizes = Stream(sector, SizeStream);
        var drawn = 0u;
        for (var f = 0; f < fieldCount; f++)
        {
            var limit = _boundedPlacement ? fields[f].LargestRadius : Math.Max(fields[f].LargestRadius, _sizes[0].Radius.Max);
            var allowed = AllowedWeight(limit);
            for (var i = 0; i < fields[f].Count; i++)
            {
                var radius = DrawRadius(ref sizes, limit, allowed);
                keys[(int)drawn] = ((ulong)~BitConverter.SingleToUInt32Bits(radius) << 32) | ((ulong)f << 24) | drawn;
                drawn++;
            }
        }

        keys.Sort();

        var place = Stream(sector, PlaceStream);
        var look = Stream(sector, LookStream);
        var half = sector.HalfSize;
        var asteroids = content.Asteroids;
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        var placed = 0;
        var occupiedFields = 0u;
        for (var i = 0; i < total; i++)
        {
            var radius = BitConverter.UInt32BitsToSingle(~(uint)(keys[i] >> 32));
            if (radius < _radiusLimits.Min) continue;
            var position = Place(ref place, fields[(int)(keys[i] >> 24 & 0xFF)], radius, half, content, placed);
            var rotation = Rotation(ref look);
            var shape = look.NextInt(Galaxy.AsteroidShapes);
            if (!float.IsFinite(position.X))
                continue;
            occupiedFields |= 1u << (int)(keys[i] >> 24 & 0xFF);
            asteroids[placed] = new Asteroid
            {
                LocalPosition = position,
                Radius = radius,
                Rotation = rotation,
                Shape = shape,
                Resource = -1,
            };
            content.Rocks.Add(placed++, position, radius);
            low = Vector3.Min(low, position - new Vector3(radius));
            high = Vector3.Max(high, position + new Vector3(radius));
        }

        content.AsteroidCount = placed;
        content.Summary.AsteroidCount = placed;
        content.Summary.FieldCount = BitOperations.PopCount(occupiedFields);
        if (placed == 0)
            return;
        total = placed;
        content.BoundsCenter = (low + high) * 0.5f;
        content.BoundsRadius = ((high - low) * 0.5f).Length();

        // What each rock is made of. Three numbers are drawn for every rock whether it turns out to carry ore or
        // not, so one rock's outcome never shifts the next one's.
        var character = content.Summary.Character;
        var oreShare = character.Resources == 0 ? 0f : Math.Min(1f, _oreShare.At(character.Richness) * _oreShareScale);
        var ore = Stream(sector, OreStream);
        for (var i = 0; i < total; i++)
        {
            var roll = ore.NextSingle();
            var pick = ore.NextSingle();
            var quality = ore.NextSingle();
            if (roll >= oreShare)
                continue;
            asteroids[i].Resource = character.Pick(pick);
            var grade = Math.Min(1f, _grade.At(_gradeFromRichness * character.Richness + (1f - _gradeFromRichness) * quality * quality) * _gradeScale);
            grade = Math.Clamp(grade, _gradeLimits.Min, _gradeLimits.Max);
            var resource = asteroids[i].Resource;
            asteroids[i].Grade = _materialGrades.Length == 0 ? grade : Math.Clamp(grade, _materialGrades[resource].Min, _materialGrades[resource].Max);
        }
    }

    /// <summary>
    /// Decides a sector's fields: how many, of which kind, where, how large and with how many asteroids each.
    /// Returns the number of fields. Cheap, so the summary can run it for any sector.
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
            Fit(ref fields[0], sector.HalfSize);
            return 1;
        }

        // Rock follows the galaxy's spiral: most sectors in an arm have a field, almost none in the void do.
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
            var share = random.NextSingle();
            field.Count = Math.Min(Math.Max(1, (int)(kind.Asteroids.At(share * share) * abundance * _amount)), MaxPerSector - total);
            field.Length = kind.Length.At(random.NextSingle());
            field.Width = Math.Min(kind.Width.At(random.NextSingle()), field.Length);
            field.Height = Math.Min(kind.Height.At(random.NextSingle()), field.Width);
            Heading(ref random, out field.AlongX, out field.AlongZ);

            // Anywhere in the sector's middle, kept far enough from the walls for the whole field to fit.
            var room = Math.Max(0f, half - field.Length);
            field.Center = new Vector3(
                Math.Clamp(random.NextSigned() * half * _centerReach, -room, room),
                Math.Clamp(random.NextSigned() * half * _centerReach * _centerFlatten, -room, room),
                Math.Clamp(random.NextSigned() * half * _centerReach, -room, room)
            );
            field.LargestRadius = _largestShareOfField * field.Width;
            Fit(ref field, half);
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

    // Legacy content preserves the recorded galaxy; rebased and custom geometry must fit at every extreme.
    private void Fit(ref Field field, float half)
    {
        if (!_boundedPlacement)
            return;
        field.Length = Math.Min(field.Length * _fieldSize, half);
        field.Width = Math.Min(field.Width * _fieldSize, field.Length);
        field.Height = Math.Min(field.Height * _fieldSize, field.Width);
        var room = half - field.Length;
        field.Center = Vector3.Clamp(field.Center, new Vector3(-room), new Vector3(room));
        field.LargestRadius = Math.Min(_largestShareOfField * field.Width, field.Height);
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
    /// Finds a place for an asteroid in its field: clear of the sector's arrival point and, where a few tries can
    /// manage it, clear of the asteroids already placed. Custom placement omits failed attempts; the original
    /// rules retain the last try to preserve the recorded default galaxy.
    /// </summary>
    private Vector3 Place(ref SectorRandom random, in Field field, float radius, float half, SectorContent content, int placed)
    {
        var wall = half - radius;
        var clear = radius + ArrivalClearance;
        var reach = radius * Math.Max(1f, _spacing * _spacingScale);
        var position = default(Vector3);
        for (var attempt = 0; attempt < _placementAttempts; attempt++)
        {
            var interior = field;
            if (_boundedPlacement)
            {
                interior.Length -= radius;
                interior.Width -= radius;
                interior.Height -= radius;
            }
            position = PointIn(ref random, interior);
            position = new Vector3(Math.Clamp(position.X, -wall, wall), Math.Clamp(position.Y, -wall, wall), Math.Clamp(position.Z, -wall, wall));
            if (position.X * position.X + position.Y * position.Y + position.Z * position.Z < clear * clear)
                continue;
            if (!Overlaps(content, placed, position, reach))
                return position;
        }

        if (_boundedPlacement)
            return new Vector3(float.NaN);

        // The arrival point stays clear whatever else gives: push the asteroid straight out of it.
        var fromArrival = MathF.Sqrt(position.X * position.X + position.Y * position.Y + position.Z * position.Z);
        if (fromArrival < clear)
        {
            var scale = fromArrival > 1e-3f ? clear / fromArrival : 0f;
            position = scale > 0f ? new Vector3(position.X * scale, position.Y * scale, position.Z * scale) : new Vector3(0, 0, -clear);
        }

        return position;
    }

    /// <summary>True if a sphere would come within reach of an asteroid already placed.</summary>
    internal static bool Overlaps(SectorContent content, int placed, Vector3 position, float reach) =>
        content.Rocks.PlacementOverlaps(content.Asteroids, placed, position, reach);

    /// <summary>
    /// A random point of a field. It is drawn evenly from a ball and then pulled toward the middle, so a field is
    /// thick at its heart and thins out toward its edge, and the ball is stretched and turned into the field.
    /// </summary>
    private static Vector3 PointIn(ref SectorRandom random, in Field field)
    {
        float x;
        float y;
        float z;
        float lengthSquared;
        var tries = 0;
        do
        {
            x = random.NextSigned();
            y = random.NextSigned();
            z = random.NextSigned();
            lengthSquared = x * x + y * y + z * z;
        } while (lengthSquared > 1f && ++tries < 8);

        var length = MathF.Sqrt(lengthSquared);
        var pull = length > 1f ? 1f / length : MathF.Sqrt(MathF.Sqrt(length));
        x *= pull;
        y *= pull;
        z *= pull;
        return new Vector3(
            field.Center.X + field.AlongX * (x * field.Length) - field.AlongZ * (z * field.Width),
            field.Center.Y + y * field.Height,
            field.Center.Z + field.AlongZ * (x * field.Length) + field.AlongX * (z * field.Width)
        );
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
