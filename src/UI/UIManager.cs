using Microsoft.Extensions.Logging;
using SLQuest.Core;
using SLQuest.Chat;
using SLQuest.Inventory;
using SLQuest.Building;
using SLQuest.Network;
using SLQuest.Rendering;

namespace SLQuest.UI
{
    public enum UIPanel { Login, InWorld, ScriptDialog, Notification }

    /// <summary>
    /// Event-driven VR UI state machine.
    ///
    /// Tracks which panel is active, accumulates chat/notification queues, and
    /// exposes ready-to-render state for WorldRenderer's UI pass.
    ///
    /// Panel geometry and Vulkan draw calls are handled by WorldRenderer when it
    /// reads the public properties below.  This class owns only logical state —
    /// no Vulkan objects.
    /// </summary>
    public sealed class UIManager : IDisposable
    {
        private readonly ILogger<UIManager>  _log;
        private readonly ChatManager         _chat;
        private readonly InventoryManager    _inventory;
        private readonly BuildingManager     _building;
        private readonly LoginManager        _login;

        // ── Public readable state (for WorldRenderer) ─────────────────────────

        public UIPanel  ActivePanel    { get; private set; } = UIPanel.Login;
        public bool     IsInWorld      => ActivePanel == UIPanel.InWorld;

        // Login panel
        public string   LoginStatus    { get; private set; } = "Ready";
        public bool     LoginFailed    { get; private set; }
        public string   LoginError     { get; private set; } = "";

        // Chat overlay (last N messages)
        public IReadOnlyList<(string Name, string Text)> RecentChat => _recentChat;
        private readonly List<(string Name, string Text)> _recentChat = new();
        private const int MaxChatLines = 8;

        // Notification toast
        public bool   NotificationVisible { get; private set; }
        public string NotificationTitle   { get; private set; } = "";
        public string NotificationBody    { get; private set; } = "";
        private float _notificationTimer;
        private float _notificationDuration;

        // Script dialog
        public bool          DialogVisible  { get; private set; }
        public string        DialogTitle    { get; private set; } = "";
        public string        DialogMessage  { get; private set; } = "";
        public List<string>  DialogButtons  { get; private set; } = new();
        private ScriptDialogEvent _pendingDialog;

        // Wrist menu visibility (toggled by thumbstick click)
        public bool WristMenuVisible { get; set; }

        private bool _disposed;

        public UIManager(
            VulkanContext vulkan,
            ChatManager chat,
            InventoryManager inventory,
            BuildingManager building,
            LoginManager login,
            ILogger<UIManager> log)
        {
            _log       = log;
            _chat      = chat;
            _inventory = inventory;
            _building  = building;
            _login     = login;

            EventBus.Subscribe<LoginSucceededEvent>(_ =>
            {
                LoginStatus = "Connected";
                LoginFailed = false;
            });

            EventBus.Subscribe<LoginFailedEvent>(e =>
            {
                LoginStatus = "Login failed";
                LoginFailed = true;
                LoginError  = e.Reason;
                _log.LogWarning("Login failed: {Reason}", e.Reason);
            });

            EventBus.Subscribe<ChatMessageEvent>(e =>
            {
                if (_recentChat.Count >= MaxChatLines)
                    _recentChat.RemoveAt(0);
                _recentChat.Add((e.FromName, e.Message));
            });

            EventBus.Subscribe<NotificationEvent>(e =>
            {
                NotificationTitle    = e.Title;
                NotificationBody     = e.Body;
                NotificationVisible  = true;
                _notificationDuration = Math.Max(e.Duration, 3f);
                _notificationTimer   = 0f;
            });

            EventBus.Subscribe<ScriptDialogEvent>(e =>
            {
                _pendingDialog  = e;
                DialogTitle     = e.ObjectName;
                DialogMessage   = e.Message;
                DialogButtons   = new List<string>(e.Buttons);
                DialogVisible   = true;
                ActivePanel     = UIPanel.ScriptDialog;
            });

            EventBus.Subscribe<TeleportEvent>(e =>
                ShowNotification("Teleporting", e.RegionName, 4f));

            EventBus.Subscribe<ParcelChangedEvent>(e =>
                ShowNotification("Parcel", e.ParcelName, 5f));
        }

        public Task InitAsync()
        {
            _log.LogInformation("UIManager ready");
            return Task.CompletedTask;
        }

        public void ShowLogin()
        {
            ActivePanel = UIPanel.Login;
            LoginStatus = "Ready";
            LoginFailed = false;
            _log.LogInformation("UI → Login panel");
        }

        public void ShowInWorld()
        {
            ActivePanel = UIPanel.InWorld;
            _log.LogInformation("UI → InWorld panel");
        }

        public void DismissDialog(string buttonLabel)
        {
            if (!DialogVisible) return;
            DialogVisible = false;
            ActivePanel   = UIPanel.InWorld;
            // Reply is sent by LSLBridge on button selection, not here.
        }

        public void Tick(float dt)
        {
            if (NotificationVisible)
            {
                _notificationTimer += dt;
                if (_notificationTimer >= _notificationDuration)
                    NotificationVisible = false;
            }
        }

        public void Render(in RenderContext ctx)
        {
            // WorldRenderer reads the public properties above and draws panel geometry.
            // Actual Vulkan draw calls live there, keeping this class logic-only.
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }

        private void ShowNotification(string title, string body, float duration)
        {
            NotificationTitle    = title;
            NotificationBody     = body;
            NotificationVisible  = true;
            _notificationDuration = duration;
            _notificationTimer   = 0f;
        }
    }
}
