using System.Collections.Generic;
using UnityEngine;
using SLQuest.Core;

namespace SLQuest.UI
{
    /// <summary>
    /// Manages all floating VR UI panels. Panels are World-Space Canvases parented
    /// to the rig so they follow the player but can be grabbed and repositioned.
    /// </summary>
    public sealed class VRUIManager : MonoBehaviour
    {
        public static VRUIManager Instance { get; private set; }

        [Header("Panel Prefabs")]
        [SerializeField] private LoginPanel      loginPanelPrefab;
        [SerializeField] private ChatPanel       chatPanelPrefab;
        [SerializeField] private InventoryPanel  inventoryPanelPrefab;
        [SerializeField] private BuildPanel      buildPanelPrefab;
        [SerializeField] private MapPanel        mapPanelPrefab;

        [Header("Layout")]
        [SerializeField] private Transform panelRoot;
        [SerializeField] private float     panelDistance = 1.2f;

        // Live instances
        public LoginPanel     LoginPanel     { get; private set; }
        public ChatPanel      ChatPanel      { get; private set; }
        public InventoryPanel InventoryPanel { get; private set; }
        public BuildPanel     BuildPanel     { get; private set; }
        public MapPanel       MapPanel       { get; private set; }

        private readonly HashSet<MonoBehaviour> _visiblePanels = new();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            // Login panel shown immediately
            ShowLogin();

            EventBus.Subscribe<LoginSucceededEvent>(OnLoginSucceeded);
            EventBus.Subscribe<LoggedOutEvent>(OnLoggedOut);
        }

        private void OnDestroy()
        {
            EventBus.Unsubscribe<LoginSucceededEvent>(OnLoginSucceeded);
            EventBus.Unsubscribe<LoggedOutEvent>(OnLoggedOut);
            Instance = null;
        }

        private void OnLoginSucceeded(LoginSucceededEvent _)
        {
            HidePanel(LoginPanel);
            ShowChat();
        }

        private void OnLoggedOut(LoggedOutEvent _)
        {
            HideAll();
            ShowLogin();
        }

        // ── Panel management ──────────────────────────────────────────────────

        public void ToggleMainMenu()
        {
            if (SLApplication.Instance?.Network?.IsInWorld == true)
            {
                if (_visiblePanels.Count > 0) HideAll();
                else { ShowChat(); ShowInventory(); }
            }
        }

        public void ShowLogin()
        {
            if (LoginPanel == null && loginPanelPrefab != null)
                LoginPanel = SpawnPanel(loginPanelPrefab, Vector3.forward * panelDistance);
            SetVisible(LoginPanel, true);
        }

        public void ShowChat()
        {
            if (ChatPanel == null && chatPanelPrefab != null)
                ChatPanel = SpawnPanel(chatPanelPrefab, new Vector3(-0.5f, 0f, panelDistance));
            SetVisible(ChatPanel, true);
        }

        public void ShowInventory()
        {
            if (InventoryPanel == null && inventoryPanelPrefab != null)
                InventoryPanel = SpawnPanel(inventoryPanelPrefab, new Vector3(0.5f, 0f, panelDistance));
            SetVisible(InventoryPanel, true);
        }

        public void ShowBuildPanel()
        {
            if (BuildPanel == null && buildPanelPrefab != null)
                BuildPanel = SpawnPanel(buildPanelPrefab, new Vector3(0f, -0.3f, panelDistance));
            SetVisible(BuildPanel, true);
        }

        public void ShowMap()
        {
            if (MapPanel == null && mapPanelPrefab != null)
                MapPanel = SpawnPanel(mapPanelPrefab, new Vector3(0.7f, 0.3f, panelDistance));
            SetVisible(MapPanel, true);
        }

        public void HidePanel(MonoBehaviour panel) => SetVisible(panel, false);

        public void HideAll()
        {
            SetVisible(LoginPanel,     false);
            SetVisible(ChatPanel,      false);
            SetVisible(InventoryPanel, false);
            SetVisible(BuildPanel,     false);
            SetVisible(MapPanel,       false);
        }

        private void SetVisible(MonoBehaviour panel, bool visible)
        {
            if (panel == null) return;
            panel.gameObject.SetActive(visible);
            if (visible) _visiblePanels.Add(panel);
            else         _visiblePanels.Remove(panel);
        }

        private T SpawnPanel<T>(T prefab, Vector3 localOffset) where T : MonoBehaviour
        {
            var root = panelRoot != null ? panelRoot : transform;
            var inst = Instantiate(prefab, root);
            inst.transform.localPosition = localOffset;
            inst.transform.localRotation = Quaternion.identity;
            inst.gameObject.SetActive(false);
            return inst;
        }
    }
}
