using System.Collections.Generic;
using OpenMetaverse;
using TMPro;
using SLQuest.Avatar;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// VR panel for managing avatar wearables.
    ///
    /// Lists every currently-worn item by layer.  Each row shows the layer name
    /// and item name plus a Remove button.  A Rebake button re-composites the
    /// local texture layers via AppearanceBaker.
    ///
    /// Inspector wiring:
    ///   wearableContent   — Content Transform inside the wearables ScrollRect
    ///   wearableRowPrefab — row with child objects: LayerLabel (TMP_Text),
    ///                        NameLabel (TMP_Text), Remove (Button)
    ///   wearableScroll    — ScrollRect that owns the content
    ///   rebakeButton      — triggers AppearanceBaker.RequestBake()
    ///   refreshButton     — re-reads wearable list from AppearanceManager
    ///   statusLabel       — TMP_Text for status messages
    ///   closeButton       — hides the panel
    /// </summary>
    public sealed class AppearanceEditorPanel : MonoBehaviour
    {
        [Header("Wearables list")]
        [SerializeField] private ScrollRect wearableScroll;
        [SerializeField] private Transform  wearableContent;
        [SerializeField] private GameObject wearableRowPrefab;

        [Header("Controls")]
        [SerializeField] private Button   rebakeButton;
        [SerializeField] private Button   refreshButton;
        [SerializeField] private Button   closeButton;
        [SerializeField] private TMP_Text statusLabel;

        private SLNetworkManager _net;
        private AppearanceBaker  _baker;

        private readonly List<GameObject> _rows = new();

        private static readonly Dictionary<WearableType, string> LayerNames = new()
        {
            [WearableType.Shape]      = "Shape",
            [WearableType.Skin]       = "Skin",
            [WearableType.Hair]       = "Hair",
            [WearableType.Eyes]       = "Eyes",
            [WearableType.Shirt]      = "Shirt",
            [WearableType.Pants]      = "Pants",
            [WearableType.Shoes]      = "Shoes",
            [WearableType.Socks]      = "Socks",
            [WearableType.Jacket]     = "Jacket",
            [WearableType.Gloves]     = "Gloves",
            [WearableType.Undershirt] = "Undershirt",
            [WearableType.Underpants] = "Underpants",
            [WearableType.Skirt]      = "Skirt",
            [WearableType.Alpha]      = "Alpha",
            [WearableType.Tattoo]     = "Tattoo",
            [WearableType.Physics]    = "Physics",
        };

        private void Awake()
        {
            _net   = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            _baker = SLApplication.Instance?.Baker   ?? FindObjectOfType<AppearanceBaker>();

            rebakeButton?.onClick.AddListener(OnRebake);
            refreshButton?.onClick.AddListener(RefreshWearables);
            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));
        }

        private void OnEnable()
        {
            EventBus.Subscribe<LoginSucceededEvent>(OnLogin);
            EventBus.Subscribe<BakeCompleteEvent>(OnBakeComplete);
            RefreshWearables();
        }

        private void OnDisable()
        {
            EventBus.Unsubscribe<LoginSucceededEvent>(OnLogin);
            EventBus.Unsubscribe<BakeCompleteEvent>(OnBakeComplete);
        }

        private void OnLogin(LoginSucceededEvent _) => RefreshWearables();
        private void OnBakeComplete(BakeCompleteEvent _) => SetStatus("Bake complete.");

        // ── Wearable list ─────────────────────────────────────────────────────

        private void RefreshWearables()
        {
            ClearRows();

            var appearances = _net?.Client?.Appearance;
            if (appearances == null) { SetStatus("Not connected."); return; }

            var wearables = appearances.GetWearables();
            if (wearables == null || wearables.Count == 0)
            {
                SetStatus("No wearables loaded yet.");
                return;
            }

            SetStatus(string.Empty);

            // Sort by WearableType enum value for consistent ordering
            var sorted = new List<KeyValuePair<WearableType, WearableData>>(wearables);
            sorted.Sort((a, b) => ((int)a.Key).CompareTo((int)b.Key));

            foreach (var kvp in sorted)
                SpawnRow(kvp.Key, kvp.Value);

            if (wearableScroll != null)
            {
                Canvas.ForceUpdateCanvases();
                wearableScroll.verticalNormalizedPosition = 1f;
            }
        }

        private void SpawnRow(WearableType type, WearableData wd)
        {
            if (wearableRowPrefab == null || wearableContent == null) return;

            var go = Instantiate(wearableRowPrefab, wearableContent);
            _rows.Add(go);

            var layerLabel = go.transform.Find("LayerLabel")?.GetComponent<TMP_Text>();
            var nameLabel  = go.transform.Find("NameLabel")?.GetComponent<TMP_Text>();
            var removeBtn  = go.transform.Find("Remove")?.GetComponent<Button>();

            if (layerLabel != null)
                layerLabel.text = LayerNames.TryGetValue(type, out string n) ? n : type.ToString();

            // Resolve item name from inventory store, fall back to asset name / partial UUID
            string itemName = "(unknown)";
            UUID itemId = wd?.ItemID ?? UUID.Zero;
            if (itemId != UUID.Zero)
            {
                var store = _net.Client.Inventory.Store;
                if (store.Contains(itemId) && store[itemId] is InventoryItem invItem)
                    itemName = invItem.Name;
            }
            if (itemName == "(unknown)" && itemId != UUID.Zero)
                itemName = itemId.ToString()[..8];

            if (nameLabel != null) nameLabel.text = itemName;

            if (removeBtn != null)
            {
                WearableType capturedType = type;
                UUID         capturedId   = itemId;
                removeBtn.onClick.AddListener(() => RemoveWearable(capturedType, capturedId));
            }
        }

        // ── Actions ───────────────────────────────────────────────────────────

        private void RemoveWearable(WearableType type, UUID itemId)
        {
            var appearances = _net?.Client?.Appearance;
            if (appearances == null) return;

            // Body parts (shape/skin/hair/eyes) cannot be fully removed — they reset to defaults
            bool isBodypart = type <= WearableType.Eyes;
            if (isBodypart)
            {
                SetStatus($"{LayerNames.GetValueOrDefault(type, type.ToString())} is a body part and can't be removed.");
                return;
            }

            if (itemId != UUID.Zero)
            {
                appearances.RemoveFromOutfit(new List<UUID> { itemId });
                SetStatus("Removed. Tap Rebake to update.");
                RefreshWearables();
                return;
            }

            SetStatus("Item ID not available.");
        }

        private void OnRebake()
        {
            if (_baker == null) { SetStatus("Baker not available."); return; }
            SetStatus("Baking…");
            _baker.RequestBake();
        }

        private void ClearRows()
        {
            foreach (var r in _rows) Destroy(r);
            _rows.Clear();
        }

        private void SetStatus(string text)
        {
            if (statusLabel == null) return;
            statusLabel.text = text;
            statusLabel.gameObject.SetActive(!string.IsNullOrEmpty(text));
        }
    }
}
