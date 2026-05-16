using OpenMetaverse;
using SLQuest.Core;
using SLQuest.Network;
using SLQuest.XR;

namespace SLQuest.Avatar
{
    /// <summary>
    /// Drives the local agent: reads Quest controller input every frame,
    /// updates the LibreMetaverse movement flags, sends AgentUpdate packets,
    /// and publishes teleport events.
    /// </summary>
    public sealed class LocalAvatar
    {
        private readonly SLNetworkManager _net;
        private XRInput? _input;

        public Vector3    Position { get; private set; }
        public Quaternion Rotation { get; private set; } = Quaternion.Identity;

        // Fly-toggle debounce
        private bool _lastFlyBtn;
        // Camera yaw accumulated between packets
        private float _yawDeg;

        public LocalAvatar(SLNetworkManager net, XRSession xr)
        {
            _net = net;
            net.Client.Self.TeleportProgress += OnTeleportProgress;
        }

        public void BindInput(XRInput input) => _input = input;

        public void Tick(float dt)
        {
            if (!_net.IsLoggedIn || _input == null) return;

            _input.SyncActions();

            var right = _input.GetController(Hand.Right);
            var left  = _input.GetController(Hand.Left);

            var mv = _net.Client.Self.Movement;

            // Right thumbstick → forward/back + strafe
            mv.AtPos   = right.ThumbstickAxis.Y >  0.25f;
            mv.AtNeg   = right.ThumbstickAxis.Y < -0.25f;
            mv.LeftNeg = right.ThumbstickAxis.X >  0.25f; // strafe right
            mv.LeftPos = right.ThumbstickAxis.X < -0.25f; // strafe left

            // Left thumbstick X → turn
            mv.TurnLeft  = left.ThumbstickAxis.X < -0.25f;
            mv.TurnRight = left.ThumbstickAxis.X >  0.25f;

            // B button → toggle fly
            if (right.ButtonB && !_lastFlyBtn)
                mv.Fly = !mv.Fly;
            _lastFlyBtn = right.ButtonB;

            // While flying: left trigger/grip = ascend/descend
            if (mv.Fly)
            {
                mv.UpPos = left.TriggerValue > 0.5f;
                mv.UpNeg = left.GripValue    > 0.5f;
            }
            else
            {
                // A button → jump
                mv.UpPos = right.ButtonA;
                mv.UpNeg = false;
            }

            // Agent yaw from left thumbstick X.  SL uses Z-up so we rotate around Z.
            _yawDeg -= left.ThumbstickAxis.X * 90f * dt;
            float halfYaw = _yawDeg * (MathF.PI / 180f) * 0.5f;
            var bodyRot = new OpenMetaverse.Quaternion(
                0f, 0f, MathF.Sin(halfYaw), MathF.Cos(halfYaw));
            mv.BodyRotation = bodyRot;
            mv.HeadRotation = bodyRot;

            mv.SendUpdate();

            // Mirror current sim position back to our public props
            Position = MathEx.SLToWorld(_net.Client.Self.SimPosition);
            Rotation = MathEx.SLToWorld(_net.Client.Self.SimRotation);
        }

        private void OnTeleportProgress(object? sender, TeleportProgressEventArgs e)
        {
            if (e.Status == TeleportStatus.Finished)
                EventBus.Publish(new TeleportEvent(
                    _net.Client.Network.CurrentSim?.Name ?? string.Empty,
                    MathEx.SLToWorld(_net.Client.Self.SimPosition)));
        }
    }
}
