using System.Collections.Concurrent;

namespace SLQuest.Core
{
    // ── Domain events (engine-agnostic) ──────────────────────────────────────

    public readonly record struct LoginSucceededEvent;
    public readonly record struct LoginFailedEvent(string Reason);
    public readonly record struct LoggedOutEvent;

    public readonly record struct SimConnectedEvent(object Simulator);
    public readonly record struct SimDisconnectedEvent(object Simulator, string Reason);

    public readonly record struct ChatMessageEvent(string FromName, string Message, int Channel);
    public readonly record struct InstantMessageEvent(string FromName, string Message, Guid SessionId);

    public readonly record struct AvatarUpdateEvent(Guid Id, Vector3 Position, Quaternion Rotation);
    public readonly record struct ObjectUpdateEvent(uint LocalId, Guid FullId, Vector3 Position, Quaternion Rotation, Vector3 Scale);
    public readonly record struct ObjectRemovedEvent(uint LocalId);
    public readonly record struct TerrainPatchEvent(int X, int Y, float[] Heights);
    public readonly record struct TeleportEvent(string RegionName, Vector3 Position);

    // Script events
    public readonly record struct ScriptDialogEvent(
        string ObjectName, string Message, List<string> Buttons, Guid ObjectId, int Channel);
    public readonly record struct ScriptTextBoxEvent(
        string ObjectName, string Message, Guid ObjectId, int Channel);
    public readonly record struct ScriptPermissionRequestEvent(
        string ObjectName, Guid ObjectId, int Permissions);

    // Social events
    public readonly record struct FriendOnlineEvent(Guid AgentId, string Name, bool Online);
    public readonly record struct FriendNearbyEvent(Guid AgentId, string Name, Vector3 Position);

    // World events
    public readonly record struct ParcelChangedEvent(string ParcelName, bool CanBuild, bool CanScript, bool CanFly);
    public readonly record struct GroupChatMessageEvent(string GroupName, string FromName, string Message, Guid GroupId);

    // UI events
    public readonly record struct NotificationEvent(string Title, string Body, float Duration);

    // ── Bus ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Thread-safe publish/subscribe bus. Network callbacks publish from background
    /// threads; subscribers receive on the frame loop thread via <see cref="Flush"/>.
    /// </summary>
    public static class EventBus
    {
        private static readonly ConcurrentQueue<Action> _pending = new();
        private static readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public static void Subscribe<T>(Action<T> h)
        {
            lock (_handlers)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list))
                    _handlers[typeof(T)] = list = new();
                list.Add(h);
            }
        }

        public static void Unsubscribe<T>(Action<T> h)
        {
            lock (_handlers)
            {
                if (_handlers.TryGetValue(typeof(T), out var list))
                    list.Remove(h);
            }
        }

        /// <summary>Thread-safe: enqueues delivery of <paramref name="evt"/> to the frame thread.</summary>
        public static void Publish<T>(T evt)
        {
            _pending.Enqueue(() =>
            {
                lock (_handlers)
                {
                    if (!_handlers.TryGetValue(typeof(T), out var list)) return;
                    foreach (var d in list)
                        ((Action<T>)d)(evt);
                }
            });
        }

        /// <summary>Called once per frame on the frame loop thread to drain the queue.</summary>
        public static void Flush()
        {
            while (_pending.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
            }
        }

        public static void Clear()
        {
            while (_pending.TryDequeue(out _)) { }
            lock (_handlers) _handlers.Clear();
        }
    }
}
