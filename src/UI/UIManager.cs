using Microsoft.Extensions.Logging;
using SLQuest.Core;
using SLQuest.Chat;
using SLQuest.Inventory;
using SLQuest.Building;
using SLQuest.Network;
using SLQuest.Rendering;

namespace SLQuest.UI
{
    /// <summary>
    /// Placeholder UIManager.  In the final app this drives world-space VR panels
    /// (login, chat, inventory, build) rendered via an ImGui-style immediate-mode
    /// layer on top of the world pass.  For now it is a no-op so the rest of the
    /// pipeline compiles and runs.
    /// </summary>
    public sealed class UIManager : IDisposable
    {
        private readonly ILogger<UIManager> _log;

        public UIManager(
            VulkanContext vulkan,
            ChatManager chat,
            InventoryManager inventory,
            BuildingManager building,
            LoginManager login,
            ILogger<UIManager> log)
        {
            _log = log;
        }

        public Task InitAsync() => Task.CompletedTask;

        public void ShowLogin()
            => _log.LogInformation("UI: ShowLogin (VR panel not yet implemented)");

        public void Render(in RenderContext ctx) { /* TODO: immediate-mode VR UI layer */ }

        public void Dispose() { }
    }
}
