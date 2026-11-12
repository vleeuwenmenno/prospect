using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.IO;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The contract between the shield passes and the tone mapper for the pilot's own hyperspace tunnel, checked on the
/// CPU: the order the shells are drawn in, when the copy before the own shield is taken, and a per-pixel model of
/// the two passes and the tone mapper's arithmetic. None of this runs the shaders on a GPU; it pins down what they
/// are written to do.
/// </summary>
public sealed class ShieldCompositeTests
{
    private static readonly float Own = WorldView.Material(true, -1);
    private static readonly float Other = WorldView.Material(false, 2);

    private static FrameConstants Tunnel(float kind, float cover) => new() { Ftl = new float4(kind, 2, 0.5f, 0), FtlUp = new float4(0, 1, 0, cover) };

    [Fact]
    public void TheOwnShieldIsDrawnLastWhateverOrderItWasAddedIn()
    {
        Span<int> order = stackalloc int[4];
        ShieldDraw[] ownFirst = [new() { Own = true }, new(), new()];
        Assert.Equal(2, ShieldComposite.Order(ownFirst, order));
        Assert.Equal([1, 2, 0], order[..3].ToArray());

        ShieldDraw[] ownLast = [new(), new(), new() { Own = true }];
        Assert.Equal(2, ShieldComposite.Order(ownLast, order));
        Assert.Equal([0, 1, 2], order[..3].ToArray());

        Assert.Equal(0, ShieldComposite.Order([], order));
    }

    [Fact]
    public void TheCopyBeforeTheOwnShieldIsTakenOnlyInTheOwnHyperspaceTunnel()
    {
        Assert.False(ShieldComposite.NeedsOwnCopy(Tunnel(1, 0.6f), false));
        Assert.False(ShieldComposite.NeedsOwnCopy(Tunnel(1, 0f), true));
        Assert.True(ShieldComposite.NeedsOwnCopy(Tunnel(1, 0.01f), true));
        Assert.True(ShieldComposite.NeedsOwnCopy(Tunnel(1, 1f), true));

        // A rift or wormhole uses the same field for how far past its opening the camera is: no tunnel there.
        Assert.False(ShieldComposite.NeedsOwnCopy(Tunnel(2, 0.8f), true));
        Assert.False(ShieldComposite.NeedsOwnCopy(Tunnel(3, 0.8f), true));
        Assert.False(ShieldComposite.NeedsOwnCopy(default, true));
    }

    [Fact]
    public void AShieldIsMarkedOwnByItsHullsMaterial()
    {
        var content = GameContent.Load();
        var shields = new ShieldVisuals(ShieldLook.Load());
        var hull = new Prospect.Game.Client.HullView { Id = 1, Design = (byte)content.PodDesign, Blocks = content.Pod.FullMask, Orientation = Quaternion.Identity, Shield = 1500f };
        var other = hull with { Id = 2 };
        shields.Update([hull, other], content.Designs, 1, 1f / 60f);
        var draws = new ShieldDraw[4];
        var instances = new ShieldInstance[4];
        var vertices = new MeshVertex[ShieldVisuals.MaxVertices];
        shields.Begin(draws);
        shields.Add(1, Vector3.Zero, Quaternion.Identity, 1f, Own, instances, vertices);
        shields.Add(2, Vector3.Zero, Quaternion.Identity, 1f, Other, instances, vertices);
        shields.Add(2, Vector3.Zero, Quaternion.Identity, 1f, WorldView.Material(true, 5), instances, vertices);
        Assert.True(draws[0].Own);
        Assert.False(draws[1].Own);
        Assert.True(draws[2].Own);
    }

    /// <summary>One shell's fragment at the modelled pixel: reverse depth (greater is nearer), light and bend.</summary>
    private record struct Fragment(bool Own, float Depth, Vector3 Light, Vector3 Bend);

    /// <summary>
    /// The scene at one pixel after the shields, as the passes are written: light added where a shell passes the
    /// depth test without writing depth, then depth and the mask by maximum; on a tunnel frame the other shields'
    /// two passes, the copy, then the own shield's. The own shield's bend is scaled by what the tunnel leaves.
    /// </summary>
    private static (Vector3 Scene, float Mask, Vector3 OwnBehind) Passes(Vector3 scene, float depth, float mask, Fragment[] shells, float cover)
    {
        var draws = new ShieldDraw[shells.Length];
        for (var i = 0; i < shells.Length; i++)
            draws[i] = new ShieldDraw { Own = shells[i].Own };
        var order = new int[shells.Length];
        var others = ShieldComposite.Order(draws, order);
        var copy = ShieldComposite.NeedsOwnCopy(Tunnel(1, cover), others < shells.Length);
        var ownBehind = new Vector3(float.NaN);

        void Light(ReadOnlySpan<int> which)
        {
            foreach (var i in which)
            {
                if (shells[i].Depth >= depth)
                    scene += shells[i].Light + shells[i].Bend * (shells[i].Own ? 1f - cover : 1f);
            }
        }

        void Mask(ReadOnlySpan<int> which)
        {
            foreach (var i in which)
            {
                if (shells[i].Depth > depth)
                {
                    depth = shells[i].Depth;
                    mask = Math.Max(mask, shells[i].Own ? ShieldComposite.OwnShieldMask : 0.375f);
                }
            }
        }

        if (!copy)
        {
            Light(order);
            Mask(order);
        }
        else
        {
            Light(order.AsSpan(0, others));
            Mask(order.AsSpan(0, others));
            ownBehind = scene;
            Light(order.AsSpan(others));
            Mask(order.AsSpan(others));
        }

        return (scene, mask, ownBehind);
    }

