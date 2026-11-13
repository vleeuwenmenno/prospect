using Brutal.Numerics;
namespace Prospect.Client.Render;

/// <summary>Seven vectors matching GasData in frame.glsl and the gas passes' push range.</summary>
internal struct GasConstants
{
    /// <summary>The cloud's centre relative to the camera, and its seed.</summary>
    public float4 Center;
    /// <summary>Half-extents in metres, and extinction per metre at full density.</summary>
    public float4 HalfSize;
    /// <summary>Scattered light tint; w is 1 when the far level leaves out the finest density octave.</summary>
    public float4 Tint;
    /// <summary>
    /// X is the noise drift; y the weight of this draw, 1 for a whole cloud and less while a level of detail or the
    /// view range fades it; w the distance to the centre, which orders the draws and which no shader reads.
    /// </summary>
    public float4 Motion;
    /// <summary>
    /// X is the view sample count; y the level of detail (<see cref="Full"/>, <see cref="Middle"/> or
    /// <see cref="Far"/>); z a fixture's number, zero in the world.
    /// </summary>
    public float4 Budget;
    /// <summary>Fill, lumpiness, warp and lean; see <c>GasCloudModule.Shape</c>.</summary>
    public float4 Shape;
    /// <summary>Edge softness, knot density, wisp strength and veil density; see <c>GasCloudModule.Look</c>.</summary>
    public float4 Look;

    /// <summary>The full march, as a cloud is drawn with the camera in or near it.</summary>
    public const float Full = 0;
    /// <summary>Fewer steps and fewer light probes over the full density.</summary>
    public const float Middle = 1;
    /// <summary>A handful of steps over a smoothed density, lit as the middle level is; drawn into the sky when nothing nearer is behind it.</summary>
    public const float Far = 2;
}
