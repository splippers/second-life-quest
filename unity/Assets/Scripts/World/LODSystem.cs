using System.Collections.Generic;
using UnityEngine;

namespace SLQuest.World
{
    /// <summary>
    /// Distance-based LOD and draw-distance culling for SLPrimitive objects.
    ///
    /// Registered prims are checked every <see cref="checkIntervalSec"/> seconds
    /// (staggered across frames) against configurable distance bands:
    ///
    ///   0 → nearDistance    full render + collider
    ///   nearDistance → farDistance   render only (collider disabled to save CPU)
    ///   farDistance → drawDistance   renderer disabled (object still exists)
    ///   > drawDistance               renderer disabled (object invisible)
    ///
    /// <see cref="DrawDistance"/> is the single knob exposed to SettingsPanel;
    /// the near/far ratios track it automatically.
    ///
    /// Register prims via <see cref="Register"/> when spawned;
    /// unregister via <see cref="Unregister"/> when destroyed.
    /// </summary>
    public sealed class LODSystem : MonoBehaviour
    {
        [SerializeField] private float drawDistance    = 128f; // metres
        [SerializeField] private float checkIntervalSec = 1.5f;

        // Near = 30% of draw distance for collider cutoff
        // Far  = 75% of draw distance for renderer cutoff
        private const float NEAR_RATIO = 0.30f;
        private const float FAR_RATIO  = 0.75f;

        public float DrawDistance
        {
            get => drawDistance;
            set
            {
                drawDistance = Mathf.Clamp(value, 32f, 512f);
                if (Camera.main != null)
                    Camera.main.farClipPlane = drawDistance + 32f; // add headroom for skybox
            }
        }

        private readonly List<SLPrimitive> _prims = new();
        private int   _checkCursor;
        private float _checkTimer;

        // ── Registration ──────────────────────────────────────────────────────

        public void Register(SLPrimitive prim)
        {
            if (prim != null && !_prims.Contains(prim))
                _prims.Add(prim);
        }

        public void Unregister(SLPrimitive prim)
        {
            _prims.Remove(prim);
        }

        // ── Update ────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Camera.main != null)
                Camera.main.farClipPlane = drawDistance + 32f;
        }

        private void Update()
        {
            if (_prims.Count == 0) return;

            _checkTimer += Time.deltaTime;
            if (_checkTimer < checkIntervalSec / _prims.Count) return;
            _checkTimer = 0f;

            // Process one prim per tick (staggered) to spread CPU cost
            if (_checkCursor >= _prims.Count) _checkCursor = 0;

            var prim = _prims[_checkCursor];
            _checkCursor++;

            if (prim == null)
            {
                _prims.RemoveAt(_checkCursor - 1);
                _checkCursor--;
                return;
            }

            ApplyLOD(prim);
        }

        private void ApplyLOD(SLPrimitive prim)
        {
            var cam = Camera.main;
            if (cam == null) return;

            float dist = Vector3.Distance(cam.transform.position, prim.transform.position);

            float near = drawDistance * NEAR_RATIO;
            float far  = drawDistance * FAR_RATIO;

            var renderer = prim.GetComponent<Renderer>();
            var collider = prim.GetComponent<Collider>();

            if (dist <= near)
            {
                if (renderer != null) renderer.enabled = true;
                if (collider != null) collider.enabled = true;
            }
            else if (dist <= far)
            {
                if (renderer != null) renderer.enabled = true;
                if (collider != null) collider.enabled = false;
            }
            else
            {
                if (renderer != null) renderer.enabled = dist <= drawDistance;
                if (collider != null) collider.enabled = false;
            }
        }
    }
}
