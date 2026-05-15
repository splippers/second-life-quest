using System;
using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;

namespace SLQuest.World
{
    public sealed class LandmarkEntry
    {
        public UUID   ItemId  { get; }
        public string Name    { get; }
        public string Region  { get; set; } = string.Empty;
        public Vector3 Position { get; set; }

        public LandmarkEntry(UUID itemId, string name)
        {
            ItemId = itemId;
            Name   = name;
        }
    }

    /// <summary>
    /// Maintains a flat list of landmarks from inventory, supports teleport and
    /// creation of new landmarks at the current position.
    ///
    /// Scans the Landmarks folder on login (and on explicit Refresh).
    /// Teleport uses <c>Client.Self.Teleport(regionHandle, position)</c>
    /// after resolving the landmark asset.
    /// </summary>
    public sealed class LandmarkManager : MonoBehaviour
    {
        public IReadOnlyList<LandmarkEntry> Landmarks => _landmarks;
        public event Action OnListChanged;

        private SLNetworkManager _net;
        private readonly List<LandmarkEntry> _landmarks = new();

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
        }

        private void Start()
        {
            _net.OnLoggedIn += Refresh;
        }

        private void OnDestroy()
        {
            if (_net != null) _net.OnLoggedIn -= Refresh;
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void Refresh()
        {
            _landmarks.Clear();
            OnListChanged?.Invoke();

            var inv = _net?.Client?.Inventory;
            if (inv == null) return;

            UUID lmFolder = inv.FindFolderForType(AssetType.Landmark);
            if (lmFolder == UUID.Zero) return;

            inv.RequestFolderContents(lmFolder, _net.Client.Self.AgentID,
                true, true, InventorySortOrder.ByDate);

            _net.Client.Inventory.FolderUpdated += OnFolderUpdated;
        }

        public void TeleportTo(LandmarkEntry entry)
        {
            _net.Client.Assets.RequestAsset(entry.ItemId, AssetType.Landmark, true,
                (transfer, asset) =>
                {
                    if (asset == null || !asset.Decode()) return;
                    var lm = asset as AssetLandmark;
                    if (lm == null) return;

                    MainThreadDispatcher.Enqueue(() =>
                    {
                        _net.Client.Self.Teleport(lm.RegionHandle, lm.Position);
                    });
                });
        }

        public void CreateLandmark(string name)
        {
            var local = SLApplication.Instance?.LocalAvatar;
            if (local == null) return;

            var sim = _net.Client.Network.CurrentSim;
            Vector3 pos = local.transform.position;
            var omvPos = new OpenMetaverse.Vector3(pos.x, pos.z, pos.y); // Unity→SL

            UUID lmFolder = _net.Client.Inventory.FindFolderForType(AssetType.Landmark);

            _net.Client.Inventory.RequestCreateItem(
                lmFolder, name, string.Empty,
                AssetType.Landmark, UUID.Zero, InventoryType.Landmark,
                PermissionMask.All,
                (success, item) =>
                {
                    if (!success || item == null) return;
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        var entry = new LandmarkEntry(item.UUID, item.Name)
                        {
                            Region   = sim.Name,
                            Position = pos,
                        };
                        _landmarks.Insert(0, entry);
                        OnListChanged?.Invoke();
                        EventBus.Publish(new LandmarkCreatedEvent(item.UUID, item.Name));
                    });
                });
        }

        // ── Inventory callback ────────────────────────────────────────────────

        private void OnFolderUpdated(object sender, FolderUpdatedEventArgs e)
        {
            var inv = _net.Client.Inventory;
            UUID lmFolder = inv.FindFolderForType(AssetType.Landmark);
            if (e.FolderID != lmFolder) return;

            // Unsubscribe — we only needed the first folder update
            inv.FolderUpdated -= OnFolderUpdated;

            var contents = inv.Store.GetContents(inv.Store[lmFolder] as InventoryFolder);
            MainThreadDispatcher.Enqueue(() =>
            {
                _landmarks.Clear();
                foreach (var child in contents)
                {
                    if (child is InventoryItem item && item.AssetType == AssetType.Landmark)
                        _landmarks.Add(new LandmarkEntry(item.UUID, item.Name));
                }
                OnListChanged?.Invoke();
            });
        }
    }
}
