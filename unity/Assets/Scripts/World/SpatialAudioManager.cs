using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;

namespace SLQuest.World
{
    /// <summary>
    /// Handles in-world spatial audio:
    ///   - llTriggerSound / llPlaySound — one-shot 3D sounds
    ///   - Prim attached sounds (looping ambient audio on objects)
    ///
    /// Uses a pool of AudioSources placed at world positions.
    /// Audio data is fetched via Client.Assets.RequestAsset(AssetType.Sound)
    /// and decoded from OGG to Unity AudioClip.
    ///
    /// Inspector wiring:
    ///   audioSourcePrefab  — prefab with AudioSource (3D spatial blend, rolloff)
    ///   poolSize           — number of pooled AudioSources for one-shots
    ///   masterVolume       — global spatial audio volume scalar
    /// </summary>
    public sealed class SpatialAudioManager : MonoBehaviour
    {
        [SerializeField] private AudioSource audioSourcePrefab;
        [SerializeField] private int   poolSize     = 12;
        [SerializeField] [Range(0f,1f)] private float masterVolume = 1f;

        private SLNetworkManager      _net;
        private readonly List<AudioSource> _pool = new();
        private readonly Dictionary<UUID, AudioClip> _clipCache = new();

        // Attached-sound tracking: objectLocalId → AudioSource
        private readonly Dictionary<uint, AudioSource> _attached = new();

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            BuildPool();
        }

        private void Start()
        {
            if (_net?.Client == null) return;
            _net.Client.Sound.SoundTrigger       += OnSoundTrigger;
            _net.Client.Sound.AttachedSound       += OnAttachedSound;
            _net.Client.Objects.KillObject        += OnKillObject;
        }

        private void OnDestroy()
        {
            if (_net?.Client?.Sound != null)
            {
                _net.Client.Sound.SoundTrigger  -= OnSoundTrigger;
                _net.Client.Sound.AttachedSound -= OnAttachedSound;
            }
            if (_net?.Client?.Objects != null)
                _net.Client.Objects.KillObject -= OnKillObject;
        }

        public float MasterVolume
        {
            get => masterVolume;
            set { masterVolume = value; foreach (var s in _pool) s.volume = value; }
        }

        // ── LibreMetaverse callbacks (background thread) ───────────────────────

        private void OnSoundTrigger(object sender, SoundTriggerEventArgs e)
        {
            UUID soundId   = e.SoundID;
            float volume   = e.Gain;
            var  position  = e.Position;

            MainThreadDispatcher.Enqueue(() =>
            {
                Vector3 worldPos = new(position.X, position.Z, position.Y);
                PlayOneShotAt(soundId, worldPos, volume);
            });
        }

        private void OnAttachedSound(object sender, AttachedSoundEventArgs e)
        {
            UUID soundId    = e.SoundID;
            uint objectId   = e.ObjectID;
            float gain      = e.Gain;
            SoundFlags flags = e.Flags;

            MainThreadDispatcher.Enqueue(() =>
            {
                // Stop any existing attached sound on this object
                if (_attached.TryGetValue(objectId, out var existing))
                {
                    existing.Stop();
                    existing.clip = null;
                    _attached.Remove(objectId);
                }

                if (soundId == UUID.Zero) return;

                bool loop = (flags & SoundFlags.Loop) != 0;
                PlayAttached(soundId, objectId, gain, loop);
            });
        }

        private void OnKillObject(object sender, KillObjectEventArgs e)
        {
            uint lid = e.ObjectLocalID;
            MainThreadDispatcher.Enqueue(() =>
            {
                if (_attached.TryGetValue(lid, out var src))
                {
                    src.Stop();
                    src.clip = null;
                    _attached.Remove(lid);
                }
            });
        }

        // ── Playback ──────────────────────────────────────────────────────────

        private void PlayOneShotAt(UUID soundId, Vector3 worldPos, float volume)
        {
            FetchClip(soundId, clip =>
            {
                var src = GetPooledSource();
                if (src == null) return;
                src.transform.position = worldPos;
                src.volume = volume * masterVolume;
                src.loop   = false;
                src.clip   = clip;
                src.Play();
            });
        }

        private void PlayAttached(UUID soundId, uint objectLocalId, float gain, bool loop)
        {
            FetchClip(soundId, clip =>
            {
                // Find the prim in the scene for world-space position tracking
                var primGo = FindPrimById(objectLocalId);

                var src = Instantiate(audioSourcePrefab, primGo != null ? primGo.transform : transform);
                src.clip   = clip;
                src.loop   = loop;
                src.volume = gain * masterVolume;
                src.spatialBlend = 1f;
                src.Play();
                _attached[objectLocalId] = src;
            });
        }

        // ── Asset loading ─────────────────────────────────────────────────────

        private void FetchClip(UUID soundId, System.Action<AudioClip> callback)
        {
            if (_clipCache.TryGetValue(soundId, out var cached))
            {
                callback(cached);
                return;
            }

            _net.Client.Assets.RequestAsset(soundId, AssetType.Sound, false,
                (transfer, asset) =>
                {
                    if (asset?.AssetData == null) return;
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        StartCoroutine(OggClipLoader.Load(asset.AssetData, soundId.ToString(), clip =>
                        {
                            if (clip == null) return;
                            _clipCache[soundId] = clip;
                            callback(clip);
                        }));
                    });
                });
        }

        // ── Pool ──────────────────────────────────────────────────────────────

        private void BuildPool()
        {
            if (audioSourcePrefab == null) return;
            for (int i = 0; i < poolSize; i++)
            {
                var src = Instantiate(audioSourcePrefab, transform);
                src.playOnAwake  = false;
                src.spatialBlend = 1f;
                src.gameObject.SetActive(true);
                _pool.Add(src);
            }
        }

        private AudioSource GetPooledSource()
        {
            foreach (var s in _pool)
                if (!s.isPlaying) return s;
            // All busy — steal the oldest (first in list that is playing)
            return _pool.Count > 0 ? _pool[0] : null;
        }

        private static GameObject FindPrimById(uint localId)
        {
            foreach (var p in Object.FindObjectsOfType<SLPrimitive>())
                if (p.LocalId == localId) return p.gameObject;
            return null;
        }
    }
}
