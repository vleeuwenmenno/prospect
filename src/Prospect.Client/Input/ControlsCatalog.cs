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
                InputAction.Halt => "Pilot / command",
                InputAction.Follow => "Command",
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
        // In the pilot view the show-cursor key frees the mouse; say which key that is now.
        var release = new List<string>();
        foreach (var binding in map.Bindings(InputAction.ReleasePointer))
            release.Add(binding.DisplayName);
        var hold = release.Count == 0 ? "the show-cursor key (unbound)" : string.Join(" or ", release);
        rows.AddRange([
            new("Pilot", "Steer view", "Mouse movement"),
            new("Pilot", "Free look", "Hold right mouse button"),
            new("Pilot", "Zoom camera", "Mouse wheel"),
            new("Pilot", "Target under reticle", "Middle mouse button"),
            new("Pilot", "Show cursor (mouse leaves flight)", $"Hold {hold}; keyboard flight still works"),
            new("Pilot", "Turn the camera with the cursor shown", $"Hold {hold}, then right drag over space (press {hold} first)"),
            new("Command", "Cursor", "Always shown; the mouse works in the world except over panels and buttons"),
            new("Command", "Select / box select", "Left mouse button / left drag; Shift or Ctrl adds"),
            new("Command", "Select all owned ships", "Ctrl + A"),
            new("Command", "Move selection / rotate view", "Right click / right drag"),
            new("Command", "Pan / zoom", "Strafe + Yaw and Forward + Pitch axes; middle drag / mouse wheel"),
            new("Command", "Raise / lower plane", "Roll axis; Shift + left drag"),
            new("Command", "Focus fleet ship", "Double-click fleet row"),
            new("Map", "Select / box select", "Left mouse button / left drag; Shift or Ctrl adds"),
            new("Map", "Pan / zoom", "Middle drag / mouse wheel; Strafe + Yaw and Forward + Pitch axes"),
            new("Map", "Move or jump course menu", "Right click sector"),
            new("Map", "Change layer / centre on own ship", "PageUp / PageDown / Space"),
            new("Social", "Send chat line / close chat", "Enter in the chat field / Escape"),
            new("Social", "Quick buttons (players, party, chat)", $"Click; in the pilot view hold {hold} first"),
        ]);
        Rows = rows.ToArray();
    }
}
