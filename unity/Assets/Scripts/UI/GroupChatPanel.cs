using System.Collections.Generic;
using OpenMetaverse;
using TMPro;
using SLQuest.Chat;
using SLQuest.Core;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space group chat panel.
    ///
    /// Shows a dropdown of joined group sessions on the left, scrollable
    /// message history in the center, and a send field at the bottom.
    ///
    /// Inspector wiring:
    ///   sessionDropdown   — TMP_Dropdown listing active group sessions
    ///   messageContainer  — ScrollRect Content Transform
    ///   messageRowPrefab  — TMP_Text row
    ///   scrollRect        — ScrollRect for auto-scroll
    ///   inputField        — TMP_InputField
    ///   sendButton
    ///   leaveButton       — leaves the currently selected group chat
    ///   statusLabel
    ///   closeButton
    /// </summary>
    public sealed class GroupChatPanel : MonoBehaviour
    {
        [Header("Session selector")]
        [SerializeField] private TMP_Dropdown sessionDropdown;

        [Header("Messages")]
        [SerializeField] private Transform    messageContainer;
        [SerializeField] private GameObject   messageRowPrefab;
        [SerializeField] private ScrollRect   scrollRect;

        [Header("Input")]
        [SerializeField] private TMP_InputField inputField;
        [SerializeField] private Button         sendButton;
        [SerializeField] private Button         leaveButton;

        [Header("Controls")]
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private Button   closeButton;

        private GroupChatManager _groupChats;
        private UUID             _activeGroupId;

        // groupId → message list
        private readonly Dictionary<UUID, List<string>> _history = new();
        private readonly List<GameObject> _rows = new();

        private void Awake()
        {
            _groupChats = SLApplication.Instance?.GroupChats ?? FindObjectOfType<GroupChatManager>();

            sendButton?.onClick.AddListener(OnSend);
            leaveButton?.onClick.AddListener(OnLeave);
            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));
            inputField?.onSubmit.AddListener(_ => OnSend());
            sessionDropdown?.onValueChanged.AddListener(OnSessionChanged);
        }

        private void OnEnable()
        {
            EventBus.Subscribe<GroupChatMessageEvent>(OnGroupMessage);
            if (_groupChats != null) _groupChats.OnGroupListChanged += RefreshDropdown;
            RefreshDropdown();
        }

        private void OnDisable()
        {
            EventBus.Unsubscribe<GroupChatMessageEvent>(OnGroupMessage);
            if (_groupChats != null) _groupChats.OnGroupListChanged -= RefreshDropdown;
        }

        // ── Group session selector ────────────────────────────────────────────

        private void RefreshDropdown()
        {
            if (sessionDropdown == null || _groupChats == null) return;

            sessionDropdown.ClearOptions();
            var options = new List<TMP_Dropdown.OptionData>();
            var groups  = _groupChats.Groups;

            int selectIdx = 0; int i = 0;
            foreach (var kvp in groups)
            {
                options.Add(new TMP_Dropdown.OptionData(kvp.Value));
                if (kvp.Key == _activeGroupId) selectIdx = i;
                i++;
            }

            sessionDropdown.AddOptions(options);
            if (options.Count > 0)
            {
                sessionDropdown.SetValueWithoutNotify(selectIdx);
                SelectGroupAt(selectIdx);
            }
            else
            {
                SetStatus("No active group sessions.");
            }
        }

        private void OnSessionChanged(int idx)
        {
            SelectGroupAt(idx);
        }

        private void SelectGroupAt(int idx)
        {
            var groups = _groupChats?.Groups;
            if (groups == null || idx < 0 || idx >= groups.Count) return;

            int i = 0;
            foreach (var kvp in groups)
            {
                if (i == idx) { _activeGroupId = kvp.Key; break; }
                i++;
            }

            RebuildHistory();
        }

        // ── Message handling ──────────────────────────────────────────────────

        private void OnGroupMessage(GroupChatMessageEvent e)
        {
            if (!_history.TryGetValue(e.GroupId, out var list))
            {
                list = new List<string>();
                _history[e.GroupId] = list;
                RefreshDropdown();
            }

            string line = $"[{e.GroupName}] {e.FromName}: {e.Message}";
            list.Add(line);

            if (e.GroupId == _activeGroupId)
                AppendRow(line);
        }

        private void RebuildHistory()
        {
            ClearRows();
            if (_history.TryGetValue(_activeGroupId, out var list))
                foreach (var line in list) AppendRow(line);
        }

        private void AppendRow(string text)
        {
            if (messageRowPrefab == null || messageContainer == null) return;
            var go  = Instantiate(messageRowPrefab, messageContainer);
            var lbl = go.GetComponent<TMP_Text>() ?? go.GetComponentInChildren<TMP_Text>();
            if (lbl != null) lbl.text = text;
            _rows.Add(go);
            ScrollToBottom();
        }

        private void ClearRows()
        {
            foreach (var r in _rows) Destroy(r);
            _rows.Clear();
        }

        private void ScrollToBottom()
        {
            if (scrollRect == null) return;
            Canvas.ForceUpdateCanvases();
            scrollRect.verticalNormalizedPosition = 0f;
        }

        // ── Actions ───────────────────────────────────────────────────────────

        private void OnSend()
        {
            if (inputField == null || _activeGroupId == UUID.Zero) return;
            string text = inputField.text.Trim();
            if (string.IsNullOrEmpty(text)) return;
            inputField.text = string.Empty;

            _groupChats?.SendGroupMessage(_activeGroupId, text);
        }

        private void OnLeave()
        {
            if (_activeGroupId == UUID.Zero) return;
            _groupChats?.LeaveGroupChat(_activeGroupId);
            _history.Remove(_activeGroupId);
            _activeGroupId = UUID.Zero;
            RefreshDropdown();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SetStatus(string text)
        {
            if (statusLabel == null) return;
            statusLabel.text = text;
            statusLabel.gameObject.SetActive(!string.IsNullOrEmpty(text));
        }
    }
}
