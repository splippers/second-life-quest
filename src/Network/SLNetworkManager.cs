using OpenMetaverse;
using Microsoft.Extensions.Logging;

namespace SLQuest.Network
{
    public sealed class SLNetworkManager
    {
        public GridClient Client { get; } = new GridClient();

        public bool IsLoggedIn => Client.Network.Connected;

        public SLNetworkManager(ILogger<SLNetworkManager> log)
        {
            Client.Settings.LOGIN_TIMEOUT          = 60_000;
            Client.Settings.SIMULATOR_TIMEOUT      = 45_000;
            Client.Settings.USE_ASSET_CACHE        = false; // we manage our own cache
            Client.Settings.MAX_CONCURRENT_TEXTURE_DOWNLOADS = 6;
            Client.Settings.LOG_LEVEL              = Helpers.LogLevel.None;
        }

        public void Logout()
        {
            if (Client.Network.Connected)
                Client.Network.Logout();
        }
    }
}
