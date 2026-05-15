using UnityEngine;

namespace SLQuest.World
{
    /// <summary>
    /// Attach to a <see cref="SLPrimitive"/> to show a hover outline when the
    /// VR hand ray is pointing at it.  Call <see cref="SetHovered"/> from
    /// <see cref="ObjectInteractor"/> when hover state changes.
    ///
    /// Uses a second mesh renderer with a rim-light / outline material rendered
    /// slightly scaled up.  The outline material is a URP shader with ZWrite Off
    /// and inverted normals (set up in the Inspector).
    ///
    /// Inspector wiring:
    ///   outlineMaterial — URP outline material (culling Front, ZWrite Off)
    ///   outlineScale    — how much to scale up the outline mesh (default 1.02)
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class HoverHighlight : MonoBehaviour
    {
        [SerializeField] private Material outlineMaterial;
        [SerializeField] private float    outlineScale = 1.02f;

        private GameObject   _outlineGO;
        private MeshRenderer _outlineRenderer;
        private bool         _hovered;

        private void Awake()
        {
            // Build outline child once
            _outlineGO = new GameObject("__Outline")
            {
                layer = gameObject.layer
            };
            _outlineGO.transform.SetParent(transform, false);
            _outlineGO.transform.localScale = Vector3.one * outlineScale;

            var mf = _outlineGO.AddComponent<MeshFilter>();
            mf.sharedMesh = GetComponent<MeshFilter>().sharedMesh;

            _outlineRenderer = _outlineGO.AddComponent<MeshRenderer>();
            _outlineRenderer.sharedMaterial = outlineMaterial;
            _outlineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _outlineRenderer.receiveShadows    = false;

            _outlineGO.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_outlineGO != null) Destroy(_outlineGO);
        }

        public void SetHovered(bool hovered)
        {
            if (_hovered == hovered) return;
            _hovered = hovered;
            if (_outlineGO != null) _outlineGO.SetActive(hovered);
        }

        public void RefreshMesh()
        {
            if (_outlineGO == null) return;
            var mf = _outlineGO.GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = GetComponent<MeshFilter>().sharedMesh;
        }
    }
}
