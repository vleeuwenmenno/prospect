using System.Numerics;
using Brutal.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>Controlled inputs for muted production-shader regression captures, never normal world placement.</summary>
internal static class GasShaderFixture
{
    /// <summary>Four rift cases, two shield cases, two pruning cases, and four wormhole cases.</summary>
    public const int Count = 12;

    /// <summary>
    /// Not a synthetic case: real sources, real density and real overlays with a frozen clock. The first capture has
    /// no opening (ordinary gas pass); after half the capture interval an unrelated opening behind the camera is
    /// submitted (deferred composition). The two images must match at every pixel the opening cannot reach.
    /// </summary>
    public const int OverlayCheck = 13;

    /// <summary>The clock every time-driven part of a frame is held at during <see cref="OverlayCheck"/>.</summary>
    public const float OverlayCheckTime = 100f;

    /// <summary>Freezes the ray basis, clock, cloud and mouths; the production compositor still executes.</summary>
    public static GasConstants Apply(int id, ref FrameConstants frame, ref CompositeConstants composite)
    {
        frame.CameraRight = new float4(1, 0, 0, 0);
        frame.CameraUp = new float4(0, 1, 0, 0);
        frame.CameraForward = new float4(0, 0, -1, 0);
        frame.Dust.W = 0;
        frame.SunDirection = new float4(0, 1, 0, 0);
        frame.SunColor = new float4(1, 1, 1, 0);
        frame.Ftl = frame.FtlAxis = frame.FtlUp = frame.FtlEntry = frame.FtlExit = frame.FtlShape = default;
        frame.Openings = default;
        composite.Setup.Z = composite.Setup.W = 0;
        if (id <= 4 || id >= 9)
        {
            var stage = id % 2 == 1 ? 1 : 2;
            frame.Ftl = new float4(id >= 9 ? 2 : 3, stage, 0.5f, 0.3f);
            frame.FtlAxis = new float4(0, 0, -1, 0);
            frame.FtlUp = new float4(0, 1, 0, 0.5f);
            frame.FtlShape = new float4(0, 1, 1, 0);
            if (stage == 1) frame.FtlEntry = new float4(0, 0, -10, 2);
            else frame.FtlExit = new float4(0, 0, -10, 2);
        }
        if (id == 6)
        {
            // Submitted, but behind the fixed inspection ray; it cannot affect that pixel.
            frame.Openings.Set(0, new Vector3(0, 0, 10), 2, -Vector3.UnitZ, Vector3.UnitY, FtlKind.Rift, 1, 0.3f);
            composite.Setup.W = 1;
        }
        if (id is 7 or 8)
        {
            // Deliberately far first, so the production insertion sort must reverse these events.
            frame.Openings.Set(0, new Vector3(0, 0, -50), 5, -Vector3.UnitZ, Vector3.UnitY, FtlKind.Rift, 1, 0.3f);
            frame.Openings.Set(1, new Vector3(0, 0, -10), 5, -Vector3.UnitZ, Vector3.UnitY, FtlKind.Rift, 1, 0.3f);
            composite.Setup.W = 2;
        }
        var gas = new GasConstants
        {
            Center = new float4(0, 0, -20, 73.74375f),
            HalfSize = new float4(40, 40, 40, id is 3 or 4 or 5 or 6 or 7 or 11 or 12 ? 0.04f : 0),
            Tint = new float4(0.42f, 0.48f, 0.52f, 0),
            Budget = new float4(48, 0, id, 0),
            // Homogeneous fixture density ignores these; they only keep the frame data well formed.
            Shape = new float4(0.56f, 1.8f, 0.1f, 0.24f),
            Look = new float4(0.14f, 2.2f, 1.5f, 0.03f),
        };
        // Keep the fixture tag even if the renderer incorrectly chooses the ordinary path: the shader reports red.
        frame.Gas.Budget.Z = id;
        return gas;
    }
}
