using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Core;
using SLQuest.World;
using UnityEngine;

namespace SLQuest.Avatar
{
    /// <summary>
    /// Owns all avatar GameObjects in the scene (both local and remote).
    /// </summary>
    public sealed class AvatarManager : MonoBehaviour
    {
        [SerializeField] private RemoteAvatar remoteAvatarPrefab;

        private SLNetworkManager _net;
        private RegionManager    _region;
        private readonly Dictionary<UUID, RemoteAvatar> _remotes = new();

        public LocalAvatar Local => SLApplication.Instance?.LocalAvatar;

        private void Awake()
        {
            _net    = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            _region = SLApplication.Instance?.Region  ?? FindObjectOfType<RegionManager>();
        }

        private void Start()
        {
            var client = _net.Client;
            client.Avatars.AvatarUpdate        += OnAvatarUpdate;
            client.Avatars.AvatarSitResponse   += OnAvatarSitResponse;
            client.Objects.KillObject          += OnKillObject;
        }

        private void OnDestroy()
        {
            if (_net?.Client?.Avatars != null)
            {
                _net.Client.Avatars.AvatarUpdate      -= OnAvatarUpdate;
                _net.Client.Avatars.AvatarSitResponse -= OnAvatarSitResponse;
                _net.Client.Objects.KillObject        -= OnKillObject;
            }
        }

        private void OnAvatarUpdate(object sender, AvatarUpdateEventArgs e)
        {
            var av = e.Avatar;
            bool isLocal = av.ID == _net.Client.Self.AgentID;
            MainThreadDispatcher.Enqueue(() =>
            {
                if (isLocal)
                {
                    Local?.ApplyServerPosition(
                        _region.LocalToUnity(av.Position, e.Simulator),
                        _region.SLToUnityRotation(av.Rotation));
                    return;
                }

                UpsertRemote(av, e.Simulator);

                EventBus.Publish(new AvatarUpdateEvent(
                    av.ID,
                    _region.LocalToUnity(av.Position, e.Simulator),
                    _region.SLToUnityRotation(av.Rotation)));
            });
        }

        private void OnAvatarSitResponse(object sender, AvatarSitResponseEventArgs e)
        {
            // Handle sit/unsit visual state for remote avatars
            MainThreadDispatcher.Enqueue(() =>
            {
                if (_remotes.TryGetValue(_net.Client.Self.AgentID, out _))
                    Local?.SetSitting(e.SitObject != UUID.Zero);
            });
        }

        private void OnKillObject(object sender, KillObjectEventArgs e)
        {
            // Avatars can also be killed via KillObject when they leave the sim
            MainThreadDispatcher.Enqueue(() => RemoveByLocalId(e.ObjectLocalID));
        }

        private void UpsertRemote(OpenMetaverse.Avatar av, Simulator sim)
        {
            if (!_remotes.TryGetValue(av.ID, out var remote))
            {
                var go = remoteAvatarPrefab != null
                    ? Instantiate(remoteAvatarPrefab, transform)
                    : new GameObject().AddComponent<RemoteAvatar>();

                go.name     = av.Name ?? av.ID.ToString();
                go.AgentId  = av.ID;
                _remotes[av.ID] = remote = go;
            }

            remote.LocalId = av.LocalID;
            var pos = _region.LocalToUnity(av.Position, sim);
            var rot = _region.SLToUnityRotation(av.Rotation);
            remote.UpdateTransform(pos, rot);
            remote.UpdateAnimations(av.Animations);
        }

        private void RemoveByLocalId(uint localId)
        {
            UUID toRemove = UUID.Zero;
            foreach (var kvp in _remotes)
            {
                if (kvp.Value.LocalId == localId) { toRemove = kvp.Key; break; }
            }
            if (toRemove != UUID.Zero)
            {
                Destroy(_remotes[toRemove].gameObject);
                _remotes.Remove(toRemove);
            }
        }
    }
}
