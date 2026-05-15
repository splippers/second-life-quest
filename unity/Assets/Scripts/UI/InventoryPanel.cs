using System.Collections.Generic;
using TMPro;
using SLQuest.Inventory;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// Scrollable inventory tree. Folders expand/collapse on click;
    /// items show a context menu (Wear, Rez, Open, etc.).
    /// </summary>
    public sealed class InventoryPanel : MonoBehaviour
    {
        [SerializeField] private ScrollRect  scrollRect;
        [SerializeField] private Transform   contentRoot;
        [SerializeField] private InventoryRow rowPrefab;
        [SerializeField] private TMP_InputField searchField;
        [SerializeField] private Button closeButton;

        private InventoryManager _inventory;
        private readonly Dictionary<InventoryNode, InventoryRow> _rows = new();
        private InventoryNode _root;

        private void Awake()
        {
            _inventory = SLApplication.Instance?.Inventory ?? FindObjectOfType<InventoryManager>();
            searchField.onValueChanged.AddListener(OnSearch);
            closeButton.onClick.AddListener(() => VRUIManager.Instance?.HidePanel(this));
        }

        private void Start()
        {
            if (_inventory == null) return;

            if (_inventory.RootFolder != null) PopulateFrom(_inventory.RootFolder);
            else _inventory.OnInventoryReady += () => PopulateFrom(_inventory.RootFolder);

            _inventory.OnFolderUpdated += OnFolderUpdated;
        }

        private void OnDestroy()
        {
            if (_inventory != null)
                _inventory.OnFolderUpdated -= OnFolderUpdated;
        }

        private void PopulateFrom(InventoryNode root)
        {
            _root = root;
            ClearRows();
            BuildRows(root, 0);
        }

        private void BuildRows(InventoryNode node, int depth)
        {
            if (node == null) return;
            var row = Instantiate(rowPrefab, contentRoot);
            row.Bind(node, depth, OnRowClicked);
            _rows[node] = row;

            if (!node.IsItem)
                foreach (var child in node.Children)
                    BuildRows(child, depth + 1);
        }

        private void OnRowClicked(InventoryNode node)
        {
            if (node.IsItem)
            {
                ShowContextMenu(node);
            }
            else
            {
                // Toggle collapse
                if (_rows.TryGetValue(node, out var row))
                    row.ToggleExpand();

                // Fetch contents if folder is empty
                if (node.Children.Count == 0)
                    _inventory.RequestContents(node);
            }
        }

        private void OnFolderUpdated(InventoryNode folder)
        {
            // Refresh the row for this folder and its children
            if (_rows.TryGetValue(folder, out var row) && row.IsExpanded)
            {
                // Remove old children rows, rebuild
                foreach (var child in folder.Children)
                {
                    if (_rows.TryGetValue(child, out var childRow))
                        Destroy(childRow.gameObject);
                    _rows.Remove(child);
                }
                // Re-insert after parent
                foreach (var child in folder.Children)
                    BuildRows(child, row.Depth + 1);
            }
        }

        private void ShowContextMenu(InventoryNode item)
        {
            // TODO: spawn a context menu popup near the item row
            // For now, default action by asset type
            switch (item.AssetType)
            {
                case OpenMetaverse.AssetType.Clothing:
                case OpenMetaverse.AssetType.Bodypart:
                    _inventory.WearItem(item.Id);
                    break;
                case OpenMetaverse.AssetType.Object:
                    var av = SLApplication.Instance?.LocalAvatar;
                    if (av != null)
                        _inventory.RezObject(item.Id,
                            new OpenMetaverse.Vector3(
                                av.transform.position.x + 2f,
                                av.transform.position.z,
                                av.transform.position.y));
                    break;
            }
        }

        private void OnSearch(string query)
        {
            if (_root == null) return;
            query = query.Trim().ToLowerInvariant();

            foreach (var kvp in _rows)
                kvp.Value.gameObject.SetActive(
                    string.IsNullOrEmpty(query) ||
                    kvp.Key.Name.ToLowerInvariant().Contains(query));
        }

        private void ClearRows()
        {
            foreach (var kvp in _rows) Destroy(kvp.Value.gameObject);
            _rows.Clear();
        }
    }

    // ── Row component ─────────────────────────────────────────────────────────

    public sealed class InventoryRow : MonoBehaviour
    {
        [SerializeField] private TMP_Text    label;
        [SerializeField] private Button      button;
        [SerializeField] private Image       icon;
        [SerializeField] private LayoutElement layout;

        public int           Depth      { get; private set; }
        public bool          IsExpanded { get; private set; } = true;
        private InventoryNode _node;

        public void Bind(InventoryNode node, int depth, System.Action<InventoryNode> onClick)
        {
            _node  = node;
            Depth  = depth;
            label.text = (node.IsItem ? "  " : (IsExpanded ? "▼ " : "▶ ")) + node.Name;

            layout.minWidth = depth * 16f;
            button.onClick.AddListener(() => onClick(node));
        }

        public void ToggleExpand()
        {
            IsExpanded = !IsExpanded;
            label.text = (_node.IsItem ? "  " : (IsExpanded ? "▼ " : "▶ ")) + _node.Name;
        }
    }
}
