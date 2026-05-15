using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Core;
using SLQuest.Assets;
using UnityEngine;

namespace SLQuest.World
{
    /// <summary>
    /// Mirrors the SL object list into Unity GameObjects.
    /// Subscribes to libopenmetaverse's object-update stream and creates,
    /// updates, or destroys SLPrimitive components as needed.
    /// </summary>
    public sealed class ObjectManager : MonoBehaviour
    {
        [SerializeField] private SLPrimitive primPrefab;

        private SLNetworkManager _net;
        private RegionManager _region;
        private AssetManager _assets;

        private readonly Dictionary<uint, SLPrimitive> _objects = new();

        private void Awake()
        {
            _net    = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            _region = SLApplication.Instance?.Region  ?? FindObjectOfType<RegionManager>();
            _assets = SLApplication.Instance?.Assets  ?? FindObjectOfType<AssetManager>();
        }

        private void Start()
        {
            var client = _net.Client;
            client.Objects.ObjectUpdate        += OnObjectUpdate;
            client.Objects.ObjectDataBlockUpdate += OnDataBlockUpdate;
            client.Objects.TerseObjectUpdate   += OnTerseUpdate;
            client.Objects.KillObject          += OnKillObject;
        }

        private void OnDestroy()
        {
            if (_net?.Client?.Objects is ObjectManager om)
            {
                _net.Client.Objects.ObjectUpdate        -= OnObjectUpdate;
                _net.Client.Objects.ObjectDataBlockUpdate -= OnDataBlockUpdate;
                _net.Client.Objects.TerseObjectUpdate   -= OnTerseUpdate;
                _net.Client.Objects.KillObject          -= OnKillObject;
            }
        }

        // ── libopenmetaverse callbacks (background thread) ────────────────────

        private void OnObjectUpdate(object sender, PrimEventArgs e)
        {
            var prim = e.Prim;
            MainThreadDispatcher.Enqueue(() => UpsertPrim(prim, e.Simulator));
        }

        private void OnDataBlockUpdate(object sender, ObjectDataBlockUpdateEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                if (_objects.TryGetValue(e.Prim.LocalID, out var slp))
                    slp.ApplyDataBlock(e);
            });
        }

        private void OnTerseUpdate(object sender, TerseObjectUpdateEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                if (!_objects.TryGetValue(e.Prim.LocalID, out var slp)) return;

                var pos = _region.LocalToUnity(e.Prim.Position, e.Simulator);
                var rot = _region.SLToUnityRotation(e.Prim.Rotation);
                slp.transform.SetPositionAndRotation(pos, rot);
                slp.ApplyVelocity(e.Prim.Velocity, e.Prim.AngularVelocity);
            });
        }

        private void OnKillObject(object sender, KillObjectEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() => RemovePrim(e.ObjectLocalID));
        }

        // ── Object lifecycle ──────────────────────────────────────────────────

        private void UpsertPrim(Primitive prim, Simulator sim)
        {
            if (!_objects.TryGetValue(prim.LocalID, out var slp))
            {
                var go = primPrefab != null
                    ? Instantiate(primPrefab, transform)
                    : new GameObject($"prim_{prim.LocalID}").AddComponent<SLPrimitive>();

                go.name = string.IsNullOrEmpty(prim.Properties?.Name)
                    ? $"prim_{prim.LocalID}"
                    : prim.Properties.Name;

                _objects[prim.LocalID] = slp = go;
            }

            slp.Initialise(prim, sim, _region, _assets);

            var pos = _region.LocalToUnity(prim.Position, sim);
            var rot = _region.SLToUnityRotation(prim.Rotation);
            var scl = new Vector3(prim.Scale.X, prim.Scale.Z, prim.Scale.Y);
            slp.transform.SetPositionAndRotation(pos, rot);
            slp.transform.localScale = scl;

            EventBus.Publish(new ObjectUpdateEvent(
                prim.LocalID, prim.ID,
                pos, rot, scl));
        }

        private void RemovePrim(uint localId)
        {
            if (!_objects.TryGetValue(localId, out var slp)) return;
            _objects.Remove(localId);
            Destroy(slp.gameObject);
            EventBus.Publish(new ObjectRemovedEvent(localId));
        }

        public bool TryGet(uint localId, out SLPrimitive prim)
            => _objects.TryGetValue(localId, out prim);
    }
}
