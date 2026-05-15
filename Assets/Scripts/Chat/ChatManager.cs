using System;
using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Core;
using UnityEngine;

namespace SLQuest.Chat
{
    public enum ChatSource { Local, IM, Group, System }

    public readonly struct ChatEntry
    {
        public readonly DateTime Timestamp;
        public readonly string   FromName;
        public readonly string   Message;
        public readonly ChatSource Source;
        public readonly int      Channel;

        public ChatEntry(string from, string msg, ChatSource src, int ch = 0)
        {
            Timestamp = DateTime.Now;
            FromName  = from;
            Message   = msg;
            Source    = src;
            Channel   = ch;
        }

        public override string ToString() => $"[{Timestamp:HH:mm}] <{FromName}> {Message}";
    }

    public sealed class ChatManager : MonoBehaviour
    {
        private const int MAX_HISTORY = 500;

        private SLNetworkManager _net;
        private readonly List<ChatEntry> _history = new(MAX_HISTORY);

        public IReadOnlyList<ChatEntry> History => _history;
        public event Action<ChatEntry> OnNewEntry;

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
        }

        private void Start()
        {
            _net.Client.Self.ChatFromSimulator += OnChatFromSim;
            _net.Client.Self.IM               += OnInstantMessage;
            _net.Client.Self.ScriptDialog     += OnScriptDialog;
        }

        private void OnDestroy()
        {
            if (_net?.Client?.Self != null)
            {
                _net.Client.Self.ChatFromSimulator -= OnChatFromSim;
                _net.Client.Self.IM               -= OnInstantMessage;
                _net.Client.Self.ScriptDialog     -= OnScriptDialog;
            }
        }

        // ── Callbacks ─────────────────────────────────────────────────────────

        private void OnChatFromSim(object sender, ChatEventArgs e)
        {
            if (e.Type == ChatType.StartTyping || e.Type == ChatType.StopTyping) return;
            if (string.IsNullOrEmpty(e.Message)) return;

            MainThreadDispatcher.Enqueue(() =>
            {
                var entry = new ChatEntry(e.FromName, e.Message, ChatSource.Local, e.Channel);
                Append(entry);
                EventBus.Publish(new ChatMessageEvent(e.FromName, e.Message, e.Channel, e.Type));
            });
        }

        private void OnInstantMessage(object sender, InstantMessageEventArgs e)
        {
            var im = e.IM;
            if (im.Dialog != InstantMessageDialog.MessageFromAgent &&
                im.Dialog != InstantMessageDialog.MessageFromObject) return;

            MainThreadDispatcher.Enqueue(() =>
            {
                var entry = new ChatEntry(im.FromAgentName, im.Message, ChatSource.IM);
                Append(entry);
                EventBus.Publish(new InstantMessageEvent(im.FromAgentName, im.Message, im.IMSessionID));
            });
        }

        private void OnScriptDialog(object sender, ScriptDialogEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                var entry = new ChatEntry(e.ObjectName, e.Message, ChatSource.System);
                Append(entry);
                // UI layer subscribes to History changes; emit same event so it can surface buttons
                EventBus.Publish(new ChatMessageEvent(e.ObjectName, e.Message, 0, null));
            });
        }

        // ── Send ──────────────────────────────────────────────────────────────

        /// <summary>Send a local-chat message on the given channel (default 0 = public).</summary>
        public void Say(string message, int channel = 0, ChatType type = ChatType.Normal)
        {
            if (!_net.IsInWorld) return;
            _net.Client.Self.Chat(message, channel, type);
        }

        /// <summary>Send an instant message to a specific agent.</summary>
        public void SendIM(UUID agentId, string message)
        {
            if (!_net.IsInWorld) return;
            _net.Client.Self.InstantMessage(agentId, message);
        }

        private void Append(ChatEntry entry)
        {
            if (_history.Count >= MAX_HISTORY)
                _history.RemoveAt(0);
            _history.Add(entry);
            OnNewEntry?.Invoke(entry);
        }
    }
}
