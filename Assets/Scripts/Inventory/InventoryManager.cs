using System;
using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Core;
using UnityEngine;

namespace SLQuest.Inventory
{
    /// <summary>
    /// Wraps libopenmetaverse's inventory system and exposes a tree of
    /// <see cref="InventoryNode"/> objects to the UI layer.
    /// </summary>
    public sealed class InventoryManager : MonoBehaviour
    {
        private SLNetworkManager _net;
        private OpenMetaverse.InventoryManager _omvInventory;

        public InventoryNode RootFolder  { get; private set; }
        public InventoryNode LibraryRoot { get; private set; }

        public event Action OnInventoryReady;
        public event Action<InventoryNode> OnFolderUpdated;

        private readonly Dictionary<UUID, InventoryNode> _nodes = new();

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
        }

        private void Start()
        {
            _omvInventory = _net.Client.Inventory;
            _omvInventory.FolderUpdated += OnFolderUpdated_OMV;

            _net.OnLoggedIn += OnLoggedIn;
        }

        private void OnDestroy()
        {
            if (_omvInventory != null)
                _omvInventory.FolderUpdated -= OnFolderUpdated_OMV;
            if (_net != null)
                _net.OnLoggedIn -= OnLoggedIn;
        }

        private void OnLoggedIn()
        {
            // Build skeleton from the login reply (Linden provides folder UUIDs at login)
            MainThreadDispatcher.Enqueue(() =>
            {
                RebuildSkeleton();
            });
        }

        private void RebuildSkeleton()
        {
            _nodes.Clear();

            // InventoryManager.Store is the in-memory tree
            var store = _omvInventory.Store;
            RootFolder  = WrapNode(store.RootFolder);
            LibraryRoot = WrapNode(store.LibraryFolder);

            OnInventoryReady?.Invoke();
        }

        /// <summary>Fetch the contents of a folder from the server.</summary>
        public void RequestContents(InventoryNode folder, bool recursive = false)
        {
            if (folder == null || folder.IsItem) return;
            _omvInventory.RequestFolderContents(
                folder.Id, _net.Client.Self.AgentID, true, true,
                InventorySortOrder.ByDate);
        }

        private void OnFolderUpdated_OMV(object sender, FolderUpdatedEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                var store = _omvInventory.Store;
                if (!store.Contains(e.FolderID)) return;

                var node = WrapNode(store[e.FolderID]);
                OnFolderUpdated?.Invoke(node);
            });
        }

        private InventoryNode WrapNode(InventoryBase inv)
        {
            if (inv == null) return null;
            if (_nodes.TryGetValue(inv.UUID, out var existing))
                return existing;

            var node = new InventoryNode(inv);
            _nodes[inv.UUID] = node;

            if (inv is InventoryFolder folder)
            {
                var store = _omvInventory.Store;
                foreach (var child in store.GetContents(folder))
                    node.AddChild(WrapNode(child));
            }

            return node;
        }

        // ── Actions ───────────────────────────────────────────────────────────

        public void WearItem(UUID itemId)
        {
            _net.Client.Appearance.AddToOutfit(new List<UUID> { itemId }, false);
        }

        public void TakeOffItem(UUID itemId)
        {
            _net.Client.Appearance.RemoveFromOutfit(new List<UUID> { itemId });
        }

        public void RezObject(UUID itemId, OpenMetaverse.Vector3 atPosition)
        {
            _net.Client.Inventory.RequestRezFromInventory(
                _net.Client.Network.CurrentSim,
                OpenMetaverse.Quaternion.Identity,
                atPosition,
                _omvInventory.Store[itemId] as InventoryItem);
        }
    }

    // ── Domain types ──────────────────────────────────────────────────────────

    public sealed class InventoryNode
    {
        public UUID         Id       { get; }
        public string       Name     { get; }
        public bool         IsItem   { get; }
        public AssetType    AssetType { get; }
        public InventoryType InvType  { get; }

        public IReadOnlyList<InventoryNode> Children => _children;
        private readonly List<InventoryNode> _children = new();

        public InventoryNode(InventoryBase inv)
        {
            Id    = inv.UUID;
            Name  = inv.Name;
            IsItem = inv is InventoryItem;

            if (inv is InventoryItem item)
            {
                AssetType = item.AssetType;
                InvType   = item.InventoryType;
            }
        }

        public void AddChild(InventoryNode child)
        {
            if (child != null) _children.Add(child);
        }
    }
}
