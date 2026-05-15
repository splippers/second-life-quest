using System;
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
    /// Tabbed chat panel supporting Local chat, Group sessions, and Direct IMs.
    ///
    /// Tab layout (assigned in Inspector):
    ///   localTabButton / groupsTabButton / imsTabButton  — toggle which panel is visible
    ///   localPanel / groupsPanel / imsPanel              — the three content areas
    ///
    /// Local panel: scrollable history + input field (unchanged from v1).
    /// Groups panel: left list of joined groups + right message feed + input.
    /// IMs panel: incoming DMs aggregated (send IM not yet implemented — phase 2).
    /// </summary>
    public sealed class ChatPanel : MonoBehaviour
    {
        // ── Local tab ────────────────────────────────────────────────────────
        [Header("Local tab")]
        [SerializeField] private ScrollRect     localScroll;
        [SerializeField] private Transform      localContent;
        [SerializeField] private TMP_Text       messageRowPrefab;
        [SerializeField] private TMP_InputField localInput;
        [SerializeField] private Button         localSend;

        // ── Groups tab ───────────────────────────────────────────────────────
        [Header("Groups tab")]
        [SerializeField] private ScrollRect     groupFeedScroll;
        [SerializeField] private Transform      groupFeedContent;
        [SerializeField] private TMP_Dropdown   groupSelector;
        [SerializeField] private TMP_InputField groupInput;
        [SerializeField] private Button         groupSend;
        [SerializeField] private Button         groupJoinButton;

        // ── IMs tab ──────────────────────────────────────────────────────────
        [Header("IMs tab")]
        [SerializeField] private ScrollRect     imScroll;
        [SerializeField] private Transform      imContent;

        // ── Tab buttons ──────────────────────────────────────────────────────
        [Header("Tabs")]
        [SerializeField] private Button         localTabButton;
        [SerializeField] private Button         groupsTabButton;
        [SerializeField] private Button         imsTabButton;
        [SerializeField] private GameObject     localPanel;
        [SerializeField] private GameObject     groupsPanel;
        [SerializeField] private GameObject     imsPanel;

        // ── Colours ──────────────────────────────────────────────────────────
        [Header("Colours")]
        [SerializeField] private Color localColour   = Color.white;
        [SerializeField] private Color imColour      = new(0.7f, 0.9f, 1f);
        [SerializeField] private Color groupColour   = new(0.7f, 1f, 0.75f);
        [SerializeField] private Color systemColour  = new(0.8f, 0.8f, 0.4f);

        [SerializeField] private int maxRows = 200;

        // ── Internal state ───────────────────────────────────────────────────

        private ChatManager      _chat;
        private GroupChatManager _groups;

        private readonly Queue<TMP_Text> _localRows  = new();
        private readonly Queue<TMP_Text> _groupRows  = new();
        private readonly Queue<TMP_Text> _imRows     = new();

        // Dropdown UUID list matches dropdown index
        private readonly List<UUID> _groupIds = new();

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            _chat   = SLApplication.Instance?.Chat       ?? FindObjectOfType<ChatManager>();
            _groups = SLApplication.Instance?.GroupChats ?? FindObjectOfType<GroupChatManager>();

            localSend?.onClick.AddListener(OnLocalSend);
            localInput?.onEndEdit.AddListener(_ =>
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    OnLocalSend();
            });

            groupSend?.onClick.AddListener(OnGroupSend);
            groupJoinButton?.onClick.AddListener(OnGroupJoin);

            localTabButton?.onClick.AddListener(() => SetTab(0));
            groupsTabButton?.onClick.AddListener(() => SetTab(1));
            imsTabButton?.onClick.AddListener(() => SetTab(2));

            SetTab(0);
        }

        private void Start()
        {
            if (_chat != null)
            {
                foreach (var e in _chat.History)
                    RouteToPanel(e);
                _chat.OnNewEntry += RouteToPanel;
            }

            if (_groups != null)
            {
                _groups.OnGroupListChanged += RefreshGroupDropdown;
                EventBus.Subscribe<GroupChatMessageEvent>(OnGroupChatMessage);
                RefreshGroupDropdown();
            }
        }

        private void OnDestroy()
        {
            if (_chat != null)   _chat.OnNewEntry -= RouteToPanel;
            if (_groups != null) _groups.OnGroupListChanged -= RefreshGroupDropdown;
            EventBus.Unsubscribe<GroupChatMessageEvent>(OnGroupChatMessage);
        }

        // ── Tab switching ─────────────────────────────────────────────────────

        private void SetTab(int index)
        {
            if (localPanel)  localPanel.SetActive(index == 0);
            if (groupsPanel) groupsPanel.SetActive(index == 1);
            if (imsPanel)    imsPanel.SetActive(index == 2);
        }

        // ── Message routing ───────────────────────────────────────────────────

        private void RouteToPanel(ChatEntry entry)
        {
            switch (entry.Source)
            {
                case ChatSource.IM:
                    AppendRow(entry, _imRows, imContent, imScroll);
                    break;
                case ChatSource.GroupIM:
                    // Group messages also go to local feed so nothing is missed
                    AppendRow(entry, _groupRows, groupFeedContent, groupFeedScroll);
                    AppendRow(entry, _localRows, localContent, localScroll);
                    break;
                default:
                    AppendRow(entry, _localRows, localContent, localScroll);
                    break;
            }
        }

        private void AppendRow(ChatEntry entry, Queue<TMP_Text> pool, Transform content, ScrollRect scroll)
        {
            if (messageRowPrefab == null || content == null) return;

            var row = Instantiate(messageRowPrefab, content);
            row.text  = entry.ToString();
            row.color = entry.Source switch
            {
                ChatSource.IM      => imColour,
                ChatSource.GroupIM => groupColour,
                ChatSource.System  => systemColour,
                _                  => localColour
            };

            pool.Enqueue(row);
            if (pool.Count > maxRows)
                Destroy(pool.Dequeue().gameObject);

            if (scroll != null)
            {
                Canvas.ForceUpdateCanvases();
                scroll.verticalNormalizedPosition = 0f;
            }
        }

        // ── Group tab UI ──────────────────────────────────────────────────────

        private void RefreshGroupDropdown()
        {
            if (groupSelector == null || _groups == null) return;

            groupSelector.ClearOptions();
            _groupIds.Clear();

            var options = new List<TMP_Dropdown.OptionData>();
            foreach (var kvp in _groups.Groups)
            {
                _groupIds.Add(kvp.Key);
                options.Add(new TMP_Dropdown.OptionData(kvp.Value));
            }
            groupSelector.AddOptions(options);
        }

        private void OnGroupChatMessage(GroupChatMessageEvent e)
        {
            // Feed is already populated via RouteToPanel (InjectEntry → OnNewEntry)
        }

        private void OnGroupJoin()
        {
            if (_groups == null || groupSelector == null) return;
            int idx = groupSelector.value;
            if (idx < 0 || idx >= _groupIds.Count) return;
            _groups.JoinGroupChat(_groupIds[idx]);
        }

        private void OnGroupSend()
        {
            if (_groups == null || groupInput == null || groupSelector == null) return;
            string text = groupInput.text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            int idx = groupSelector.value;
            if (idx < 0 || idx >= _groupIds.Count) return;

            _groups.SendGroupMessage(_groupIds[idx], text);
            groupInput.text = string.Empty;
        }

        // ── Local send ────────────────────────────────────────────────────────

        private void OnLocalSend()
        {
            if (localInput == null) return;
            string text = localInput.text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (text.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
                _chat?.Say(text[4..], 0, OpenMetaverse.ChatType.Emote);
            else if (text.StartsWith("/shout ", StringComparison.OrdinalIgnoreCase))
                _chat?.Say(text[7..], 0, OpenMetaverse.ChatType.Shout);
            else if (text.StartsWith("/whisper ", StringComparison.OrdinalIgnoreCase))
                _chat?.Say(text[9..], 0, OpenMetaverse.ChatType.Whisper);
            else
                _chat?.Say(text);

            localInput.text = string.Empty;
        }
    }
}
