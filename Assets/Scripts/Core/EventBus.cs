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
