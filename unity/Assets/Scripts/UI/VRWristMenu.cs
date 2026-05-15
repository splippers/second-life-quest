using TMPro;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// Quick-access wrist menu attached to the left controller/wrist anchor.
    ///
    /// Shown when the player looks at their left wrist (palm-up gesture) or
    /// presses the menu button.  Contains icon buttons that toggle the main panels.
    ///
    /// Inspector wiring:
    ///   activationAnchor   — Transform on the left wrist (set to left hand anchor)
    ///   menuRoot           — child panel that is toggled visible/hidden
    ///   chatButton         — opens ChatPanel
    ///   inventoryButton    — opens InventoryPanel
    ///   friendsButton      — opens FriendsPanel
    ///   mapButton          — opens MapPanel
    ///   teleportButton     — opens TeleportPanel
    ///   groupsButton       — opens GroupPanel
    ///   settingsButton     — opens SettingsPanel
    ///   marketplaceButton  — opens MarketplacePanel
    ///   landmarksButton    — opens LandmarkPanel
    ///   gesturesButton     — opens GesturePanel
    ///   logoutButton       — logs out
    ///   palmActivation     — if true, show when palm faces HMD camera
    ///   palmAngleThreshold — dot product threshold for palm-up detection (0.6 = ~53°)
    /// </summary>
    public sealed class VRWristMenu : MonoBehaviour
    {
        [Header("Anchors")]
        [SerializeField] private Transform activationAnchor;
        [SerializeField] private GameObject menuRoot;

        [Header("Buttons")]
        [SerializeField] private Button chatButton;
        [SerializeField] private Button imButton;
        [SerializeField] private Button inventoryButton;
        [SerializeField] private Button friendsButton;
        [SerializeField] private Button mapButton;
        [SerializeField] private Button teleportButton;
        [SerializeField] private Button groupsButton;
        [SerializeField] private Button groupChatButton;
        [SerializeField] private Button settingsButton;
        [SerializeField] private Button marketplaceButton;
        [SerializeField] private Button landmarksButton;
        [SerializeField] private Button gesturesButton;
        [SerializeField] private Button searchButton;
        [SerializeField] private Button voiceButton;
        [SerializeField] private Button logoutButton;

        [Header("Palm detection")]
        [SerializeField] private bool  palmActivation     = true;
        [SerializeField] private float palmAngleThreshold = 0.55f;

        private SLNetworkManager _net;
        private bool             _manualOverride;

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();

            Wire(chatButton,        () => VRUIManager.Instance?.ShowChat());
            Wire(imButton,          () => VRUIManager.Instance?.ShowIM());
            Wire(inventoryButton,   () => VRUIManager.Instance?.ShowInventory());
            Wire(friendsButton,     () => VRUIManager.Instance?.ShowFriends());
            Wire(mapButton,         () => VRUIManager.Instance?.ShowMap());
            Wire(teleportButton,    () => VRUIManager.Instance?.ShowTeleport());
            Wire(groupsButton,      () => VRUIManager.Instance?.ShowGroups());
            Wire(groupChatButton,   () => VRUIManager.Instance?.ShowGroupChat());
            Wire(settingsButton,    () => VRUIManager.Instance?.ShowSettings());
            Wire(marketplaceButton, () => VRUIManager.Instance?.ShowMarketplace());
            Wire(landmarksButton,   () => VRUIManager.Instance?.ShowLandmarks());
            Wire(gesturesButton,    () => VRUIManager.Instance?.ShowGestures());
            Wire(searchButton,      () => VRUIManager.Instance?.ShowSearch());
            Wire(voiceButton,       () => VRUIManager.Instance?.ShowVoice());
            Wire(logoutButton,      OnLogout);

            if (menuRoot != null) menuRoot.SetActive(false);
        }

        private void OnEnable()
        {
            EventBus.Subscribe<LoggedOutEvent>(OnLoggedOut);
            EventBus.Subscribe<LoginSucceededEvent>(OnLogin);
        }

        private void OnDisable()
        {
            EventBus.Unsubscribe<LoggedOutEvent>(OnLoggedOut);
            EventBus.Unsubscribe<LoginSucceededEvent>(OnLogin);
        }

        private void Update()
        {
            if (!(_net?.IsInWorld ?? false)) return;

            // Menu button (left controller) toggles manual override
            if (OVRInput.GetDown(OVRInput.Button.Start))
            {
                _manualOverride = !_manualOverride;
                SetMenuVisible(_manualOverride);
                return;
            }

            if (_manualOverride || !palmActivation) return;

            // Palm-up detection: wrist palm normal should face the HMD
            if (activationAnchor != null)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    Vector3 toCamera  = (cam.transform.position - activationAnchor.position).normalized;
                    float   dot       = Vector3.Dot(activationAnchor.up, toCamera);
                    bool    palmUp    = dot > palmAngleThreshold;
                    SetMenuVisible(palmUp);
                }
            }
        }

        private void SetMenuVisible(bool visible)
        {
            if (menuRoot != null) menuRoot.SetActive(visible);

            // Billboard toward HMD
            if (visible)
            {
                var cam = Camera.main;
                if (cam != null && menuRoot != null)
                    menuRoot.transform.rotation = Quaternion.LookRotation(
                        menuRoot.transform.position - cam.transform.position);
            }
        }

        private void OnLogout()
        {
            SetMenuVisible(false);
            _net?.Disconnect();
        }

        private void OnLoggedOut(LoggedOutEvent _) => SetMenuVisible(false);
        private void OnLogin(LoginSucceededEvent _) { /* ready */ }

        private static void Wire(Button btn, System.Action action)
        {
            btn?.onClick.AddListener(() => action?.Invoke());
        }
    }
}
