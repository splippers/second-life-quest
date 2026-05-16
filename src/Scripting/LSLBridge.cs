using OpenMetaverse;
using SLQuest.Core;
using SLQuest.Network;

namespace SLQuest.Scripting
{
    /// <summary>
    /// Handles viewer-side script events: llDialog, llTextBox, permission requests,
    /// and URL loading.  Publishes events that the UI layer subscribes to.
    /// </summary>
    public sealed class LSLBridge
    {
        private readonly SLNetworkManager _net;

        public LSLBridge(SLNetworkManager net)
        {
            _net = net;
            net.Client.Self.ScriptDialog         += OnScriptDialog;
            net.Client.Self.ScriptQuestion        += OnScriptPermission;
            net.Client.Self.LoadURL               += OnLoadUrl;
        }

        private void OnScriptDialog(object? sender, ScriptDialogEventArgs e)
        {
            EventBus.Publish(new ScriptDialogEvent(
                e.ObjectName,
                e.Message,
                new List<string>(e.ButtonLabels),
                e.ObjectID.Guid,
                e.Channel));
        }

        private void OnScriptPermission(object? sender, ScriptQuestionEventArgs e)
        {
            EventBus.Publish(new ScriptPermissionRequestEvent(
                e.ObjectName,
                e.TaskID.Guid,
                (int)e.Questions));
        }

        private void OnLoadUrl(object? sender, LoadUrlEventArgs e)
        {
            EventBus.Publish(new NotificationEvent(
                e.ObjectName,
                $"Open URL: {e.URL}",
                8f));
        }

        public void RespondDialog(Guid objectId, int channel, string buttonLabel)
            => _net.Client.Self.ReplyToScriptDialog(channel, 0, buttonLabel, new UUID(objectId));

        public void GrantPermissions(Guid objectId, Guid taskId, int permissions)
            => _net.Client.Self.ScriptQuestionReply(
                _net.Client.Network.CurrentSim!,
                new UUID(objectId),
                new UUID(taskId),
                (ScriptPermission)permissions);

        public void DenyPermissions(Guid objectId, Guid taskId)
            => _net.Client.Self.ScriptQuestionReply(
                _net.Client.Network.CurrentSim!,
                new UUID(objectId),
                new UUID(taskId),
                ScriptPermission.None);
    }
}
