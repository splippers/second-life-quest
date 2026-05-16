using OpenMetaverse;
using OpenMetaverse.Assets;
using OpenMetaverse.Imaging;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using SLQuest.Network;

namespace SLQuest.Assets
{
    public sealed class TextureData
    {
        public int    Width  { get; init; }
        public int    Height { get; init; }
        public byte[] Rgba   { get; init; } = [];
    }

    public sealed class AssetManager
    {
        private readonly SLNetworkManager    _net;
        private readonly CapabilityHandler   _caps;
        private readonly ILogger<AssetManager> _log;
        private readonly string              _cacheDir;

        private readonly ConcurrentDictionary<Guid, TextureData>                         _memCache  = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<TextureData?>> _inFlight  = new();

        public AssetManager(SLNetworkManager net, CapabilityHandler caps, ILogger<AssetManager> log)
        {
            _net  = net;
            _caps = caps;
            _log  = log;

            // /data/data/<appId>/cache is guaranteed writable on Android
            _cacheDir = Path.Combine("/data/data/com.slquest.viewer/cache", "SLTextures");
            Directory.CreateDirectory(_cacheDir);
        }

        public bool TryGet(Guid id, out TextureData? data) => _memCache.TryGetValue(id, out data);

        /// <summary>
        /// Returns cached texture immediately if available; otherwise queues a network
        /// request and returns the task.  Safe to call from any thread.
        /// </summary>
        public Task<TextureData?> RequestAsync(Guid id)
        {
            if (_memCache.TryGetValue(id, out var cached))
                return Task.FromResult<TextureData?>(cached);

            // GetOrAdd is atomic — only one TCS is created per ID
            var tcs = _inFlight.GetOrAdd(id, _ =>
            {
                var t = new TaskCompletionSource<TextureData?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(() => StartFetch(id, t));
                return t;
            });

            return tcs.Task;
        }

        // ── Fetch pipeline ────────────────────────────────────────────────────

        private async Task StartFetch(Guid id, TaskCompletionSource<TextureData?> tcs)
        {
            // 1. Disk cache
            var diskPath = DiskPath(id);
            if (File.Exists(diskPath))
            {
                var fromDisk = LoadDisk(id, diskPath);
                if (fromDisk != null) { Complete(id, fromDisk, tcs); return; }
            }

            // 2. GetTexture capability (faster than UDP)
            var j2c = await _caps.GetTextureAsync(new UUID(id));
            if (j2c != null && j2c.Length > 0)
            {
                var decoded = Decode(id, j2c);
                if (decoded != null) { SaveDisk(id, diskPath, decoded); Complete(id, decoded, tcs); return; }
            }

            // 3. UDP asset pipeline
            var udpTcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _net.Client.Assets.RequestImage(new UUID(id), ImageType.Normal,
                (state, asset) =>
                {
                    if (state == TextureRequestState.Finished && asset?.AssetData != null)
                        udpTcs.TrySetResult(asset.AssetData);
                    else if (state is TextureRequestState.Timeout
                             or TextureRequestState.NotFound
                             or TextureRequestState.Aborted)
                        udpTcs.TrySetResult(null);
                    // Progress states arrive multiple times — ignore until terminal
                });

            var udpBytes = await udpTcs.Task;
            if (udpBytes != null)
            {
                var decoded = Decode(id, udpBytes);
                if (decoded != null) { SaveDisk(id, diskPath, decoded); Complete(id, decoded, tcs); return; }
            }

            _log.LogDebug("Texture {Id}: all sources failed", id);
            tcs.TrySetResult(null);
            _inFlight.TryRemove(id, out _);
        }

        private TextureData? Decode(Guid id, byte[] j2c)
        {
            try
            {
                if (!OpenJPEG.DecodeToImage(j2c, out ManagedImage img)) return null;

                var rgba = new byte[img.Width * img.Height * 4];
                for (int i = 0, n = img.Width * img.Height; i < n; i++)
                {
                    rgba[i * 4]     = img.Red?[i]   ?? 255;
                    rgba[i * 4 + 1] = img.Green?[i] ?? 255;
                    rgba[i * 4 + 2] = img.Blue?[i]  ?? 255;
                    rgba[i * 4 + 3] = img.Alpha?[i] ?? 255;
                }
                return new TextureData { Width = img.Width, Height = img.Height, Rgba = rgba };
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "J2K decode failed for {Id}", id);
                return null;
            }
        }

        // ── Disk cache ────────────────────────────────────────────────────────

        private string DiskPath(Guid id) => Path.Combine(_cacheDir, id.ToString("N") + ".raw");

        private TextureData? LoadDisk(Guid id, string path)
        {
            try
            {
                var b = File.ReadAllBytes(path);
                if (b.Length < 8) return null;
                int w = BitConverter.ToInt32(b, 0);
                int h = BitConverter.ToInt32(b, 4);
                if (b.Length != 8 + w * h * 4) return null;
                return new TextureData { Width = w, Height = h, Rgba = b[8..] };
            }
            catch { return null; }
        }

        private void SaveDisk(Guid id, string path, TextureData d)
        {
            try
            {
                using var fs = File.Create(path);
                fs.Write(BitConverter.GetBytes(d.Width));
                fs.Write(BitConverter.GetBytes(d.Height));
                fs.Write(d.Rgba);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Failed to write texture disk cache for {Id}", id);
            }
        }

        private void Complete(Guid id, TextureData data, TaskCompletionSource<TextureData?> tcs)
        {
            _memCache[id] = data;
            _inFlight.TryRemove(id, out _);
            tcs.TrySetResult(data);
        }
    }
}
