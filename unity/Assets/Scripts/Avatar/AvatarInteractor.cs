using SLQuest.Core;
using SLQuest.UI;
using SLQuest.VR;
using UnityEngine;

namespace SLQuest.Avatar
{
    /// <summary>
    /// Detects B (right) / Y (left) button presses while the hand ray hovers a
    /// RemoteAvatar and spawns an <see cref="AvatarInspector"/> card.
    ///
    /// Inspector wiring:
    ///   inspectorPrefab — world-space prefab with AvatarInspector component
    /// </summary>
    public sealed class AvatarInteractor : MonoBehaviour
    {
        [SerializeField] private AvatarInspector inspectorPrefab;

        private RemoteAvatar _rightHovered;
        private RemoteAvatar _leftHovered;
        private AvatarInspector _activeInspector;

        private void Start()
        {
            var vr = SLApplication.Instance?.VR;
            if (vr == null) return;

            foreach (var hc in vr.GetComponentsInChildren<HandController>())
            {
                bool isRight = hc.Side == HandSide.Right;

                hc.OnHover += hit =>
                {
                    var av = hit.collider.GetComponentInParent<RemoteAvatar>();
                    if (isRight) _rightHovered = av;
                    else         _leftHovered  = av;
                };

                hc.OnDeselect += () =>
                {
                    if (isRight) _rightHovered = null;
                    else         _leftHovered  = null;
                };
            }
        }

        private void Update()
        {
            bool rightPress = OVRInput.GetDown(OVRInput.Button.Two,  OVRInput.Controller.RTouch);
            bool leftPress  = OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.LTouch);

            RemoteAvatar target = null;
            if (rightPress && _rightHovered != null) target = _rightHovered;
            else if (leftPress && _leftHovered != null) target = _leftHovered;

            if (target != null)
                OpenInspector(target);
        }

        private void OpenInspector(RemoteAvatar av)
        {
            if (inspectorPrefab == null) return;

            if (_activeInspector != null)
                Destroy(_activeInspector.gameObject);

            _activeInspector = Instantiate(inspectorPrefab);
            _activeInspector.Inspect(av.AgentId, av.transform.position);
        }
    }
}
