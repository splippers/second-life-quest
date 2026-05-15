using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Core;
using UnityEngine;

namespace SLQuest.World
{
    /// <summary>
    /// Tracks the current sim and its neighbours.
    /// Converts SL (Y-up, Z-forward) coordinates to Unity (Y-up, Z-forward via
    /// a fixed-offset origin anchored to the current region's south-west corner).
    /// </summary>
    public sealed class RegionManager : MonoBehaviour
    {
        public Simulator CurrentSim { get; private set; }
        public string CurrentRegionName => CurrentSim?.Name ?? "—";

        // World-space offset so region 0,0 maps to Unity 0,0,0
        private Vector3 _originOffset = Vector3.zero;

        private readonly Dictionary<ulong, Simulator> _knownSims = new();

        private SLNetworkManager _net;

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
        }

        private void Start()
        {
            EventBus.Subscribe<SimConnectedEvent>(OnSimConnected);
            EventBus.Subscribe<SimDisconnectedEvent>(OnSimDisconnected);
            EventBus.Subscribe<TeleportEvent>(OnTeleport);
        }

        private void OnDestroy()
        {
            EventBus.Unsubscribe<SimConnectedEvent>(OnSimConnected);
            EventBus.Unsubscribe<SimDisconnectedEvent>(OnSimDisconnected);
            EventBus.Unsubscribe<TeleportEvent>(OnTeleport);
        }

        private void OnSimConnected(SimConnectedEvent evt)
        {
            var sim = evt.Simulator as Simulator;
            if (sim == null) return;

            _knownSims[sim.Handle] = sim;

            if (CurrentSim == null)
            {
                CurrentSim = sim;
                RecalcOrigin(sim);
                Debug.Log($"[Region] Primary sim: {sim.Name}  handle={sim.Handle}");
            }
        }

        private void OnSimDisconnected(SimDisconnectedEvent evt)
        {
            var sim = evt.Simulator as Simulator;
            if (sim == null) return;
            _knownSims.Remove(sim.Handle);
        }

        private void OnTeleport(TeleportEvent evt)
        {
            // After teleport the agent's current sim may have changed
            var client = _net?.Client;
            if (client?.Network?.CurrentSim is Simulator newSim && newSim != CurrentSim)
            {
                CurrentSim = newSim;
                RecalcOrigin(newSim);
            }
        }

        private void RecalcOrigin(Simulator sim)
        {
            // SL global coords: sim handle encodes globalX, globalY
            ulong globalX = (sim.Handle >> 32) & 0xFFFFFFFF;
            ulong globalY = sim.Handle & 0xFFFFFFFF;
            _originOffset = new Vector3(
                -(globalX * SLConstants.REGION_SIZE),
                0f,
                -(globalY * SLConstants.REGION_SIZE));
        }

        // ── Coordinate helpers ────────────────────────────────────────────────

        /// <summary>Converts a Second Life world-space position to Unity world-space.</summary>
        public Vector3 SLToUnity(Vector3d slGlobal)
        {
            return new Vector3(
                (float)slGlobal.X + _originOffset.x,
                (float)slGlobal.Z,          // SL Z → Unity Y
                (float)slGlobal.Y + _originOffset.z);
        }

        /// <summary>Converts a per-region local position (0–255) to Unity world-space.</summary>
        public Vector3 LocalToUnity(OpenMetaverse.Vector3 local, Simulator sim = null)
        {
            sim ??= CurrentSim;
            if (sim == null) return new Vector3(local.X, local.Z, local.Y);

            ulong gx = (sim.Handle >> 32) & 0xFFFFFFFF;
            ulong gy = sim.Handle & 0xFFFFFFFF;

            return new Vector3(
                gx * SLConstants.REGION_SIZE + local.X + _originOffset.x,
                local.Z,
                gy * SLConstants.REGION_SIZE + local.Y + _originOffset.z);
        }

        public Quaternion SLToUnityRotation(OpenMetaverse.Quaternion q)
            => new Quaternion(q.X, q.Z, q.Y, q.W);  // swap Y/Z for handedness

        public bool TryGetSim(ulong handle, out Simulator sim)
            => _knownSims.TryGetValue(handle, out sim);
    }
}
