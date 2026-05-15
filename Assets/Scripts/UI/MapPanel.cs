using TMPro;
using OpenMetaverse;
using SLQuest.Core;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// Mini-map panel displaying the current region's terrain and avatar positions.
    /// Uses a RawImage with a RenderTexture updated periodically.
    /// </summary>
    public sealed class MapPanel : MonoBehaviour
    {
        [SerializeField] private RawImage mapImage;
        [SerializeField] private RawImage dotPrefab;
        [SerializeField] private TMP_Text regionNameLabel;
        [SerializeField] private Button closeButton;
        [SerializeField] private float updateInterval = 2f;

        private SLNetworkManager _net;
        private Texture2D        _mapTexture;
        private float            _timer;

        private const int MAP_SIZE = 256;

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            closeButton.onClick.AddListener(() => VRUIManager.Instance?.HidePanel(this));

            _mapTexture = new Texture2D(MAP_SIZE, MAP_SIZE, TextureFormat.RGB24, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp
            };
            mapImage.texture = _mapTexture;
        }

        private void Start()
        {
            EventBus.Subscribe<SimConnectedEvent>(OnSimConnected);
        }

        private void OnDestroy()
        {
            EventBus.Unsubscribe<SimConnectedEvent>(OnSimConnected);
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer >= updateInterval)
            {
                _timer = 0f;
                RequestMapTile();
            }

            UpdateRegionLabel();
        }

        private void OnSimConnected(SimConnectedEvent evt)
        {
            RequestMapTile();
        }

        private void RequestMapTile()
        {
            var sim = _net?.Client?.Network?.CurrentSim;
            if (sim == null) return;

            // Request the map tile image for the current region
            _net.Client.Grid.RequestMapItems(sim.Handle, GridItemType.AgentLocations, GridLayerType.Terrain);
            // Map image download uses the grid map cap — fetch via MapBlock
            _net.Client.Grid.RequestMapBlocks(GridLayerType.Terrain,
                (ushort)(sim.Handle >> 32), (ushort)(sim.Handle & 0xFFFF),
                (ushort)((sim.Handle >> 32) + 1), (ushort)((sim.Handle & 0xFFFF) + 1), false);
        }

        private void UpdateRegionLabel()
        {
            var rm = SLApplication.Instance?.Region;
            if (rm != null) regionNameLabel.text = rm.CurrentRegionName;
        }
    }
}
