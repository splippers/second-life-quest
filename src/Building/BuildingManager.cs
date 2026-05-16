using OpenMetaverse;
using SLQuest.Network;

namespace SLQuest.Building
{
    public sealed class BuildingManager
    {
        private readonly SLNetworkManager _net;
        private readonly World.RegionManager _region;

        public BuildingManager(SLNetworkManager net, World.RegionManager region)
        {
            _net    = net;
            _region = region;
        }

        public void CreatePrimAt(Vector3 worldPos)
        {
            var sim = _region.CurrentSim;
            if (sim == null) return;
            // AddPrim expects OpenMetaverse types
            var slPos = MathEx.WorldToSL(worldPos);
            _net.Client.Objects.AddPrim(sim,
                new Primitive.ConstructionData { PCode = PCode.Prim },
                UUID.Zero,
                slPos,
                new OpenMetaverse.Vector3(1f, 1f, 1f),
                OpenMetaverse.Quaternion.Identity);
        }

        public void DeleteObject(uint localId)
        {
            var sim = _region.CurrentSim;
            if (sim != null)
                _net.Client.Objects.DeRezObject(sim, (int)localId,
                    UUID.Zero, UUID.Zero, DeRezDestination.Trash, UUID.Zero);
        }
    }
}