    /// <summary>The tone mapper's tunnel step at one pixel, as tonemap.frag is written.</summary>
    private static Vector3 Tonemap(Vector3 scene, float mask, Vector3 ownBehind, float cover, Vector3 tunnel)
    {
        var ownHull = mask > 0.44f;
        var t = ownHull ? 0f : cover;
        if (t <= 0f)
            return scene;
        var ownShield = mask is > 0.41f and < 0.47f ? scene - ownBehind : Vector3.Zero;
        return Vector3.Lerp(scene - ownShield, tunnel, t) + ownShield;
    }

    private static readonly Vector3 World = new(0.2f, 0.3f, 0.4f);
    private static readonly Vector3 TunnelLight = new(0.05f, 0.05f, 0.5f);
    private static readonly Fragment NearOwn = new(true, 0.9f, new(0.01f, 0.02f, 0.03f), new(0.004f, -0.002f, 0.001f));
    private static readonly Fragment FarOwn = NearOwn with { Depth = 0.5f };
    private static readonly Fragment NearOther = new(false, 0.8f, new(0.07f, 0f, 0f), new(-0.01f, 0.01f, 0f));

    [Theory]
    [InlineData(0f)]
    [InlineData(0.4f)]
    [InlineData(1f)]
    public void TheMaskIsTheNearestShieldsInEitherOrder(float cover)
    {
        foreach (var shells in new[] { new[] { NearOwn, NearOther }, new[] { NearOther, NearOwn } })
            Assert.Equal(ShieldComposite.OwnShieldMask, Passes(World, 0f, 0f, shells, cover).Mask);
        foreach (var shells in new[] { new[] { FarOwn, NearOther }, new[] { NearOther, FarOwn } })
            Assert.Equal(0.375f, Passes(World, 0f, 0f, shells, cover).Mask);

        // Over the pilot's own hull its mark stays, whichever shield is nearest.
        Assert.Equal(0.5f, Passes(World, 0.2f, 0.5f, [NearOther, NearOwn], cover).Mask);
    }

    [Fact]
    public void InTheTunnelOnlyTheOwnShieldsLightIsLeftOverTheTunnel()
    {
        // Full cover: the tunnel and the own shield's light, and nothing of the other shield or the old world.
        var full = Passes(World, 0f, 0f, [NearOwn, NearOther], 1f);
        var shown = Tonemap(full.Scene, full.Mask, full.OwnBehind, 1f, TunnelLight);
        Assert.True(Vector3.Distance(TunnelLight + NearOwn.Light, shown) < 1e-5f, $"{shown}");

        // Part cover: the old world (with the other shield) fades as the tunnel does, and so does the own bend.
        var part = Passes(World, 0f, 0f, [NearOther, NearOwn], 0.4f);
        var expected = Vector3.Lerp(World + NearOther.Light + NearOther.Bend, TunnelLight, 0.4f) + NearOwn.Light + NearOwn.Bend * 0.6f;
        Assert.True(Vector3.Distance(expected, Tonemap(part.Scene, part.Mask, part.OwnBehind, 0.4f, TunnelLight)) < 1e-5f);

        // An own shield behind another hull's: no own band there, the pixel is tunnelled as before.
        var behind = Passes(World, 0f, 0f, [FarOwn, NearOther], 1f);
        Assert.Equal(TunnelLight, Tonemap(behind.Scene, behind.Mask, behind.OwnBehind, 1f, TunnelLight));
    }

    [Fact]
    public void OutsideTheTunnelNothingChanges()
    {
        var none = Passes(World, 0f, 0f, [NearOwn, NearOther], 0f);
        Assert.True(float.IsNaN(none.OwnBehind.X), "no copy is taken");
        Assert.Equal(none.Scene, Tonemap(none.Scene, none.Mask, none.OwnBehind, 0f, TunnelLight));
        Assert.True(Vector3.Distance(World + NearOwn.Light + NearOwn.Bend + NearOther.Light + NearOther.Bend, none.Scene) < 1e-6f);

        // No shield at all, or the own shield offline (not drawn): no copy, the tunnel covers as it always did.
        var bare = Passes(World, 0f, 0f, [NearOther], 1f);
        Assert.True(float.IsNaN(bare.OwnBehind.X));
        Assert.Equal(TunnelLight, Tonemap(bare.Scene, bare.Mask, bare.OwnBehind, 1f, TunnelLight));
    }

    [Fact]
    public void TheShadersAndTheRendererAgreeOnTheOwnShieldBand()
    {
        static string Shader(string name) => File.ReadAllText(AppPaths.Asset(Path.Combine("shaders", name)));
        var tonemap = Shader("tonemap.frag");
        var shield = Shader("shield.frag");
        Assert.Contains("own != 0 ? 0.4375 : 0.375", shield);
        Assert.Equal(0.4375f, ShieldComposite.OwnShieldMask);
        Assert.Contains("layout(set = 1, binding = 5) uniform sampler2D ownBehind;", tonemap);
        Assert.Contains("here.a > 0.41 && here.a < 0.47", tonemap);
        Assert.Contains("float tunnel = ownKind == 1 && !ownHull ? frame.ftlUp.w : 0.0;", tonemap);
        Assert.Contains("if (own != 0 && int(frame.ftl.x + 0.5) == 1)\n        bent *= 1.0 - frame.ftlUp.w;", shield);

        // The band lies between a hull's mark and the own hull's, both of which the tone mapper reads apart.
        Assert.True(0.375f < 0.41f && 0.41f < ShieldComposite.OwnShieldMask && ShieldComposite.OwnShieldMask < 0.44f && 0.47f < 0.5f);
        var renderer = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "Prospect.Client", "Render", "SceneRenderer.cs"));
        Assert.Contains("Binding = 5,", renderer);
        Assert.Contains("DstBinding = 5,", renderer);
    }
}
