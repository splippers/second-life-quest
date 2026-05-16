using OpenMetaverse;
using Microsoft.Extensions.Logging;
using SLQuest.Core;

namespace SLQuest.Network
{
    public sealed class LoginManager
    {
        private readonly SLNetworkManager _net;
        private readonly ILogger<LoginManager> _log;

        public LoginManager(SLNetworkManager net, ILogger<LoginManager> log)
        {
            _net = net;
            _log = log;
        }

        /// <summary>
        /// Performs an LLSD login on a background thread and publishes the result
        /// to the EventBus on completion.
        /// </summary>
        /// <param name="startLocation">"home", "last", or "uri:RegionName/x/y/z"</param>
        public async Task<bool> LoginAsync(
            string firstName,
            string lastName,
            string password,
            string gridUri,
            string startLocation = "last")
        {
            _log.LogInformation("Logging in as {First} {Last} → {Grid}", firstName, lastName, gridUri);

            var client = _net.Client;
            var lp = client.Network.DefaultLoginParams(
                firstName, lastName, password, "SLQuest Beta", "0.1.0");
            lp.URI     = gridUri;
            lp.Start   = startLocation;
            lp.Channel = "SLQuest Beta";
            lp.Version = "0.1.0";

            bool ok = await Task.Run(() => client.Network.Login(lp));

            if (ok)
            {
                _log.LogInformation("Login succeeded — agent {Name}", client.Self.Name);
                EventBus.Publish(new LoginSucceededEvent());
            }
            else
            {
                string reason = client.Network.LoginMessage;
                _log.LogWarning("Login failed: {Reason}", reason);
                EventBus.Publish(new LoginFailedEvent(reason));
            }

            return ok;
        }
    }
}
