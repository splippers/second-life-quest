using TMPro;
using SLQuest.Core;
using SLQuest.Voice;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// Compact voice-chat control panel.
    ///
    /// Inspector wiring:
    ///   muteButton        — toggles microphone mute
    ///   muteLabel         — TMP_Text on the mute button ("Mute" / "Unmute")
    ///   statusLabel       — "Voice: Connected / Disconnected"
    ///   speakingIndicator — Image that pulses green when speaking (driven by Vivox)
    ///   volumeSlider      — Slider 0–1 controls output volume
    ///   closeButton       — hides panel
    /// </summary>
    public sealed class VoicePanel : MonoBehaviour
    {
        [SerializeField] private Button    muteButton;
        [SerializeField] private TMP_Text  muteLabel;
        [SerializeField] private TMP_Text  statusLabel;
        [SerializeField] private Image     speakingIndicator;
        [SerializeField] private Slider    volumeSlider;
        [SerializeField] private Button    closeButton;

        private VoiceManager _voice;
        private float        _pulseTimer;
        private static readonly Color SpeakingColor = new(0.2f, 1f, 0.3f, 1f);
        private static readonly Color SilentColor   = new(0.5f, 0.5f, 0.5f, 0.5f);

        private void Awake()
        {
            _voice = SLApplication.Instance?.Voice ?? FindObjectOfType<VoiceManager>();

            muteButton?.onClick.AddListener(OnMute);
            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));
            volumeSlider?.onValueChanged.AddListener(OnVolume);
        }

        private void OnEnable()
        {
            if (_voice != null) _voice.OnMuteChanged += OnMuteChanged;
            Refresh();
        }

        private void OnDisable()
        {
            if (_voice != null) _voice.OnMuteChanged -= OnMuteChanged;
        }

        private void Update()
        {
            if (speakingIndicator == null || _voice == null) return;

            // Vivox provides speaking state via IsSpeaking; pulse the indicator
#if UNITY_VIVOX
            bool speaking = !_voice.IsMuted && VivoxService.Instance?.IsLoggedIn == true
                         && VivoxService.Instance.IsSpeaking;
#else
            bool speaking = false;
#endif
            if (speaking)
            {
                _pulseTimer += Time.deltaTime * 8f;
                float a = (Mathf.Sin(_pulseTimer) + 1f) * 0.5f;
                speakingIndicator.color = Color.Lerp(SilentColor, SpeakingColor, a);
            }
            else
            {
                speakingIndicator.color = SilentColor;
                _pulseTimer = 0f;
            }
        }

        // ── Handlers ──────────────────────────────────────────────────────────

        private void OnMute()
        {
            _voice?.ToggleMute();
        }

        private void OnMuteChanged(bool muted)
        {
            if (muteLabel != null) muteLabel.text = muted ? "Unmute" : "Mute";
        }

        private void OnVolume(float value)
        {
#if UNITY_VIVOX
            VivoxService.Instance?.SetOutputVolume((int)(value * 100));
#endif
        }

        private void Refresh()
        {
            if (_voice == null) return;
            if (muteLabel != null) muteLabel.text = _voice.IsMuted ? "Unmute" : "Mute";
            if (statusLabel != null)
                statusLabel.text = _voice.IsConnected ? "Voice: Connected" : "Voice: Not connected";
            if (volumeSlider != null) volumeSlider.value = 0.7f;
        }
    }
}
