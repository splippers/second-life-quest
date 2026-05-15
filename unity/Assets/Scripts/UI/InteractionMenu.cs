using System;
using System.Collections;
using OpenMetaverse;
using TMPro;
using SLQuest.Core;
using SLQuest.Network;
using SLQuest.World;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space context menu that appears when the user presses A/X while
    /// pointing at a prim.  Positions itself at the prim and faces the camera.
    ///
    /// Inspector wiring:
    ///   titleLabel   — TMP_Text showing prim name
    ///   ownerLabel   — TMP_Text showing owner display name
    ///   touchButton  — fires a Touch event on the prim
    ///   sitButton    — RequestSit / stand if already sitting
    ///   buyButton    — requests sale info then sends buy
    ///   infoButton   — opens an info sub-panel (shows desc, perms, sale price)
    ///   closeButton  — destroys the menu
    ///   sitLabel     — TMP_Text on sitButton ("Sit" / "Stand")
    ///   infoPanel    — child GameObject shown when infoButton is pressed
    ///   infoText     — TMP_Text inside infoPanel
    /// </summary>
    public sealed class InteractionMenu : MonoBehaviour
    {
        [Header("Labels")]
        [SerializeField] private TMP_Text titleLabel;
        [SerializeField] private TMP_Text ownerLabel;
        [SerializeField] private TMP_Text sitLabel;

        [Header("Buttons")]
        [SerializeField] private Button touchButton;
        [SerializeField] private Button sitButton;
        [SerializeField] private Button buyButton;
        [SerializeField] private Button infoButton;
        [SerializeField] private Button closeButton;

        [Header("Info sub-panel")]
        [SerializeField] private GameObject infoPanel;
        [SerializeField] private TMP_Text   infoText;

        private SLPrimitive      _prim;
        private SLNetworkManager _net;
        private Action           _onClose;
        private bool             _isSitting;
        private bool             _propertiesFetched;

        // ── Public API ────────────────────────────────────────────────────────

        public void Open(SLPrimitive prim, SLNetworkManager net, Action onClose)
        {
            _prim    = prim;
            _net     = net;
            _onClose = onClose;

            // Face the camera
            var cam = Camera.main;
            if (cam != null)
            {
                transform.position = prim.transform.position + Vector3.up * 0.5f;
                transform.LookAt(cam.transform.position);
                transform.rotation = Quaternion.LookRotation(
                    transform.position - cam.transform.position);
            }

            // Button wiring
            touchButton?.onClick.AddListener(OnTouch);
            sitButton?.onClick.AddListener(OnSitOrStand);
            buyButton?.onClick.AddListener(OnBuy);
            infoButton?.onClick.AddListener(OnInfo);
            closeButton?.onClick.AddListener(OnClose);

            if (infoPanel != null) infoPanel.SetActive(false);
            buyButton?.gameObject.SetActive(false); // shown after properties arrive

            UpdateSitLabel();
            PopulateFromPrim();
            RequestProperties();
        }

        // ── Populate ──────────────────────────────────────────────────────────

        private void PopulateFromPrim()
        {
            if (titleLabel != null)
            {
                string name = _prim.Prim?.Properties?.Name ?? _prim.FullId.ToString()[..8];
                titleLabel.text = string.IsNullOrEmpty(name) ? "(unnamed)" : name;
            }

            if (ownerLabel != null)
                ownerLabel.text = "…";
        }

        private void RequestProperties()
        {
            if (_propertiesFetched) return;
            _net.Client.Objects.RequestObjectPropertiesFamily(
                _net.Client.Network.CurrentSim, _prim.FullId);
            _net.Client.Objects.ObjectPropertiesFamily += OnPropertiesReceived;
        }

        private void OnPropertiesReceived(object sender, ObjectPropertiesFamilyEventArgs e)
        {
            if (e.Properties.ObjectID != _prim.FullId) return;
            _net.Client.Objects.ObjectPropertiesFamily -= OnPropertiesReceived;
            _propertiesFetched = true;

            MainThreadDispatcher.Enqueue(() =>
            {
                if (this == null) return; // menu might have closed

                if (titleLabel != null && !string.IsNullOrEmpty(e.Properties.Name))
                    titleLabel.text = e.Properties.Name;

                // Resolve owner UUID to display name
                _net.Client.Avatars.RequestAvatarName(e.Properties.OwnerID, (uuid, names) =>
                {
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        if (ownerLabel != null && names.TryGetValue(uuid, out string dname))
                            ownerLabel.text = $"Owner: {dname}";
                    });
                });

                // Show buy button if for sale
                bool forSale = e.Properties.SaleType != SaleType.Not;
                buyButton?.gameObject.SetActive(forSale);

                // Cache for info panel
                if (infoText != null)
                {
                    infoText.text =
                        $"{e.Properties.Name}\n\n" +
                        $"{e.Properties.Description}\n\n" +
                        $"Sale: {(forSale ? $"L${e.Properties.SalePrice} ({e.Properties.SaleType})" : "not for sale")}\n" +
                        $"Owner: {e.Properties.OwnerID}";
                }
            });
        }

        // ── Actions ───────────────────────────────────────────────────────────

        private void OnTouch()
        {
            if (_net?.Client?.Self == null) return;
            var pos = new OpenMetaverse.Vector3(
                _prim.transform.position.x,
                _prim.transform.position.z,
                _prim.transform.position.y);
            _net.Client.Self.Grab(_prim.LocalId, UUID.Zero, UUID.Zero, UUID.Zero, 0, pos,
                                  OpenMetaverse.Vector3.Zero, OpenMetaverse.Vector3.Zero);
            StartCoroutine(DeGrabNextFrame(pos));
            OnClose();
        }

        private IEnumerator DeGrabNextFrame(OpenMetaverse.Vector3 pos)
        {
            yield return null;
            _net?.Client?.Self?.DeGrab(_prim.LocalId, UUID.Zero, UUID.Zero, 0, pos,
                                       OpenMetaverse.Vector3.Zero, OpenMetaverse.Vector3.Zero);
        }

        private void OnSitOrStand()
        {
            if (_net?.Client?.Self == null) return;

            if (_isSitting)
            {
                _net.Client.Self.Stand();
                _isSitting = false;
            }
            else
            {
                _net.Client.Self.RequestSit(_prim.FullId, OpenMetaverse.Vector3.Zero);
                _net.Client.Self.Sit();
                _isSitting = true;
            }
            UpdateSitLabel();
            OnClose();
        }

        private void OnBuy()
        {
            if (_net?.Client?.Self == null) return;
            var props = _prim.Prim?.Properties;
            if (props == null) return;

            _net.Client.Self.Buy(_prim.LocalId,
                                 props.GroupID,
                                 props.SalePrice,
                                 props.SaleType,
                                 _net.Client.Network.CurrentSim,
                                 _prim.FullId,
                                 props.GroupID,
                                 UUID.Zero);
            OnClose();
        }

        private void OnInfo()
        {
            if (infoPanel == null) return;
            infoPanel.SetActive(!infoPanel.activeSelf);
        }

        private void OnClose()
        {
            _net.Client.Objects.ObjectPropertiesFamily -= OnPropertiesReceived;
            _onClose?.Invoke();
            Destroy(gameObject);
        }

        private void UpdateSitLabel()
        {
            if (sitLabel != null) sitLabel.text = _isSitting ? "Stand" : "Sit";
        }

        private void OnDestroy()
        {
            if (_net?.Client?.Objects != null)
                _net.Client.Objects.ObjectPropertiesFamily -= OnPropertiesReceived;
        }
    }
}
