using Brutal.Concurrency.Jobs;
using Prospect.Engine.Jobs;
using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>
/// One bounded worker build at a time; only the currently requested immutable catalogue may be installed. A
/// catalogue that grows at its end during play keeps what is installed in use while the worker prepares the
/// longer one, and a build that fails leaves the installed one as it is.
/// </summary>
internal sealed class CatalogueMeshes(JobSystem jobs)
{
    private HullDesign[]? _requested;
    private HullDesign[]? _installed;
    private MeshData[]? _installedMeshes;
    private BuildJob? _pending;
    private JobHandle _handle;

    /// <summary>Preparation failure for the current request, cleared on a different request.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether GPU installation was acknowledged for this exact catalogue.</summary>
    public bool IsReady(HullDesign[] designs) => ReferenceEquals(_installed, designs);

    /// <summary>
    /// How many of the first designs of <paramref name="designs"/> have their meshes installed: all of them for
    /// the installed catalogue, the installed ones for a catalogue that is the installed one with more designs
    /// after it, and none for any other. Takes no memory.
    /// </summary>
    public int Installed(HullDesign[]? designs)
    {
        if (designs is null || _installed is null || _installed.Length > designs.Length)
            return 0;
        if (ReferenceEquals(_installed, designs))
            return designs.Length;
        for (var i = 0; i < _installed.Length; i++)
        {
            if (!ReferenceEquals(_installed[i], designs[i]))
                return 0;
        }

        return _installed.Length;
    }

    /// <summary>Polls on the render thread. Null abandons admission without waiting for or publishing old work.</summary>
    public MeshData[]? Prepare(HullDesign[]? designs)
    {
        if (!ReferenceEquals(_requested, designs))
        {
            _requested = designs;
            Error = null;
        }
        if (_pending is not null && jobs.IsComplete(_handle))
        {
            var completed = _pending;
            _pending = null;
            if (designs is not null && ReferenceEquals(completed.Designs, designs))
            {
                Error = completed.Error;
                return completed.Result;
            }
        }
        if (designs is null || IsReady(designs) || Error is not null || _pending is not null)
            return null;

        // A catalogue that only grew keeps the meshes it has: the worker builds the new designs alone.
        var kept = Installed(designs);
        _pending = new BuildJob(designs, kept > 0 ? _installedMeshes : null, kept);
        _handle = jobs.Schedule(_pending, 3);
        return null;
    }

    /// <summary>Called only after the render thread has uploaded the current result successfully.</summary>
    public void Accept(HullDesign[] designs, MeshData[]? meshes = null)
    {
        if (!ReferenceEquals(_requested, designs))
            throw new InvalidOperationException("Cannot install an abandoned catalogue.");
        _installed = designs;
        _installedMeshes = meshes;
    }

    private sealed class BuildJob(HullDesign[] designs, MeshData[]? kept, int keptCount) : IJob
    {
        public HullDesign[] Designs => designs;
        public MeshData[]? Result;
        public string? Error;

        public void Run()
        {
            try
            {
                var meshes = new MeshData[designs.Length];
                for (var i = 0; i < meshes.Length; i++)
                {
                    if (kept is not null && i < keptCount)
                    {
                        meshes[i] = kept[i];
                        continue;
                    }

                    // Every design reaches a catalogue admitted, its intact shield shell with it; one that somehow did
                    // not is admitted here, on the worker, so a hull of it is never drawn without a shield to fall back to.
                    if (designs[i].IntactShell.IsEmpty)
                        GeometryAdmission.Validate(designs[i]);
                    meshes[i] = MeshBuilder.Hull(designs[i]);
                }
                Result = meshes;
            }
            catch (Exception exception)
            {
                Prospect.Engine.Diagnostics.Logs.App.Error($"Could not prepare ships: {exception}");
                Error = "Could not prepare ships. Leave this game and try joining again.";
            }
        }
    }
}
