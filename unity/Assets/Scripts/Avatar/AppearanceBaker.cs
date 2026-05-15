using System;
using System.Collections;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.Assets;
using SLQuest.Assets;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;

namespace SLQuest.Avatar
{
    /// <summary>
    /// Client-side avatar texture baking.
    ///
    /// Supplements <see cref="AvatarAppearance"/>, which applies server-baked
    /// textures after login. This baker composites the wearable texture layers
    /// locally so the avatar looks correct immediately — without waiting for a
    /// server bake cycle. The result is applied to the local avatar's renderers
    /// but NOT uploaded (the server-bake path in AvatarAppearance handles upload).
    ///
    /// Pipeline per bake layer:
    ///   1. Collect wearables for this layer (in paint order)
    ///   2. Download each wearable asset (AssetClothing / AssetBodypart)
    ///   3. Collect the texture UUIDs declared in each wearable
    ///   4. Download each texture
    ///   5. CPU alpha-blend the textures on to a 512×512 accumulator
    ///   6. Apply the resulting Texture2D to the avatar renderer slot
    ///
    /// This is a simplified baker. A production implementation would also:
    ///   - Apply visual param morphs to the UV mapping before blending
    ///   - Encode the result as JPEG2000 for upload
    ///   - Handle tint colours from visual params
    /// </summary>
    public sealed class AppearanceBaker : MonoBehaviour
    {
        private const int BAKE_SIZE = 512;

        [Header("Layer → Renderer wiring (match AvatarAppearance.layers order)")]
        [SerializeField] private BakeTarget[] targets;

        [Serializable]
        public struct BakeTarget
        {
            public BakeType  bakeType;
            public Renderer  renderer;
            public int       materialIndex;
        }

        private SLNetworkManager _net;
        private AssetManager     _assets;

        // AvatarTextureIndex groups that contribute to each BakeType, in composite order
        private static readonly Dictionary<BakeType, AvatarTextureIndex[]> BakeGroups = new()
        {
            [BakeType.Head] = new[]
            {
                AvatarTextureIndex.HeadBodyPaint,
                AvatarTextureIndex.HeadTattoo,
                AvatarTextureIndex.HeadAlpha,
            },
            [BakeType.UpperBody] = new[]
            {
                AvatarTextureIndex.UpperBodyPaint,
                AvatarTextureIndex.UpperUndershirt,
                AvatarTextureIndex.UpperShirt,
                AvatarTextureIndex.UpperJacket,
                AvatarTextureIndex.UpperTattoo,
                AvatarTextureIndex.UpperAlpha,
            },
            [BakeType.LowerBody] = new[]
            {
                AvatarTextureIndex.LowerBodyPaint,
                AvatarTextureIndex.LowerUnderwear,
                AvatarTextureIndex.LowerPants,
                AvatarTextureIndex.LowerJacket,
                AvatarTextureIndex.LowerTattoo,
                AvatarTextureIndex.LowerAlpha,
            },
            [BakeType.Eyes] = new[]
            {
                AvatarTextureIndex.EyesIris,
                AvatarTextureIndex.EyesAlpha,
            },
            [BakeType.Hair] = new[]
            {
                AvatarTextureIndex.Hair,
                AvatarTextureIndex.HairAlpha,
            },
            [BakeType.Skirt] = new[]
            {
                AvatarTextureIndex.Skirt,
                AvatarTextureIndex.SkirtAlpha,
            },
        };

        private void Awake()
        {
            _net    = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            _assets = SLApplication.Instance?.Assets  ?? FindObjectOfType<AssetManager>();
        }

        private void Start()
        {
            EventBus.Subscribe<LoginSucceededEvent>(OnLogin);
        }

        private void OnDestroy()
        {
            EventBus.Unsubscribe<LoginSucceededEvent>(OnLogin);
        }

        private void OnLogin(LoginSucceededEvent _)
        {
            // Give the network a moment to populate wearables, then bake
            StartCoroutine(DelayedBake(3f));
        }

