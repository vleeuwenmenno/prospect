using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private void SendModules(Connection connection)
    {
        if (!connection.CatalogueReady) return;
        var owner = ModuleOwnerFor(connection.PlayerId);
        Span<ModuleObservation> records = stackalloc ModuleObservation[ModuleMessage.MaxObservations];
        Span<int> indices = stackalloc int[ModuleMessage.MaxObservations];
        var count = 0;
        var cursor = connection.ModuleCursor;
        if (owner is not null && owner.Count > 0)
            for (var visited = 0; visited < Math.Min(32, owner.Count) && count < records.Length; visited++)
            {
                if (cursor >= owner.Count) cursor = 0;
                var index = cursor++;
                if (connection.ModuleSentRecords[index] == owner.Records[index].RecordedTick) continue;
                records[count] = owner.Records[index]; indices[count++] = index;
            }
        var scan = connection.ModuleScan;
        var sent = connection.ModuleSentScan;
        if (connection.ModulesSent && count == 0 && scan.Serial == sent.Serial && scan.State == sent.State && scan.Refusal == sent.Refusal
            && scan.StartedTick == sent.StartedTick && scan.CompletesTick == sent.CompletesTick && scan.GasCooldownUntilTick == sent.GasCooldownUntilTick
            && scan.AsteroidCooldownUntilTick == sent.AsteroidCooldownUntilTick && scan.LocatorCooldownUntilTick == sent.LocatorCooldownUntilTick
            && scan.Partial == sent.Partial)
        { connection.ModuleCursor = cursor; return; }
        var writer = new NetWriter(_sendBuffer);
        ModuleMessage.Write(ref writer, scan, records[..count]);
        if (!connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written)) return;
        connection.ModuleCursor = cursor; connection.ModuleSentScan = scan; connection.ModulesSent = true;
        for (var i = 0; i < count; i++) connection.ModuleSentRecords[indices[i]] = records[i].RecordedTick;
    }
}
