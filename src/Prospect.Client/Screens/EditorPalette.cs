using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.World;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The ship editor's part list without its drawing: which parts a tab and a search show, the figures each card
/// shows at the size parts are placed at, and the one interface item that may hand a held press to the world.
/// </summary>
internal sealed class EditorPalette
{
    /// <summary>Pixels a press on a card must move before it becomes a drag of the part.</summary>
    public const float DragPixels = 6f;

    private PartFigures[] _figures = [];
    private GameContent? _figuresContent;
    private GridSize _figuresSize;
    private bool _figuresHalf;

    /// <summary>The chosen tab, as an index into <see cref="EditorTuning.Categories"/>.</summary>
    public int Category { get; set; }

    /// <summary>The search, trimmed; empty for none. While there is one it covers every tab.</summary>
    public string Query { get; set; } = "";

    /// <summary>Whether a part shows under the chosen tab and the search.</summary>
    public bool Shows(EditorTuning tuning, BlockType type)
    {
        if (Query.Length > 0)
            return Matches(tuning, type, Query);
        return (uint)Category >= (uint)tuning.Categories.Length || tuning.Lists(tuning.Categories[Category], type);
    }

    /// <summary>Whether a search finds a part: by its name, its id, what it is for, or "drone" for a compact part.</summary>
    public static bool Matches(EditorTuning tuning, BlockType type, string query) =>
        type.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || type.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
        || RoleWord(EditorGeometry.Role(type)).Contains(query, StringComparison.OrdinalIgnoreCase)
        || (tuning.Compact(type) && "drone compact".Contains(query, StringComparison.OrdinalIgnoreCase));

    /// <summary>The words a search finds a role by.</summary>
    public static string RoleWord(PartRole role) => role switch
    {
        PartRole.Core => "core computer",
        PartRole.Drive => "jump drive ftl",
        PartRole.Engine => "engine thrust",
        PartRole.Thruster => "thruster thrust",
        PartRole.Gyro => "gyro turn",
        PartRole.Power => "generator power",
        PartRole.Storage => "battery energy",
        PartRole.Shield => "shield",
        PartRole.Cargo => "cargo",
        PartRole.Crew => "crew quarters",
        _ => "structure armour hull",
    };

    /// <summary>
    /// Each part's figures at <paramref name="size"/> in <paramref name="shape"/>, worked out again only when the
    /// size, the shape's volume or the content changes. Every wedge fills half its box, so all twelve share one set.
    /// </summary>
    public ReadOnlySpan<PartFigures> Figures(GameContent content, GridSize size, BlockShape shape = BlockShape.Box)
    {
        var types = content.Blocks.Types;
        var half = shape != BlockShape.Box;
        if (ReferenceEquals(content, _figuresContent) && size == _figuresSize && half == _figuresHalf && _figures.Length == types.Count)
            return _figures;
        if (_figures.Length != types.Count)
            _figures = new PartFigures[types.Count];
        var cells = (long)size.X * size.Y * size.Z;
        for (var i = 0; i < types.Count; i++)
            _figures[i] = PartFigures.Of(types[i], cells, content.Rules, shape);
        _figuresContent = content;
        _figuresSize = size;
        _figuresHalf = half;
        return _figures;
    }

    /// <summary>
    /// One part card's button, at the cursor. A click chooses the part to place with clicks. A press held and
    /// moved past <see cref="DragPixels"/> carries the part out to the ship, the one press the world takes over
    /// from the interface; let go over a panel, it changes nothing. Either way the keyboard goes back to the world
    /// when the press ends (<see cref="HandBack"/>), so the ghost follows the pointer onto the ship at once and the
    /// next click there places. True when the card is under the pointer.
    /// </summary>
    public static bool Card(int part, float2 size, ShipEditor editor)
    {
        var clicked = ImGui.InvisibleButton("##part"u8, size);
        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemDeactivated())
        {
            editor.EndPartPress();
            HandBack(true);
        }

        if (!editor.CanEdit)
            return hovered;
        if (ImGui.IsItemActive() && editor.Carry == EditorCarry.None && ImGui.IsMouseDragging(ImGuiMouseButton.Left, DragPixels))
            editor.BeginPartDrag(part);
        else if (clicked && editor.Carry == EditorCarry.None)
            editor.ChoosePart(part);
        return hovered;
    }

    /// <summary>
    /// After a pointer choice on the part list (a card, a tab, a shape or the select tool), gives the keyboard back
    /// to the world: the click left the panel's window focused, and keyboard navigation in a focused window claims
    /// the keyboard, which keeps the world's input off until a click elsewhere. Nothing when
    /// <paramref name="chosen"/> is false, while a text field or any other item is still active, or when keyboard
    /// navigation made the choice, whose focus stays where it is. Returns <paramref name="chosen"/>.
    /// </summary>
    public static bool HandBack(bool chosen)
    {
        if (chosen && !ImGui.IsAnyItemActive() && !ImGui.GetIO().NavVisible)
            ImGui.Internal.FocusWindow(default);
        return chosen;
    }
}
