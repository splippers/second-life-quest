using System;
using SLQuest.Building;
using SLQuest.World;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SLQuest.VR
{
    public enum HandSide { Left, Right }

    /// <summary>
    /// Per-hand controller logic: ray casting for UI and world interaction,
    /// grab detection, and teleport arc.
    /// Works with both controller input (OVRInput) and hand tracking (OVRHand).
    /// </summary>
    public sealed class HandController : MonoBehaviour
    {
        [Header("Setup")]
        [SerializeField] private HandSide side;
        [SerializeField] private LineRenderer pointerRay;
        [SerializeField] private Transform pointerOrigin;
        [SerializeField] private LayerMask interactableLayer;
        [SerializeField] private LayerMask uiLayer;
        [SerializeField] private TeleportArc teleportArc;

        [Header("Hand Tracking")]
        [SerializeField] private OVRHand ovrHand;
        [SerializeField] private OVRSkeleton skeleton;

        public event Action<RaycastHit> OnHover;
        public event Action<RaycastHit> OnSelect;   // trigger down
        public event Action             OnDeselect; // trigger up
        public event Action<RaycastHit> OnGrab;
        public event Action             OnRelease;

        private bool _wasTrigger;
        private bool _wasGrip;
        private bool _isTeleporting;
        private SLPrimitive _hovered;

        private OVRInput.Controller OvrController =>
            side == HandSide.Left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;

        private OVRInput.Button TriggerButton =>
            side == HandSide.Left ? OVRInput.Button.PrimaryIndexTrigger : OVRInput.Button.SecondaryIndexTrigger;

        private OVRInput.Button GripButton =>
            side == HandSide.Left ? OVRInput.Button.PrimaryHandTrigger : OVRInput.Button.SecondaryHandTrigger;

        private void Update()
        {
            UpdatePointer();
            UpdateButtons();
        }

        private void UpdatePointer()
        {
            var origin = pointerOrigin != null ? pointerOrigin : transform;
            var ray    = new Ray(origin.position, origin.forward);

            // UI raycast first
            bool hitUI = CastUI(ray);
            if (hitUI) { SetPointerLength(SLConstants.VR_POINTER_DISTANCE); return; }

            // World raycast
            if (Physics.Raycast(ray, out var hit, SLConstants.VR_POINTER_DISTANCE, interactableLayer))
            {
                SetPointerLength(hit.distance);
                var prim = hit.collider.GetComponentInParent<SLPrimitive>();
                if (prim != _hovered)
                {
                    _hovered = prim;
                    OnHover?.Invoke(hit);
                }
            }
            else
            {
                _hovered = null;
                SetPointerLength(SLConstants.VR_POINTER_DISTANCE);
            }
        }

        private bool CastUI(Ray ray)
        {
            if (Physics.Raycast(ray, out var hit, SLConstants.VR_POINTER_DISTANCE, uiLayer))
            {
                SetPointerLength(hit.distance);
                return true;
            }
            return false;
        }

        private void UpdateButtons()
        {
            bool trigger = OVRInput.Get(TriggerButton);
            bool grip    = OVRInput.Get(GripButton);

            // Trigger press
            if (trigger && !_wasTrigger)
            {
                var origin = pointerOrigin != null ? pointerOrigin : transform;
                if (Physics.Raycast(new Ray(origin.position, origin.forward),
                                    out var hit, SLConstants.VR_POINTER_DISTANCE))
                {
                    OnSelect?.Invoke(hit);

                    // Auto-select prim in build mode
                    var bm = SLApplication.Instance?.Building;
                    var prim = hit.collider.GetComponentInParent<SLPrimitive>();
                    if (bm != null && prim != null)
                        bm.Select(prim, false);
                }
            }

            if (!trigger && _wasTrigger)
                OnDeselect?.Invoke();

            // Grip press — grab nearby object
            if (grip && !_wasGrip)
            {
                var origin = pointerOrigin != null ? pointerOrigin : transform;
                if (Physics.Raycast(new Ray(origin.position, origin.forward),
                                    out var hit, SLConstants.VR_GRAB_DISTANCE, interactableLayer))
                    OnGrab?.Invoke(hit);
            }

            if (!grip && _wasGrip)
                OnRelease?.Invoke();

            _wasTrigger = trigger;
            _wasGrip    = grip;
        }

        // ── Teleport ──────────────────────────────────────────────────────────

        public void BeginTeleport()
        {
            _isTeleporting = true;
            teleportArc?.gameObject.SetActive(true);
        }

        public void CommitTeleport()
        {
            _isTeleporting = false;
            teleportArc?.gameObject.SetActive(false);

            if (teleportArc != null && teleportArc.TryGetLandingPoint(out var point))
            {
                SLApplication.Instance?.LocalAvatar?.transform.position.Equals(point);
                SLApplication.Instance?.Network?.Client?.Self?.Movement?.TeleportLookAt(
                    new OpenMetaverse.Vector3(point.x, point.z, point.y),
                    new OpenMetaverse.Vector3(0, 0, 0));
            }
        }

        private void SetPointerLength(float len)
        {
            if (pointerRay == null) return;
            var origin = pointerOrigin != null ? pointerOrigin : transform;
            pointerRay.SetPosition(0, origin.position);
            pointerRay.SetPosition(1, origin.position + origin.forward * len);
        }
    }
}
