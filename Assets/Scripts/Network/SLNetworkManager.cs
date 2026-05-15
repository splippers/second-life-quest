using System;
using System.Threading.Tasks;
using OpenMetaverse;
using SLQuest.Core;
using UnityEngine;

namespace SLQuest.Network
{
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        LoggingIn,
        InWorld,
        Disconnecting
    }

    /// <summary>
    /// Owns the <see cref="GridClient"/> and bridges its background-thread events
    /// to Unity's main thread via <see cref="MainThreadDispatcher"/>.
    /// </summary>
    public sealed class SLNetworkManager : MonoBehaviour
    {
        public GridClient Client { get; private set; }
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        public event Action<ConnectionState> OnStateChanged;
        public event Action<string> OnLoginError;
        public event Action OnLoggedIn;
        public event Action OnDisconnected;

        private void Awake()
        {
            BuildClient();
        }

        private void BuildClient()
        {
            Client = new GridClient();

            var s = Client.Settings;
            s.MULTIPLE_SIMS          = true;
            s.ALWAYS_DECODE_OBJECTS  = true;
            s.ALWAYS_REQUEST_OBJECTS = true;
            s.OBJECT_TRACKING        = true;
            s.AVATAR_TRACKING        = true;
            s.STORE_LAND_PATCHES     = true;
            s.SEND_AGENT_THROTTLE    = true;
            s.SEND_AGENT_UPDATES     = true;
            s.LOG_RESENDS            = false;
            s.ENABLE_SIMSTATS        = true;

            Client.Network.LoginProgress  += OnLoginProgress;
            Client.Network.SimConnected   += OnSimConnected;
            Client.Network.SimDisconnected += OnSimDisconnected;
            Client.Network.Disconnected   += OnNetworkDisconnected;
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void BeginLogin(string firstName, string lastName, string password,
                               string startLocation = "last", string gridUri = null)
        {
            if (State != ConnectionState.Disconnected)
            {
                Debug.LogWarning("[Net] Already connected or connecting");
                return;
            }

            SetState(ConnectionState.Connecting);

            var lp = Client.Network.DefaultLoginParams(
                firstName, lastName, password,
                SLConstants.VIEWER_CHANNEL, SLConstants.VIEWER_VERSION);

            lp.Start = startLocation;
            if (!string.IsNullOrEmpty(gridUri))
                lp.URI = gridUri;

            // Login is blocking — run on thread pool, never on Unity main thread
            Task.Run(() => Client.Network.BeginLogin(lp));
        }

        public void Logout()
        {
            if (State == ConnectionState.Disconnected) return;
            SetState(ConnectionState.Disconnecting);
            Task.Run(() => Client.Network.Logout());
        }

        public bool IsInWorld => State == ConnectionState.InWorld;

        // ── GridClient callbacks (background thread) ──────────────────────────

        private void OnLoginProgress(object sender, LoginProgressEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                switch (e.Status)
                {
                    case LoginStatus.Success:
                        SetState(ConnectionState.InWorld);
                        OnLoggedIn?.Invoke();
                        EventBus.Publish(new LoginSucceededEvent());
                        break;

                    case LoginStatus.Failed:
                        SetState(ConnectionState.Disconnected);
                        OnLoginError?.Invoke(e.FailReason);
                        EventBus.Publish(new LoginFailedEvent(e.FailReason));
                        break;

                    case LoginStatus.ConnectingToLogin:
                    case LoginStatus.ReadingResponse:
                    case LoginStatus.ConnectingToSim:
                        SetState(ConnectionState.LoggingIn);
                        break;
                }
            });
        }

        private void OnSimConnected(object sender, SimConnectedEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                Debug.Log($"[Net] Sim connected: {e.Simulator.Name}");
                EventBus.Publish(new SimConnectedEvent(e.Simulator));
            });
        }

        private void OnSimDisconnected(object sender, SimDisconnectedEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                Debug.Log($"[Net] Sim disconnected: {e.Simulator.Name} ({e.Reason})");
                EventBus.Publish(new SimDisconnectedEvent(e.Simulator, e.Reason.ToString()));
            });
        }

        private void OnNetworkDisconnected(object sender, DisconnectedEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                Debug.Log($"[Net] Disconnected: {e.Reason} — {e.Message}");
                SetState(ConnectionState.Disconnected);
                OnDisconnected?.Invoke();
                EventBus.Publish(new LoggedOutEvent());
            });
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SetState(ConnectionState next)
        {
            if (State == next) return;
            State = next;
            OnStateChanged?.Invoke(next);
        }

        private void OnDestroy()
        {
            if (Client?.Network?.Connected == true)
                Client.Network.Logout();
        }
    }
}
