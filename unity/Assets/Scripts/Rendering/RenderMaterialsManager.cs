using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using SLQuest.Assets;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;
using UnityEngine.Networking;

namespace SLQuest.Rendering
{
    /// <summary>
    /// Fetches and caches Second Life PBSM data from the RenderMaterials capability.
    ///
    /// Protocol summary:
    ///   GET  /cap/RenderMaterials   (body = zlib-compressed binary LLSD list of material UUIDs)
    ///   Response = zlib-compressed binary LLSD:
    ///     [ { "ID": uuid, "Material": { ... pbsm fields ... } }, ... ]
    ///
    /// MaterialIDs come from Primitive.TextureEntry face MaterialID fields.
    /// Prims call RequestMaterial(id, callback); the manager batches and fetches.
    /// </summary>
    public sealed class RenderMaterialsManager : MonoBehaviour
    {
        private CapabilityHandler _caps;
        private AssetManager      _assets;

        // Cached resolved materials
        private readonly Dictionary<UUID, PBSMaterial> _cache = new();
        // Pending callbacks waiting for a given material UUID
        private readonly Dictionary<UUID, List<Action<PBSMaterial>>> _pending = new();
        // UUIDs queued for the next batch fetch
        private readonly HashSet<UUID> _fetchQueue = new();

        private Coroutine _batchCoroutine;

        private void Awake()
        {
            _caps   = SLApplication.Instance?.Caps   ?? FindObjectOfType<CapabilityHandler>();
            _assets = SLApplication.Instance?.Assets ?? FindObjectOfType<AssetManager>();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Request PBSM data for a material UUID.
        /// If already cached the callback fires synchronously; otherwise it is
        /// deferred until the next batch fetch round-trip completes.
        /// </summary>
        public void RequestMaterial(UUID materialId, Action<PBSMaterial> callback)
        {
            if (materialId == UUID.Zero) return;

            if (_cache.TryGetValue(materialId, out var cached))
            {
                callback(cached);
                return;
            }

            if (!_pending.TryGetValue(materialId, out var list))
            {
                list = new List<Action<PBSMaterial>>();
                _pending[materialId] = list;
            }
            list.Add(callback);

            _fetchQueue.Add(materialId);
            if (_batchCoroutine == null)
                _batchCoroutine = StartCoroutine(BatchFetch());
        }

        // ── Batch fetch ───────────────────────────────────────────────────────

        private IEnumerator BatchFetch()
        {
            // Accumulate for one frame so multiple prims can batch together
            yield return null;

            if (_fetchQueue.Count == 0) { _batchCoroutine = null; yield break; }

            if (_caps == null || !_caps.TryGetCap("RenderMaterials", out var capUri))
            {
                Debug.LogWarning("[RenderMaterials] Cap not available; skipping fetch.");
                _fetchQueue.Clear();
                _batchCoroutine = null;
                yield break;
            }

            var ids = new List<UUID>(_fetchQueue);
            _fetchQueue.Clear();
            _batchCoroutine = null;

            // Build binary LLSD list of UUIDs, then zlib-compress it
            byte[] body = BuildCompressedIdList(ids);
            if (body == null) yield break;

            using var req = new UnityWebRequest(capUri.ToString(), "GET");
            req.uploadHandler   = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/octet-stream");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[RenderMaterials] Fetch failed: {req.error}");
                yield break;
            }

            ParseResponse(req.downloadHandler.data);
        }

        // ── LLSD / zlib helpers ───────────────────────────────────────────────

        private static byte[] BuildCompressedIdList(List<UUID> ids)
        {
            try
            {
                // Build an LLSD array of binary-16 UUIDs
                var osdList = new OSDArray(ids.Count);
                foreach (var id in ids)
                    osdList.Add(OSD.FromBinary(id.GetBytes()));

                byte[] llsdBin = OSDParser.SerializeLLSDBinary(osdList);
                return ZlibCompress(llsdBin);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return null;
            }
        }

        private void ParseResponse(byte[] compressed)
        {
            try
            {
                byte[] raw = ZlibDecompress(compressed);
                if (raw == null) return;

                var osd = OSDParser.DeserializeLLSDBinary(raw);
                if (osd is not OSDArray arr) return;

                foreach (var item in arr)
                {
                    if (item is not OSDMap entry) continue;
                    if (!entry.ContainsKey("ID") || !entry.ContainsKey("Material")) continue;

                    var id   = entry["ID"].AsUUID();
                    var mat  = PBSMaterial.FromOSD(entry["Material"] as OSDMap ?? new OSDMap());
                    _cache[id] = mat;

                    if (!_pending.TryGetValue(id, out var callbacks)) continue;
                    _pending.Remove(id);
                    foreach (var cb in callbacks)
                    {
                        try { cb(mat); }
                        catch (Exception ex) { Debug.LogException(ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private static byte[] ZlibCompress(byte[] data)
        {
            using var ms  = new MemoryStream();
            // zlib header (CM=8, CINFO=7, FCHECK for no dict/default compression)
            ms.WriteByte(0x78);
            ms.WriteByte(0x9C);
            using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(data, 0, data.Length);
            // Adler-32 checksum (required by zlib)
            uint adler = Adler32(data);
            byte[] chk = BitConverter.GetBytes(adler);
            ms.WriteByte(chk[3]); ms.WriteByte(chk[2]);
            ms.WriteByte(chk[1]); ms.WriteByte(chk[0]);
            return ms.ToArray();
        }

        private static byte[] ZlibDecompress(byte[] data)
        {
            if (data == null || data.Length < 3) return null;
            // Skip 2-byte zlib header; ignore 4-byte checksum at end
            using var src = new MemoryStream(data, 2, data.Length - 6);
            using var dst = new MemoryStream();
            using var deflate = new DeflateStream(src, CompressionMode.Decompress);
            deflate.CopyTo(dst);
            return dst.ToArray();
        }

        private static uint Adler32(byte[] data)
        {
            const uint MOD = 65521;
            uint a = 1, b = 0;
            foreach (byte by in data)
            {
                a = (a + by) % MOD;
                b = (b + a) % MOD;
            }
            return (b << 16) | a;
        }
    }
}
