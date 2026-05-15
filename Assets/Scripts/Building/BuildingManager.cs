using System;
using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Core;
using SLQuest.World;
using UnityEngine;

namespace SLQuest.Building
{
    public enum BuildTool { Select, Move, Rotate, Scale, Create }

    /// <summary>
    /// Handles in-world building: prim creation, selection, transformation,
    /// linking/unlinking, and property editing.
    /// All grid mutations go through libopenmetaverse's ObjectManager.
    /// </summary>
    public sealed class BuildingManager : MonoBehaviour
    {
        public BuildTool CurrentTool { get; private set; } = BuildTool.Select;
        public SLPrimitive SelectedPrim { get; private set; }
        public IReadOnlyList<SLPrimitive> Selection => _selection;

        private readonly List<SLPrimitive> _selection = new();
        private SLNetworkManager _net;
        private RegionManager    _region;
        private ObjectManager    _objects;

        public event Action<SLPrimitive>      OnSelectionChanged;
        public event Action<BuildTool>        OnToolChanged;
        public event Action<Primitive.ObjectProperties> OnPropertiesUpdated;

        private void Awake()
        {
            _net     = SLApplication.Instance?.Network  ?? FindObjectOfType<SLNetworkManager>();
            _region  = SLApplication.Instance?.Region   ?? FindObjectOfType<RegionManager>();
            _objects = SLApplication.Instance?.Objects  ?? FindObjectOfType<ObjectManager>();
        }

        private void Start()
        {
            _net.Client.Objects.ObjectProperties += OnObjectProperties;
        }

        private void OnDestroy()
        {
            if (_net?.Client?.Objects != null)
                _net.Client.Objects.ObjectProperties -= OnObjectProperties;
        }

        // ── Tool selection ────────────────────────────────────────────────────

        public void SetTool(BuildTool tool)
        {
            CurrentTool = tool;
            OnToolChanged?.Invoke(tool);
        }

        // ── Selection ─────────────────────────────────────────────────────────

        public void Select(SLPrimitive prim, bool addToSelection = false)
        {
            if (!addToSelection) _selection.Clear();
            if (prim != null && !_selection.Contains(prim))
                _selection.Add(prim);

            SelectedPrim = prim;
            OnSelectionChanged?.Invoke(prim);

            if (prim != null)
                RequestProperties(prim.LocalId);
        }

        public void Deselect()
        {
            _selection.Clear();
            SelectedPrim = null;
            OnSelectionChanged?.Invoke(null);
        }

        // ── Prim creation ─────────────────────────────────────────────────────

        /// <summary>Rez a new box prim at the given Unity world position.</summary>
        public void RezBox(Vector3 position)
        {
            var slPos = ToSLPos(position);
            _net.Client.Objects.AddPrim(
                _net.Client.Network.CurrentSim,
                new Primitive.ConstructionData
                {
                    PCode       = PCode.Prim,
                    Material    = Material.Wood,
                    State       = 0,
                    TypeData    = new Primitive.ConstructionData.TypeDataBox()
                },
                UUID.Zero,
                slPos,
                new OpenMetaverse.Vector3(0.5f, 0.5f, 0.5f),
                OpenMetaverse.Quaternion.Identity);
        }

        // ── Transformation ────────────────────────────────────────────────────

        /// <summary>Move the selected prim to a Unity world-space position.</summary>
        public void MoveSelected(Vector3 worldPos)
        {
            if (SelectedPrim == null) return;
            var slPos = ToSLPos(worldPos);
            _net.Client.Objects.SetPosition(
                _net.Client.Network.CurrentSim,
                SelectedPrim.LocalId, slPos);
        }

        /// <summary>Rotate the selected prim.</summary>
        public void RotateSelected(Quaternion rotation)
        {
            if (SelectedPrim == null) return;
            var slRot = ToSLRot(rotation);
            _net.Client.Objects.SetRotation(
                _net.Client.Network.CurrentSim,
                SelectedPrim.LocalId, slRot);
        }

        /// <summary>Scale the selected prim.</summary>
        public void ScaleSelected(Vector3 scale)
        {
            if (SelectedPrim == null) return;
            scale = Vector3.Max(scale, Vector3.one * SLConstants.MIN_PRIM_SIZE);
            scale = Vector3.Min(scale, Vector3.one * SLConstants.MAX_PRIM_SIZE);
            _net.Client.Objects.SetScale(
                _net.Client.Network.CurrentSim,
                SelectedPrim.LocalId,
                new OpenMetaverse.Vector3(scale.x, scale.z, scale.y));
        }

        // ── Link sets ─────────────────────────────────────────────────────────

        public void LinkSelection()
        {
            if (_selection.Count < 2) return;
            var ids = new List<uint>();
            foreach (var p in _selection) ids.Add(p.LocalId);
            _net.Client.Objects.LinkPrims(_net.Client.Network.CurrentSim, ids);
        }

        public void UnlinkSelected()
        {
            if (SelectedPrim == null) return;
            _net.Client.Objects.DelinkPrims(
                _net.Client.Network.CurrentSim,
                new List<uint> { SelectedPrim.LocalId });
        }

        // ── Properties ────────────────────────────────────────────────────────

        private void RequestProperties(uint localId)
        {
            _net.Client.Objects.SelectObject(
                _net.Client.Network.CurrentSim, localId);
        }

        private void OnObjectProperties(object sender, ObjectPropertiesEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
                OnPropertiesUpdated?.Invoke(e.Properties));
        }

        public void SetName(string name)
        {
            if (SelectedPrim == null) return;
            _net.Client.Objects.SetName(
                _net.Client.Network.CurrentSim,
                SelectedPrim.LocalId, name);
        }

        public void SetDescription(string desc)
        {
            if (SelectedPrim == null) return;
            _net.Client.Objects.SetDescription(
                _net.Client.Network.CurrentSim,
                SelectedPrim.LocalId, desc);
        }

        public void DeleteSelected()
        {
            if (SelectedPrim == null) return;
            _net.Client.Objects.RequestObjectOwner(
                _net.Client.Network.CurrentSim,
                new List<uint> { SelectedPrim.LocalId });
            _net.Client.Objects.RequestObjectDerez(
                _net.Client.Network.CurrentSim,
                new List<uint> { SelectedPrim.LocalId },
                DeRezDestination.AgentInventoryTake,
                UUID.Zero, UUID.Random());
        }

        // ── Coordinate conversion ─────────────────────────────────────────────

        private static OpenMetaverse.Vector3 ToSLPos(Vector3 u)
            => new(u.x, u.z, u.y);

        private static OpenMetaverse.Quaternion ToSLRot(Quaternion u)
            => new(u.x, u.z, u.y, u.w);
    }
}
