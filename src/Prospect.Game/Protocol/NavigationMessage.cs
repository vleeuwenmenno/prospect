using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

public static class NavigationMessage
{
    public const int RequestWireSize = 1 + 4 + 1 + 24;
    public const int MaxWireSize = RequestWireSize + 5;

    public static void Write(ref NetWriter writer, MessageId id, in NavigationView view)
    {
        writer.WriteByte((byte)id);
        writer.WriteUInt32(view.Hull);
        writer.WriteByte(view.HasPlot ? (byte)1 : (byte)0);
        writer.WriteDouble(view.Target.X); writer.WriteDouble(view.Target.Y); writer.WriteDouble(view.Target.Z);
        if (id == MessageId.Navigation) { writer.WriteUInt32(view.ResultHull); writer.WriteByte((byte)view.Refusal); }
    }

    public static bool Read(ref NetReader reader, bool response, out NavigationView view)
    {
        view = new NavigationView { Hull = reader.ReadUInt32() };
        var flag = reader.ReadByte();
        view.HasPlot = flag == 1;
        view.Target = new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        if (response) { view.ResultHull = reader.ReadUInt32(); view.Refusal = (JumpRefusal)reader.ReadByte(); }
        return reader.IsValid && reader.Remaining == 0 && flag <= 1 && view.Refusal <= JumpRefusal.Busy
            && double.IsFinite(view.Target.X) && double.IsFinite(view.Target.Y) && double.IsFinite(view.Target.Z);
    }
}
