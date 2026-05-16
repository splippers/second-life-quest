using OpenMetaverse;
using TMPro;
using SLQuest.Core;
using SLQuest.Network;
using SLQuest.World;
using UnityEngine;

namespace SLQuest.UI
{
    /// <summary>
    /// Compact always-visible HUD panel (attach to VR wrist or fixed camera offset).
    ///
    /// Shows: parcel name, region name, avatar coordinates, L$ balance,
    /// and a no-fly icon when flight is restricted.
    ///
    /// Inspector wiring:
    ///   parcelLabel   — TMP_Text  "Infohub Central"
    ///   regionLabel   — TMP_Text  "Ahern (128, 128)"
    ///   coordLabel    — TMP_Text  "(128, 25, 128)"
    ///   balanceLabel  — TMP_Text  "L$1,234"
    ///   noFlyIcon     — GameObject shown when AllowFly is false
    /// </summary>
    public sealed class ParcelHUD : MonoBehaviour
    {
        [SerializeField] private TMP_Text  parcelLabel;
        [SerializeField] private TMP_Text  regionLabel;
        [SerializeField] private TMP_Text  coordLabel;
        [SerializeField] private TMP_Text  balanceLabel;
        [SerializeField] private TMP_Text  landImpactLabel;
        [SerializeField] private GameObject noFlyIcon;
        [SerializeField] private GameObject noBuildIcon;
        [SerializeField] private GameObject noScriptsIcon;

        [Header("Position update")]
        [SerializeField] private float coordUpdateSec = 0.5f;

        private SLNetworkManager _net;
        private ParcelManager    _parcels;
        private float            _coordTimer;

        private void Awake()
        {
            _net    = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            _parcels = FindObjectOfType<ParcelManager>();
        }

        private void OnEnable()
        {
            EventBus.Subscribe<ParcelChangedEvent>(OnParcelChanged);
            EventBus.Subscribe<BalanceUpdatedEvent>(OnBalanceUpdated);
            EventBus.Subscribe<SimConnectedEvent>(OnSimConnected);

            // Request current balance on show
            _net?.Client?.Self?.RequestBalance();
        }

        private void OnDisable()
        {
            EventBus.Unsubscribe<ParcelChangedEvent>(OnParcelChanged);
            EventBus.Unsubscribe<BalanceUpdatedEvent>(OnBalanceUpdated);
            EventBus.Unsubscribe<SimConnectedEvent>(OnSimConnected);
        }

        private void Update()
        {
            _coordTimer += Time.deltaTime;
            if (_coordTimer < coordUpdateSec) return;
            _coordTimer = 0f;
            RefreshCoords();
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private void OnParcelChanged(ParcelChangedEvent e)
        {
            var p = e.Parcel;
            if (parcelLabel != null)
                parcelLabel.text = string.IsNullOrEmpty(p.Name) ? "(unnamed parcel)" : p.Name;

            // Parcel capacity: SL reports TotalPrims and MaxPrims for the parcel boundary.
            // SimWide variants cover the whole region. Show parcel-level impact.
            if (landImpactLabel != null)
            {
                int used = p.TotalPrims;
                int cap  = p.MaxPrims;
                landImpactLabel.text = cap > 0 ? $"LI {used}/{cap}" : $"LI {used}";
            }

            bool canFly   = (p.Flags & ParcelFlags.AllowFly)      != 0;
            bool canBuild = (p.Flags & ParcelFlags.CreateObjects)  != 0;
            bool canScript= (p.Flags & ParcelFlags.AllowOtherScripts) != 0;

            if (noFlyIcon    != null) noFlyIcon.SetActive(!canFly);
            if (noBuildIcon  != null) noBuildIcon.SetActive(!canBuild);
            if (noScriptsIcon != null) noScriptsIcon.SetActive(!canScript);
        }

        private void OnBalanceUpdated(BalanceUpdatedEvent e)
        {
            if (balanceLabel != null)
                balanceLabel.text = $"L${e.Balance:N0}";
        }

        private void OnSimConnected(SimConnectedEvent e)
        {
            RefreshRegion();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void RefreshCoords()
        {
            var local = SLApplication.Instance?.LocalAvatar;
            if (local == null || coordLabel == null) return;

            var p = local.transform.position;
            coordLabel.text = $"({p.x:F0}, {p.y:F0}, {p.z:F0})";

            RefreshRegion();
        }

        private void RefreshRegion()
        {
            var sim = _net?.Client?.Network?.CurrentSim;
            if (sim == null || regionLabel == null) return;

            var local = SLApplication.Instance?.LocalAvatar;
            Vector3 pos = local != null ? local.transform.position : Vector3.zero;

            regionLabel.text = $"{sim.Name} ({(int)pos.x}, {(int)pos.z})";
        }
    }
}
