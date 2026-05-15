using Meta.XR.MRUtilityKit;
using Oculus.Interaction;
using UnityEngine;
using UnityEngine.XR;

namespace SLQuest.VR
{
    /// <summary>
    /// Root of the Quest 3 VR rig. Owns the OVRCameraRig reference and exposes
    /// transforms needed by the locomotion and interaction systems.
    /// Requires OVRManager on the scene root (set up via Meta XR Project Setup Tool).
    /// </summary>
    [RequireComponent(typeof(OVRCameraRig))]
    public sealed class VRRig : MonoBehaviour
    {
        private OVRCameraRig _rig;

        [Header("Interaction")]
        [SerializeField] private HandController leftController;
        [SerializeField] private HandController rightController;
        [SerializeField] private LocomotionSystem locomotion;

        public Transform HeadTransform  { get; private set; }
        public Transform LeftHandRoot   { get; private set; }
        public Transform RightHandRoot  { get; private set; }
        public HandController LeftHand  => leftController;
        public HandController RightHand => rightController;
        public LocomotionSystem Locomotion => locomotion;

        // Passthrough
        private OVRPassthroughLayer _passthrough;

        private void Awake()
        {
            _rig = GetComponent<OVRCameraRig>();
        }

        private void Start()
        {
            HeadTransform = _rig.centerEyeAnchor;
            LeftHandRoot  = _rig.leftHandAnchor;
            RightHandRoot = _rig.rightHandAnchor;

            // Passthrough on Quest 3 for mixed-reality capability
            _passthrough = GetComponent<OVRPassthroughLayer>()
                        ?? gameObject.AddComponent<OVRPassthroughLayer>();
            SetPassthrough(false); // off by default; user can toggle
        }

        private void Update()
        {
            HandleSystemButtons();
        }

        private void HandleSystemButtons()
        {
            // Menu button toggles in-world UI
            if (OVRInput.GetDown(OVRInput.Button.Start))
                SLQuest.UI.VRUIManager.Instance?.ToggleMainMenu();

            // Y button toggles passthrough (mixed reality)
            if (OVRInput.GetDown(OVRInput.Button.Three))
                SetPassthrough(!_passthroughActive);
        }

        // ── Passthrough ───────────────────────────────────────────────────────

        private bool _passthroughActive;

        public void SetPassthrough(bool active)
        {
            _passthroughActive = active;
            if (_passthrough != null) _passthrough.enabled = active;

            // Make skybox transparent when passthrough is on
            if (Camera.main != null)
                Camera.main.clearFlags = active
                    ? CameraClearFlags.SolidColor
                    : CameraClearFlags.Skybox;
        }

        // ── Eye tracking (Quest 3 Pro / Quest 3 with ET) ───────────────────────

        public bool TryGetGazeDirection(out Vector3 gazeDir)
        {
            if (OVRPlugin.GetEyeGazesState(OVRPlugin.Step.Render, -1, ref _eyeGazesState))
            {
                var eye = _eyeGazesState.EyeGazes[0];
                if (eye.IsValid)
                {
                    var rot = eye.Orientation.FromFlippedZQuatf();
                    gazeDir = HeadTransform.rotation * new Vector3(
                        rot.x, rot.y, rot.z);
                    return true;
                }
            }
            gazeDir = HeadTransform.forward;
            return false;
        }

        private OVRPlugin.EyeGazesState _eyeGazesState;
    }
}
