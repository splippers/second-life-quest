using System;
using System.Collections;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.Assets;
using SLQuest.Core;
using SLQuest.Assets;
using UnityEngine;

namespace SLQuest.Avatar
{
    /// <summary>
    /// Manages avatar visual appearance: visual params (shape/skin sliders),
    /// wearables (clothing/body parts), and baked texture layers.
    ///
    /// The SL appearance pipeline:
    ///   1. Fetch wearables from inventory
    ///   2. Download each wearable's asset
    ///   3. Bake texture layers (skin, hair, eyes, upper, lower, skirt)
    ///   4. Upload baked textures and set AgentSetAppearance
    ///
    /// This implementation downloads baked textures from the server rather than
    /// performing full client-side baking (which requires J2K encode/decode of
    /// every layer). Full baking is phased in via AppearanceBaker.cs.
    /// </summary>
    public sealed class AvatarAppearance : MonoBehaviour
    {
        [Serializable]
        public struct AvatarLayer
        {
            public BakeType bakeType;
            public Renderer targetRenderer;
            public int materialIndex;
        }

        [SerializeField] private AvatarLayer[] layers;

        private SLNetworkManager _net;
        private AssetManager     _assets;

        private void Awake()
        {
            _net    = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            _assets = SLApplication.Instance?.Assets  ?? FindObjectOfType<AssetManager>();
        }

        private void Start()
        {
            _net.Client.Appearance.AppearanceSet += OnAppearanceSet;
        }

        private void OnDestroy()
        {
            if (_net?.Client?.Appearance != null)
                _net.Client.Appearance.AppearanceSet -= OnAppearanceSet;
        }

        private void OnAppearanceSet(object sender, AppearanceSetEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                if (e.Success)
                    StartCoroutine(FetchBakedTextures());
            });
        }

        private IEnumerator FetchBakedTextures()
        {
            var textures = _net.Client.Appearance.GetCachedBakes();

            foreach (var layer in layers)
            {
                if (!textures.TryGetValue(layer.bakeType, out UUID texId)) continue;
                if (texId == UUID.Zero) continue;

                var handle = _assets.RequestTexture(texId);
                yield return new WaitUntil(() => handle.IsReady);
                if (handle.Texture == null) continue;

                var mats = layer.targetRenderer.materials;
                mats[layer.materialIndex].mainTexture = handle.Texture;
                layer.targetRenderer.materials = mats;
            }
        }

        /// <summary>Triggers a full appearance re-send to the grid.</summary>
        public void RefreshAppearance()
        {
            _net.Client.Appearance.RequestSetAppearance(true);
        }
    }
}