        private IEnumerator DelayedBake(float delay)
        {
            yield return new WaitForSeconds(delay);
            yield return StartCoroutine(BakeAll());
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Trigger a full local bake of all configured layers.
        /// Call after wearable changes for immediate visual feedback.
        /// </summary>
        public void RequestBake() => StartCoroutine(BakeAll());

        // ── Bake pipeline ─────────────────────────────────────────────────────

        private IEnumerator BakeAll()
        {
            // Snapshot wearables once; AppearanceManager keeps them thread-safe
            var wearableMap = BuildWearableTextureMap();
            if (wearableMap == null || wearableMap.Count == 0)
            {
                Debug.Log("[Baker] No wearable textures found — skipping local bake");
                yield break;
            }

            Debug.Log($"[Baker] Starting local bake: {targets?.Length ?? 0} layer(s)");

            foreach (var target in targets ?? Array.Empty<BakeTarget>())
            {
                if (!BakeGroups.TryGetValue(target.bakeType, out var slots)) continue;

                var layerTexIds = new List<UUID>();
                foreach (var slot in slots)
                {
                    if (wearableMap.TryGetValue(slot, out UUID texId) && texId != UUID.Zero)
                        layerTexIds.Add(texId);
                }

                if (layerTexIds.Count == 0) continue;

                yield return StartCoroutine(BakeLayer(target, layerTexIds));
            }

            EventBus.Publish(new BakeCompleteEvent());
            Debug.Log("[Baker] Local bake complete");
        }

        private IEnumerator BakeLayer(BakeTarget target, List<UUID> texIds)
        {
            // Accumulator starts as transparent black
            var accumulator = new Color32[BAKE_SIZE * BAKE_SIZE];
            for (int i = 0; i < accumulator.Length; i++)
                accumulator[i] = new Color32(0, 0, 0, 0);

            foreach (UUID texId in texIds)
            {
                var handle = _assets.RequestTexture(texId);
                yield return new WaitUntil(() => handle.IsReady);

                if (handle.Texture == null) continue;

                // Resize to BAKE_SIZE if needed
                Texture2D layer = EnsureSize(handle.Texture, BAKE_SIZE, BAKE_SIZE);
                Color32[] pixels = layer.GetPixels32();

                AlphaOver(accumulator, pixels);

                if (layer != handle.Texture)
                    Destroy(layer);
            }

            // Apply to renderer
            var resultTex = new Texture2D(BAKE_SIZE, BAKE_SIZE, TextureFormat.RGBA32, false)
            {
                name = $"LocalBake_{target.bakeType}",
                wrapMode = TextureWrapMode.Clamp,
            };
            resultTex.SetPixels32(accumulator);
            resultTex.Apply(false);

            if (target.renderer != null)
            {
                var mats = target.renderer.materials;
                if (target.materialIndex < mats.Length)
                {
                    mats[target.materialIndex].mainTexture = resultTex;
                    target.renderer.materials = mats;
                }
            }
        }

        // ── Wearable texture map ──────────────────────────────────────────────

        /// <summary>
        /// Asks LibreMetaverse for the current wearable set, downloads each asset,
        /// and returns a mapping of AvatarTextureIndex → texture UUID.
        /// </summary>
        private Dictionary<AvatarTextureIndex, UUID> BuildWearableTextureMap()
        {
            var appearances = _net?.Client?.Appearance;
            if (appearances == null) return null;

            var map = new Dictionary<AvatarTextureIndex, UUID>();

            // Wearables already downloaded by AppearanceManager
            var wearables = appearances.GetWearables();
            if (wearables == null) return map;

            foreach (var kvp in wearables)
            {
                WearableData wd = kvp.Value;
                if (wd?.Asset == null) continue;

                switch (wd.Asset)
                {
                    case AssetClothing clothing:
                        if (!clothing.Decode()) continue;
                        foreach (var tex in clothing.Textures)
                            map[tex.Key] = tex.Value;
                        break;

                    case AssetBodypart bodypart:
                        if (!bodypart.Decode()) continue;
                        foreach (var tex in bodypart.Textures)
                            map[tex.Key] = tex.Value;
                        break;
                }
            }

            return map;
        }

        // ── CPU compositing ───────────────────────────────────────────────────

        /// <summary>
        /// Alpha-over blending: dst = src_alpha * src + (1 - src_alpha) * dst
        /// Applied in-place to <paramref name="dst"/>.
        /// </summary>
        private static void AlphaOver(Color32[] dst, Color32[] src)
        {
            int len = Math.Min(dst.Length, src.Length);
            for (int i = 0; i < len; i++)
            {
                float a  = src[i].a / 255f;
                float ia = 1f - a;
                dst[i].r = (byte)(a * src[i].r + ia * dst[i].r);
                dst[i].g = (byte)(a * src[i].g + ia * dst[i].g);
                dst[i].b = (byte)(a * src[i].b + ia * dst[i].b);
                // Porter-Duff "over" alpha: αo = αs + αd*(1−αs)
                dst[i].a = (byte)Math.Min(255, src[i].a + (int)(dst[i].a * ia));
            }
        }

        /// <summary>Returns a resized copy if the texture isn't already the target size.</summary>
        private static Texture2D EnsureSize(Texture2D src, int w, int h)
        {
            if (src.width == w && src.height == h) return src;

            var rt  = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);

            var prev = RenderTexture.active;
            RenderTexture.active = rt;

            var dst = new Texture2D(w, h, TextureFormat.RGBA32, false);
            dst.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            dst.Apply(false);

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            return dst;
        }
    }
}
