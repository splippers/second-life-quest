using System;
using System.Collections;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.Assets;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;

namespace SLQuest.Scripting
{
    public sealed class ActiveGesture
    {
        public UUID          ItemId    { get; }
        public UUID          AssetId   { get; }
        public string        Name      { get; }
        public AssetGesture  Asset     { get; internal set; }
        public bool          IsPlaying { get; internal set; }
        public bool          IsActive  { get; set; } = true;

        public string TriggerPhrase => Asset?.Trigger  ?? string.Empty;
        public short  VirtualKey    => Asset?.TriggerKey ?? -1;
        public uint   KeyMask       => Asset?.TriggerKeyMask ?? 0;

        public ActiveGesture(UUID itemId, UUID assetId, string name)
        {
            ItemId  = itemId;
            AssetId = assetId;
            Name    = name;
        }
    }

    /// <summary>
    /// Loads the agent's active gestures from the login reply, downloads their
    /// asset data, and drives step-by-step playback via a coroutine.
    ///
    /// Triggers:
    ///   - Keyboard  — F1-F12 (with optional Shift/Ctrl/Alt modifier)
    ///   - Chat text — exact-match against <see cref="AssetGesture.Trigger"/>
    ///   - Direct    — <see cref="PlayGesture(UUID)"/> from UI
    /// </summary>
    public sealed class GestureManager : MonoBehaviour
    {
        // Windows VK codes used by SL gesture assets → Unity KeyCode
        private static readonly Dictionary<short, KeyCode> VKtoUnity = new()
        {
            { 0x70, KeyCode.F1  }, { 0x71, KeyCode.F2  }, { 0x72, KeyCode.F3  },
            { 0x73, KeyCode.F4  }, { 0x74, KeyCode.F5  }, { 0x75, KeyCode.F6  },
            { 0x76, KeyCode.F7  }, { 0x77, KeyCode.F8  }, { 0x78, KeyCode.F9  },
            { 0x79, KeyCode.F10 }, { 0x7A, KeyCode.F11 }, { 0x7B, KeyCode.F12 },
        };

        // SL modifier mask bits (same as Windows KEY_MASK_* constants in the viewer)
        private const uint MASK_SHIFT = 0x01;
        private const uint MASK_CTRL  = 0x02;
        private const uint MASK_ALT   = 0x04;

        private SLNetworkManager _net;

        private readonly Dictionary<UUID, ActiveGesture> _gestures = new();

        public event Action OnGestureListChanged;
        public IReadOnlyDictionary<UUID, ActiveGesture> Gestures => _gestures;

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
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
            // Inventory may not be fully populated yet; give it a tick to settle
            MainThreadDispatcher.Enqueue(LoadActiveGestures);
        }

        // ── Loading ───────────────────────────────────────────────────────────

        /// <summary>
        /// Reads <see cref="AgentManager.ActiveGestures"/> from the login reply and
        /// kicks off asset downloads for each one. Safe to call again to refresh.
        /// </summary>
        public void LoadActiveGestures()
        {
            _gestures.Clear();

            var active = _net?.Client?.Self?.ActiveGestures;
            if (active == null) return;

            var store = _net.Client.Inventory.Store;

            foreach (var kvp in active)
            {
                UUID itemId  = kvp.Key;
                UUID assetId = kvp.Value;
                if (assetId == UUID.Zero) continue;

                string name = itemId.ToString();
                if (store.Contains(itemId) && store[itemId] is InventoryItem item)
                    name = item.Name;

                var ag = new ActiveGesture(itemId, assetId, name);
                _gestures[itemId] = ag;

                _net.Client.Assets.RequestAsset(
                    assetId, AssetType.Gesture, false,
                    (transfer, asset) => OnAssetReceived(itemId, transfer, asset));
            }

            OnGestureListChanged?.Invoke();
        }

