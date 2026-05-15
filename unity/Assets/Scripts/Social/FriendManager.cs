using System;
using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;

namespace SLQuest.Social
{
    public sealed class FriendEntry
    {
        public UUID   Id       { get; }
        public string Name     { get; internal set; }
        public bool   IsOnline { get; internal set; }
        public bool   CanSeeMeOnMap  { get; internal set; }
        public bool   CanModifyMyObjects { get; internal set; }

        public FriendEntry(UUID id, string name, bool online)
        { Id = id; Name = name; IsOnline = online; }
    }

    /// <summary>
    /// Wraps LibreMetaverse's FriendsManager and maintains a live friend list.
    ///
    /// Fires <see cref="OnFriendListChanged"/> when presence or names change.
    /// Exposes actions: <see cref="OfferFriendship"/>, <see cref="RemoveFriend"/>,
    /// <see cref="SendTeleportOffer"/>.
    /// </summary>
    public sealed class FriendManager : MonoBehaviour
    {
        private SLNetworkManager _net;
        private readonly Dictionary<UUID, FriendEntry> _friends = new();

        public event Action OnFriendListChanged;
        public IReadOnlyDictionary<UUID, FriendEntry> Friends => _friends;

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
        }

        private void Start()
        {
            EventBus.Subscribe<LoginSucceededEvent>(OnLogin);
        }

        private void OnDestroy()
        {
            EventBus.Unsubscribe<LoginSucceededEvent>(OnLogin);
            UnwireEvents();
        }

        // ── Login ─────────────────────────────────────────────────────────────

        private void OnLogin(LoginSucceededEvent _)
        {
            WireEvents();
            // Populate from what LibreMetaverse already fetched during login
            MainThreadDispatcher.Enqueue(RebuildFriendList);
        }

        private void WireEvents()
        {
            var f = _net?.Client?.Friends;
            if (f == null) return;
            f.FriendOnline       += OnFriendOnline;
            f.FriendOffline      += OnFriendOffline;
            f.FriendNames        += OnFriendNames;
            f.FriendAdded        += OnFriendAdded;
            f.FriendRemoved      += OnFriendRemoved;
            f.FriendRightsUpdate += OnFriendRightsUpdate;
        }

        private void UnwireEvents()
        {
            var f = _net?.Client?.Friends;
            if (f == null) return;
            f.FriendOnline       -= OnFriendOnline;
            f.FriendOffline      -= OnFriendOffline;
            f.FriendNames        -= OnFriendNames;
            f.FriendAdded        -= OnFriendAdded;
            f.FriendRemoved      -= OnFriendRemoved;
            f.FriendRightsUpdate -= OnFriendRightsUpdate;
        }

        private void RebuildFriendList()
        {
            _friends.Clear();
            var fl = _net?.Client?.Friends?.FriendList;
            if (fl == null) return;

            fl.ForEach((id, info) =>
            {
                _friends[id] = new FriendEntry(id, info.Name, info.IsOnline)
                {
                    CanSeeMeOnMap       = info.CanSeeMeOnMap,
                    CanModifyMyObjects  = info.CanModifyMyObjects,
                };
            });

            OnFriendListChanged?.Invoke();
        }

        // ── LibreMetaverse callbacks (background thread) ──────────────────────

        private void OnFriendOnline(object sender, FriendInfoEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                if (_friends.TryGetValue(e.Friend.UUID, out var entry))
                    entry.IsOnline = true;
                else
                    _friends[e.Friend.UUID] = new FriendEntry(e.Friend.UUID, e.Friend.Name, true);

                OnFriendListChanged?.Invoke();
                EventBus.Publish(new FriendPresenceChangedEvent(e.Friend.UUID, true));
            });
        }

        private void OnFriendOffline(object sender, FriendInfoEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                if (_friends.TryGetValue(e.Friend.UUID, out var entry))
                    entry.IsOnline = false;

                OnFriendListChanged?.Invoke();
                EventBus.Publish(new FriendPresenceChangedEvent(e.Friend.UUID, false));
            });
        }

        private void OnFriendNames(object sender, FriendNamesEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                foreach (var kvp in e.Names)
                {
                    if (_friends.TryGetValue(kvp.Key, out var entry))
                        entry.Name = kvp.Value;
                }
                OnFriendListChanged?.Invoke();
            });
        }

        private void OnFriendAdded(object sender, FriendInfoEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                _friends[e.Friend.UUID] = new FriendEntry(e.Friend.UUID, e.Friend.Name, e.Friend.IsOnline);
                OnFriendListChanged?.Invoke();
            });
        }

        private void OnFriendRemoved(object sender, FriendInfoEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                _friends.Remove(e.Friend.UUID);
                OnFriendListChanged?.Invoke();
            });
        }

        private void OnFriendRightsUpdate(object sender, FriendInfoEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                if (_friends.TryGetValue(e.Friend.UUID, out var entry))
                {
                    entry.CanSeeMeOnMap      = e.Friend.CanSeeMeOnMap;
                    entry.CanModifyMyObjects = e.Friend.CanModifyMyObjects;
                }
                OnFriendListChanged?.Invoke();
            });
        }

        // ── Public actions ────────────────────────────────────────────────────

        public void OfferFriendship(UUID agentId)
        {
            _net?.Client?.Friends?.OfferFriendship(agentId, string.Empty);
        }

        public void RemoveFriend(UUID agentId)
        {
            _net?.Client?.Friends?.TerminateFriendship(agentId);
        }

        /// <summary>Sends a teleport lure so the friend can TP to your location.</summary>
        public void SendTeleportOffer(UUID agentId, string message = "Come join me!")
        {
            _net?.Client?.Self?.SendTeleportLure(agentId, message);
        }

        /// <summary>Opens an IM session with a friend.</summary>
        public void SendIM(UUID agentId, string message)
        {
            _net?.Client?.Self?.InstantMessage(agentId, message);
        }
    }
}
