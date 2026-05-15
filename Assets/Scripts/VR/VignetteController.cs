using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SLQuest.VR
{
    /// <summary>Comfort vignette that fades in during locomotion to reduce motion sickness.</summary>
    public sealed class VignetteController : MonoBehaviour
    {
        [SerializeField] private Volume postProcessVolume;
        [SerializeField] [Range(0f, 1f)] private float maxStrength = 0.5f;

        private Vignette _vignette;

        private void Start()
        {
            postProcessVolume?.profile.TryGet(out _vignette);
        }

        public void SetStrength(float normalised)
        {
            if (_vignette == null) return;
            _vignette.intensity.value = Mathf.Lerp(0f, maxStrength, normalised);
        }
    }
}
