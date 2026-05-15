using TMPro;
using SLQuest.Avatar;
using SLQuest.Core;
using SLQuest.VR;
using SLQuest.World;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space settings panel. Controls graphics quality, draw distance,
    /// locomotion comfort, and avatar display options at runtime.
    ///
    /// Inspector wiring:
    ///   drawDistanceSlider  — Slider (32–512 m, value = metres)
    ///   drawDistanceLabel   — TMP_Text "128 m"
    ///   qualityDropdown     — TMP_Dropdown: Low / Medium / High
    ///   turnModeDropdown    — TMP_Dropdown: Snap Turn / Smooth Turn
    ///   snapAngleDropdown   — TMP_Dropdown: 15° / 30° / 45° / 60°
    ///   vignetteToggle      — Toggle: vignette on movement
    ///   nameTagToggle       — Toggle: show/hide avatar name tags
    ///   closeButton         — hides the panel
    /// </summary>
    public sealed class SettingsPanel : MonoBehaviour
    {
        [Header("Graphics")]
        [SerializeField] private Slider      drawDistanceSlider;
        [SerializeField] private TMP_Text    drawDistanceLabel;
        [SerializeField] private TMP_Dropdown qualityDropdown;

        [Header("Locomotion")]
        [SerializeField] private TMP_Dropdown turnModeDropdown;
        [SerializeField] private TMP_Dropdown snapAngleDropdown;
        [SerializeField] private Toggle       vignetteToggle;

        [Header("Avatars")]
        [SerializeField] private Toggle nameTagToggle;

        [Header("Audio")]
        [SerializeField] private Slider musicVolumeSlider;
        [SerializeField] private Slider spatialVolumeSlider;
        [SerializeField] private Toggle musicMuteToggle;

        [Header("Controls")]
        [SerializeField] private Button closeButton;

        private LODSystem          _lod;
        private LocomotionSystem   _loco;
        private NameTagManager     _nameTags;
        private RegionAudioManager _regionAudio;
        private SpatialAudioManager _spatialAudio;

        private static readonly float[] SnapAngles = { 15f, 30f, 45f, 60f };

        private void Awake()
        {
            _lod          = SLApplication.Instance?.LOD         ?? FindObjectOfType<LODSystem>();
            _loco         = SLApplication.Instance?.Loco        ?? FindObjectOfType<LocomotionSystem>();
            _nameTags     = SLApplication.Instance?.NameTags    ?? FindObjectOfType<NameTagManager>();
            _regionAudio  = SLApplication.Instance?.RegionAudio ?? FindObjectOfType<RegionAudioManager>();
            _spatialAudio = SLApplication.Instance?.SpatialAudio ?? FindObjectOfType<SpatialAudioManager>();

            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));
        }

        private void OnEnable()
        {
            PopulateFromCurrentSettings();
            WireCallbacks();
        }

        private void OnDisable()
        {
            UnwireCallbacks();
        }

        // ── Populate ──────────────────────────────────────────────────────────

        private void PopulateFromCurrentSettings()
        {
            if (drawDistanceSlider != null && _lod != null)
            {
                drawDistanceSlider.minValue = 32f;
                drawDistanceSlider.maxValue = 512f;
                drawDistanceSlider.value    = _lod.DrawDistance;
                UpdateDrawDistanceLabel(_lod.DrawDistance);
            }

            if (qualityDropdown != null)
                qualityDropdown.value = QualitySettings.GetQualityLevel();

            if (_loco != null)
            {
                if (turnModeDropdown != null)
                    turnModeDropdown.value = _loco.TurnMode == LocomotionMode.SnapTurn ? 0 : 1;

                if (snapAngleDropdown != null)
                    snapAngleDropdown.value = NearestSnapIndex(_loco.SnapAngle);

                if (vignetteToggle != null)
                    vignetteToggle.isOn = _loco.VignetteOnMove;
            }

            if (nameTagToggle != null && _nameTags != null)
                nameTagToggle.isOn = _nameTags.Visible;

            if (musicVolumeSlider != null && _regionAudio != null)
                musicVolumeSlider.value = _regionAudio.Volume;
            if (spatialVolumeSlider != null && _spatialAudio != null)
                spatialVolumeSlider.value = _spatialAudio.MasterVolume;
            if (musicMuteToggle != null && _regionAudio != null)
                musicMuteToggle.isOn = _regionAudio.IsMuted;
        }

        // ── Callbacks ─────────────────────────────────────────────────────────

        private void WireCallbacks()
        {
            drawDistanceSlider?.onValueChanged.AddListener(OnDrawDistance);
            qualityDropdown?.onValueChanged.AddListener(OnQuality);
            turnModeDropdown?.onValueChanged.AddListener(OnTurnMode);
            snapAngleDropdown?.onValueChanged.AddListener(OnSnapAngle);
            vignetteToggle?.onValueChanged.AddListener(OnVignette);
            nameTagToggle?.onValueChanged.AddListener(OnNameTags);
            musicVolumeSlider?.onValueChanged.AddListener(v => { if (_regionAudio != null) _regionAudio.Volume = v; });
            spatialVolumeSlider?.onValueChanged.AddListener(v => { if (_spatialAudio != null) _spatialAudio.MasterVolume = v; });
            musicMuteToggle?.onValueChanged.AddListener(on => _regionAudio?.SetMuted(on));
        }

        private void UnwireCallbacks()
        {
            drawDistanceSlider?.onValueChanged.RemoveListener(OnDrawDistance);
            qualityDropdown?.onValueChanged.RemoveListener(OnQuality);
            turnModeDropdown?.onValueChanged.RemoveListener(OnTurnMode);
            snapAngleDropdown?.onValueChanged.RemoveListener(OnSnapAngle);
            vignetteToggle?.onValueChanged.RemoveListener(OnVignette);
            nameTagToggle?.onValueChanged.RemoveListener(OnNameTags);
            musicVolumeSlider?.onValueChanged.RemoveAllListeners();
            spatialVolumeSlider?.onValueChanged.RemoveAllListeners();
            musicMuteToggle?.onValueChanged.RemoveAllListeners();
        }

        // ── Handlers ──────────────────────────────────────────────────────────

        private void OnDrawDistance(float value)
        {
            if (_lod != null) _lod.DrawDistance = value;
            UpdateDrawDistanceLabel(value);
        }

        private void OnQuality(int index)
        {
            QualitySettings.SetQualityLevel(index, true);
        }

        private void OnTurnMode(int index)
        {
            if (_loco != null)
                _loco.TurnMode = index == 0 ? LocomotionMode.SnapTurn : LocomotionMode.SmoothTurn;
        }

        private void OnSnapAngle(int index)
        {
            if (_loco != null && index < SnapAngles.Length)
                _loco.SnapAngle = SnapAngles[index];
        }

        private void OnVignette(bool on)
        {
            if (_loco != null) _loco.VignetteOnMove = on;
        }

        private void OnNameTags(bool on)
        {
            if (_nameTags != null) _nameTags.Visible = on;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void UpdateDrawDistanceLabel(float value)
        {
            if (drawDistanceLabel != null) drawDistanceLabel.text = $"{value:F0} m";
        }

        private static int NearestSnapIndex(float angle)
        {
            int best = 0;
            float bestDiff = float.MaxValue;
            for (int i = 0; i < SnapAngles.Length; i++)
            {
                float diff = Mathf.Abs(SnapAngles[i] - angle);
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            return best;
        }
    }
}
