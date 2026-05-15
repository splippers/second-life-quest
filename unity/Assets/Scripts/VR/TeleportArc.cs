using UnityEngine;

namespace SLQuest.VR
{
    /// <summary>
    /// Renders a parabolic arc from the controller to a candidate landing point,
    /// visualised as a line renderer with a target indicator.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public sealed class TeleportArc : MonoBehaviour
    {
        [SerializeField] private int segmentCount = 30;
        [SerializeField] private float launchSpeed = 8f;
        [SerializeField] private GameObject landingIndicator;
        [SerializeField] private LayerMask groundMask;

        private LineRenderer _line;
        private Vector3      _landingPoint;
        private bool         _hasLanding;

        private void Awake()
        {
            _line = GetComponent<LineRenderer>();
            _line.positionCount = segmentCount;
        }

        private void Update()
        {
            DrawArc();
        }

        private void DrawArc()
        {
            _hasLanding = false;
            var origin  = transform.position;
            var dir     = transform.forward;
            var vel     = dir * launchSpeed;

            for (int i = 0; i < segmentCount; i++)
            {
                float t = i * 0.05f;
                var p = origin + vel * t + 0.5f * Physics.gravity * t * t;
                _line.SetPosition(i, p);

                if (i > 0)
                {
                    var prev = _line.GetPosition(i - 1);
                    if (Physics.Linecast(prev, p, out var hit, groundMask))
                    {
                        _landingPoint = hit.point;
                        _hasLanding   = true;
                        if (landingIndicator != null)
                        {
                            landingIndicator.SetActive(true);
                            landingIndicator.transform.position = _landingPoint;
                        }
                        // Truncate remaining segments to landing
                        for (int r = i + 1; r < segmentCount; r++)
                            _line.SetPosition(r, _landingPoint);
                        return;
                    }
                }
            }

            if (landingIndicator != null) landingIndicator.SetActive(false);
        }

        public bool TryGetLandingPoint(out Vector3 point)
        {
            point = _landingPoint;
            return _hasLanding;
        }
    }
}
