using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The icons on the ship editor's category rail, drawn as thin vector strokes with the draw list: no font and no
/// image. Each icon is one function that draws inside a square of half-size <c>r</c> around a centre, in unit
/// coordinates from -1 to 1 with y pointing down, so one drawing is crisp at any interface scale.
/// </summary>
internal static class EditorIcons
{
    /// <summary>The ids <c>editor.json</c> may name, for the message when one is not known.</summary>
    public const string Names = "grid, cube, bolt, nozzle, tank, shield, jump, radar";

    /// <summary>Reads an icon id from the file; false for one no function draws.</summary>
    public static bool TryParse(string? id, out EditorIcon icon)
    {
        switch (id)
        {
            case "grid": icon = EditorIcon.Grid; return true;
            case "cube": icon = EditorIcon.Cube; return true;
            case "bolt": icon = EditorIcon.Bolt; return true;
            case "nozzle": icon = EditorIcon.Nozzle; return true;
            case "tank": icon = EditorIcon.Tank; return true;
            case "shield": icon = EditorIcon.Shield; return true;
            case "jump": icon = EditorIcon.Jump; return true;
            case "radar": icon = EditorIcon.Radar; return true;
            default: icon = default; return false;
        }
    }

    /// <summary>Draws an icon in a square of half-size <paramref name="r"/> around <paramref name="c"/>.</summary>
    public static void Draw(ImDrawListPtr draw, EditorIcon icon, float2 c, float r, ImColor8 color, float width)
    {
        switch (icon)
        {
            case EditorIcon.Grid: Grid(draw, c, r, color, width); break;
            case EditorIcon.Cube: Cube(draw, c, r, color, width); break;
            case EditorIcon.Bolt: Bolt(draw, c, r, color, width); break;
            case EditorIcon.Nozzle: Nozzle(draw, c, r, color, width); break;
            case EditorIcon.Tank: Tank(draw, c, r, color, width); break;
            case EditorIcon.Shield: Shield(draw, c, r, color, width); break;
            case EditorIcon.Jump: Jump(draw, c, r, color, width); break;
            case EditorIcon.Radar: Radar(draw, c, r, color, width); break;
        }
    }

    private static float2 At(float2 c, float r, float x, float y) => c + new float2(x, y) * r;

    private static void Line(ImDrawListPtr draw, float2 c, float r, float ax, float ay, float bx, float by, ImColor8 color, float width) =>
        draw.AddLine(At(c, r, ax, ay), At(c, r, bx, by), color, width);

    /// <summary>Two squares by two: every part.</summary>
    private static void Grid(ImDrawListPtr draw, float2 c, float r, ImColor8 color, float width)
    {
        for (var row = 0; row < 2; row++)
            for (var column = 0; column < 2; column++)
            {
                var x = column == 0 ? -0.85f : 0.1f;
                var y = row == 0 ? -0.85f : 0.1f;
                draw.AddRect(At(c, r, x, y), At(c, r, x + 0.75f, y + 0.75f), color, 0f, ImDrawFlags.None, width);
            }
    }

    /// <summary>An isometric cube, edges only: the hull.</summary>
    private static void Cube(ImDrawListPtr draw, float2 c, float r, ImColor8 color, float width)
    {
        Span<float2> hexagon =
        [
            At(c, r, 0f, -1f), At(c, r, 0.87f, -0.5f), At(c, r, 0.87f, 0.5f),
            At(c, r, 0f, 1f), At(c, r, -0.87f, 0.5f), At(c, r, -0.87f, -0.5f),
        ];
        draw.AddPolyline(hexagon, color, ImDrawFlags.Closed, width);
        Line(draw, c, r, 0f, 0f, -0.87f, -0.5f, color, width);
        Line(draw, c, r, 0f, 0f, 0.87f, -0.5f, color, width);
        Line(draw, c, r, 0f, 0f, 0f, 1f, color, width);
    }

    /// <summary>A lightning bolt outline: power.</summary>
    private static void Bolt(ImDrawListPtr draw, float2 c, float r, ImColor8 color, float width)
    {
        Span<float2> bolt =
        [
            At(c, r, 0.3f, -1f), At(c, r, -0.65f, 0.15f), At(c, r, -0.05f, 0.15f),
            At(c, r, -0.3f, 1f), At(c, r, 0.65f, -0.2f), At(c, r, 0.05f, -0.2f),
        ];
        draw.AddPolyline(bolt, color, ImDrawFlags.Closed, width);
    }

