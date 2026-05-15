using SLQuest.Avatar;
using SLQuest.Core;
using UnityEngine;

namespace SLQuest.VR
{
    public enum LocomotionMode { SmoothTurn, SnapTurn }

    /// <summary>
    /// Reads Quest 3 thumbstick input and drives <see cref="LocalAvatar"/> movement.
    /// Supports smooth locomotion (comfort mode), snap turn, and fly mode.
    ///
    /// Left stick  = move forward/back/strafe
    /// Right stick = turn (smooth or snap, configurable)
    /// Right stick click = toggle fly
    /// A = jump / rise while flying
    /// B = crouch / descend while flying
    /// </summary>
    public sealed class LocomotionSystem : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private LocomotionMode turnMode = LocomotionMode.SnapTurn;
        [SerializeField] private float snapAngle = 45f;
        [SerializeField] private float smoothTurnSpeed = 90f;
        [SerializeField] private float deadzone = 0.15f;

        [Header("Comfort")]
        [SerializeField] private bool vignetteOnMove = true;
        [SerializeField] private VignetteController vignette;

        private LocalAvatar _avatar;
        private VRRig       _rig;

        private bool  _snapHeld;
        private float _snapCooldown;

        private void Awake()
        {
            _avatar = SLApplication.Instance?.LocalAvatar ?? FindObjectOfType<LocalAvatar>();
            _rig    = SLApplication.Instance?.VR          ?? FindObjectOfType<VRRig>();
        }

        private void Update()
        {
            if (_avatar == null) return;

            HandleMove();
            HandleTurn();
            HandleFly();
            HandleTeleport();
        }

        private void HandleMove()
        {
            var stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);

            if (stick.magnitude < deadzone)
            {
                _avatar.SetMoveInput(Vector3.zero, 0f);
                if (vignetteOnMove && vignette != null) vignette.SetStrength(0f);
                return;
            }

            // Run when pushing stick fully forward
            bool run = stick.y > 0.85f && _avatar.MovementMode == AvatarMovementMode.Walk;
            _avatar.SetMovementMode(run ? AvatarMovementMode.Run : AvatarMovementMode.Walk);

            _avatar.SetMoveInput(new Vector3(stick.x, 0f, stick.y), 0f);

            if (vignetteOnMove && vignette != null)
                vignette.SetStrength(Mathf.InverseLerp(deadzone, 1f, stick.magnitude));
        }

        private void HandleTurn()
        {
            var stick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);

            if (turnMode == LocomotionMode.SnapTurn)
            {
                _snapCooldown -= Time.deltaTime;
                if (Mathf.Abs(stick.x) > 0.5f && _snapCooldown <= 0f)
                {
                    transform.Rotate(0f, Mathf.Sign(stick.x) * snapAngle, 0f, Space.World);
                    _snapCooldown = 0.35f;
                }
            }
            else
            {
                if (Mathf.Abs(stick.x) > deadzone)
                    transform.Rotate(0f, stick.x * smoothTurnSpeed * Time.deltaTime, 0f, Space.World);
            }
        }

        private void HandleFly()
        {
            // Right stick click toggles fly
            if (OVRInput.GetDown(OVRInput.Button.SecondaryThumbstick))
            {
                bool flying = _avatar.MovementMode == AvatarMovementMode.Fly;
                _avatar.SetMovementMode(flying ? AvatarMovementMode.Walk : AvatarMovementMode.Fly);
            }

            if (_avatar.MovementMode != AvatarMovementMode.Fly) return;

            // A = up, B = down while flying
            float vertical = 0f;
            if (OVRInput.Get(OVRInput.Button.One)) vertical = 1f;
            if (OVRInput.Get(OVRInput.Button.Two)) vertical = -1f;
            _avatar.SetMoveInput(_avatar.MovementMode == AvatarMovementMode.Fly
                ? new Vector3(
                    OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick).x,
                    0f,
                    OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick).y)
                : Vector3.zero, vertical);
        }

        private void HandleTeleport()
        {
            // Grip + trigger on right hand = teleport (SL-standard)
            bool gripRight    = OVRInput.Get(OVRInput.Button.SecondaryHandTrigger);
            bool triggerRight = OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger);

            if (gripRight && triggerRight)
                SLApplication.Instance?.VR?.RightHand?.BeginTeleport();
        }
    }
}
