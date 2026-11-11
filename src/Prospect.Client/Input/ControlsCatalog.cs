namespace Prospect.Client.Input;

/// <summary>Cached controls help, built from the loaded bindings rather than their defaults.</summary>
internal sealed class ControlsCatalog
{
    internal static readonly string[] AxisLabels = ["Strafe left / right", "Down / up", "Back / forward", "Look down / up", "Look left / right", "Roll"];
    internal static readonly string[] ActionLabels =
    [
        "Menu / back", "Performance overlay", "Fullscreen", "Screenshot", "Boost", "Dampeners on / off",
        "Command view", "Galaxy map", "Halt (cancel order)", "Focus on selection (command view)", "Ship data sheet", "Take control of target / leave in drone",
        "Controls / help", "Hold to show cursor", "Players on this server", "Party (social)", "Chat",
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
            var category = (InputAction)i switch
            {
                InputAction.Boost or InputAction.ToggleDampeners or InputAction.SwitchHull => "Pilot",
                InputAction.Halt or InputAction.Follow => "Pilot / command",
                InputAction.TogglePlayers or InputAction.ToggleSocial or InputAction.ToggleChat => "Social",
                _ => "General",
            };
            rows.Add(new Row(category, ActionLabels[i], names.Count == 0 ? "Unbound" : string.Join(" / ", names)));
        }
        for (var i = 0; i < (int)InputAxis.Count; i++)
        {
            var names = new List<string>();
            foreach (var binding in map.Bindings((InputAxis)i))
                names.Add(binding.DisplayName);
            rows.Add(new Row("Flight axes / command / map", AxisLabels[i], names.Count == 0 ? "Unbound" : string.Join(" / ", names)));
        }
        // The command view's mouse works only while the show-cursor key is held; say which key that is now.
        var release = new List<string>();
        foreach (var binding in map.Bindings(InputAction.ReleasePointer))
            release.Add(binding.DisplayName);
        var hold = release.Count == 0 ? "the show-cursor key (unbound)" : string.Join(" or ", release);
        rows.AddRange([
            new("Pilot", "Steer view", "Mouse movement"),
            new("Pilot", "Free look / camera distance", "Hold right mouse button / hold right + wheel"),
            new("Pilot", "Target under reticle", "Middle mouse button"),
            new("Pilot", "Show cursor (mouse leaves flight)", $"Hold {hold}"),
            new("Command", "Show cursor and use the mouse", $"Hold {hold}; without it the cursor is hidden and only keys work"),
            new("Command", "Select / box select", $"Hold {hold}: left mouse button / left drag; Shift or Ctrl adds"),
            new("Command", "Select all owned ships", "Ctrl + A"),
            new("Command", "Move selection / rotate view", $"Hold {hold}: right click / right drag"),
            new("Command", "Pan / zoom", $"Strafe + Yaw and Forward + Pitch axes; hold {hold}: middle drag / mouse wheel"),
            new("Command", "Raise / lower plane", $"Roll axis; hold {hold}: Shift + left drag"),
            new("Command", "Focus fleet ship", $"Hold {hold}: double-click fleet row"),
            new("Map", "Select / box select", "Left mouse button / left drag; Shift or Ctrl adds"),
            new("Map", "Pan / zoom", "Middle drag / mouse wheel; Strafe + Yaw and Forward + Pitch axes"),
            new("Map", "Move or jump course menu", "Right click sector"),
            new("Map", "Change layer / centre on own ship", "PageUp / PageDown / Space"),
            new("Social", "Send chat line / close chat", "Enter in the chat field / Escape"),
            new("Social", "Quick buttons (players, party, chat)", $"Hold {hold}, then click"),
        ]);
        Rows = rows.ToArray();
    }
}
