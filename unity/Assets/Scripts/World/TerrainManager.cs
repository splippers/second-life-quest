using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Core;
using UnityEngine;

namespace SLQuest.World
{
    /// <summary>
    /// Reconstructs terrain from the 16×16-patch heightmap packets the sim sends.
    /// Each patch is 16×16 metres, giving a 256×256-m region terrain grid.
    /// </summary>
    public sealed class TerrainManager : MonoBehaviour
    {
        private const int PATCHES = SLConstants.TERRAIN_PATCHES_PER_EDGE; // 16
        private const int PATCH_SIZE = SLConstants.TERRAIN_PATCH_SIZE;      // 16

        [SerializeField] private Material terrainMaterial;

        private Terrain     _terrain;
        private TerrainData _terrainData;
        private SLNetworkManager _net;

        // Store all heights so we can rebuild contiguous
        private readonly float[,] _heights = new float[PATCHES * PATCH_SIZE + 1, PATCHES * PATCH_SIZE + 1];

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            BuildTerrain();
        }

        private void Start()
        {
            _net.Client.Terrain.LandPatchReceived += OnLandPatch;
            EventBus.Subscribe<SimConnectedEvent>(OnSimConnected);
        }

        private void OnDestroy()
        {
            if (_net?.Client?.Terrain != null)
                _net.Client.Terrain.LandPatchReceived -= OnLandPatch;
            EventBus.Unsubscribe<SimConnectedEvent>(OnSimConnected);
        }

        private void BuildTerrain()
        {
            _terrainData = new TerrainData
            {
                heightmapResolution = PATCHES * PATCH_SIZE + 1,
                size = new Vector3(
                    SLConstants.REGION_SIZE,
                    SLConstants.REGION_HEIGHT,
                    SLConstants.REGION_SIZE)
            };

            var go = Terrain.CreateTerrainGameObject(_terrainData);
            go.transform.SetParent(transform, false);
            _terrain = go.GetComponent<Terrain>();

            if (terrainMaterial != null)
                _terrain.materialTemplate = terrainMaterial;
        }

        private void OnSimConnected(SimConnectedEvent evt)
        {
            // Request terrain for new sim
            _net.Client.Terrain.RequestTerrainForSim(_net.Client.Network.CurrentSim);
        }

        private void OnLandPatch(object sender, LandPatchReceivedEventArgs e)
        {
            var patch = e.Data;
            int px = e.X;
            int py = e.Y;

            MainThreadDispatcher.Enqueue(() =>
            {
                // Write the 16×16 height values into our master array
                for (int y = 0; y < PATCH_SIZE; y++)
                for (int x = 0; x < PATCH_SIZE; x++)
                {
                    float h = patch[y * PATCH_SIZE + x] / SLConstants.REGION_HEIGHT;
                    _heights[py * PATCH_SIZE + y, px * PATCH_SIZE + x] = h;
                }

                // Unity terrain SetHeightsDelayLOD is cheaper for batch updates
                _terrainData.SetHeightsDelayLOD(
                    px * PATCH_SIZE, py * PATCH_SIZE,
                    PatchSlice(patch));
                _terrain.ApplyDelayedHeightmapModification();

                EventBus.Publish(new TerrainPatchEvent(px, py, patch));
            });
        }

        private static float[,] PatchSlice(float[] raw)
        {
            var arr = new float[PATCH_SIZE, PATCH_SIZE];
            for (int y = 0; y < PATCH_SIZE; y++)
            for (int x = 0; x < PATCH_SIZE; x++)
                arr[y, x] = raw[y * PATCH_SIZE + x] / SLConstants.REGION_HEIGHT;
            return arr;
        }

        /// <summary>Returns the terrain height at a Unity world-space XZ position.</summary>
        public float SampleHeight(float wx, float wz)
        {
            return _terrain != null ? _terrain.SampleHeight(new Vector3(wx, 0, wz)) : 0f;
        }
    }
}
