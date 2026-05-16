using OpenMetaverse;
using Microsoft.Extensions.Logging;

namespace SLQuest.Network
{
    /// <summary>
    /// Thin helper over LibreMetaverse's capability URL infrastructure.
    /// Use <see cref="GetUriAsync"/> to request per-sim capability URIs, then do
    /// raw HTTP calls against them.
    /// </summary>
    public sealed class CapabilityHandler
    {
        private readonly SLNetworkManager _net;
        private readonly ILogger<CapabilityHandler> _log;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        public CapabilityHandler(SLNetworkManager net, ILogger<CapabilityHandler> log)
        {
            _net = net;
            _log = log;
        }

        public bool Has(string cap)
        {
            var sim = _net.Client.Network.CurrentSim;
            return sim?.Caps?.IsCapAvailable(cap) ?? false;
        }

        public Uri? GetUri(string cap)
        {
            var sim = _net.Client.Network.CurrentSim;
            return sim?.Caps?.CapabilityURI(cap);
        }

        /// <summary>Performs a GET on a named capability URI and returns the body bytes.</summary>
        public async Task<byte[]?> GetAsync(string cap)
        {
            var uri = GetUri(cap);
            if (uri == null)
            {
                _log.LogDebug("Cap {Cap} not available on current sim", cap);
                return null;
            }
            try
            {
                var resp = await _http.GetAsync(uri);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cap GET {Cap} failed", cap);
                return null;
            }
        }

        /// <summary>
        /// Fetches a texture via the GetTexture HTTP capability.
        /// Falls back to null if the cap is unavailable.
        /// </summary>
        public async Task<byte[]?> GetTextureAsync(UUID assetId)
        {
            var uri = GetUri("GetTexture");
            if (uri == null) return null;
            try
            {
                var url = new Uri($"{uri}?texture_id={assetId}");
                var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "GetTexture {Id} via cap failed", assetId);
                return null;
            }
        }
    }
}
