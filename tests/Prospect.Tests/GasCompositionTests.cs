using System.Numerics;
using System.Runtime.InteropServices;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.IO;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Equation examples and source-wiring checks only. These do not execute production gasComposite, its event
/// sorting, source selection or opaque reset, and cannot establish shader correctness. The muted --gas-fixture
/// captures call the real shader functions; they must be run separately after GPU scheduling permits it.
/// </summary>
public sealed class GasCompositionTests
{
    private readonly record struct Volume(Vector3 Light, float Transmission)
    {
        public Vector3 Over(Vector3 behind) => Light + Transmission * behind;
    }

    private static readonly Vector3 Scatter = new(0.4f, 0.5f, 0.6f);
    private static readonly Vector3 Sky = new(0.03f, 0.04f, 0.08f);
    private static readonly Vector3 Portal = new(0.8f, 0.1f, 0.02f);

    private static Volume Segment(float start, float end, float extinction = 0.02f)
    {
        var transmission = MathF.Exp(-extinction * Math.Max(0, end - start));
        return new Volume(Scatter * (1 - transmission), transmission);
    }

    private static void Close(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < 2e-6f, $"expected {expected}, actual {actual}");
    private static string Shader(string name) => File.ReadAllText(AppPaths.Asset(Path.Combine("shaders", name)));

    [Fact]
    public void ForegroundSurvivesFullPortalCoverageAndNeverComesFromTheBentRay()
    {
        var foreground = Segment(0, 20);
        var selectedBackground = Segment(20, 100).Over(Sky);
        var covered = Vector3.Lerp(selectedBackground, Portal, 1);
        var actual = foreground.Over(covered);
        Close(Scatter * (1 - MathF.Exp(-0.4f)) + MathF.Exp(-0.4f) * Portal, actual);
        Assert.NotEqual(Portal, actual); // The former whole-pixel replacement erased this foreground.
        Assert.NotEqual(Segment(0, 20, 0.08f).Over(Portal), actual); // A bent ray sees different foreground.
        var shader = Shader("tonemap.frag");
        Assert.Contains("for(int stage=5;stage>=0;stage--)", shader);
        Assert.Contains("surface=mix(surface,coverLight,event.cover.a)+event.glow;", shader);
        Assert.Contains("bends[0]=vec2(0); masks[0]=0;", shader);
    }

    [Theory]
    [InlineData(60f)] // Scene hit behind the mouth.
    [InlineData(90f)] // Rock fallback, excluding the obscuring hull.
    [InlineData(160f)] // Sky fallback: the cloud's own support ends the ray.
    public void SelectedFallbackGetsItsOwnBackgroundAndForegroundIsCountedOnce(float cutoff)
    {
        var foreground = Segment(0, 20);
        var background = Segment(20, cutoff);
        Close(Segment(0, cutoff).Over(Sky), foreground.Over(background.Over(Sky)));
        Assert.NotEqual(foreground.Over(Sky), foreground.Over(background.Over(Sky)));
        Assert.NotEqual(foreground.Over(Segment(0, cutoff).Over(Sky)), foreground.Over(background.Over(Sky)));
        var shader = Shader("tonemap.frag");
        Assert.Contains("sceneClear ? viewDepth(texture(opaqueDepth,uv).r) : rocksClear ? viewDepth(texture(rocksDepth,uv).r) : Far", shader);
        Assert.Contains("vec3 base=gasReadSource(uv,masks[stage],bends[stage]==vec2(0),cutoff,delta,shieldAt,ownSource);", shader);
        // An unbent pixel reads its own texels exactly, like the ordinary pass; a filtered read blended silhouettes.
        Assert.Contains("cutoff=viewDepth(texelFetch(opaqueDepth,own,0).r);", shader);
        Assert.Contains("vec3 before=texelFetch(beforeShields,own,0).rgb;", shader);
    }

    [Fact]
    public void GasBetweenTwoMouthsIsNeitherDroppedNorGivenTheFartherBend()
    {
        var near = Segment(0, 20, 0.02f);
        var middle = Segment(20, 70, 0.04f); // Ray with the first bend only.
        var far = Segment(70, 120, 0.08f); // Ray with both bends.
        var shown = near.Over(middle.Over(far.Over(Sky)));
        var expectedTransmission = MathF.Exp(-(20 * 0.02f + 50 * 0.04f + 50 * 0.08f));
        Close(Scatter * (1 - expectedTransmission) + Sky * expectedTransmission, shown);
        Assert.NotEqual(near.Over(far.Over(Sky)), shown);
        Assert.NotEqual(near.Over(Segment(20, 120, 0.08f).Over(Sky)), shown);
        var shader = Shader("tonemap.frag");
        Assert.Contains("GasEvent gasEvents[5];", shader);
        Assert.Contains("bends[i+1]=bends[i]+events[i].bend;", shader);
        Assert.Contains("gasLookup(pos,bends[stage],aspect)", shader);
        Assert.Contains("if(stage==count || occludes)", shader);
    }

