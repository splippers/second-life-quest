using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Core;
using SLQuest.VR;
using UnityEngine;

namespace SLQuest.Avatar
{
    public enum AvatarMovementMode { Walk, Run, Fly, Sit }

    /// <summary>
    /// Represents the player's own avatar. Sends AgentUpdate packets to the sim
    /// at the rate libopenmetaverse expects (~10 Hz) and receives position corrections.
    /// </summary>
    public sealed class LocalAvatar : MonoBehaviour
    {
        [Header("Physics")]
        [SerializeField] private CharacterController controller;
        [SerializeField] private float walkSpeed = SLConstants.WALK_SPEED;
        [SerializeField] private float runSpeed  = SLConstants.RUN_SPEED;
        [SerializeField] private float flySpeed  = SLConstants.FLY_SPEED;
        [SerializeField] private float gravity   = -9.81f;

        [Header("Animation")]
        [SerializeField] private Animator animator;

        public AvatarMovementMode MovementMode { get; private set; } = AvatarMovementMode.Walk;
        public bool IsSitting { get; private set; }

        private SLNetworkManager _net;
        private VRRig            _vrRig;
        private Vector3          _velocity;
        private Vector3          _moveInput;
        private float            _verticalInput;

        private static readonly int AnimSpeed   = Animator.StringToHash("Speed");
        private static readonly int AnimFlying  = Animator.StringToHash("Flying");
        private static readonly int AnimSitting = Animator.StringToHash("Sitting");

        private void Awake()
        {
            _net   = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            _vrRig = SLApplication.Instance?.VR      ?? FindObjectOfType<VRRig>();

            controller ??= GetComponent<CharacterController>();
        }

        private void Start()
        {
            EventBus.Subscribe<TeleportEvent>(OnTeleport);
        }

        private void OnDestroy()
        {
            EventBus.Unsubscribe<TeleportEvent>(OnTeleport);
        }

        // ── Called by VRLocomotionSystem each frame ────────────────────────────

        public void SetMoveInput(Vector3 localDirection, float vertical)
        {
            _moveInput     = localDirection;
            _verticalInput = vertical;
        }

        public void SetMovementMode(AvatarMovementMode mode)
        {
            MovementMode = mode;
            animator?.SetBool(AnimFlying, mode == AvatarMovementMode.Fly);
        }

        public void SetSitting(bool sit)
        {
            IsSitting = sit;
            animator?.SetBool(AnimSitting, sit);
        }

        // ── Physics & network sync ────────────────────────────────────────────

        private float _agentUpdateTimer;
        private const float AGENT_UPDATE_INTERVAL = 0.1f; // 10 Hz

        private void Update()
        {
            if (!_net.IsInWorld || IsSitting) return;

            Move();

            _agentUpdateTimer += Time.deltaTime;
            if (_agentUpdateTimer >= AGENT_UPDATE_INTERVAL)
            {
                SendAgentUpdate();
                _agentUpdateTimer = 0f;
            }

            if (animator != null)
                animator.SetFloat(AnimSpeed, _velocity.magnitude);
        }

        private void Move()
        {
            float speed = MovementMode switch
            {
                AvatarMovementMode.Run => runSpeed,
                AvatarMovementMode.Fly => flySpeed,
                _                      => walkSpeed
            };

            var head = _vrRig?.HeadTransform ?? transform;
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            Vector3 right   = Vector3.ProjectOnPlane(head.right,   Vector3.up).normalized;

            Vector3 desiredMove = (forward * _moveInput.z + right * _moveInput.x) * speed;

            if (MovementMode == AvatarMovementMode.Fly)
                desiredMove.y = _verticalInput * flySpeed;
            else
            {
                _velocity.y += gravity * Time.deltaTime;
                if (controller != null && controller.isGrounded)
                    _velocity.y = -0.5f;
                desiredMove.y = _velocity.y;
            }

            _velocity = desiredMove;

            if (controller != null)
                controller.Move(_velocity * Time.deltaTime);
            else
                transform.position += _velocity * Time.deltaTime;
        }

        private void SendAgentUpdate()
        {
            if (_net?.Client?.Self == null) return;

            var self = _net.Client.Self;
            var pos  = transform.position;

            // Convert back to SL coordinate space
            self.Movement.Camera.Position    = new Vector3d(pos.x, pos.z, pos.y);
            self.Movement.Camera.LeftAxis    = new OpenMetaverse.Vector3(1, 0, 0);
            self.Movement.Camera.AtAxis      = new OpenMetaverse.Vector3(0, 0, 1);
            self.Movement.Camera.UpAxis      = new OpenMetaverse.Vector3(0, 1, 0);

            bool moving = _velocity.sqrMagnitude > 0.01f;
            self.Movement.AtPos = moving && _moveInput.z > 0;
            self.Movement.AtNeg = moving && _moveInput.z < 0;
            self.Movement.LeftPos  = moving && _moveInput.x < 0;
            self.Movement.LeftNeg  = moving && _moveInput.x > 0;
            self.Movement.Fly      = MovementMode == AvatarMovementMode.Fly;
            self.Movement.FastAt   = MovementMode == AvatarMovementMode.Run;

            self.Movement.SendUpdate(false);
        }

        /// <summary>Server-authoritative position correction (called by AvatarManager).</summary>
        public void ApplyServerPosition(Vector3 pos, Quaternion rot)
        {
            // Lerp to avoid jarring snaps when the server corrects minor drift
            transform.position = Vector3.Lerp(transform.position, pos, 0.1f);
        }

        private void OnTeleport(TeleportEvent evt)
        {
            transform.position = evt.Position;
            _velocity = Vector3.zero;
        }
    }
}
