using System.Numerics;
using System.Text.Json;
using Prospect.Engine.IO;

namespace Prospect.Client.World;

/// <summary>Client-only distance and fixed particle budgets.</summary>
internal sealed class CollisionDetail
{
    public float Near { get; set; } = 400;
    public float Far { get; set; } = 800;
    public float MinPixels { get; set; } = 8;
    public float FullPixels { get; set; } = 16;
    public int MeshVertices { get; set; } = 40000;
    public int FrameVertices { get; set; } = 2000000;
    public int SparkCapacity { get; set; } = 128;
    public int Streaks { get; set; } = 6;
    public float Seconds { get; set; } = 0.35f;
    public float Speed { get; set; } = 14;
    public float Width { get; set; } = 0.025f;
    public float Length { get; set; } = 0.3f;

    public static CollisionDetail Load()
    {
        var value = JsonSerializer.Deserialize<CollisionDetail>(File.ReadAllText(AppPaths.Content("visuals", "collision.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (!(value.Near > 0 && value.Far > value.Near && value.Far <= 2000 && value.MinPixels > 0 && value.FullPixels > value.MinPixels
            && value.MeshVertices is >= 36 and <= 40000 && value.FrameVertices is >= 40000 and <= 2000000
            && value.SparkCapacity is > 0 and <= 128 && value.Streaks is > 0 and <= 12 && value.Seconds > 0 && value.Seconds <= 0.5f
            && value.Speed > 0 && value.Speed <= 100 && value.Width > 0 && value.Width <= 1 && value.Length > 0 && value.Length <= 5))
            throw new InvalidDataException("Invalid collision visual tuning.");
        return value;
    }

    public float Fade(Vector3 position, float radius, float pixels)
    {
        var surface = Math.Max(0, position.Length() - radius);
        return Math.Clamp((Far - surface) / (Far - Near), 0, 1) * Math.Clamp((pixels - MinPixels) / (FullPixels - MinPixels), 0, 1);
    }
}
