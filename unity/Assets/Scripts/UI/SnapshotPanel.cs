using System;
using System.Collections;
using System.IO;
using TMPro;
using SLQuest.Core;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// VR panel for capturing and saving in-world screenshots.
    ///
    /// Captures the full rendered frame (including the HMD view) as a PNG and
    /// saves it to the device's persistent data path. On Quest 3 this appears in
    /// /sdcard/Android/data/&lt;package&gt;/files/.
    ///
    /// Inspector wiring:
    ///   previewImage    — RawImage showing the last captured frame
    ///   captureButton   — triggers a new snapshot
    ///   saveButton      — saves the last capture to disk (enabled after capture)
    ///   filenameInput   — TMP_InputField for an optional filename prefix
    ///   statusLabel     — TMP_Text for status / saved-path messages
    ///   closeButton     — hides the panel
    ///   hideHudToggle   — Toggle; when on, hides all UI panels during capture
    /// </summary>
    public sealed class SnapshotPanel : MonoBehaviour
    {
        [Header("Preview")]
        [SerializeField] private RawImage previewImage;

        [Header("Controls")]
        [SerializeField] private Button         captureButton;
        [SerializeField] private Button         saveButton;
        [SerializeField] private TMP_InputField filenameInput;
        [SerializeField] private Toggle         hideHudToggle;
        [SerializeField] private TMP_Text       statusLabel;
        [SerializeField] private Button         closeButton;

        private Texture2D _lastCapture;

        private void Awake()
        {
            captureButton?.onClick.AddListener(() => StartCoroutine(CaptureRoutine()));
            saveButton?.onClick.AddListener(SaveCapture);
            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));

            if (saveButton != null) saveButton.interactable = false;
        }

        // ── Capture ───────────────────────────────────────────────────────────

        private IEnumerator CaptureRoutine()
        {
            SetStatus("Capturing…");
            captureButton.interactable = false;

            bool hideHud = hideHudToggle != null && hideHudToggle.isOn;
            if (hideHud) gameObject.SetActive(false); // hide this panel too

            // Wait two frames so UI hide takes effect before we read pixels
            yield return null;
            yield return null;

            // ScreenCapture.CaptureScreenshotAsTexture captures the current rendered frame
            _lastCapture = ScreenCapture.CaptureScreenshotAsTexture();

            if (hideHud) gameObject.SetActive(true);

            if (_lastCapture != null)
            {
                if (previewImage != null)
                {
                    previewImage.texture = _lastCapture;
                    float aspect = (float)_lastCapture.width / _lastCapture.height;
                    var rt = previewImage.rectTransform;
                    rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,
                        rt.rect.height * aspect);
                }

                if (saveButton != null) saveButton.interactable = true;
                SetStatus("Captured. Tap Save to write to disk.");
            }
            else
            {
                SetStatus("Capture failed.");
            }

            captureButton.interactable = true;
        }

        // ── Save ──────────────────────────────────────────────────────────────

        private void SaveCapture()
        {
            if (_lastCapture == null) { SetStatus("Nothing captured yet."); return; }

            string prefix = filenameInput != null && !string.IsNullOrWhiteSpace(filenameInput.text)
                ? filenameInput.text.Trim()
                : "SLSnapshot";

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string filename  = $"{prefix}_{timestamp}.png";
            string path      = Path.Combine(Application.persistentDataPath, filename);

            try
            {
                byte[] png = _lastCapture.EncodeToPNG();
                File.WriteAllBytes(path, png);
                SetStatus($"Saved: {filename}");

#if UNITY_ANDROID && !UNITY_EDITOR
                // Notify Android media scanner so it appears in the gallery
                using var mediaScannerConn = new AndroidJavaClass("android.media.MediaScannerConnection");
                var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity    = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                mediaScannerConn.CallStatic("scanFile", activity, new[] { path }, null, null);
#endif
            }
            catch (Exception ex)
            {
                SetStatus($"Save failed: {ex.Message}");
                Debug.LogException(ex);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SetStatus(string text)
        {
            if (statusLabel != null)
            {
                statusLabel.text = text;
                statusLabel.gameObject.SetActive(!string.IsNullOrEmpty(text));
            }
        }

        private void OnDestroy()
        {
            if (_lastCapture != null)
                Destroy(_lastCapture);
        }
    }
}
