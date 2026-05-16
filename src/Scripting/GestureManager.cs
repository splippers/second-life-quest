using OpenMetaverse;
using OpenMetaverse.Assets;
using SLQuest.Network;
using SLQuest.Assets;

namespace SLQuest.Scripting
{
    /// <summary>
    /// Plays gestures: resolves the asset, sequences animation + sound + chat triggers,
    /// and fires each step with correct timing.
    /// </summary>
    public sealed class GestureManager
    {
        private readonly SLNetworkManager _net;
        private readonly AssetManager     _assets;

        // Loaded gesture assets keyed by inventory item UUID
        private readonly Dictionary<Guid, AssetGesture> _loaded = new();

        public GestureManager(SLNetworkManager net, AssetManager assets)
        {
            _net    = net;
            _assets = assets;
            net.Client.Self.ChatFromSimulator += OnChat;
        }

        // Trigger gesture by its inventory UUID
        public void Play(Guid gestureId)
        {
            if (_loaded.TryGetValue(gestureId, out var gesture))
                _ = PlayStepsAsync(gesture);
            else
                _ = LoadAndPlayAsync(gestureId);
        }

        private async Task LoadAndPlayAsync(Guid gestureId)
        {
            var uuid = new UUID(gestureId);
            _net.Client.Assets.RequestAsset(uuid, AssetType.Gesture, false, (trans, asset) =>
            {
                if (asset is AssetGesture gesture && gesture.Decode())
                {
                    _loaded[gestureId] = gesture;
                    _ = PlayStepsAsync(gesture);
                }
            });

            await Task.CompletedTask;
        }

        private async Task PlayStepsAsync(AssetGesture gesture)
        {
            if (gesture.Sequence == null) return;

            foreach (var step in gesture.Sequence)
            {
                switch (step)
                {
                    case GestureStepAnimation anim:
                        if (anim.AnimationAsset != UUID.Zero)
                        {
                            if (anim.AnimationStart)
                                _net.Client.Self.AnimationStart(anim.AnimationAsset, false);
                            else
                                _net.Client.Self.AnimationStop(anim.AnimationAsset, false);
                        }
                        break;

                    case GestureStepSound sound:
                        // Sound playback via object attachment would go here
                        // For now just request the asset so it caches
                        _ = _assets.RequestAsync(sound.SoundAsset.Guid);
                        break;

                    case GestureStepChat chat:
                        _net.Client.Self.Chat(chat.Text, 0, ChatType.Normal);
                        break;

                    case GestureStepWait wait:
                        if (wait.WaitForTime)
                            await Task.Delay(TimeSpan.FromSeconds(wait.WaitTime));
                        break;
                }
            }
        }

        // Intercept chat triggers like "/wave", "/clap" from inventory active gestures
        private void OnChat(object? sender, ChatEventArgs e)
        {
            if (e.Type != ChatType.Normal || e.SourceID != _net.Client.Self.AgentID) return;
            foreach (var (id, gesture) in _loaded)
            {
                if (!string.IsNullOrEmpty(gesture.Trigger) &&
                    e.Message.StartsWith(gesture.Trigger, StringComparison.OrdinalIgnoreCase))
                    _ = PlayStepsAsync(gesture);
            }
        }
    }
}
