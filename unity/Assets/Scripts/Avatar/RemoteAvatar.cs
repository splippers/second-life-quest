using System.Collections.Generic;
using OpenMetaverse;
using UnityEngine;

namespace SLQuest.Avatar
{
    /// <summary>
    /// Represents a remote avatar in the scene.
    /// Smoothly interpolates between server-provided position updates.
    /// </summary>
    public sealed class RemoteAvatar : MonoBehaviour
    {
        public uint LocalId { get; set; }
        public string DisplayName { get; private set; }

        [SerializeField] private Animator animator;
        [SerializeField] private float interpolationSpeed = 10f;

        private Vector3    _targetPos;
        private Quaternion _targetRot;

        private static readonly int AnimSpeed   = Animator.StringToHash("Speed");
        private static readonly int AnimFlying  = Animator.StringToHash("Flying");
        private static readonly int AnimSitting = Animator.StringToHash("Sitting");

        private void Awake()
        {
            _targetPos = transform.position;
            _targetRot = transform.rotation;
        }

        private void Update()
        {
            transform.position = Vector3.Lerp(
                transform.position, _targetPos, Time.deltaTime * interpolationSpeed);
            transform.rotation = Quaternion.Slerp(
                transform.rotation, _targetRot, Time.deltaTime * interpolationSpeed);
        }

        public void UpdateTransform(Vector3 pos, Quaternion rot)
        {
            _targetPos = pos;
            _targetRot = rot;
        }

        public void UpdateAnimations(List<Animation> animations)
        {
            if (animator == null || animations == null) return;

            bool isFlying  = false;
            bool isSitting = false;
            float speed    = 0f;

            foreach (var anim in animations)
            {
                // Linden-defined animation UUIDs for locomotion states
                if (anim.AnimationID == Animations.FLY)         isFlying  = true;
                if (anim.AnimationID == Animations.SIT)         isSitting = true;
                if (anim.AnimationID == Animations.WALK ||
                    anim.AnimationID == Animations.TURNLEFT ||
                    anim.AnimationID == Animations.TURNRIGHT)   speed = 1f;
                if (anim.AnimationID == Animations.RUN)         speed = 2f;
            }

            animator.SetFloat(AnimSpeed,    speed);
            animator.SetBool(AnimFlying,  isFlying);
            animator.SetBool(AnimSitting, isSitting);
        }
    }
}