        private void OnAssetReceived(UUID itemId, AssetDownload transfer, Asset asset)
        {
            if (!transfer.Success) return;
            if (asset is not AssetGesture gesture) return;
            if (!gesture.Decode()) return;

            MainThreadDispatcher.Enqueue(() =>
            {
                if (_gestures.TryGetValue(itemId, out var ag))
                {
                    ag.Asset = gesture;
                    OnGestureListChanged?.Invoke();
                }
            });
        }

        // ── Keyboard trigger polling ──────────────────────────────────────────

        private void Update()
        {
            if (_net == null || !_net.IsInWorld) return;

            foreach (var ag in _gestures.Values)
            {
                if (!ag.IsActive || ag.IsPlaying || ag.Asset == null) continue;

                short vk = ag.VirtualKey;
                if (vk <= 0) continue;
                if (!VKtoUnity.TryGetValue(vk, out var key)) continue;
                if (!Input.GetKeyDown(key)) continue;

                uint mask = ag.KeyMask;
                bool shiftOk = (mask & MASK_SHIFT) == 0 ||
                               Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                bool ctrlOk  = (mask & MASK_CTRL)  == 0 ||
                               Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                bool altOk   = (mask & MASK_ALT)   == 0 ||
                               Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

                if (shiftOk && ctrlOk && altOk)
                    StartCoroutine(PlayCoroutine(ag));
            }
        }

        // ── Chat trigger interception ─────────────────────────────────────────

        /// <summary>
        /// Returns true if <paramref name="text"/> exactly matches a gesture trigger
        /// phrase and the gesture was started. ChatPanel must skip normal send in that case.
        /// </summary>
        public bool TryInterceptChat(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string trimmed = text.Trim();
            foreach (var ag in _gestures.Values)
            {
                if (!ag.IsActive || ag.Asset == null || ag.IsPlaying) continue;

                string trigger = ag.TriggerPhrase;
                if (string.IsNullOrEmpty(trigger)) continue;
                if (!string.Equals(trimmed, trigger.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                // Send the gesture's replacement text (may be empty)
                string replace = ag.Asset.Replace;
                if (!string.IsNullOrEmpty(replace))
                    _net.Client.Self.Chat(replace, 0, ChatType.Normal);

                StartCoroutine(PlayCoroutine(ag));
                return true;
            }
            return false;
        }

        // ── Direct API ────────────────────────────────────────────────────────

        /// <summary>Play a gesture by its inventory item UUID.</summary>
        public void PlayGesture(UUID itemId)
        {
            if (!_gestures.TryGetValue(itemId, out var ag)) return;
            if (ag.Asset == null || ag.IsPlaying) return;
            StartCoroutine(PlayCoroutine(ag));
        }

        // ── Playback coroutine ────────────────────────────────────────────────

        private IEnumerator PlayCoroutine(ActiveGesture ag)
        {
            ag.IsPlaying = true;
            EventBus.Publish(new GestureTriggeredEvent(ag.ItemId, ag.Name));

            bool finished = false;
            foreach (var step in ag.Asset.Sequence)
            {
                switch (step)
                {
                    case GestureStepAnimation anim:
                        _net.Client.Self.AnimationStart(anim.ID, true);
                        if (anim.WaitForAnimation)
                            yield return new WaitForSeconds(2f);
                        break;

                    case GestureStepSound snd:
                        _net.Client.Sound.SoundTrigger(
                            snd.ID, _net.Client.Self.AgentID,
                            UUID.Zero, UUID.Zero, 1.0f,
                            _net.Client.Network.CurrentSim,
                            _net.Client.Self.SimPosition);
                        break;

                    case GestureStepChat chat:
                        _net.Client.Self.Chat(chat.Text, 0, ChatType.Normal);
                        break;

                    case GestureStepWait wait:
                        if (wait.WaitTime > 0f)
                            yield return new WaitForSeconds(wait.WaitTime);
                        break;

                    case GestureStepEnd:
                        finished = true;
                        break;
                }

                if (finished) break;
            }

            ag.IsPlaying = false;
        }
    }
}
