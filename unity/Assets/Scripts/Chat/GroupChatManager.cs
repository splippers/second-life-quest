using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using OpenMetaverse;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;
using UnityEngine.Networking;

namespace SLQuest.Chat
{
    /// <summary>
    /// Manages Second Life group chat sessions.
    /// Each active group session is joined via the ChatSessionRequest cap (falling back
    /// to UDP RequestJoinGroupChat). Incoming group IMs are routed through ChatManager
    /// so they appear in the unified history and fire GroupChatMessageEvent on the bus.
    /// </summary>
    public sealed class GroupChatManager : MonoBehaviour
    {
        private SLNetworkManager _net;
        private CapabilityHandler _caps;
        private ChatManager _chat;

        // groupId → group name for all current groups
        private readonly Dictionary<UUID, string> _groupNames = new();
        // groupId → session UUID for actively joined sessions
        private readonly Dictionary<UUID, UUID> _sessions = new();

        public IReadOnlyDictionary<UUID, string> Groups => _groupNames;

        public event Action<UUID, string> OnGroupJoined;
        public event Action<UUID>         OnGroupLeft;
        public event Action               OnGroupListChanged;

        private void Awake()
        {
            _net  = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            _caps = SLApplication.Instance?.Caps    ?? FindObjectOfType<CapabilityHandler>();
            _chat = SLApplication.Instance?.Chat    ?? FindObjectOfType<ChatManager>();
        }

        private void Start()
        {
            _net.Client.Self.GroupChatJoined  += OnGroupChatJoined;
            _net.Client.Self.IM               += OnIM;
            _net.Client.Groups.CurrentGroups  += OnCurrentGroups;
            _net.OnLoggedIn                   += RequestCurrentGroups;
        }

        private void OnDestroy()
        {
            if (_net?.Client?.Self != null)
            {
                _net.Client.Self.GroupChatJoined -= OnGroupChatJoined;
                _net.Client.Self.IM              -= OnIM;
            }
            if (_net?.Client?.Groups != null)
                _net.Client.Groups.CurrentGroups -= OnCurrentGroups;
            if (_net != null)
                _net.OnLoggedIn -= RequestCurrentGroups;
        }

        // ── Group list ────────────────────────────────────────────────────────

        private void RequestCurrentGroups() => _net.Client.Groups.RequestCurrentGroups();

        private void OnCurrentGroups(object sender, CurrentGroupsEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                _groupNames.Clear();
                foreach (var kvp in e.Groups)
                {
                    _groupNames[kvp.Key] = kvp.Value.Name;
                    EventBus.Publish(new GroupListUpdatedEvent(kvp.Key, kvp.Value.Name));
                }
                OnGroupListChanged?.Invoke();
            });
        }

        // ── Session management ────────────────────────────────────────────────

        /// <summary>Join a group chat session. No-op if already joined.</summary>
        public void JoinGroupChat(UUID groupId)
        {
            if (_sessions.ContainsKey(groupId)) return;

            if (_caps != null && _caps.TryGetCap("ChatSessionRequest", out var capUri))
                StartCoroutine(RequestSessionViaCap(groupId, capUri));
            else
                _net.Client.Self.RequestJoinGroupChat(groupId);
        }

        private IEnumerator RequestSessionViaCap(UUID groupId, Uri capUri)
        {
            // LLSD JSON body for ChatSessionRequest
            string body = "{\"method\":\"start session\","
                        + $"\"params\":{{\"type\":\"group\",\"session-id\":\"{groupId}\"}}}}";

            using var req = new UnityWebRequest(capUri.ToString(), "POST");
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/llsd+json");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[GroupChat] Cap request failed for {groupId}: {req.error}. Falling back to UDP.");
                _net.Client.Self.RequestJoinGroupChat(groupId);
            }
        }

        /// <summary>Leave a group chat session.</summary>
        public void LeaveGroupChat(UUID groupId)
        {
            if (!_sessions.TryGetValue(groupId, out _)) return;

            _net.Client.Self.RequestLeaveGroupChat(groupId);
            _sessions.Remove(groupId);

            OnGroupLeft?.Invoke(groupId);
            EventBus.Publish(new GroupLeftEvent(groupId));
        }

        /// <summary>Send a message to an active group chat session.</summary>
        public void SendGroupMessage(UUID groupId, string message)
        {
            if (!_net.IsInWorld || string.IsNullOrEmpty(message)) return;

            if (!_sessions.ContainsKey(groupId))
            {
                Debug.LogWarning($"[GroupChat] Not in session for group {groupId}; joining first.");
                JoinGroupChat(groupId);
                return;
            }

            _net.Client.Groups.InstantMessageGroup(groupId, message);
        }

        // ── Callbacks ─────────────────────────────────────────────────────────

        private void OnGroupChatJoined(object sender, GroupChatJoinedEventArgs e)
        {
            if (!e.Success) return;
            MainThreadDispatcher.Enqueue(() =>
            {
                _sessions[e.GroupID] = e.TmpSessionID;
                string name = _groupNames.TryGetValue(e.GroupID, out var n)
                    ? n : e.GroupID.ToString();
                OnGroupJoined?.Invoke(e.GroupID, name);
                EventBus.Publish(new GroupJoinedEvent(e.GroupID, name));
            });
        }

        private void OnIM(object sender, InstantMessageEventArgs e)
        {
            var im = e.IM;
            // Group chat messages arrive as SessionSend IMs whose session ID matches the group UUID
            if (im.Dialog != InstantMessageDialog.SessionSend) return;
            if (!_groupNames.ContainsKey(im.IMSessionID)) return;

            MainThreadDispatcher.Enqueue(() =>
            {
                string groupName = _groupNames.TryGetValue(im.IMSessionID, out var n) ? n : "Group";
                string display   = $"[{groupName}] {im.FromAgentName}: {im.Message}";
                var entry = new ChatEntry(im.FromAgentName, display, ChatSource.GroupIM);
                _chat?.InjectEntry(entry);
                EventBus.Publish(new GroupChatMessageEvent(im.IMSessionID, groupName, im.FromAgentName, im.Message));
            });
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        public bool IsInSession(UUID groupId) => _sessions.ContainsKey(groupId);

        public string GetGroupName(UUID groupId)
            => _groupNames.TryGetValue(groupId, out var n) ? n : groupId.ToString();
    }
}
