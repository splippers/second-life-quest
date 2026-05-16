using OpenMetaverse;
using SLQuest.Core;
using SLQuest.Network;

namespace SLQuest.World
{
    public sealed class RegionManager
    {
        private readonly SLNetworkManager _net;

        public Simulator? CurrentSim   => _net.Client.Network.CurrentSim;
        public string     RegionName   => CurrentSim?.Name ?? string.Empty;
        public ulong      RegionHandle => CurrentSim?.Handle ?? 0;

        public RegionManager(SLNetworkManager net)
        {
            _net = net;
            net.Client.Network.SimConnected    += OnSimConnected;
            net.Client.Network.SimDisconnected += OnSimDisconnected;
        }

        private void OnSimConnected(object? sender, SimConnectedEventArgs e)
            => EventBus.Publish(new SimConnectedEvent(e.Simulator));

        private void OnSimDisconnected(object? sender, SimDisconnectedEventArgs e)
            => EventBus.Publish(new SimDisconnectedEvent(e.Simulator, e.Reason.ToString()));
    }
}
