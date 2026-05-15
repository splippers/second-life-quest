using System;
using System.Collections;
using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;

namespace SLQuest.World
{
    /// <summary>
    /// Handles in-world media surfaces on a single prim face.
    ///
    /// Workflow:
    ///   1. SLPrimitive detects MediaFlags on a face and adds MediaSurface component.
    ///   2. MediaSurface calls the ObjectMedia cap to resolve the media URL.
    ///   3. For video/* MIME types: a VideoPlayer + RenderTexture streams into the face material.
    ///   4. For other types: a placeholder texture is shown; future work = in-headset browser.
    ///
    /// ObjectMedia GET format: { "PrimID": uuid, "FaceMedia": [...per face...] }
    /// Uses the ObjectMedia cap; falls back silently if the cap is unavailable.
    /// </summary>
    [RequireComponent(typeof(SLPrimitive))]
    public sealed class MediaSurface : MonoBehaviour
    {
        [Tooltip("Which face index on the prim has media.")]
        public int FaceIndex;

        private SLPrimitive    _prim;
        private CapabilityHandler _caps;
        private Renderer       _renderer;

        private VideoPlayer    _videoPlayer;
        private RenderTexture  _renderTexture;
        private string         _currentUrl;

        // Resolution for the render texture
        private const int RT_WIDTH  = 1024;
        private const int RT_HEIGHT = 512;

        private void Awake()
        {
            _prim     = GetComponent<SLPrimitive>();
            _renderer = GetComponent<Renderer>();
            _caps     = SLApplication.Instance?.Caps ?? FindObjectOfType<CapabilityHandler>();
        }

        private void Start()
        {
            EventBus.Subscribe<MediaNavigateEvent>(OnNavigate);
            StartCoroutine(FetchMediaInfo());
        }

        private void OnDestroy()
        {
            EventBus.Unsubscribe<MediaNavigateEvent>(OnNavigate);
            if (_videoPlayer != null) Destroy(_videoPlayer);
            if (_renderTexture != null) _renderTexture.Release();
        }

        // ── Cap fetch ─────────────────────────────────────────────────────────

        private IEnumerator FetchMediaInfo()
        {
            if (_caps == null || !_caps.TryGetCap("ObjectMedia", out var capUri))
                yield break;

            // GET with JSON body listing the prim UUID
            string body = $"{{\"PrimID\":\"{_prim.FullId}\"}}";
            using var req = new UnityWebRequest(capUri + "?object-id=" + _prim.FullId, "GET");
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/llsd+json");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Media] ObjectMedia fetch failed for {_prim.FullId}: {req.error}");
                yield break;
            }

            ParseAndPlay(req.downloadHandler.text);
        }

        private void ParseAndPlay(string json)
        {
            try
            {
                var osd = OSDParser.DeserializeJson(json);
                if (osd is not OSDMap root) return;

                OSDArray faceMedia = null;
                if (root.ContainsKey("FaceMedia") && root["FaceMedia"] is OSDArray arr)
                    faceMedia = arr;
                else if (root.ContainsKey("ObjectMediaData") &&
                         root["ObjectMediaData"] is OSDMap mediaData &&
                         mediaData.ContainsKey("FaceMedia") &&
                         mediaData["FaceMedia"] is OSDArray arr2)
                    faceMedia = arr2;

                if (faceMedia == null || FaceIndex >= faceMedia.Count) return;

                if (faceMedia[FaceIndex] is not OSDMap face) return;

                string url      = face.ContainsKey("current_url") ? face["current_url"].AsString() : string.Empty;
                string homeUrl  = face.ContainsKey("home_url")    ? face["home_url"].AsString()    : string.Empty;
                string mimeType = face.ContainsKey("media_type")  ? face["media_type"].AsString()  : string.Empty;
                bool   autoPlay = face.ContainsKey("auto_play")   && face["auto_play"].AsBoolean();

                string resolvedUrl = !string.IsNullOrEmpty(url) ? url : homeUrl;
                if (string.IsNullOrEmpty(resolvedUrl)) return;

                _currentUrl = resolvedUrl;

                if (autoPlay)
                    StartMedia(resolvedUrl, mimeType);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        // ── Playback ──────────────────────────────────────────────────────────

        private void StartMedia(string url, string mimeType)
        {
            if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
                url.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                StartVideoPlayback(url);
            }
            else
            {
                // Non-video (HTML, image) — apply a placeholder tint
                SetFaceColor(new Color(0.2f, 0.2f, 0.8f, 1f));
                Debug.Log($"[Media] Non-video media on face {FaceIndex}: {url}");
            }
        }

        private void StartVideoPlayback(string url)
        {
            _renderTexture = new RenderTexture(RT_WIDTH, RT_HEIGHT, 0, RenderTextureFormat.ARGB32);
            _renderTexture.Create();

            _videoPlayer = gameObject.AddComponent<VideoPlayer>();
            _videoPlayer.source        = VideoSource.Url;
            _videoPlayer.url           = url;
            _videoPlayer.renderMode    = VideoRenderMode.RenderTexture;
            _videoPlayer.targetTexture = _renderTexture;
            _videoPlayer.isLooping     = true;
            _videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
            _videoPlayer.prepareCompleted += OnVideoPrepared;
            _videoPlayer.Prepare();

            SetFaceTexture(_renderTexture);
        }

        private void OnVideoPrepared(VideoPlayer vp)
        {
            vp.Play();
        }

        // ── Material helpers ──────────────────────────────────────────────────

        private void SetFaceTexture(Texture tex)
        {
            if (_renderer == null || FaceIndex >= _renderer.materials.Length) return;
            var mats = _renderer.materials;
            mats[FaceIndex].mainTexture = tex;
            _renderer.materials = mats;
        }

        private void SetFaceColor(Color color)
        {
            if (_renderer == null || FaceIndex >= _renderer.materials.Length) return;
            var mats = _renderer.materials;
            mats[FaceIndex].color = color;
            _renderer.materials = mats;
        }

        // ── Navigation ────────────────────────────────────────────────────────

        private void OnNavigate(MediaNavigateEvent e)
        {
            if (e.PrimLocalId != _prim.LocalId || e.FaceIndex != FaceIndex) return;
            Navigate(e.Url);
        }

        public void Navigate(string url)
        {
            if (_videoPlayer != null)
            {
                _videoPlayer.Stop();
                _videoPlayer.url = url;
                _videoPlayer.Prepare();
            }
            _currentUrl = url;
        }

        public void Stop()
        {
            _videoPlayer?.Stop();
        }
    }
}
