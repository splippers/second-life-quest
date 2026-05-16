using OpenMetaverse;
using SLQuest.Network;

namespace SLQuest.Inventory
{
    public sealed class InventoryManager
    {
        private readonly SLNetworkManager _net;

        public InventoryManager(SLNetworkManager net)
        {
            _net = net;
        }

        public void FetchRootFolder()
            => _net.Client.Inventory.RequestFolderContents(
                _net.Client.Inventory.Store.RootFolder.UUID,
                _net.Client.Self.AgentID,
                true, true, InventorySortOrder.ByName);
    }
}
