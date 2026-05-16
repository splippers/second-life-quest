using System.Collections.Generic;
using OpenMetaverse;
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
        [SerializeField] private LoginPanel       loginPanelPrefab;
        [SerializeField] private ChatPanel        chatPanelPrefab;
        [SerializeField] private InventoryPanel   inventoryPanelPrefab;
        [SerializeField] private BuildPanel       buildPanelPrefab;
        [SerializeField] private MapPanel         mapPanelPrefab;
        [SerializeField] private FriendsPanel     friendsPanelPrefab;
        [SerializeField] private GroupPanel       groupPanelPrefab;
        [SerializeField] private SettingsPanel    settingsPanelPrefab;
        [SerializeField] private TeleportPanel    teleportPanelPrefab;
        [SerializeField] private LandmarkPanel    landmarkPanelPrefab;
        [SerializeField] private GesturePanel     gesturePanelPrefab;
        [SerializeField] private MarketplacePanel marketplacePanelPrefab;
        [SerializeField] private NotificationToast notificationToastPrefab;
        [SerializeField] private IMPanel          imPanelPrefab;
        [SerializeField] private SearchPanel     searchPanelPrefab;
        [SerializeField] private VoicePanel      voicePanelPrefab;
        [SerializeField] private GroupChatPanel         groupChatPanelPrefab;
        [SerializeField] private AppearanceEditorPanel  appearanceEditorPrefab;
        [SerializeField] private SnapshotPanel          snapshotPanelPrefab;

        [Header("Layout")]
        [SerializeField] private Transform panelRoot;
        [SerializeField] private float     panelDistance = 1.2f;

        // Live instances
        public LoginPanel       LoginPanel       { get; private set; }
        public ChatPanel        ChatPanel        { get; private set; }
        public InventoryPanel   InventoryPanel   { get; private set; }
        public BuildPanel       BuildPanel       { get; private set; }
        public MapPanel         MapPanel         { get; private set; }
        public FriendsPanel     FriendsPanel     { get; private set; }
        public GroupPanel       GroupPanel       { get; private set; }
        public SettingsPanel    SettingsPanel    { get; private set; }
        public TeleportPanel    TeleportPanel    { get; private set; }
        public LandmarkPanel    LandmarkPanel    { get; private set; }
        public GesturePanel     GesturePanel     { get; private set; }
        public MarketplacePanel MarketplacePanel { get; private set; }
        public NotificationToast NotificationToast { get; private set; }
        public IMPanel          IMPanel           { get; private set; }
        public SearchPanel      SearchPanel       { get; private set; }
        public VoicePanel       VoicePanel        { get; private set; }
        public GroupChatPanel        GroupChatPanel       { get; private set; }
        public AppearanceEditorPanel AppearanceEditor     { get; private set; }
        public SnapshotPanel         SnapshotPanel        { get; private set; }

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
            // Ensure notification toast overlay is alive
            if (NotificationToast == null && notificationToastPrefab != null)
                NotificationToast = SpawnPanel(notificationToastPrefab, Vector3.zero);
            if (NotificationToast != null)
                NotificationToast.gameObject.SetActive(true);
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

        public void ShowFriends()
        {
            if (FriendsPanel == null && friendsPanelPrefab != null)
                FriendsPanel = SpawnPanel(friendsPanelPrefab, new Vector3(-0.6f, 0.2f, panelDistance));
            SetVisible(FriendsPanel, true);
        }

        public void ShowGroups()
        {
            if (GroupPanel == null && groupPanelPrefab != null)
                GroupPanel = SpawnPanel(groupPanelPrefab, new Vector3(0.6f, 0.2f, panelDistance));
            SetVisible(GroupPanel, true);
        }

        public void ShowSettings()
        {
            if (SettingsPanel == null && settingsPanelPrefab != null)
                SettingsPanel = SpawnPanel(settingsPanelPrefab, new Vector3(0f, 0.4f, panelDistance));
            SetVisible(SettingsPanel, true);
        }

        public void ShowTeleport()
        {
            if (TeleportPanel == null && teleportPanelPrefab != null)
                TeleportPanel = SpawnPanel(teleportPanelPrefab, new Vector3(0f, 0f, panelDistance));
            SetVisible(TeleportPanel, true);
        }

        public void ShowLandmarks()
        {
            if (LandmarkPanel == null && landmarkPanelPrefab != null)
                LandmarkPanel = SpawnPanel(landmarkPanelPrefab, new Vector3(-0.4f, 0f, panelDistance));
            SetVisible(LandmarkPanel, true);
        }

        public void ShowGestures()
        {
            if (GesturePanel == null && gesturePanelPrefab != null)
                GesturePanel = SpawnPanel(gesturePanelPrefab, new Vector3(0.4f, 0f, panelDistance));
            SetVisible(GesturePanel, true);
        }

        public void ShowMarketplace()
        {
            if (MarketplacePanel == null && marketplacePanelPrefab != null)
                MarketplacePanel = SpawnPanel(marketplacePanelPrefab, new Vector3(0f, -0.2f, panelDistance));
            SetVisible(MarketplacePanel, true);
        }

        public void ShowSearch()
        {
            if (SearchPanel == null && searchPanelPrefab != null)
                SearchPanel = SpawnPanel(searchPanelPrefab, new Vector3(0f, 0.1f, panelDistance));
            SetVisible(SearchPanel, true);
        }

        public void ShowVoice()
        {
            if (VoicePanel == null && voicePanelPrefab != null)
                VoicePanel = SpawnPanel(voicePanelPrefab, new Vector3(0.5f, -0.3f, panelDistance));
            SetVisible(VoicePanel, true);
        }

        public void ShowGroupChat()
        {
            if (GroupChatPanel == null && groupChatPanelPrefab != null)
                GroupChatPanel = SpawnPanel(groupChatPanelPrefab, new Vector3(-0.5f, 0f, panelDistance));
            SetVisible(GroupChatPanel, true);
        }

        public void ShowAppearanceEditor()
        {
            if (AppearanceEditor == null && appearanceEditorPrefab != null)
                AppearanceEditor = SpawnPanel(appearanceEditorPrefab, new Vector3(0.5f, 0.1f, panelDistance));
            SetVisible(AppearanceEditor, true);
        }

        public void ShowSnapshot()
        {
            if (SnapshotPanel == null && snapshotPanelPrefab != null)
                SnapshotPanel = SpawnPanel(snapshotPanelPrefab, new Vector3(0f, 0.2f, panelDistance));
            SetVisible(SnapshotPanel, true);
        }

        public void ShowIM(OpenMetaverse.UUID agentId = default, string displayName = "")
        {
            if (IMPanel == null && imPanelPrefab != null)
                IMPanel = SpawnPanel(imPanelPrefab, new Vector3(-0.3f, 0.1f, panelDistance));
            SetVisible(IMPanel, true);
            if (agentId != OpenMetaverse.UUID.Zero)
                IMPanel?.OpenConversation(agentId, displayName);
        }

        public void TogglePanel(MonoBehaviour panel)
        {
            if (panel == null) return;
            SetVisible(panel, !panel.gameObject.activeSelf);
        }

        public void HidePanel(MonoBehaviour panel) => SetVisible(panel, false);

        public void HideAll()
        {
            SetVisible(LoginPanel,        false);
            SetVisible(ChatPanel,         false);
            SetVisible(InventoryPanel,    false);
            SetVisible(BuildPanel,        false);
            SetVisible(MapPanel,          false);
            SetVisible(FriendsPanel,      false);
            SetVisible(GroupPanel,        false);
            SetVisible(SettingsPanel,     false);
            SetVisible(TeleportPanel,     false);
            SetVisible(LandmarkPanel,     false);
            SetVisible(GesturePanel,      false);
            SetVisible(MarketplacePanel,  false);
            SetVisible(IMPanel,           false);
            SetVisible(SearchPanel,       false);
            SetVisible(VoicePanel,        false);
            SetVisible(GroupChatPanel,    false);
            SetVisible(AppearanceEditor,  false);
            SetVisible(SnapshotPanel,     false);
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