    [Theory]
    [InlineData(FtlKind.Rift, 1)]
    [InlineData(FtlKind.Wormhole, 1)]
    [InlineData(FtlKind.Rift, 2)]
    [InlineData(FtlKind.Wormhole, 2)]
    public void OwnHullProtectionDoesNotAdmitGasFromTheThroat(FtlKind kind, int stage)
    {
        var aperture = FtlApertureTests.Opening(kind, stage, new Vector3(0, 0, 10), 2, 0, Vector3.UnitZ);
        float RealAt(float depth)
        {
            // At past=0, the aperture window is the far-side fraction. This uses the real shared geometry,
            // including the wormhole's near sphere intersection, rather than substituting a depth plane.
            var far = aperture.WindowBefore(new Vector3(0.1f, 0, depth), aperture.PixelAngle);
            return stage == 1 ? 1 - far : far;
        }
        Assert.Equal(stage == 1 ? 1f : 0f, RealAt(2));
        Assert.Equal(stage == 1 ? 0f : 1f, RealAt(20));
        var opticalDepth = 0.1f * (RealAt(2) + RealAt(20));
        Assert.Equal(0.1f, opticalDepth); // The protected surface would incorrectly admit both samples.
        Assert.NotEqual(MathF.Exp(-0.2f), MathF.Exp(-opticalDepth));
        var shader = Shader("gas.glsl");
        Assert.Contains("ftlOwnReal(ownRay,t*forward)", shader);
        Assert.DoesNotContain("ownHull", shader);
    }

    [Fact]
    public void FractionalOwnPassageVisibilityDoesNotDissolveScatteringTwice()
    {
        var visibleGas = Segment(20, 80, 0.02f * 0.5f);
        var throat = new Vector3(0.7f, 0.2f, 0.1f);
        var surface = Vector3.Lerp(throat, visibleGas.Transmission * Sky, 0.5f);
        var shown = surface + visibleGas.Light;
        Assert.NotEqual(Vector3.Lerp(throat, visibleGas.Over(Sky), 0.5f), shown);
        var shader = Shader("tonemap.frag");
        Assert.Contains("if(!event.own) scattering*=1.0-event.cover.a;", shader);
        Assert.Contains("scattering=volume.rgb;", shader);
    }

    [Fact]
    public void ShieldLightUsesOnlyForegroundTransmissionAndSurvivesTheOwnTunnel()
    {
        var light = new Vector3(0.2f, 0.3f, 0.4f);
        var whole = Segment(0, 100);
        var front = Segment(0, 10);
        var rendered = whole.Over(Sky) + front.Transmission * light;
        Assert.NotEqual(whole.Over(Sky + light), rendered);
        var tunnel = new Vector3(0.1f, 0.05f, 0.2f);
        foreach (var cover in new[] { 0f, 0.4f, 1f })
        {
            var result = Vector3.Lerp(rendered - front.Transmission * light, tunnel, cover) + Vector3.Lerp(front.Transmission * light, light, cover);
            Close(Vector3.Lerp(whole.Over(Sky), tunnel, cover) + Vector3.Lerp(front.Transmission * light, light, cover), result);
        }
        var shader = Shader("tonemap.frag");
        Assert.Contains("hdr - shieldInHdr", shader);
        Assert.Contains("mix(shieldInHdr, ownShield, tunnel)", shader);
        // A shield in front of the opaque depth takes the gas in front of it alone; one at or behind it, the whole interval.
        Assert.Contains("shieldAt>=cutoff ? volume.a : gasSegment(gas,ray,start,shieldAt,pixelAngle).a", shader);
        var shield = Shader("shield.frag");
        Assert.Contains("gasRay(there),at,gasViewDepth", shield);
        Assert.Contains("gasRay(here),at,gasViewDepth", shield);
    }

    [Fact]
    public void AShieldBetweenMouthsKeepsItsLightAtItsOwnDepth()
    {
        var light = new Vector3(0.3f, 0.2f, 0.1f);
        var behind = Segment(70, 120).Over(Sky);
        var middle = Segment(20, 70).Over(behind) + Segment(20, 40).Transmission * light;
        var shown = Segment(0, 20).Over(middle);
        Close(Segment(0, 120).Over(Sky) + Segment(0, 40).Transmission * light, shown);
        Assert.Contains("shieldAt>=start && shieldAt<stop", Shader("tonemap.frag"));
    }

