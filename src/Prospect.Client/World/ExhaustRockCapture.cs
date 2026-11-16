using System.Numerics;
using Prospect.Client.Render;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>Render-only test hull and rock; no simulation, inventory or saved world is changed.</summary>
internal sealed class ExhaustRockCapture
{
    public readonly MeshVertex[] Triangles;
    public readonly ThrustNozzle[] Nozzles;
    public HullView Hull;
    public Double3 Anchor;
    public double Started = -1;
    public const float Radius = 40;
    public readonly Vector3 RockCenter;

    public ExhaustRockCapture(GameContent content, string kind)
    {
        var id = kind switch { "hot" => "hotThruster", "cold" => "coldGasThruster", "ion" => "ionDrive", _ => "engine" };
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        var nozzles = new List<ThrustNozzle>();
        var type = content.Blocks.Get(id);
        PropulsionModels.Build(PropulsionLook.Shared.Models[id], new PartBox(Vector3.Zero, new Vector3(0.5f), BlockFacing.Back, 1),
            type.Fuel, 0, true, vertices, indices, nozzles);
        Nozzles = nozzles.ToArray();
        Triangles = new MeshVertex[indices.Count];
        for (var i = 0; i < Triangles.Length; i++) Triangles[i] = vertices[(int)indices[i]];
        Hull = new HullView { Id = uint.MaxValue, Blocks = new[] { ulong.MaxValue } };
        var look = PropulsionLook.Shared.PlumeOrder[Nozzles[0].Plume];
        var contact = Nozzles[0].Exit.Z + Nozzles[0].ExitRadius * look.Length * 0.3f;
        RockCenter = new Vector3(0, 0, contact + Radius * AsteroidShape.RadiusAlong(0, -Vector3.UnitZ));
    }

    public void Advance(double time)
    {
        if (Started < 0) Started = time;
        Hull.Thrust = time - Started is >= 1 and < 5 ? new ThrustState { Z = -127, Flags = 23 } : default;
    }
}
