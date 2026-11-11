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
    public int MaxPerSector { get; }

    /// <summary>Radius of the smallest and of the largest asteroid the size table allows, in metres.</summary>
    public float SmallestRadius { get; }
    public float LargestRadius { get; }

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
            if (radius >= size.Radius.Min)
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
            var limit = Math.Max(fields[f].LargestRadius, _sizes[0].Radius.Max);
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
        for (var i = 0; i < total; i++)
        {
            var radius = BitConverter.UInt32BitsToSingle(~(uint)(keys[i] >> 32));
            var position = Place(ref place, fields[(int)(keys[i] >> 24 & 0xFF)], radius, half, content);
            var rotation = Rotation(ref look);
            var shape = look.NextInt(Galaxy.AsteroidShapes);
            asteroids[i] = new Asteroid
            {
                LocalPosition = position,
                Radius = radius,
                Rotation = rotation,
                Shape = shape,
                Resource = -1,
            };
            content.Rocks.Add(i, position, radius);
            low = Vector3.Min(low, position - new Vector3(radius));
            high = Vector3.Max(high, position + new Vector3(radius));
        }

        content.BoundsCenter = (low + high) * 0.5f;
        content.BoundsRadius = ((high - low) * 0.5f).Length();

        // What each rock is made of. Three numbers are drawn for every rock whether it turns out to carry ore or
        // not, so one rock's outcome never shifts the next one's.
        var character = content.Summary.Character;
        var oreShare = character.Resources == 0 ? 0f : _oreShare.At(character.Richness);
        var ore = Stream(sector, OreStream);
        for (var i = 0; i < total; i++)
        {
            var roll = ore.NextSingle();
            var pick = ore.NextSingle();
            var quality = ore.NextSingle();
            if (roll >= oreShare)
                continue;
            asteroids[i].Resource = character.Pick(pick);
            asteroids[i].Grade = _grade.At(_gradeFromRichness * character.Richness + (1f - _gradeFromRichness) * quality * quality);
        }
    }

    /// <summary>
    /// Decides a sector's fields: how many, of which kind, where, how large and with how many asteroids each.
    /// Returns the number of fields. Cheap, so the summary can run it for any sector.
    /// </summary>
    private int Plan(in SectorContext sector, Span<Field> fields, out int total)
    {
        if (sector.IsHome)
        {
            // The home sector always has the same field to work, beside the arrival point.
            fields[0] = _home;
            fields[0].Count = total = Math.Min(_home.Count, MaxPerSector);
            return 1;
        }

        // Rock follows the galaxy's spiral: most sectors in an arm have a field, almost none in the void do.
        total = 0;
        var random = Stream(sector, PlanStream);
        var fill = Math.Min(1f, sector.Density * _densityGain);
        if (random.NextSingle() >= _voidFieldChance + (1f - _emptySectorChance) * fill)
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
            field.Count = Math.Min(Math.Max(1, (int)(kind.Asteroids.At(share * share) * abundance)), MaxPerSector - total);
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
            total += field.Count;
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
            if (i == 0 || _sizes[i].Radius.Min <= limit)
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
            if (i != 0 && _sizes[i].Radius.Min > limit)
                continue;
            chosen = i;
            pick -= _sizes[i].Weight;
            if (pick < 0)
                break;
        }

        return Math.Min(_sizes[chosen].Radius.At(within * within), limit);
    }

    /// <summary>
    /// Finds a place for an asteroid in its field: clear of the sector's arrival point and, where a few tries can
    /// manage it, clear of the asteroids already placed. In a field too crowded for that the last try stands, so
    /// that the number of asteroids in a sector never depends on how they happened to fall.
    /// </summary>
    private Vector3 Place(ref SectorRandom random, in Field field, float radius, float half, SectorContent content)
    {
        var wall = half - radius;
        var clear = radius + ArrivalClearance;
        var reach = radius * _spacing;
        var position = default(Vector3);
        for (var attempt = 0; attempt < _placementAttempts; attempt++)
        {
            position = PointIn(ref random, field);
            position = new Vector3(Math.Clamp(position.X, -wall, wall), Math.Clamp(position.Y, -wall, wall), Math.Clamp(position.Z, -wall, wall));
            if (position.X * position.X + position.Y * position.Y + position.Z * position.Z < clear * clear)
                continue;
            if (!Overlaps(content, position, reach))
                return position;
        }

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
    private static bool Overlaps(SectorContent content, Vector3 position, float reach)
    {
        var asteroids = content.Asteroids;
        var query = content.Rocks.Near(position, reach);
        while (query.Next(out var other))
        {
            ref readonly var rock = ref asteroids[other];
            var dx = position.X - rock.LocalPosition.X;
            var dy = position.Y - rock.LocalPosition.Y;
            var dz = position.Z - rock.LocalPosition.Z;
            var apart = reach + rock.Radius;
            if (dx * dx + dy * dy + dz * dz < apart * apart)
                return true;
        }

        return false;
    }

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