    /// <summary>An engine's chamber and bell with three lines of exhaust under it: thrust.</summary>
    private static void Nozzle(ImDrawListPtr draw, float2 c, float r, ImColor8 color, float width)
    {
        draw.AddRect(At(c, r, -0.35f, -1f), At(c, r, 0.35f, -0.5f), color, 0f, ImDrawFlags.None, width);
        Span<float2> bell =
        [
            At(c, r, -0.35f, -0.5f), At(c, r, 0.35f, -0.5f), At(c, r, 0.8f, 0.15f), At(c, r, -0.8f, 0.15f),
        ];
        draw.AddPolyline(bell, color, ImDrawFlags.Closed, width);
        Line(draw, c, r, -0.45f, 0.45f, -0.45f, 0.75f, color, width);
        Line(draw, c, r, 0f, 0.45f, 0f, 1f, color, width);
        Line(draw, c, r, 0.45f, 0.45f, 0.45f, 0.75f, color, width);
    }

    /// <summary>An upright capsule with a band round it: a fuel tank.</summary>
    private static void Tank(ImDrawListPtr draw, float2 c, float r, ImColor8 color, float width)
    {
        draw.AddRect(At(c, r, -0.5f, -1f), At(c, r, 0.5f, 1f), color, r * 0.5f, ImDrawFlags.None, width);
        Line(draw, c, r, -0.5f, -0.2f, 0.5f, -0.2f, color, width);
        Line(draw, c, r, -0.5f, 0.3f, 0.5f, 0.3f, color, width);
    }

    /// <summary>A shield crest with a seam down it: the shield.</summary>
    private static void Shield(ImDrawListPtr draw, float2 c, float r, ImColor8 color, float width)
    {
        Span<float2> crest =
        [
            At(c, r, 0f, -1f), At(c, r, 0.85f, -0.65f), At(c, r, 0.85f, 0.1f), At(c, r, 0.55f, 0.55f),
            At(c, r, 0f, 1f), At(c, r, -0.55f, 0.55f), At(c, r, -0.85f, 0.1f), At(c, r, -0.85f, -0.65f),
        ];
        draw.AddPolyline(crest, color, ImDrawFlags.Closed, width);
        Line(draw, c, r, 0f, -0.55f, 0f, 0.55f, color, width);
    }

    /// <summary>A chevron arrow driving through two rings: the jump drive.</summary>
    private static void Jump(ImDrawListPtr draw, float2 c, float r, ImColor8 color, float width)
    {
        var centre = At(c, r, -0.25f, 0f);
        draw.AddCircle(centre, r * 0.75f, color, 20, width);
        draw.AddCircle(centre, r * 0.38f, color, 14, width);
        Line(draw, c, r, -0.25f, 0f, 0.95f, 0f, color, width);
        Span<float2> head = [At(c, r, 0.4f, -0.55f), At(c, r, 0.95f, 0f), At(c, r, 0.4f, 0.55f)];
        draw.AddPolyline(head, color, ImDrawFlags.None, width);
    }

    private static readonly float[] RadarRadii = [0.6f, 1.1f, 1.6f];

    /// <summary>A source with waves spreading from it: the sensors.</summary>
    private static void Radar(ImDrawListPtr draw, float2 c, float r, ImColor8 color, float width)
    {
        var source = At(c, r, -0.65f, 0.65f);
        draw.AddCircleFilled(source, MathF.Max(width * 1.2f, r * 0.14f), color, 8);
        Span<float2> arc = stackalloc float2[10];
        foreach (var radius in RadarRadii)
        {
            for (var i = 0; i < arc.Length; i++)
            {
                // A quarter circle opening to the upper right, drawn from straight up round to straight right.
                var angle = -MathF.PI * 0.5f * (1f - i / (float)(arc.Length - 1));
                arc[i] = source + new float2(MathF.Cos(angle), MathF.Sin(angle)) * (r * radius);
            }

            draw.AddPolyline(arc, color, ImDrawFlags.None, width);
        }
    }
}
