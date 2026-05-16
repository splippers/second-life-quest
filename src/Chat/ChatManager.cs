using OpenMetaverse;
using SLQuest.Core;
using SLQuest.Network;

namespace SLQuest.Chat
{
    public sealed class ChatManager
    {
        private readonly SLNetworkManager _net;

        public ChatManager(SLNetworkManager net)
        {
            _net = net;
            net.Client.Self.ChatFromSimulator += OnChat;
            net.Client.Self.IM               += OnIM;
        }

        private void OnChat(object? sender, ChatEventArgs e)
        {
            if (e.Type == ChatType.Normal || e.Type == ChatType.Shout || e.Type == ChatType.Whisper)
                EventBus.Publish(new ChatMessageEvent(e.FromName, e.Message, e.Channel));
        }

        private void OnIM(object? sender, InstantMessageEventArgs e)
        {
            var msg = e.IM;
            if (msg.Dialog == InstantMessageDialog.MessageFromAgent ||
                msg.Dialog == InstantMessageDialog.MessageFromObject)
                EventBus.Publish(new InstantMessageEvent(msg.FromAgentName, msg.Message, msg.IMSessionID.Guid));
        }

        public void SendChat(string message, int channel = 0)
            => _net.Client.Self.Chat(message, channel, ChatType.Normal);

        public void SendIM(Guid targetId, string message, Guid sessionId)
            => _net.Client.Self.InstantMessage(new UUID(targetId), message, new UUID(sessionId));
    }
}
