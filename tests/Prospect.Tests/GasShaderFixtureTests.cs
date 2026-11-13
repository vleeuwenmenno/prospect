using Brutal.Numerics;
using Prospect.Client.Render;
using Xunit;

namespace Prospect.Tests;

/// <summary>Exercises production fixture input construction only; it neither opens a window nor runs GLSL.</summary>
public sealed class GasShaderFixtureTests
{
    [Fact]
    public void AllOwnCasesHaveFractionalPastAndBothEntryAndExit()
    {
        foreach (var id in new[] { 1, 2, 3, 4, 9, 10, 11, 12 })
        {
            var frame = new FrameConstants();
            var composite = new CompositeConstants();
            var gas = GasShaderFixture.Apply(id, ref frame, ref composite);
            Assert.Equal(id >= 9 ? 2f : 3f, frame.Ftl.X);
            Assert.Equal(id % 2 == 1 ? 1f : 2f, frame.Ftl.Y);
            Assert.Equal(0.5f, frame.FtlUp.W);
            Assert.Equal(-10f, id % 2 == 1 ? frame.FtlEntry.Z : frame.FtlExit.Z);
            Assert.Equal(id, gas.Budget.Z);
            Assert.Equal(id, frame.Gas.Budget.Z);
            Assert.Equal(0, frame.Dust.W);
        }
    }

    [Fact]
    public void ShieldPairDiffersOnlyInUnrelatedMouthAndFixtureTag()
    {
        var a = new FrameConstants(); var b = new FrameConstants();
        var ca = new CompositeConstants(); var cb = new CompositeConstants();
        var ga = GasShaderFixture.Apply(5, ref a, ref ca);
        var gb = GasShaderFixture.Apply(6, ref b, ref cb);
        Assert.Equal(ga.Center, gb.Center);
        Assert.Equal(ga.HalfSize, gb.HalfSize);
        Assert.Equal(ga.Tint, gb.Tint);
        Assert.Equal(ga.Motion, gb.Motion);
        Assert.Equal(a.Ftl, b.Ftl);
        Assert.Equal(a.CameraForward, b.CameraForward);
        Assert.Equal(a.SunDirection, b.SunDirection);
        Assert.Equal(0, ca.Setup.W);
        Assert.Equal(1, cb.Setup.W);
        Assert.True(b.Openings[0].Z > 0); // Behind the fixed -Z inspection ray.
    }

    [Fact]
    public void PruningInputsAreFarFirstAndCapturePreparationDoesNotAllocate()
    {
        var frame = new FrameConstants { Lens = new float4(0.5f, 16f / 9, 1920, 1080) };
        var composite = new CompositeConstants();
        GasShaderFixture.Apply(7, ref frame, ref composite);
        Assert.Equal(2, composite.Setup.W);
        Assert.Equal(-50, frame.Openings[0].Z);
        Assert.Equal(-10, frame.Openings[4].Z);
        for (var i = 1; i <= GasShaderFixture.Count; i++) GasShaderFixture.Apply(i, ref frame, ref composite);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i <= GasShaderFixture.Count; i++) GasShaderFixture.Apply(i, ref frame, ref composite);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
