using Brutal.Numerics;
namespace Prospect.Client.Render;

/// <summary>Seven vectors matching GasData in frame.glsl and the ordinary gas pass push range.</summary>
internal struct GasConstants
{
    public float4 Center;
    public float4 HalfSize;
    public float4 Tint;
    public float4 Motion;
    public float4 Budget;
    /// <summary>Fill, lumpiness, warp and lean; see <c>GasCloudModule.Shape</c>.</summary>
    public float4 Shape;
    /// <summary>Edge softness, knot density, wisp strength and veil density; see <c>GasCloudModule.Look</c>.</summary>
    public float4 Look;
}
