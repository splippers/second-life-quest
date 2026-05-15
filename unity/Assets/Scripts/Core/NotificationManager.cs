using System;
using System.Collections;
using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Network;
using UnityEngine;

namespace SLQuest.Core
{
    public enum NotificationType
    {
        InventoryOffer,
        FriendRequest,
        TeleportLure,
        GroupInvitation,
        ScriptPermission,
    }

    public sealed class PendingNotification
    {
        public NotificationType Type        { get; }
        public string           FromName    { get; }
        public UUID             FromId      { get; }
        public UUID             SessionId   { get; }
        public string           Message     { get; }
        public object           Payload     { get; }   // type-specific extra data
        public Action           OnAccept    { get; }
        public Action           OnDecline   { get; }

        public PendingNotification(NotificationType type, string fromName, UUID fromId,
                                   UUID sessionId, string message, object payload,
                                   Action onAccept, Action onDecline)
        {
            Type      = type;
            FromName  = fromName;
            FromId    = fromId;
            SessionId = sessionId;
            Message   = message;
            Payload   = payload;
            OnAccept  = onAccept;
            OnDecline = onDecline;
        }
    }

    /// <summary>
    /// Intercepts IM packets that carry interactive requests (inventory offers,
    /// friend requests, teleport lures, group invitations, script permission
    /// requests) and publishes <see cref="NotificationReceivedEvent"/> for the UI.
    ///
    /// Also provides <see cref="Dismiss"/> so toasts can cancel a pending
    /// notification without accepting or declining it.
    /// </summary>
    public sealed class NotificationManager : MonoBehaviour
    {
        private SLNetworkManager _net;
        private readonly Dictionary<UUID, PendingNotification> _pending = new();

        public event Action<PendingNotification> OnNotificationReceived;

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
        }

        private void Start()
        {
            _net.Client.Self.IM                    += OnIM;
            _net.Client.Self.ScriptQuestion        += OnScriptQuestion;
            _net.OnDisconnected                    += OnDisconnected;
        }

        private void OnDestroy()
        {
            if (_net?.Client?.Self != null)
            {
                _net.Client.Self.IM             -= OnIM;
                _net.Client.Self.ScriptQuestion -= OnScriptQuestion;
            }
            _net.OnDisconnected -= OnDisconnected;
        }

        private void OnDisconnected() => _pending.Clear();

        // ── IM dispatch ───────────────────────────────────────────────────────

        private void OnIM(object sender, InstantMessageEventArgs e)
        {
            var im = e.IM;
            MainThreadDispatcher.Enqueue(() => DispatchIM(im));
        }

        private void DispatchIM(InstantMessage im)
        {
            PendingNotification note;

            switch (im.Dialog)
            {
                case InstantMessageDialog.FriendshipOffered:
                    note = MakeNote(
                        NotificationType.FriendRequest, im,
                        $"{im.FromAgentName} wants to be your friend.",
                        onAccept:  () => _net.Client.Friends.AcceptFriendship(im.FromAgentID, im.IMSessionID),
                        onDecline: () => _net.Client.Friends.DeclineFriendship(im.FromAgentID, im.IMSessionID));
                    break;

                case InstantMessageDialog.RequestTeleport:
                    note = MakeNote(
                        NotificationType.TeleportLure, im,
                        $"{im.FromAgentName} is offering you a teleport:\n{im.Message}",
                        onAccept:  () => _net.Client.Self.TeleportLureRespond(im.FromAgentID, im.IMSessionID, true),
                        onDecline: () => _net.Client.Self.TeleportLureRespond(im.FromAgentID, im.IMSessionID, false));
                    break;

                case InstantMessageDialog.InventoryOffered:
                    note = MakeNote(
                        NotificationType.InventoryOffer, im,
                        $"{im.FromAgentName} is offering you: {im.Message}",
                        payload:   im.BinaryBucket,
                        onAccept:  () => AcceptInventoryOffer(im),
                        onDecline: () => DeclineInventoryOffer(im));
                    break;

                case InstantMessageDialog.TaskInventoryOffered:
                    note = MakeNote(
                        NotificationType.InventoryOffer, im,
                        $"An object is offering you: {im.Message}",
                        payload:   im.BinaryBucket,
                        onAccept:  () => AcceptInventoryOffer(im),
                        onDecline: () => DeclineInventoryOffer(im));
                    break;

                case InstantMessageDialog.GroupInvitation:
                    note = MakeNote(
                        NotificationType.GroupInvitation, im,
                        $"{im.FromAgentName} invites you to join a group.",
                        onAccept:  () => _net.Client.Self.InstantMessage(
                                            im.ToAgentID, im.IMSessionID, "join",
                                            im.IMSessionID, InstantMessageDialog.GroupInvitationAccept,
                                            im.FromAgentName, false, im.Timestamp,
                                            im.BinaryBucket),
                        onDecline: () => { /* no explicit reject packet for group invites */ });
                    break;

                default:
                    return;
            }

            _pending[note.SessionId] = note;
            OnNotificationReceived?.Invoke(note);
            EventBus.Publish(new NotificationReceivedEvent(note));
        }

        // ── Script permission request ─────────────────────────────────────────

        // Only the permissions that LSLBridge doesn't auto-grant need a toast
        private const ScriptPermission DangerousPerms =
            ScriptPermission.ControlCamera |
            ScriptPermission.TakeControls  |
            ScriptPermission.TrackCamera;

        private void OnScriptQuestion(object sender, ScriptQuestionEventArgs e)
        {
            if ((e.Questions & DangerousPerms) == 0) return; // LSLBridge auto-grants these

            MainThreadDispatcher.Enqueue(() =>
            {
                var perm = e.Questions;
                string objectName = e.ObjectName;
                string fromName   = e.ItemName;

                var note = new PendingNotification(
                    NotificationType.ScriptPermission,
                    fromName, UUID.Zero, e.ItemID,
                    $"\"{objectName}\" requests permissions: {perm}",
                    payload: perm,
                    onAccept:  () => _net.Client.Self.ScriptQuestionReply(
                                        _net.Client.Network.CurrentSim,
                                        e.ItemID, e.TaskID, perm),
                    onDecline: () => _net.Client.Self.ScriptQuestionReply(
                                        _net.Client.Network.CurrentSim,
                                        e.ItemID, e.TaskID, 0));

                _pending[e.ItemID] = note;
                OnNotificationReceived?.Invoke(note);
                EventBus.Publish(new NotificationReceivedEvent(note));
            });
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        public void Dismiss(PendingNotification note)
        {
            _pending.Remove(note.SessionId);
        }

        private PendingNotification MakeNote(NotificationType type, InstantMessage im,
                                             string message,
                                             object payload = null,
                                             Action onAccept = null,
                                             Action onDecline = null)
        {
            return new PendingNotification(
                type, im.FromAgentName, im.FromAgentID, im.IMSessionID,
                message, payload, onAccept, onDecline);
        }

        private void AcceptInventoryOffer(InstantMessage im)
        {
            _net.Client.Self.InstantMessage(
                _net.Client.Self.Name, im.FromAgentID, string.Empty,
                im.IMSessionID, InstantMessageDialog.InventoryAccepted,
                "main", false, im.Timestamp, im.BinaryBucket);
        }

        private void DeclineInventoryOffer(InstantMessage im)
        {
            _net.Client.Self.InstantMessage(
                _net.Client.Self.Name, im.FromAgentID, string.Empty,
                im.IMSessionID, InstantMessageDialog.InventoryDeclined,
                "main", false, im.Timestamp, im.BinaryBucket);
        }
    }
}
