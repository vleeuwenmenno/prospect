using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Client;

internal sealed partial class ClientWorld
{
    private readonly ModuleKnowledgeView _modules = new();

    public void OnModuleSurvey(ref NetReader reader)
    {
        Span<ModuleObservation> records = stackalloc ModuleObservation[ModuleMessage.MaxObservations];
        if (!ModuleMessage.Read(ref reader, records, out var scan, out var count)) return;
        for (var i = 0; i < count; i++)
            if (records[i].Owner != _playerId || records[i].Resource >= _content.Galaxy.Resources.Length) return;
        _modules.Scan = scan;
        for (var i = 0; i < count; i++)
        {
            var record = records[i];
            var index = _modules.Find(record.Family, record.Sector, record.Index);
            if (index < 0)
            {
                if (_modules.Count == _modules.Records.Length) continue;
                index = _modules.Count++;
            }
            if (_modules.Records[index].RecordedTick <= record.RecordedTick) _modules.Records[index] = record;
        }
    }

    public void CopyModules(ModuleKnowledgeView view)
    {
        view.Scan = _modules.Scan;
        view.Count = _modules.Count;
        _modules.Records.AsSpan(0, _modules.Count).CopyTo(view.Records);
    }
}
