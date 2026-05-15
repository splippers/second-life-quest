using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using OpenMetaverse;
using OpenMetaverse.Assets;
using SLQuest.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace SLQuest.Assets
{
    // ── Request handles ───────────────────────────────────────────────────────

    public sealed class TextureHandle
    {
        public bool      IsReady  { get; internal set; }
        public Texture2D Texture  { get; internal set; }
        public string    Error    { get; internal set; }
    }

    public sealed class MeshHandle
    {
        public bool  IsReady { get; internal set; }
        public Mesh  Mesh    { get; internal set; }
        public string Error  { get; internal set; }
    }

    /// <summary>
    /// Downloads and caches textures and mesh assets from the Second Life asset system.
    /// Uses the HTTP GetTexture / GetMesh2 capability endpoints for efficiency.
    /// Falls back to the UDP asset pipeline if caps are unavailable.
    /// </summary>
    public sealed class AssetManager : MonoBehaviour
    {
        [Tooltip("On-disk cache root")]
        [SerializeField] private string cacheRoot = "SLCache";

        private string _textureCacheDir;
        private string _meshCacheDir;

        private SLNetworkManager _net;

        // In-flight and in-cache registries prevent duplicate downloads
        private readonly Dictionary<UUID, TextureHandle> _texHandles = new();
        private readonly Dictionary<UUID, MeshHandle>    _meshHandles = new();

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();

            _textureCacheDir = Path.Combine(Application.persistentDataPath, cacheRoot, "textures");
            _meshCacheDir    = Path.Combine(Application.persistentDataPath, cacheRoot, "meshes");

            Directory.CreateDirectory(_textureCacheDir);
            Directory.CreateDirectory(_meshCacheDir);
        }

        // ── Public API ────────────────────────────────────────────────────────

        public TextureHandle RequestTexture(UUID id)
        {
            if (id == UUID.Zero) return Ready(new TextureHandle { IsReady = true });

            if (_texHandles.TryGetValue(id, out var existing)) return existing;

            var handle = new TextureHandle();
            _texHandles[id] = handle;
            StartCoroutine(FetchTexture(id, handle));
            return handle;
        }

        public MeshHandle RequestMesh(UUID id)
        {
            if (id == UUID.Zero) return new MeshHandle { IsReady = true };

            if (_meshHandles.TryGetValue(id, out var existing)) return existing;

            var handle = new MeshHandle();
            _meshHandles[id] = handle;
            StartCoroutine(FetchMesh(id, handle));
            return handle;
        }

        // ── Texture pipeline ──────────────────────────────────────────────────

        private IEnumerator FetchTexture(UUID id, TextureHandle handle)
        {
            // 1. Disk cache
            string cachePath = Path.Combine(_textureCacheDir, id + ".j2c");
            if (File.Exists(cachePath))
            {
                yield return StartCoroutine(LoadTextureFromDisk(cachePath, handle));
                yield break;
            }

            // 2. HTTP capability
            var caps = SLApplication.Instance?.GetComponent<Network.CapabilityHandler>();
            if (caps != null && caps.TryGetCap("GetTexture", out var capUri))
            {
                string url = $"{capUri}?texture_id={id}";
                using var req = UnityWebRequestTexture.GetTexture(url);
                req.SetRequestHeader("Accept", "image/x-j2c");
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    var tex = DownloadHandlerTexture.GetContent(req);
                    // Also cache raw bytes
                    File.WriteAllBytes(cachePath, req.downloadHandler.data);
                    handle.Texture = tex;
                    handle.IsReady = true;
                    yield break;
                }
            }

            // 3. UDP fallback via libopenmetaverse
            bool done = false;
            _net.Client.Assets.RequestImage(id, ImageType.Normal, (state, asset) =>
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (state == TextureRequestState.Finished && asset != null)
                    {
                        File.WriteAllBytes(cachePath, asset.AssetData);
                        handle.Texture = DecodeJ2K(asset.AssetData, id.ToString());
                    }
                    else
                    {
                        handle.Error = $"Texture {id} failed: {state}";
                    }
                    handle.IsReady = true;
                    done = true;
                });
            });

            yield return new WaitUntil(() => done);
        }

        private IEnumerator LoadTextureFromDisk(string path, TextureHandle handle)
        {
            byte[] data = File.ReadAllBytes(path);
            handle.Texture = DecodeJ2K(data, Path.GetFileNameWithoutExtension(path));
            handle.IsReady = true;
            yield break;
        }

        private Texture2D DecodeJ2K(byte[] j2kData, string name)
        {
            try
            {
                // Use libopenmetaverse's J2K decoder
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (OpenMetaverse.Imaging.OpenJPEG.DecodeToImage(j2kData, out var image))
                {
                    tex.Reinitialize(image.Width, image.Height);
                    tex.SetPixels32(ConvertPixels(image));
                    tex.Apply(true);
                    tex.name = name;
                }
                return tex;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Assets] J2K decode failed for {name}: {ex.Message}");
                return Texture2D.whiteTexture;
            }
        }

        private static Color32[] ConvertPixels(OpenMetaverse.Imaging.ManagedImage img)
        {
            var pixels = new Color32[img.Width * img.Height];
            bool hasAlpha = (img.Channels & OpenMetaverse.Imaging.ManagedImage.ImageChannels.Alpha) != 0;

            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(
                    img.Red[i], img.Green[i], img.Blue[i],
                    hasAlpha ? img.Alpha[i] : (byte)255);
            }
            return pixels;
        }

        // ── Mesh pipeline ─────────────────────────────────────────────────────

        private IEnumerator FetchMesh(UUID id, MeshHandle handle)
        {
            string cachePath = Path.Combine(_meshCacheDir, id + ".llmesh");

            byte[] meshData = null;

            if (File.Exists(cachePath))
            {
                meshData = File.ReadAllBytes(cachePath);
            }
            else
            {
                var caps = SLApplication.Instance?.GetComponent<Network.CapabilityHandler>();
                if (caps != null && caps.TryGetCap("GetMesh2", out var capUri))
                {
                    string url = $"{capUri}?mesh_id={id}";
                    using var req = new UnityWebRequest(url, "GET");
                    req.downloadHandler = new DownloadHandlerBuffer();
                    yield return req.SendWebRequest();

                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        meshData = req.downloadHandler.data;
                        File.WriteAllBytes(cachePath, meshData);
                    }
                }
            }

            if (meshData != null)
            {
                try
                {
                    handle.Mesh = MeshDecoder.Decode(meshData, id.ToString());
                }
                catch (Exception ex)
                {
                    handle.Error = ex.Message;
                    Debug.LogWarning($"[Assets] Mesh decode failed {id}: {ex.Message}");
                }
            }
            else
            {
                handle.Error = $"Mesh {id} unavailable";
            }

            handle.IsReady = true;
        }

        private static T Ready<T>(T h) => h;
    }
}
