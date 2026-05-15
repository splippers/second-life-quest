using SLQuest.Network;
using SLQuest.World;
using SLQuest.Avatar;
using SLQuest.Assets;
using SLQuest.Chat;
using SLQuest.Inventory;
using SLQuest.Building;
using SLQuest.Voice;
using SLQuest.VR;
using SLQuest.UI;
using SLQuest.Rendering;
using UnityEngine;

namespace SLQuest.Core
{
    /// <summary>
    /// Root application controller. Add to the scene's bootstrap GameObject.
    /// All subsystem managers are children of this object so they share its
    /// DontDestroyOnLoad lifetime.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class SLApplication : MonoBehaviour
    {
        public static SLApplication Instance { get; private set; }

        [Header("Subsystems")]
        [SerializeField] private SLNetworkManager networkManager;
        [SerializeField] private LoginManager loginManager;
        [SerializeField] private RegionManager regionManager;
        [SerializeField] private ObjectManager objectManager;
        [SerializeField] private TerrainManager terrainManager;
        [SerializeField] private AvatarManager avatarManager;
        [SerializeField] private LocalAvatar localAvatar;
        [SerializeField] private AssetManager assetManager;
        [SerializeField] private ChatManager chatManager;
        [SerializeField] private InventoryManager inventoryManager;
        [SerializeField] private BuildingManager buildingManager;
        [SerializeField] private VoiceManager voiceManager;
        [SerializeField] private VRRig vrRig;
        [SerializeField] private VRUIManager uiManager;
        [SerializeField] private MaterialConverter materialConverter;
        [SerializeField] private Network.CapabilityHandler capabilityHandler;

        // Public accessors so subsystems can find each other without circular deps
        public SLNetworkManager Network => networkManager;
        public Network.CapabilityHandler Caps => capabilityHandler;
        public LoginManager Login => loginManager;
        public RegionManager Region => regionManager;
        public ObjectManager Objects => objectManager;
        public TerrainManager Terrain => terrainManager;
        public AvatarManager Avatars => avatarManager;
        public LocalAvatar LocalAvatar => localAvatar;
        public AssetManager Assets => assetManager;
        public ChatManager Chat => chatManager;
        public InventoryManager Inventory => inventoryManager;
        public BuildingManager Building => buildingManager;
        public VoiceManager Voice => voiceManager;
        public VRRig VR => vrRig;
        public VRUIManager UI => uiManager;
        public MaterialConverter Materials => materialConverter;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Ensure dispatcher exists before any subsystem might need it
            _ = MainThreadDispatcher.Instance;

            ValidateSubsystems();
        }

        private void ValidateSubsystems()
        {
            if (networkManager == null) Debug.LogError("[SLApp] SLNetworkManager not assigned");
            if (assetManager == null)   Debug.LogError("[SLApp] AssetManager not assigned");
            if (vrRig == null)          Debug.LogError("[SLApp] VRRig not assigned");
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                EventBus.Clear();
            }
        }
    }
}
