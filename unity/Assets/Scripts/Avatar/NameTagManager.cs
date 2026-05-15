using System.Collections.Generic;
using OpenMetaverse;
using TMPro;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;

namespace SLQuest.Avatar
{
    /// <summary>
    /// Maintains world-space billboard name tags above remote avatars.
    ///
    /// Subscribes to AvatarUpdateEvent to learn about avatars; resolves their
    /// display names asynchronously via Client.Avatars.RequestAvatarName.
    /// Tags billboard-face the main camera each LateUpdate.
    ///
    /// Inspector wiring:
    ///   tagPrefab     — a world-space Canvas (Screen Space Overlay OFF) with a
    ///                    TMP_Text child named "Label" and an optional background Image
    ///   tagYOffset    — metres above avatar root (default 2.2 m)
    ///   visible       — show/hide all tags (toggled by SettingsPanel)
    /// </summary>
    public sealed class NameTagManager : MonoBehaviour
    {
        [SerializeField] private GameObject tagPrefab;
        [SerializeField] private float      tagYOffset = 2.2f;
        [SerializeField] private bool       visible    = true;

        private SLNetworkManager _net;

        private sealed class TagEntry
        {
            public GameObject Go;
            public TMP_Text   Label;
            public Vector3    WorldPos;  // last known avatar position
            public bool       NameResolved;
        }

        private readonly Dictionary<UUID, TagEntry> _tags = new();

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
        }

        private void Start()
        {
            EventBus.Subscribe<AvatarUpdateEvent>(OnAvatarUpdate);
            EventBus.Subscribe<LoggedOutEvent>(OnLoggedOut);
        }

        private void OnDestroy()
        {
            EventBus.Unsubscribe<AvatarUpdateEvent>(OnAvatarUpdate);
            EventBus.Unsubscribe<LoggedOutEvent>(OnLoggedOut);
        }

        // ── Runtime toggle (SettingsPanel) ────────────────────────────────────

        public bool Visible
        {
            get => visible;
            set
            {
                visible = value;
                foreach (var e in _tags.Values)
                    if (e.Go != null) e.Go.SetActive(value);
            }
        }

        // ── AvatarUpdate handling ─────────────────────────────────────────────

        private void OnAvatarUpdate(AvatarUpdateEvent evt)
        {
            // Skip self
            if (_net?.Client?.Self?.AgentID == (UUID)evt.Id) return;

            var id = (UUID)evt.Id;

            if (!_tags.TryGetValue(id, out var entry))
            {
                entry = SpawnTag(id);
                _tags[id] = entry;
            }

            entry.WorldPos = evt.Position;
        }

        private TagEntry SpawnTag(UUID id)
        {
            var entry = new TagEntry();

            if (tagPrefab != null)
            {
                entry.Go    = Instantiate(tagPrefab);
                entry.Label = entry.Go.GetComponentInChildren<TMP_Text>();
                if (entry.Label != null) entry.Label.text = "…";
                entry.Go.SetActive(visible);
            }

            // Kick off async name resolution
            _net?.Client?.Avatars?.RequestAvatarName(id, (resolvedId, names) =>
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (!_tags.TryGetValue(resolvedId, out var e)) return;
                    if (names.TryGetValue(resolvedId, out string name) && e.Label != null)
                    {
                        e.Label.text     = name;
                        e.NameResolved   = true;
                    }
                });
            });

            return entry;
        }

        private void OnLoggedOut(LoggedOutEvent _) => ClearAll();

        // ── Billboard update ──────────────────────────────────────────────────

        private void LateUpdate()
        {
            var cam = Camera.main;

            foreach (var kvp in _tags)
            {
                var e = kvp.Value;
                if (e.Go == null) continue;

                // Position above avatar
                e.Go.transform.position = e.WorldPos + Vector3.up * tagYOffset;

                // Face camera (Y axis only to avoid tilting)
                if (cam != null)
                {
                    Vector3 dir = cam.transform.position - e.Go.transform.position;
                    dir.y = 0f;
                    if (dir.sqrMagnitude > 0.001f)
                        e.Go.transform.rotation = Quaternion.LookRotation(-dir);
                }
            }
        }

        // ── Cleanup ───────────────────────────────────────────────────────────

        private void ClearAll()
        {
            foreach (var e in _tags.Values)
                if (e.Go != null) Destroy(e.Go);
            _tags.Clear();
        }
    }
}
