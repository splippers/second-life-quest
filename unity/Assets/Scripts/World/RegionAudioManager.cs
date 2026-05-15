using System.Collections;
using SLQuest.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace SLQuest.World
{
    /// <summary>
    /// Plays the parcel music stream (if any) using Unity's AudioSource + streaming
    /// AudioClip.  When <see cref="ParcelChangedEvent"/> fires, stops any current
    /// stream and starts the new one if <c>Parcel.MusicURL</c> is non-empty.
    ///
    /// Volume is exposed so SettingsPanel (or a separate audio slider) can control it.
    ///
    /// Inspector wiring:
    ///   audioSource — AudioSource set to loop=false, playOnAwake=false
    ///   defaultVolume — starting volume (0–1)
    /// </summary>
    public sealed class RegionAudioManager : MonoBehaviour
    {
        [SerializeField] private AudioSource audioSource;
        [SerializeField] [Range(0f, 1f)] private float defaultVolume = 0.5f;

        public float Volume
        {
            get => audioSource != null ? audioSource.volume : 0f;
            set { if (audioSource != null) audioSource.volume = value; }
        }

        public bool IsMuted { get; private set; }
        private string _currentUrl;
        private Coroutine _streamCoroutine;

        private void Awake()
        {
            if (audioSource != null) audioSource.volume = defaultVolume;
        }

        private void OnEnable()
        {
            EventBus.Subscribe<ParcelChangedEvent>(OnParcelChanged);
            EventBus.Subscribe<LoggedOutEvent>(OnLoggedOut);
        }

        private void OnDisable()
        {
            EventBus.Unsubscribe<ParcelChangedEvent>(OnParcelChanged);
            EventBus.Unsubscribe<LoggedOutEvent>(OnLoggedOut);
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private void OnParcelChanged(ParcelChangedEvent e)
        {
            string url = e.Parcel.MusicURL;
            if (url == _currentUrl) return;
            _currentUrl = url;

            StopStream();
            if (!string.IsNullOrWhiteSpace(url) && !IsMuted)
                _streamCoroutine = StartCoroutine(StreamAudio(url));
        }

        private void OnLoggedOut(LoggedOutEvent _) => StopStream();

        // ── Public controls ───────────────────────────────────────────────────

        public void SetMuted(bool muted)
        {
            IsMuted = muted;
            if (muted)
                StopStream();
            else if (!string.IsNullOrWhiteSpace(_currentUrl))
                _streamCoroutine = StartCoroutine(StreamAudio(_currentUrl));
        }

        // ── Streaming ─────────────────────────────────────────────────────────

        private IEnumerator StreamAudio(string url)
        {
            if (audioSource == null) yield break;

            using var req = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.UNKNOWN);
            ((DownloadHandlerAudioClip)req.downloadHandler).streamAudio = true;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Audio] Stream error for {url}: {req.error}");
                yield break;
            }

            var clip = DownloadHandlerAudioClip.GetContent(req);
            if (clip == null) yield break;

            audioSource.clip   = clip;
            audioSource.loop   = true;
            audioSource.Play();
        }

        private void StopStream()
        {
            if (_streamCoroutine != null)
            {
                StopCoroutine(_streamCoroutine);
                _streamCoroutine = null;
            }
            if (audioSource != null && audioSource.isPlaying)
            {
                audioSource.Stop();
                audioSource.clip = null;
            }
        }
    }
}
