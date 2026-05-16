using SLQuest.Network;

namespace SLQuest.Voice
{
    /// <summary>
    /// Placeholder for Vivox voice integration.
    /// Voice credentials are provisioned by the grid after login via the
    /// VoiceAccountProvision and ParcelVoiceInfoRequest capabilities.
    /// </summary>
    public sealed class VoiceManager : IAsyncDisposable
    {
        public VoiceManager(SLNetworkManager net) { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
