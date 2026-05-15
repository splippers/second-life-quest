using UnityEngine;

namespace SLQuest.UI
{
    /// <summary>
    /// Attach to any world-space panel to make it physically grabbable.
    ///
    /// The player grabs by pointing at the panel and squeezing the grip trigger
    /// (OVRInput.Axis1D.PrimaryHandTrigger / SecondaryHandTrigger > 0.7).
    /// While held, the panel follows the controller.  Release stops tracking.
    ///
    /// Also supports a double-tap of the grip to reset the panel to the default
    /// camera-relative position stored in <see cref="defaultOffset"/>.
    ///
    /// Inspector wiring:
    ///   defaultOffset — camera-local position the panel snaps back to on reset
    /// </summary>
    public sealed class GrabbablePanel : MonoBehaviour
    {
        [SerializeField] private Vector3 defaultOffset = new(0f, 0f, 1.2f);

        private bool      _heldRight;
        private bool      _heldLeft;
        private Transform _holdingController;
        private Vector3   _localGrabOffset;
        private Quaternion _localGrabRot;

        private float _lastGripRightTime = -10f;
        private float _lastGripLeftTime  = -10f;
        private const float DoubleTapWindow = 0.35f;

        // Hover state set by HandController ray
        public bool IsHoveredRight { get; set; }
        public bool IsHoveredLeft  { get; set; }

        private void Update()
        {
            float gripRight = OVRInput.Get(OVRInput.Axis1D.SecondaryHandTrigger);
            float gripLeft  = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger);

            // Grab
            if (!_heldRight && !_heldLeft)
            {
                if (IsHoveredRight && gripRight > 0.7f)
                    BeginGrab(OVRInput.Controller.RTouch, ref _heldRight);
                else if (IsHoveredLeft && gripLeft > 0.7f)
                    BeginGrab(OVRInput.Controller.LTouch, ref _heldLeft);
            }

            // Release
            if (_heldRight && gripRight < 0.2f) EndGrab(ref _heldRight);
            if (_heldLeft  && gripLeft  < 0.2f) EndGrab(ref _heldLeft);

            // Follow controller
            if (_holdingController != null)
            {
                transform.position = _holdingController.TransformPoint(_localGrabOffset);
                transform.rotation = _holdingController.rotation * _localGrabRot;
            }

            // Double-tap grip to reset position
            if (OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger))
            {
                if (Time.time - _lastGripRightTime < DoubleTapWindow) ResetPosition();
                _lastGripRightTime = Time.time;
            }
            if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger))
            {
                if (Time.time - _lastGripLeftTime < DoubleTapWindow) ResetPosition();
                _lastGripLeftTime = Time.time;
            }
        }

        private void BeginGrab(OVRInput.Controller ctrl, ref bool held)
        {
            var anchor = GetControllerAnchor(ctrl);
            if (anchor == null) return;
            held = true;
            _holdingController = anchor;
            _localGrabOffset   = anchor.InverseTransformPoint(transform.position);
            _localGrabRot      = Quaternion.Inverse(anchor.rotation) * transform.rotation;
        }

        private void EndGrab(ref bool held)
        {
            held               = false;
            _holdingController = null;
        }

        public void ResetPosition()
        {
            var cam = Camera.main;
            if (cam == null) return;
            transform.position = cam.transform.TransformPoint(defaultOffset);
            transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
        }

        private static Transform GetControllerAnchor(OVRInput.Controller ctrl)
        {
            // OVRCameraRig has left/right hand anchors; fall back to camera if not found
            var rig = Object.FindObjectOfType<OVRCameraRig>();
            if (rig == null) return Camera.main?.transform;
            return ctrl == OVRInput.Controller.RTouch
                ? rig.rightHandAnchor
                : rig.leftHandAnchor;
        }
    }
}
