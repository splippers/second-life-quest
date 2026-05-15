using System.Collections;
using OpenMetaverse;
using SLQuest.Core;
using SLQuest.Network;
using SLQuest.UI;
using SLQuest.VR;
using UnityEngine;

namespace SLQuest.World
{
    /// <summary>
    /// Handles VR controller interaction with in-world prims.
    ///
    /// Wires up both hand controllers at Start and drives:
    ///   Trigger press → Touch the hovered prim (Grab + DeGrab)
    ///   A / X button  → Toggle the context menu near the hovered prim
    ///                   (menu offers: Touch, Sit/Stand, Buy, Object Info)
    ///
    /// Place on the VRRig or SLApplication root; it finds HandControllers
    /// via SLApplication.Instance.VR.GetComponentsInChildren.
    /// </summary>
    public sealed class ObjectInteractor : MonoBehaviour
    {
        [Header("Context menu")]
        [SerializeField] private InteractionMenu interactionMenuPrefab;

        private SLNetworkManager _net;
        private InteractionMenu  _activeMenu;

        // Last prim each hand was pointing at when trigger fired
        private SLPrimitive _rightHovered;
        private SLPrimitive _leftHovered;

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
        }

        private void Start()
        {
            var vr = SLApplication.Instance?.VR;
            if (vr == null) return;

            foreach (var hc in vr.GetComponentsInChildren<HandController>())
            {
                bool isRight = hc.Side == HandSide.Right;

                hc.OnHover += hit =>
                {
                    var prim = hit.collider.GetComponentInParent<SLPrimitive>();
                    if (isRight) { _rightHovered?.GetComponent<HoverHighlight>()?.SetHovered(false); _rightHovered = prim; }
                    else         { _leftHovered?.GetComponent<HoverHighlight>()?.SetHovered(false);  _leftHovered  = prim; }
                    prim?.GetComponent<HoverHighlight>()?.SetHovered(true);
                };

                hc.OnSelect += hit =>
                {
                    var prim = hit.collider.GetComponentInParent<SLPrimitive>();
                    if (prim != null)
                        StartCoroutine(SendTouch(prim, hit.point));
                };

                hc.OnDeselect += () =>
                {
                    if (isRight) { _rightHovered?.GetComponent<HoverHighlight>()?.SetHovered(false); _rightHovered = null; }
                    else         { _leftHovered?.GetComponent<HoverHighlight>()?.SetHovered(false);  _leftHovered  = null; }
                };
            }
        }

        private void Update()
        {
            // A (right) or X (left) face button → context menu
            bool rightMenu = OVRInput.GetDown(OVRInput.Button.One,  OVRInput.Controller.RTouch);
            bool leftMenu  = OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch);

            if (rightMenu && _rightHovered != null)
                ToggleContextMenu(_rightHovered);
            else if (leftMenu && _leftHovered != null)
                ToggleContextMenu(_leftHovered);
        }

        // ── Touch ─────────────────────────────────────────────────────────────

        private IEnumerator SendTouch(SLPrimitive prim, Vector3 hitPoint)
        {
            if (_net?.Client?.Self == null || !_net.IsInWorld) yield break;

            var sim = _net.Client.Network.CurrentSim;
            var omvPos = new OpenMetaverse.Vector3(hitPoint.x, hitPoint.z, hitPoint.y);

            _net.Client.Self.Grab(prim.LocalId, UUID.Zero, UUID.Zero, UUID.Zero,
                                  0, omvPos,
                                  OpenMetaverse.Vector3.Zero,
                                  OpenMetaverse.Vector3.Zero);
            yield return null; // one frame between grab and degrab
            _net.Client.Self.DeGrab(prim.LocalId, UUID.Zero, UUID.Zero,
                                    0, omvPos,
                                    OpenMetaverse.Vector3.Zero,
                                    OpenMetaverse.Vector3.Zero);

            EventBus.Publish(new ObjectTouchedEvent(prim.LocalId, prim.FullId));
        }

        // ── Context menu ──────────────────────────────────────────────────────

        private void ToggleContextMenu(SLPrimitive prim)
        {
            if (_activeMenu != null)
            {
                Destroy(_activeMenu.gameObject);
                _activeMenu = null;
                return;
            }

            if (interactionMenuPrefab == null) return;

            _activeMenu = Instantiate(interactionMenuPrefab);
            _activeMenu.Open(prim, _net, onClose: () => _activeMenu = null);
        }
    }
}
