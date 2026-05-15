using System;
using System.Collections.Generic;
using UnityEngine;

namespace SLQuest.Core
{
    // ── Domain events ────────────────────────────────────────────────────────

    public readonly struct SimConnectedEvent
    {
        public readonly object Simulator; // OpenMetaverse.Simulator
        public SimConnectedEvent(object sim) => Simulator = sim;
    }

    public readonly struct SimDisconnectedEvent
    {
        public readonly object Simulator;
        public readonly string Reason;
        public SimDisconnectedEvent(object sim, string reason) { Simulator = sim; Reason = reason; }
    }

    public readonly struct LoginSucceededEvent { }
    public readonly struct LoginFailedEvent { public readonly string Reason; public LoginFailedEvent(string r) => Reason = r; }
    public readonly struct LoggedOutEvent { }

    public readonly struct ChatMessageEvent
    {
        public readonly string FromName;
        public readonly string Message;
        public readonly int Channel;
        public readonly object ChatType;  // OpenMetaverse.ChatType
        public ChatMessageEvent(string from, string msg, int ch, object type)
        { FromName = from; Message = msg; Channel = ch; ChatType = type; }
    }

    public readonly struct InstantMessageEvent
    {
        public readonly string FromName;
        public readonly string Message;
        public readonly Guid SessionId;
        public InstantMessageEvent(string from, string msg, Guid sid)
        { FromName = from; Message = msg; SessionId = sid; }
    }

    public readonly struct AvatarUpdateEvent
    {
        public readonly Guid Id;
        public readonly UnityEngine.Vector3 Position;
        public readonly UnityEngine.Quaternion Rotation;
        public AvatarUpdateEvent(Guid id, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot)
        { Id = id; Position = pos; Rotation = rot; }
    }

    public readonly struct ObjectUpdateEvent
    {
        public readonly uint LocalId;
        public readonly Guid FullId;
        public readonly UnityEngine.Vector3 Position;
        public readonly UnityEngine.Quaternion Rotation;
        public readonly UnityEngine.Vector3 Scale;
        public ObjectUpdateEvent(uint local, Guid full, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot, UnityEngine.Vector3 scale)
        { LocalId = local; FullId = full; Position = pos; Rotation = rot; Scale = scale; }
    }

    public readonly struct ObjectRemovedEvent { public readonly uint LocalId; public ObjectRemovedEvent(uint id) => LocalId = id; }

    public readonly struct TerrainPatchEvent
    {
        public readonly int X;
        public readonly int Y;
        public readonly float[] Heights; // 16×16 patch
        public TerrainPatchEvent(int x, int y, float[] h) { X = x; Y = y; Heights = h; }
    }

    public readonly struct TeleportEvent
    {
        public readonly string RegionName;
        public readonly UnityEngine.Vector3 Position;
        public TeleportEvent(string region, UnityEngine.Vector3 pos) { RegionName = region; Position = pos; }
    }

    public readonly struct GroupListUpdatedEvent
    {
        public readonly OpenMetaverse.UUID GroupId;
        public readonly string Name;
        public GroupListUpdatedEvent(OpenMetaverse.UUID id, string name) { GroupId = id; Name = name; }
    }

    public readonly struct GroupJoinedEvent
    {
        public readonly OpenMetaverse.UUID GroupId;
        public readonly string Name;
        public GroupJoinedEvent(OpenMetaverse.UUID id, string name) { GroupId = id; Name = name; }
    }

    public readonly struct GroupLeftEvent
    {
        public readonly OpenMetaverse.UUID GroupId;
        public GroupLeftEvent(OpenMetaverse.UUID id) => GroupId = id;
    }

    public readonly struct GroupChatMessageEvent
    {
        public readonly OpenMetaverse.UUID GroupId;
        public readonly string GroupName;
        public readonly string FromName;
        public readonly string Message;
        public GroupChatMessageEvent(OpenMetaverse.UUID gid, string gname, string from, string msg)
        { GroupId = gid; GroupName = gname; FromName = from; Message = msg; }
    }

    public readonly struct RenderMaterialReadyEvent
    {
        public readonly uint PrimLocalId;
        public RenderMaterialReadyEvent(uint id) => PrimLocalId = id;
    }

    public readonly struct MediaNavigateEvent
    {
        public readonly uint PrimLocalId;
        public readonly int  FaceIndex;
        public readonly string Url;
        public MediaNavigateEvent(uint id, int face, string url) { PrimLocalId = id; FaceIndex = face; Url = url; }
    }

    public readonly struct GestureTriggeredEvent
    {
        public readonly OpenMetaverse.UUID ItemId;
        public readonly string Name;
        public GestureTriggeredEvent(OpenMetaverse.UUID id, string name) { ItemId = id; Name = name; }
    }

    public readonly struct BakeCompleteEvent { }

    public readonly struct EstateInfoReceivedEvent
    {
        public readonly string EstateName;
        public readonly OpenMetaverse.UUID OwnerId;
        public EstateInfoReceivedEvent(string name, OpenMetaverse.UUID owner) { EstateName = name; OwnerId = owner; }
    }

    public readonly struct ObjectTouchedEvent
    {
        public readonly uint LocalId;
        public readonly System.Guid FullId;
        public ObjectTouchedEvent(uint local, System.Guid full) { LocalId = local; FullId = full; }
    }

    public readonly struct NotificationReceivedEvent
    {
        public readonly SLQuest.Core.PendingNotification Notification;
        public NotificationReceivedEvent(SLQuest.Core.PendingNotification n) => Notification = n;
    }

    public readonly struct FriendPresenceChangedEvent
    {
        public readonly OpenMetaverse.UUID FriendId;
        public readonly bool IsOnline;
        public FriendPresenceChangedEvent(OpenMetaverse.UUID id, bool online) { FriendId = id; IsOnline = online; }
    }

    public readonly struct ParcelChangedEvent
    {
        public readonly OpenMetaverse.Parcel Parcel;
        public ParcelChangedEvent(OpenMetaverse.Parcel p) => Parcel = p;
    }

    public readonly struct BalanceUpdatedEvent
    {
        public readonly int Balance;
        public BalanceUpdatedEvent(int b) => Balance = b;
    }

    public readonly struct LandmarkCreatedEvent
    {
        public readonly OpenMetaverse.UUID ItemId;
        public readonly string             Name;
        public LandmarkCreatedEvent(OpenMetaverse.UUID id, string n) { ItemId = id; Name = n; }
    }

    // ── Bus implementation ───────────────────────────────────────────────────

    public static class EventBus
    {
        private static readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public static void Subscribe<T>(Action<T> handler)
        {
            var key = typeof(T);
            if (!_handlers.TryGetValue(key, out var list))
            {
                list = new List<Delegate>();
                _handlers[key] = list;
            }
            list.Add(handler);
        }

        public static void Unsubscribe<T>(Action<T> handler)
        {
            if (_handlers.TryGetValue(typeof(T), out var list))
                list.Remove(handler);
        }

        public static void Publish<T>(T evt)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list)) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                try { ((Action<T>)list[i])(evt); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        public static void Clear() => _handlers.Clear();
    }
}
