using System.Collections.Generic;
using OpenMetaverse;
using TMPro;
using SLQuest.Core;
using SLQuest.Scripting;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space VR panel that lists the agent's active gestures.
    ///
    /// Inspector wiring required:
    ///   scrollRect      — the ScrollRect for the gesture list
    ///   listContent     — the Content Transform inside the ScrollRect
    ///   rowPrefab       — a GameObject with GestureRow child components (see below)
    ///   refreshButton   — calls LoadActiveGestures and rebuilds list
    ///   closeButton     — hides the panel
    ///   emptyLabel      — TMP_Text shown when there are no gestures
    ///
    /// Each row prefab needs four named children (found by GetComponent / find):
    ///   [Name]     TMP_Text  — gesture display name
    ///   [Trigger]  TMP_Text  — trigger phrase or F-key label
    ///   [Play]     Button    — plays the gesture immediately
    ///   [Active]   Toggle    — enables/disables the gesture slot
    /// </summary>
    public sealed class GesturePanel : MonoBehaviour
    {
        [Header("List")]
        [SerializeField] private ScrollRect scrollRect;
        [SerializeField] private Transform  listContent;
        [SerializeField] private GameObject rowPrefab;

        [Header("Controls")]
        [SerializeField] private Button   refreshButton;
        [SerializeField] private Button   closeButton;
        [SerializeField] private TMP_Text emptyLabel;

        private GestureManager _gestures;
        private readonly List<GameObject> _rows = new();

        private void Awake()
        {
            _gestures = SLApplication.Instance?.Gestures ?? FindObjectOfType<GestureManager>();

            refreshButton?.onClick.AddListener(OnRefresh);
            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));
        }

        private void OnEnable()
        {
            if (_gestures != null)
            {
                _gestures.OnGestureListChanged += Rebuild;
                Rebuild();
            }
        }

        private void OnDisable()
        {
            if (_gestures != null)
                _gestures.OnGestureListChanged -= Rebuild;
        }

        private void OnRefresh()
        {
            _gestures?.LoadActiveGestures();
        }

        // ── List rebuild ──────────────────────────────────────────────────────

        private void Rebuild()
        {
            foreach (var row in _rows)
                Destroy(row);
            _rows.Clear();

            if (_gestures == null) return;

            bool any = false;
            foreach (var ag in _gestures.Gestures.Values)
            {
                any = true;
                SpawnRow(ag);
            }

            if (emptyLabel != null)
                emptyLabel.gameObject.SetActive(!any);

            if (scrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                scrollRect.verticalNormalizedPosition = 1f;
            }
        }

        private void SpawnRow(ActiveGesture ag)
        {
            if (rowPrefab == null || listContent == null) return;

            var go = Instantiate(rowPrefab, listContent);
            _rows.Add(go);

            // Name label
            var nameLabel = go.transform.Find("Name")?.GetComponent<TMP_Text>();
            if (nameLabel != null)
                nameLabel.text = ag.Name;

            // Trigger label — show key or phrase
            var triggerLabel = go.transform.Find("Trigger")?.GetComponent<TMP_Text>();
            if (triggerLabel != null)
                triggerLabel.text = TriggerDescription(ag);

            // Active toggle
            var activeToggle = go.transform.Find("Active")?.GetComponent<Toggle>();
            if (activeToggle != null)
            {
                activeToggle.isOn = ag.IsActive;
                activeToggle.onValueChanged.AddListener(on => ag.IsActive = on);
            }

            // Play button
            var playButton = go.transform.Find("Play")?.GetComponent<Button>();
            if (playButton != null)
            {
                UUID itemId = ag.ItemId;
                bool hasAsset = ag.Asset != null;
                playButton.interactable = hasAsset;
                playButton.onClick.AddListener(() => _gestures?.PlayGesture(itemId));
            }
        }

        private static string TriggerDescription(ActiveGesture ag)
        {
            if (ag.Asset == null) return "(loading…)";

            string phrase = ag.TriggerPhrase;
            short  vk     = ag.VirtualKey;
            uint   mask   = ag.KeyMask;

            if (!string.IsNullOrEmpty(phrase))
                return phrase;

            if (vk > 0)
            {
                string modifiers = BuildModLabel(mask);
                string key = vk switch
                {
                    0x70 => "F1",  0x71 => "F2",  0x72 => "F3",
                    0x73 => "F4",  0x74 => "F5",  0x75 => "F6",
                    0x76 => "F7",  0x77 => "F8",  0x78 => "F9",
                    0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
                    _ => $"0x{vk:X2}"
                };
                return string.IsNullOrEmpty(modifiers) ? key : $"{modifiers}+{key}";
            }

            return "(no trigger)";
        }

        private static string BuildModLabel(uint mask)
        {
            var parts = new System.Text.StringBuilder();
            if ((mask & 0x02) != 0) parts.Append("Ctrl+");
            if ((mask & 0x01) != 0) parts.Append("Shift+");
            if ((mask & 0x04) != 0) parts.Append("Alt+");
            string s = parts.ToString();
            return s.Length > 0 ? s.TrimEnd('+') : string.Empty;
        }
    }
}
