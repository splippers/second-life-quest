using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using TMPro;
using SLQuest.Core;
using SLQuest.Social;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space VR friends list panel.
    ///
    /// Shows online friends first (green dot), then offline (grey dot).
    /// Optional search filter narrows by name.
    ///
    /// Inspector wiring:
    ///   friendsContent   — Content Transform in the friends ScrollRect
    ///   friendRowPrefab  — row with: OnlineIndicator (Image), FriendName (TMP_Text),
    ///                       IMButton (Button), TPButton (Button), RemoveButton (Button)
    ///   searchInput      — TMP_InputField for name filter
    ///   onlineCountLabel — shows "N online"
    ///   closeButton      — hides the panel
    /// </summary>
    public sealed class FriendsPanel : MonoBehaviour
    {
        [Header("List")]
        [SerializeField] private ScrollRect  friendsScroll;
        [SerializeField] private Transform   friendsContent;
        [SerializeField] private GameObject  friendRowPrefab;

        [Header("Controls")]
        [SerializeField] private TMP_InputField searchInput;
        [SerializeField] private TMP_Text       onlineCountLabel;
        [SerializeField] private Button         closeButton;

        private FriendManager          _friends;
        private readonly List<GameObject> _rows = new();

        private void Awake()
        {
            _friends = SLApplication.Instance?.Friends ?? FindObjectOfType<FriendManager>();

            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));
            searchInput?.onValueChanged.AddListener(_ => Rebuild());
        }

        private void OnEnable()
        {
            if (_friends != null)
            {
                _friends.OnFriendListChanged += Rebuild;
                Rebuild();
            }
        }

        private void OnDisable()
        {
            if (_friends != null)
                _friends.OnFriendListChanged -= Rebuild;
        }

        private void Rebuild()
        {
            foreach (var r in _rows) Destroy(r);
            _rows.Clear();

            if (_friends == null) return;

            string filter = searchInput?.text.Trim().ToLowerInvariant() ?? string.Empty;

            var sorted = _friends.Friends.Values
                .Where(f => string.IsNullOrEmpty(filter) ||
                            (f.Name?.ToLowerInvariant().Contains(filter) == true))
                .OrderByDescending(f => f.IsOnline)
                .ThenBy(f => f.Name);

            int onlineCount = 0;
            foreach (var entry in sorted)
            {
                SpawnRow(entry);
                if (entry.IsOnline) onlineCount++;
            }

            if (onlineCountLabel != null)
                onlineCountLabel.text = $"{onlineCount} online";

            if (friendsScroll != null)
            {
                Canvas.ForceUpdateCanvases();
                friendsScroll.verticalNormalizedPosition = 1f;
            }
        }

        private void SpawnRow(FriendEntry entry)
        {
            if (friendRowPrefab == null || friendsContent == null) return;

            var go = Instantiate(friendRowPrefab, friendsContent);
            _rows.Add(go);

            // Online indicator
            var dot = go.transform.Find("OnlineIndicator")?.GetComponent<Image>();
            if (dot != null)
                dot.color = entry.IsOnline ? new Color(0.3f, 1f, 0.4f) : new Color(0.4f, 0.4f, 0.4f);

            // Name
            var nameLabel = go.transform.Find("FriendName")?.GetComponent<TMP_Text>();
            if (nameLabel != null)
                nameLabel.text = entry.Name ?? entry.Id.ToString()[..8];

            UUID id = entry.Id;

            // IM
            var imBtn = go.transform.Find("IMButton")?.GetComponent<Button>();
            if (imBtn != null)
                imBtn.onClick.AddListener(() => _friends?.SendIM(id, string.Empty));

            // TP offer
            var tpBtn = go.transform.Find("TPButton")?.GetComponent<Button>();
            if (tpBtn != null)
            {
                tpBtn.interactable = entry.IsOnline;
                tpBtn.onClick.AddListener(() => _friends?.SendTeleportOffer(id));
            }

            // Remove
            var removeBtn = go.transform.Find("RemoveButton")?.GetComponent<Button>();
            if (removeBtn != null)
                removeBtn.onClick.AddListener(() =>
                {
                    _friends?.RemoveFriend(id);
                });
        }
    }
}
