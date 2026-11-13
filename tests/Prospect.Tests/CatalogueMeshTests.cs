using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.Jobs;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class CatalogueMeshTests
{
    [Fact]
    public void ReorderedAndDownloadedDesignsBuildTheirOwnVertices()
    {
        var local = GameContent.Load();
        using var copy = new ContentCopy();
        copy.Replace("ships/command_pod.json", "{ \"at\": [-2, 0, 1], \"type\": \"cargo\" }",
            "{ \"at\": [-2, 0, 1], \"type\": \"cargo\" }, { \"at\": [10, 10, 10], \"type\": \"hull\" }");
        var remote = copy.Load();
        HullDesign[] designs = [local.Designs[1], remote.Pod, local.Pod];
        using var jobs = new JobSystem(1, "Meshes");
        var preparation = new CatalogueMeshes(jobs);
        Assert.Null(preparation.Prepare(designs));
        jobs.CompleteAll();
        var meshes = Assert.IsType<MeshData[]>(preparation.Prepare(designs));
        Assert.Equal(3, meshes.Length);
        for (var i = 0; i < designs.Length; i++)
        {
            var expected = MeshBuilder.Hull(designs[i]);
            Assert.Equal(expected.Indices, meshes[i].Indices);
            Assert.Equal(expected.Vertices, meshes[i].Vertices);
        }
        Assert.Equal(meshes[2].Vertices.Count + 24, meshes[1].Vertices.Count);
        var center = (10.5f - remote.Pod.CenterOfMass.X) * remote.Pod.BlockSize;
        Assert.Contains(meshes[1].Vertices, vertex => vertex.Position.X == center);
        Assert.False(preparation.IsReady(designs));
        preparation.Accept(designs);
        var allocation = new AllocationProbe();
        for (var i = 0; i < 100; i++)
        {
            while (allocation.Measuring())
            {
                preparation.Prepare(designs);
                preparation.IsReady(designs);
            }
        }
        Assert.Equal(0, allocation.Bytes);
        Assert.True(preparation.IsReady(designs));
        Assert.True(WorldView.AsteroidMesh(0, 0) >= 255);
        // The cube and then the twelve wedge prisms close the list, after every rock.
        Assert.Equal(WorldView.CubeMesh(), WorldView.BlockMesh(BlockShape.Box));
        Assert.Equal(WorldView.MeshCount() - 1, WorldView.BlockMesh(BlockShape.WedgeZMinusMinus));
        Assert.True(WorldView.AsteroidMesh(Galaxy.AsteroidShapes - 1, WorldView.AsteroidDetail.Length - 1) < WorldView.CubeMesh());
    }

    [Fact]
    public void AbandonedBuildCannotPublishOverANewCatalogueOrMenu()
    {
        var content = GameContent.Load();
        HullDesign[] next = [content.Designs[1], content.Pod];
        using var jobs = new JobSystem(1, "Meshes");
        var preparation = new CatalogueMeshes(jobs);
        preparation.Prepare(content.Designs);
        preparation.Prepare(null);
        jobs.CompleteAll();
        Assert.Null(preparation.Prepare(null));
        Assert.False(preparation.IsReady(content.Designs));
        preparation.Prepare(content.Designs);
        preparation.Prepare(next);
        jobs.CompleteAll();
        Assert.Null(preparation.Prepare(next));
        jobs.CompleteAll();
        Assert.NotNull(preparation.Prepare(next));
        Assert.Throws<InvalidOperationException>(() => preparation.Accept(content.Designs));
        preparation.Accept(next);
        Assert.True(preparation.IsReady(next));
        Assert.False(preparation.IsReady(content.Designs));
    }

    [Fact]
    public void FailedBuildDoesNotBecomeReadyAndAnotherAdmissionCanRecover()
    {
        using var jobs = new JobSystem(1, "Meshes");
        var preparation = new CatalogueMeshes(jobs);
        HullDesign[] broken = [null!];
        preparation.Prepare(broken);
        jobs.CompleteAll();
        Assert.Null(preparation.Prepare(broken));
        Assert.NotNull(preparation.Error);
        Assert.False(preparation.IsReady(broken));
        Assert.Null(preparation.Prepare(broken));
        Assert.Equal(0, jobs.Pending);
        var valid = GameContent.Load().Designs;
        preparation.Prepare(valid);
        jobs.CompleteAll();
        Assert.NotNull(preparation.Prepare(valid));
        Assert.Null(preparation.Error);
    }
}
