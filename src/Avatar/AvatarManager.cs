using OpenMetaverse;
using System.Collections.Concurrent;
using SLQuest.Core;
using SLQuest.Network;

namespace SLQuest.Avatar
{
    public sealed class RemoteAvatar
    {
        public Guid       Id       { get; set; }
        public string     Name     { get; set; } = string.Empty;
        public Vector3    Position { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
    }

    public sealed class AvatarManager
    {
        private readonly ConcurrentDictionary<Guid, RemoteAvatar> _avatars = new();

        public IReadOnlyDictionary<Guid, RemoteAvatar> Avatars => _avatars;

        public AvatarManager(SLNetworkManager net, World.RegionManager region)
        {
            // Avatar positions arrive via object updates; grab the AvatarUpdate event
            net.Client.Objects.AvatarUpdate     += OnAvatarUpdate;
            net.Client.Avatars.UUIDNameReply    += OnNameReply;
            EventBus.Subscribe<SimConnectedEvent>(_ => _avatars.Clear());
        }

        private void OnAvatarUpdate(object? sender, AvatarUpdateEventArgs e)
        {
            var av  = e.Avatar;
            var rav = _avatars.GetOrAdd(av.ID.Guid, id => new RemoteAvatar { Id = id });

            rav.Position = MathEx.SLToWorld(av.Position);
            rav.Rotation = MathEx.SLToWorld(av.Rotation);

            // Position is already published by ObjectManager's AvatarUpdate handler;
            // we just keep the avatar dictionary up to date here.
        }

        private void OnNameReply(object? sender, UUIDNameReplyEventArgs e)
        {
            foreach (var kv in e.Names)
            {
                if (_avatars.TryGetValue(kv.Key.Guid, out var rav))
                    rav.Name = kv.Value;
            }
        }

        /// <summary>
        /// Request display names for any avatars that don't have one yet.
        /// Call once per second or so.
        /// </summary>
        public void RequestMissingNames(OpenMetaverse.GridClient client)
        {
            var missing = _avatars.Values
                .Where(a => string.IsNullOrEmpty(a.Name))
                .Select(a => new UUID(a.Id))
                .Take(10)
                .ToList();

            if (missing.Count > 0)
                client.Avatars.RequestAvatarNames(missing);
        }
    }
}
