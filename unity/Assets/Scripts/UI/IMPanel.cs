using System;
using System.Collections.Generic;
using OpenMetaverse;
using TMPro;
using SLQuest.Chat;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space instant messaging panel.
    ///
    /// Maintains a tabbed list of open conversations (by session ID).  Each
    /// conversation shows a scrollable message history and a send field.
    /// New IM arrivals open a tab automatically.
    ///
    /// Inspector wiring:
    ///   tabContainer       — HorizontalLayoutGroup Transform for tab buttons
    ///   tabButtonPrefab    — Button with TMP_Text "TabLabel"
    ///   messageContainer   — Content Transform inside conversation ScrollRect
    ///   messageRowPrefab   — TMP_Text row (left-align for others, right for self)
    ///   selfNamePrefab     — TMP_Text row tinted differently (own messages)
    ///   inputField         — TMP_InputField for composing messages
    ///   sendButton         — sends the current message
    ///   conversationScroll — ScrollRect to auto-scroll to bottom
    ///   closeButton        — hides panel
    ///   emptyLabel         — shown when no conversations are open
    /// </summary>
    public sealed class IMPanel : MonoBehaviour
    {
        [Header("Tabs")]
        [SerializeField] private Transform  tabContainer;
        [SerializeField] private GameObject tabButtonPrefab;

        [Header("Conversation")]
        [SerializeField] private Transform   messageContainer;
        [SerializeField] private GameObject  messageRowPrefab;
        [SerializeField] private GameObject  selfMessageRowPrefab;
        [SerializeField] private ScrollRect  conversationScroll;
        [SerializeField] private TMP_InputField inputField;
        [SerializeField] private Button      sendButton;

        [Header("Controls")]
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private Button   closeButton;

        private SLNetworkManager _net;
        private ChatManager      _chat;

        private sealed class Conversation
        {
            public UUID   AgentId;
            public Guid   SessionId;
            public string DisplayName;
            public readonly List<(bool self, string text)> Messages = new();
            public GameObject TabButton;
        }

        private readonly Dictionary<Guid, Conversation> _convos = new();
        private Conversation _active;
        private readonly List<GameObject> _messageRows = new();

        private void Awake()
        {
            _net  = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            _chat = SLApplication.Instance?.Chat    ?? FindObjectOfType<ChatManager>();

            sendButton?.onClick.AddListener(OnSend);
            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));
            inputField?.onSubmit.AddListener(_ => OnSend());
        }

        private void OnEnable()
        {
            EventBus.Subscribe<InstantMessageEvent>(OnIMReceived);
            RefreshEmptyState();
        }

        private void OnDisable()
        {
            EventBus.Unsubscribe<InstantMessageEvent>(OnIMReceived);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Open or focus a conversation with the given agent.</summary>
        public void OpenConversation(UUID agentId, string displayName)
        {
            // Find existing session by agentId
            foreach (var c in _convos.Values)
            {
                if (c.AgentId == agentId)
                {
                    ActivateConversation(c);
                    return;
                }
            }

            // Create new empty conversation
            Guid sessionId = Guid.NewGuid();
            var convo = MakeConversation(sessionId, agentId, displayName);
            ActivateConversation(convo);
        }

        // ── Event handling ────────────────────────────────────────────────────

        private void OnIMReceived(InstantMessageEvent e)
        {
            if (!_convos.TryGetValue(e.SessionId, out var convo))
            {
                // New conversation from incoming IM
                convo = MakeConversation(e.SessionId, UUID.Zero, e.FromName);
            }

            convo.Messages.Add((false, $"{e.FromName}: {e.Message}"));
            if (_active == convo) AppendMessage(false, $"{e.FromName}: {e.Message}");

            // Visual indicator on tab if not active
            if (_active != convo)
                MarkTabUnread(convo);

            gameObject.SetActive(true);
        }

        // ── Conversation management ───────────────────────────────────────────

        private Conversation MakeConversation(Guid sessionId, UUID agentId, string name)
        {
            var c = new Conversation
            {
                AgentId     = agentId,
                SessionId   = sessionId,
                DisplayName = name,
            };
            _convos[sessionId] = c;
            SpawnTab(c);
            RefreshEmptyState();
            return c;
        }

        private void SpawnTab(Conversation c)
        {
            if (tabButtonPrefab == null || tabContainer == null) return;

            var go  = Instantiate(tabButtonPrefab, tabContainer);
            var lbl = go.GetComponentInChildren<TMP_Text>();
            var btn = go.GetComponent<Button>();

            if (lbl != null) lbl.text = c.DisplayName;
            if (btn != null) btn.onClick.AddListener(() => ActivateConversation(c));

            c.TabButton = go;
        }

        private void ActivateConversation(Conversation c)
        {
            _active = c;

            // Rebuild message view
            ClearMessageRows();
            foreach (var (self, text) in c.Messages)
                AppendMessage(self, text);

            ScrollToBottom();
            ClearUnread(c);
            RefreshEmptyState();
        }

        // ── Message display ───────────────────────────────────────────────────

        private void AppendMessage(bool self, string text)
        {
            if (messageContainer == null) return;

            var prefab = (self && selfMessageRowPrefab != null) ? selfMessageRowPrefab : messageRowPrefab;
            if (prefab == null) return;

            var go  = Instantiate(prefab, messageContainer);
            var lbl = go.GetComponent<TMP_Text>() ?? go.GetComponentInChildren<TMP_Text>();
            if (lbl != null) lbl.text = text;
            _messageRows.Add(go);

            ScrollToBottom();
        }

        private void ClearMessageRows()
        {
            foreach (var r in _messageRows) Destroy(r);
            _messageRows.Clear();
        }

        private void ScrollToBottom()
        {
            if (conversationScroll == null) return;
            Canvas.ForceUpdateCanvases();
            conversationScroll.verticalNormalizedPosition = 0f;
        }

        // ── Send ──────────────────────────────────────────────────────────────

        private void OnSend()
        {
            if (_active == null || inputField == null) return;
            string text = inputField.text.Trim();
            if (string.IsNullOrEmpty(text)) return;
            inputField.text = string.Empty;

            _chat?.SendIM(_active.AgentId, text);
            _active.Messages.Add((true, $"You: {text}"));
            AppendMessage(true, $"You: {text}");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void RefreshEmptyState()
        {
            bool empty = _convos.Count == 0;
            if (statusLabel != null)
            {
                statusLabel.text = "No conversations.";
                statusLabel.gameObject.SetActive(empty);
            }
            if (messageContainer != null)
                messageContainer.gameObject.SetActive(!empty);
        }

        private static void MarkTabUnread(Conversation c)
        {
            if (c.TabButton == null) return;
            var lbl = c.TabButton.GetComponentInChildren<TMP_Text>();
            if (lbl != null && !lbl.text.StartsWith("●"))
                lbl.text = "● " + lbl.text;
        }

        private static void ClearUnread(Conversation c)
        {
            if (c.TabButton == null) return;
            var lbl = c.TabButton.GetComponentInChildren<TMP_Text>();
            if (lbl != null)
                lbl.text = lbl.text.Replace("● ", string.Empty);
        }
    }
}
