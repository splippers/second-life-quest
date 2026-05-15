using OpenMetaverse;
using TMPro;
using SLQuest.Avatar;
using SLQuest.Core;
using SLQuest.Network;
using SLQuest.Social;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space avatar profile card.
    ///
    /// Triggered by <see cref="AvatarInteractor"/> when the player presses
    /// B/Y while aiming at a RemoteAvatar.  Fetches the profile via
    /// <c>Client.Avatars.RequestAvatarProperties</c> and shows name,
    /// profile text, account age, online status, and quick-action buttons.
    ///
    /// Inspector wiring:
    ///   nameLabel      — TMP_Text
    ///   profileLabel   — TMP_Text (profile bio)
    ///   accountLabel   — TMP_Text "Member since …"
    ///   profileImage   — RawImage (profile picture)
    ///   imButton       — opens IM / chat
    ///   friendButton   — offer/remove friendship
    ///   tpButton       — offer teleport
    ///   profileImageLoader — uses AssetManager to download profile texture
    ///   closeButton    — destroys self
    /// </summary>
    public sealed class AvatarInspector : MonoBehaviour
    {
        [SerializeField] private TMP_Text  nameLabel;
        [SerializeField] private TMP_Text  profileLabel;
        [SerializeField] private TMP_Text  accountLabel;
        [SerializeField] private RawImage  profileImage;
        [SerializeField] private Button    imButton;
        [SerializeField] private Button    friendButton;
        [SerializeField] private Button    tpButton;
        [SerializeField] private Button    closeButton;

        private SLNetworkManager _net;
        private FriendManager    _friends;
        private UUID             _agentId;
        private bool             _isFriend;

        public void Inspect(UUID agentId, Vector3 worldPos)
        {
            _net     = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            _friends = SLApplication.Instance?.Friends ?? FindObjectOfType<FriendManager>();
            _agentId = agentId;

            PlaceNearSubject(worldPos);
            closeButton?.onClick.AddListener(() => Destroy(gameObject));

            // Check friendship status
            _isFriend = _friends?.Friends.ContainsKey(agentId) ?? false;
            RefreshFriendButton();

            // Wire quick actions
            imButton?.onClick.AddListener(OnIM);
            friendButton?.onClick.AddListener(OnToggleFriend);
            tpButton?.onClick.AddListener(OnTP);

            // Request profile
            if (nameLabel != null) nameLabel.text = "…";
            _net.Client.Avatars.AvatarPropertiesReply += OnProperties;
            _net.Client.Avatars.RequestAvatarProperties(agentId);
        }

        private void OnDestroy()
        {
            if (_net?.Client?.Avatars != null)
                _net.Client.Avatars.AvatarPropertiesReply -= OnProperties;
        }

        // ── Profile data ──────────────────────────────────────────────────────

        private void OnProperties(object sender, AvatarPropertiesReplyEventArgs e)
        {
            if (e.AvatarID != _agentId) return;
            _net.Client.Avatars.AvatarPropertiesReply -= OnProperties;

            var props = e.Properties;
            MainThreadDispatcher.Enqueue(() =>
            {
                if (nameLabel    != null) nameLabel.text    = props.DisplayName ?? props.FirstName + " " + props.LastName;
                if (profileLabel != null) profileLabel.text = props.AboutText ?? string.Empty;
                if (accountLabel != null)
                {
                    string born = props.BornOn ?? "unknown";
                    accountLabel.text = $"Member since {born}";
                }

                // Request profile picture
                if (profileImage != null && props.ProfileImage != UUID.Zero)
                    DownloadProfileImage(props.ProfileImage);
            });
        }

        private void DownloadProfileImage(UUID imageId)
        {
            _net.Client.Assets.RequestImage(imageId, ImageType.Normal,
                (transfer, asset) =>
                {
                    if (asset?.AssetData == null) return;
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        var tex = new Texture2D(2, 2);
                        if (tex.LoadImage(asset.AssetData))
                            profileImage.texture = tex;
                        else
                            Destroy(tex);
                    });
                });
        }

        // ── Quick actions ─────────────────────────────────────────────────────

        private void OnIM()
        {
            var name = nameLabel != null ? nameLabel.text : _agentId.ToString();
            VRUIManager.Instance?.ShowIM(_agentId, name);
            Destroy(gameObject);
        }

        private void OnToggleFriend()
        {
            if (_isFriend)
                _friends?.RemoveFriend(_agentId);
            else
                _friends?.OfferFriendship(_agentId);
            _isFriend = !_isFriend;
            RefreshFriendButton();
        }

        private void OnTP()
        {
            _friends?.SendTeleportOffer(_agentId);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void RefreshFriendButton()
        {
            if (friendButton == null) return;
            var lbl = friendButton.GetComponentInChildren<TMP_Text>();
            if (lbl != null) lbl.text = _isFriend ? "Remove Friend" : "Add Friend";
        }

        private void PlaceNearSubject(Vector3 subjectWorldPos)
        {
            var cam = Camera.main;
            if (cam == null) return;

            transform.position = subjectWorldPos + Vector3.up * 0.5f + cam.transform.right * 0.4f;
            transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
        }
    }
}
