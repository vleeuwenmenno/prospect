using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Persistence;

public static partial class WorldSnapshotCodec
{
    internal const int FreeContainerBytes = 110;

    private static void WriteFreeContainer(ref NetWriter writer, in FreeContainer c)
    {
        writer.WriteUInt32(c.Id); Write(ref writer, c.Position); Write(ref writer, c.Velocity);
        writer.WriteSingle(c.Orientation.X); writer.WriteSingle(c.Orientation.Y); writer.WriteSingle(c.Orientation.Z); writer.WriteSingle(c.Orientation.W);
        Write(ref writer, c.AngularVelocity);
        writer.WriteByte((byte)c.Size); writer.WriteByte(c.Resource < 0 ? byte.MaxValue : (byte)c.Resource);
        writer.WriteDouble(c.Amount); writer.WriteSingle(c.Hull);
        writer.WriteUInt32(c.ClaimOwner); writer.WriteInt64(c.PickupReadyTick);
        writer.WriteInt64(c.Born); writer.WriteInt64(c.Age);
    }

    private static FreeContainer ReadFreeContainer(ref NetReader reader)
    {
        var c = new FreeContainer
        {
            Id = reader.ReadUInt32(),
            Position = ReadDouble3(ref reader),
            Velocity = ReadVector3(ref reader),
            Orientation = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            AngularVelocity = ReadVector3(ref reader),
            Size = reader.ReadByte(),
            Resource = reader.ReadByte(),
            Amount = reader.ReadDouble(),
            Hull = reader.ReadSingle(),
            ClaimOwner = reader.ReadUInt32(),
            PickupReadyTick = reader.ReadInt64(),
            Born = reader.ReadInt64(),
            Age = reader.ReadInt64()
        };
        if (c.Resource == byte.MaxValue) c.Resource = -1;
        return c;
    }

    internal static bool ValidFreeContainer(in FreeContainer c, in SavedCargoRules rules, long tick, uint next)
    {
        if (c.PickupReadyTick < 0 || c.PickupReadyTick > long.MaxValue / 2 || c.Id == 0 || next != 0 && c.Id >= next || c.Born < 0 || c.Born > tick || c.Age < 0 || c.Age > tick - c.Born
            || !double.IsFinite(c.Position.X) || !double.IsFinite(c.Position.Y) || !double.IsFinite(c.Position.Z)
            || !float.IsFinite(c.Velocity.LengthSquared()) || !float.IsFinite(c.AngularVelocity.LengthSquared())
            || !float.IsFinite(c.Orientation.LengthSquared()) || Math.Abs(c.Orientation.LengthSquared() - 1) >= .001f
            || (uint)c.Size >= 8 || c.Resource < -1 || c.Resource >= Galaxy.MaxResources) return false;
        var shape = rules.Size[c.Size];
        if (!float.IsFinite(rules.Unit) || rules.Unit < .0625f || rules.Unit > 1
            || shape.X < 1 || shape.Y < 1 || shape.Z < 1 || shape.X > 128 || shape.Y > 128 || shape.Z > 128
            || shape.X * shape.Y * shape.Z != 1 << c.Size || !float.IsFinite(rules.Hull[c.Size])) return false;
        var unit = (double)rules.Unit;
        var capacity = c.Resource < 0 ? 0 : (1 << c.Size) * unit * unit * unit * rules.Density[c.Resource];
        return double.IsFinite(capacity) && (c.Resource < 0 || rules.Density[c.Resource] > 0)
            && c.Amount >= 0 && c.Amount <= capacity && c.Hull > 0 && c.Hull <= rules.Hull[c.Size];
    }
}
