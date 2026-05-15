using System;
using System.Collections;
using OpenMetaverse;
using SLQuest.Core;
using Unity.Services.Vivox;
using UnityEngine;

namespace SLQuest.Voice
{
    /// <summary>
    /// Second Life voice is powered by Vivox (same as desktop viewer).
    /// This manager:
    ///   1. Requests a Vivox provisioning token from the SL grid via the
    ///      ProvisionVoiceAccountRequest capability
    ///   2. Initialises the Vivox Unity SDK with the returned credentials
    ///   3. Joins the region's positional voice channel
    ///   4. Handles 3D spatialization updates
    ///
    /// Requires: Unity Gaming Services → Vivox package (com.unity.vivox)
    /// and a Vivox app registered at developer.vivox.com (or use SL's Vivox domain).
    /// </summary>
    public sealed class VoiceManager : MonoBehaviour
    {
        public bool IsConnected { get; private set; }
        public bool IsMuted     { get; private set; }

        private SLNetworkManager _net;
        private string _vivoxServer;
        private string _vivoxCredentials;
        private string _activeChannelName;

        public event Action<bool> OnMuteChanged;

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
        }

        private void Start()
        {
            _net.OnLoggedIn += OnLoggedIn;
            EventBus.Subscribe<SimConnectedEvent>(OnSimConnected);
        }

        private void OnDestroy()
        {
            if (_net != null) _net.OnLoggedIn -= OnLoggedIn;
            EventBus.Unsubscribe<SimConnectedEvent>(OnSimConnected);
            Disconnect();
        }

        private void OnLoggedIn() => StartCoroutine(ProvisionVoiceAccount());

        private void OnSimConnected(SimConnectedEvent evt)
        {
            if (IsConnected)
                StartCoroutine(JoinRegionChannel(evt.Simulator as Simulator));
        }

        // ── Provisioning ──────────────────────────────────────────────────────

        private IEnumerator ProvisionVoiceAccount()
        {
            // Ask the SL grid for Vivox credentials
            bool done = false;
            string server = null, credentials = null;

            _net.Client.Self.VoiceSessionNew += (s, e) =>
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    server      = e.URI;
                    credentials = e.Handle;
                    done        = true;
                });
            };

            // Trigger provisioning via cap
            _net.Client.Self.RequestJoinGroupChat(UUID.Zero); // nudges voice provisioning
            yield return new WaitUntil(() => done);

            if (!string.IsNullOrEmpty(server))
                yield return StartCoroutine(InitVivox(server, credentials));
        }

        private IEnumerator InitVivox(string server, string credentials)
        {
            _vivoxServer      = server;
            _vivoxCredentials = credentials;

            var initOpts = new VivoxConfigurationOptions
            {
                Server = server
            };

            var initTask = VivoxService.Instance.InitializeAsync(initOpts);
            yield return new WaitUntil(() => initTask.IsCompleted);

            if (initTask.IsFaulted)
            {
                Debug.LogError($"[Voice] Vivox init failed: {initTask.Exception?.Message}");
                yield break;
            }

            IsConnected = true;
            Debug.Log("[Voice] Vivox initialised");

            // Join current sim channel
            if (_net.Client.Network.CurrentSim != null)
                yield return StartCoroutine(JoinRegionChannel(_net.Client.Network.CurrentSim));
        }

        private IEnumerator JoinRegionChannel(Simulator sim)
        {
            if (!IsConnected || sim == null) yield break;

            // SL voice channel naming: sip:regionName@vivox-domain
            string channelName = $"sip:confctl-g@{_vivoxServer}";
            _activeChannelName = channelName;

            var joinOptions = new ChannelOptions
            {
                Channel3DProperties = new Channel3DProperties(
                    audibleDistance:  32,
                    conversationalDistance: 1,
                    audioFadeIntensityByDistance: 1f,
                    audioFadeModel: AudioFadeModel.InverseByDistance)
            };

            var joinTask = VivoxService.Instance.JoinPositionalChannelAsync(channelName, joinOptions);
            yield return new WaitUntil(() => joinTask.IsCompleted);

            if (joinTask.IsFaulted)
                Debug.LogWarning($"[Voice] Channel join failed: {joinTask.Exception?.Message}");
            else
                Debug.Log($"[Voice] Joined channel {channelName}");
        }

        // ── Update spatial position ────────────────────────────────────────────

        private void Update()
        {
            if (!IsConnected) return;

            var av = SLApplication.Instance?.LocalAvatar;
            if (av == null) return;

            VivoxService.Instance.Set3DPosition(
                av.transform.position,
                av.transform.position,
                av.transform.forward,
                av.transform.up);
        }

        // ── Mute toggle ───────────────────────────────────────────────────────

        public void ToggleMute()
        {
            IsMuted = !IsMuted;
            VivoxService.Instance.MuteInputDevice(IsMuted);
            OnMuteChanged?.Invoke(IsMuted);
        }

        public void Disconnect()
        {
            if (!IsConnected) return;
            IsConnected = false;
            VivoxService.Instance?.UninitializeAsync();
        }
    }
}
