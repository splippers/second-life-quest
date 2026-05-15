using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Avatar;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;

namespace SLQuest.World
{
    /// <summary>
    /// Tracks the current sim and its neighbours.
    /// Converts SL (Y-up, Z-forward) coordinates to Unity (Y-up, Z-forward via
    /// a fixed-offset origin anchored to the current region's south-west corner).
    ///
    /// Also handles seamless region crossing: when the avatar walks across a
    /// sim boundary, LibreMetaverse fires a new SimConnected + a position update
    /// on the new sim.  We recalculate the Unity origin and reposition the avatar
    /// so the transition is seamless from the player's perspective.
    /// </summary>
    public sealed class RegionManager : MonoBehaviour
    {
        public Simulator CurrentSim { get; private set; }
        public string CurrentRegionName => CurrentSim?.Name ?? "—";

        private SLNetworkManager _net;

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

            if (_net?.Client?.Self != null)
                _net.Client.Self.RegionCrossed += OnRegionCrossed;
        }

        private void OnDestroy()
        {
            EventBus.Unsubscribe<SimConnectedEvent>(OnSimConnected);
            EventBus.Unsubscribe<SimDisconnectedEvent>(OnSimDisconnected);
            EventBus.Unsubscribe<TeleportEvent>(OnTeleport);

            if (_net?.Client?.Self != null)
                _net.Client.Self.RegionCrossed -= OnRegionCrossed;
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
            var client = _net?.Client;
            if (client?.Network?.CurrentSim is Simulator newSim && newSim != CurrentSim)
            {
                CurrentSim = newSim;
                RecalcOrigin(newSim);
            }
        }

        private void OnRegionCrossed(object sender, RegionCrossedEventArgs e)
        {
            // e.OldSimulator → e.NewSimulator; avatar is now in new sim
            var newSim = e.NewSimulator;
            if (newSim == null || newSim == CurrentSim) return;

            MainThreadDispatcher.Enqueue(() =>
            {
                CurrentSim = newSim;
                RecalcOrigin(newSim);
                Debug.Log($"[Region] Crossed into {newSim.Name}");

                // Reposition local avatar so Unity coords stay consistent
                var local = SLApplication.Instance?.LocalAvatar;
                var agentPos = _net?.Client?.Self?.SimPosition;
                if (local != null && agentPos.HasValue)
                {
                    local.transform.position = LocalToUnity(agentPos.Value, newSim);
                }

                EventBus.Publish(new SimConnectedEvent(newSim));
            });
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
