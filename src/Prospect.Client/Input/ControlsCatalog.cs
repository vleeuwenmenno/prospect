namespace Prospect.Client.Input;

/// <summary>Cached controls help, built from the loaded bindings rather than their defaults.</summary>
internal sealed class ControlsCatalog
{
    internal static readonly string[] AxisLabels = ["Strafe left / right", "Down / up", "Back / forward", "Look down / up", "Look left / right", "Roll"];
    internal static readonly string[] ActionLabels =
    [
        "Menu / back", "Performance overlay", "Fullscreen", "Screenshot", "Boost", "Dampeners on / off",
        "Command view", "Galaxy map", "Halt (cancel order)", "Focus on selection (command view)", "Ship data sheet", "Take control of target / leave in drone",
        "Controls / help", "Hold to release cursor",
    ];


    public readonly record struct Row(string Category, string Action, string Binding)
    {
        public bool Matches(string query) => Category.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Action.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Binding.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public Row[] Rows { get; }

    public ControlsCatalog(ActionMap map)
    {
        var rows = new List<Row>();
        for (var i = 0; i < (int)InputAction.Count; i++)
        {
            var names = new List<string>();
            foreach (var binding in map.Bindings((InputAction)i))
                names.Add(binding.DisplayName);
            var category = i is 4 or 5 or 11 ? "Pilot" : i is 8 or 9 ? "Pilot / command" : "General";
            rows.Add(new Row(category, ActionLabels[i], names.Count == 0 ? "Unbound" : string.Join(" / ", names)));
        }
        for (var i = 0; i < (int)InputAxis.Count; i++)
        {
            var names = new List<string>();
            foreach (var binding in map.Bindings((InputAxis)i))
                names.Add(binding.DisplayName);
            rows.Add(new Row("Flight axes / command / map", AxisLabels[i], names.Count == 0 ? "Unbound" : string.Join(" / ", names)));
        }
        rows.AddRange([
            new("Pilot", "Steer view", "Mouse movement"),
            new("Pilot", "Free look / camera distance", "Hold right mouse button / hold right + wheel"),
            new("Pilot", "Target under reticle", "Middle mouse button"),
            new("Command / map", "Select / box select", "Left mouse button / left drag; Shift or Ctrl adds"),
            new("Command", "Select all owned ships", "Ctrl + A"),
            new("Command", "Move selection / rotate view", "Right click / right drag"),
            new("Command / map", "Pan / zoom", "Middle drag / mouse wheel; Strafe + Yaw and Forward + Pitch axes"),
            new("Command", "Raise / lower plane", "Roll axis or Shift + left drag"),
            new("Command", "Focus fleet ship", "Double-click fleet row"),
            new("Map", "Move or jump course menu", "Right click sector"),
            new("Map", "Change layer / centre on own ship", "PageUp / PageDown / Space"),
        ]);
        Rows = rows.ToArray();
    }
}