    [Fact]
    public void FrameAndResourceContractHasOneDensityImplementationAndNoGasDepthWrites()
    {
        // The gas block closes the frame: the deferred record, the pair partners, the flashes and the kind and quality vectors,
        // laid out as frame.glsl declares them.
        var gas = Marshal.OffsetOf<FrameConstants>(nameof(FrameConstants.Gas)).ToInt32();
        var record = Marshal.SizeOf<GasConstants>();
        Assert.Equal(gas + record, Marshal.OffsetOf<FrameConstants>(nameof(FrameConstants.GasPartners)).ToInt32());
        Assert.Equal(gas + record * (1 + GasPartners.Capacity), Marshal.OffsetOf<FrameConstants>(nameof(FrameConstants.GasFlashes)).ToInt32());
        var bolts = 16 * (3 * LightningBolts.MaxBolts + 2 * LightningBolts.MaxBolts * LightningBolts.MaxSegments);
        Assert.Equal(gas + record * (1 + GasPartners.Capacity) + 16 * GasFlashes.Capacity + bolts + 16 * 7, Marshal.SizeOf<FrameConstants>());
        Assert.Contains($"GasData gasPartners[{GasPartners.Capacity}];", Shader("frame.glsl"));
        Assert.Contains($"vec4 gasFlashes[{GasFlashes.Capacity}];", Shader("frame.glsl"));
        Assert.Contains($"vec4 gasBoltHeads[{3 * LightningBolts.MaxBolts}];", Shader("frame.glsl"));
        Assert.Contains($"vec4 gasBoltSegments[{2 * LightningBolts.MaxBolts * LightningBolts.MaxSegments}];", Shader("frame.glsl"));
        Assert.EndsWith("} frame;\n", Shader("frame.glsl"));
        Assert.Contains("GasData gas;", Shader("frame.glsl"));
        foreach (var name in new[] { "gas.frag", "gas_half.frag", "gas_scene.frag", "gas_sky.frag", "tonemap.frag", "shield.frag" })
        {
            var shader = Shader(name);
            Assert.Contains("#include \"gas.glsl\"", shader);
            Assert.DoesNotContain("float gasDensity", shader);
            Assert.DoesNotContain("gl_FragDepth", shader);
        }
        var renderer = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "Prospect.Client", "Render", "SceneRenderer.cs"));
        Assert.Contains("lensing || _shieldCount > 0 || sceneGas || deferredGas", renderer);
        // Composition takes the primary cloud only; every other draw, and the primary without composition, is in the scene.
        Assert.Contains("var deferredGas = _gas.HasPrimary && (lensing || _shieldCount > 0);", renderer);
        Assert.Contains("frame.Gas = deferredGas ? _gas.Primary : default;", renderer);
        Assert.Contains("if (sceneGas) DrawGas(commands, frameIndex, skyGas, !deferredGas, halfGas, ref color, ref depth);", renderer);
        // The half-resolution march of the primary is read back by the pass that draws it, ordinary or composed, and
        // never by the fixtures, which must take the exact march.
        Assert.Contains("if (deferredGas && halfGas) MarchHalf(commands, frameIndex, primary);", renderer);
        Assert.Contains("constants.Gas.Budget.Z > 0 || _gas.Primary.Budget.Z > 0", renderer);
        Assert.Contains("DstBinding = 8,", renderer);
        Assert.Contains("DestroyShaderModule(halfVertex, null)", renderer);
        foreach (var name in new[] { "gas.frag", "tonemap.frag" })
            Assert.Contains("gasHalfRead(halfGas,opaqueDepth,", Shader(name));
        Assert.Contains("if (deferredGas && _shieldCount == 0) KeepBehindShields", renderer);
        Assert.Contains("DstBinding = 6,", renderer);
        Assert.Contains("DstBinding = 7,", renderer);
        Assert.Contains("DestroyShaderModule(gasVertex, null)", renderer);
        Assert.Contains("DestroyShaderModule(skyVertex, null)", renderer);
        Assert.Contains("DestroyShaderModule(sceneVertex, null)", renderer);
        // Only a cloud at the full level takes the scene copy; the other levels read the attached depth.
        Assert.Contains("if (!copied) KeepBehindShields(commands, ref color, ref depth);", renderer);
    }
}
