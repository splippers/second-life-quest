using OpenMetaverse;
using SLQuest.Core;
using SLQuest.Network;

namespace SLQuest.World
{
    /// <summary>
    /// Receives land-patch packets from LibreMetaverse and exposes the region
    /// heightmap as a flat float array indexed by [y * 257 + x] (0–256 inclusive).
    /// </summary>
    public sealed class TerrainManager
    {
        public const int PatchCount  = 16;   // patches per axis
        public const int PatchSize   = 16;   // metres (and height samples) per patch
        public const int RegionVerts = PatchCount * PatchSize + 1; // 257 — includes shared edges

        // Full-resolution heightmap: [y * RegionVerts + x], x/y in [0, 256]
        private readonly float[] _heights = new float[RegionVerts * RegionVerts];
        private readonly bool[]  _received = new bool[PatchCount * PatchCount];

        public ReadOnlySpan<float> Heights => _heights;

        public TerrainManager(SLNetworkManager net)
        {
            net.Client.Terrain.LandPatchReceived += OnLandPatch;
            EventBus.Subscribe<SimConnectedEvent>(_ => Reset());
        }

        private void OnLandPatch(object? sender, LandPatchReceivedEventArgs e)
        {
            int px = e.X, py = e.Y;
            if ((uint)px >= PatchCount || (uint)py >= PatchCount) return;

            float[] patch = e.HeightMap; // float[PatchSize * PatchSize]

            // Copy patch heights into the flat array.
            // Terrain row 0 in SL is the south edge (Y=0 in world space).
            for (int row = 0; row < PatchSize; row++)
            {
                for (int col = 0; col < PatchSize; col++)
                {
                    int wx = px * PatchSize + col;
                    int wy = py * PatchSize + row;
                    _heights[wy * RegionVerts + wx] = patch[row * PatchSize + col];
                }
            }

            // Fill the +1 edge columns/rows on the last patches
            if (px == PatchCount - 1)
                for (int row = 0; row < PatchSize; row++)
                {
                    int wy = py * PatchSize + row;
                    _heights[wy * RegionVerts + PatchCount * PatchSize] =
                        _heights[wy * RegionVerts + PatchCount * PatchSize - 1];
                }

            if (py == PatchCount - 1)
                for (int col = 0; col <= (px == PatchCount - 1 ? PatchSize : PatchSize - 1); col++)
                {
                    int wx = px * PatchSize + col;
                    _heights[PatchCount * PatchSize * RegionVerts + wx] =
                        _heights[(PatchCount * PatchSize - 1) * RegionVerts + wx];
                }

            _received[py * PatchCount + px] = true;
            EventBus.Publish(new TerrainPatchEvent(px, py, patch));
        }

        public float GetHeight(int x, int z)
        {
            x = Math.Clamp(x, 0, RegionVerts - 1);
            z = Math.Clamp(z, 0, RegionVerts - 1);
            return _heights[z * RegionVerts + x];
        }

        public bool IsFullyReceived()
        {
            foreach (var b in _received) if (!b) return false;
            return true;
        }

        private void Reset()
        {
            Array.Clear(_heights);
            Array.Clear(_received);
        }
    }
}
