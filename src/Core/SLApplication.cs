using Microsoft.Extensions.Logging;
using SLQuest.Network;
using SLQuest.World;
using SLQuest.Avatar;
using SLQuest.Assets;
using SLQuest.Chat;
using SLQuest.Inventory;
using SLQuest.Building;
using SLQuest.Voice;
using SLQuest.XR;
using SLQuest.Rendering;
using SLQuest.UI;

namespace SLQuest.Core
{
    /// <summary>
    /// Application root. Owns all subsystems; drives the frame loop.
    /// </summary>
    public sealed class SLApplication : IAsyncDisposable
    {
        public static SLApplication Instance { get; private set; } = null!;

        // ── Subsystems ────────────────────────────────────────────────────────
        public ILoggerFactory     LogFactory  { get; }
        public SLNetworkManager   Network     { get; }
        public LoginManager       Login       { get; }
        public CapabilityHandler  Caps        { get; }
        public RegionManager      Region      { get; }
        public ObjectManager      Objects     { get; }
        public TerrainManager     Terrain     { get; }
        public AvatarManager      Avatars     { get; }
        public LocalAvatar        LocalAvatar { get; }
        public AssetManager       Assets      { get; }
        public ChatManager        Chat        { get; }
        public InventoryManager   Inventory   { get; }
        public BuildingManager    Building    { get; }
        public VoiceManager       Voice       { get; }
        public XRSession          XR          { get; }
        public VulkanContext      Vulkan      { get; }
        public SwapchainRenderer  Swapchain   { get; }
        public WorldRenderer      WorldRender { get; }
        public UIManager          UI          { get; }

        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        public SLApplication(ILoggerFactory logFactory, XRSession xr, VulkanContext vulkan)
        {
            Instance   = this;
            LogFactory = logFactory;
            XR         = xr;
            Vulkan     = vulkan;

            Network     = new SLNetworkManager(logFactory.CreateLogger<SLNetworkManager>());
            Login       = new LoginManager(Network, logFactory.CreateLogger<LoginManager>());
            Caps        = new CapabilityHandler(Network, logFactory.CreateLogger<CapabilityHandler>());
            Region      = new RegionManager(Network);
            Assets      = new AssetManager(Network, Caps, logFactory.CreateLogger<AssetManager>());
            Objects     = new ObjectManager(Network, Region, Assets);
            Terrain     = new TerrainManager(Network);
            Avatars     = new AvatarManager(Network, Region);
            LocalAvatar = new LocalAvatar(Network, XR);
            Chat        = new ChatManager(Network);
            Inventory   = new InventoryManager(Network);
            Building    = new BuildingManager(Network, Region);
            Voice       = new VoiceManager(Network);

            Swapchain   = new SwapchainRenderer(XR, Vulkan);
            WorldRender = new WorldRenderer(Vulkan, Objects, Terrain, Avatars);
            WorldRender.BindSwapchain(Swapchain); // must be called before InitAsync
            UI          = new UIManager(Vulkan, Chat, Inventory, Building, Login,
                                        logFactory.CreateLogger<UIManager>());
        }

        // ── Frame loop ────────────────────────────────────────────────────────

        public async Task RunAsync()
        {
            await XR.InitAsync();
            await Swapchain.InitAsync();
            await WorldRender.InitAsync();
            await UI.InitAsync();
            UI.ShowLogin();

            var log = LogFactory.CreateLogger<SLApplication>();
            log.LogInformation("SLQuest running");

            var token = _cts.Token;
            while (!token.IsCancellationRequested && XR.IsRunning)
            {
                XR.PollEvents();
                if (!XR.SessionActive) { await Task.Yield(); continue; }

                var (waitResult, frameState) = XR.WaitFrame();
                if (!waitResult) continue;

                XR.BeginFrame();
                LocalAvatar.Tick(XR.DeltaTime);
                EventBus.Flush();

                var views = XR.LocateViews();
                Swapchain.RenderFrame(views, frameState.PredictedDisplayTime, RenderScene);
                XR.EndFrame(frameState, Swapchain.ProjectionLayers);
            }

            log.LogInformation("SLQuest exiting");
        }

        private void RenderScene(in RenderContext ctx)
        {
            WorldRender.Render(ctx);
            UI.Render(ctx);
        }

        public void RequestExit() => _cts.Cancel();

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            Network.Logout();
            await Voice.DisposeAsync();
            WorldRender.Dispose();
            UI.Dispose();
            Swapchain.Dispose();
            Vulkan.Dispose();
            XR.Dispose();
        }
    }
}
