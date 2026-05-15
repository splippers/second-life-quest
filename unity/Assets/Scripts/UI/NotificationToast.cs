using System.Collections;
using System.Collections.Generic;
using TMPro;
using SLQuest.Core;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// Stacked VR notification toasts. Rendered at a fixed offset from the camera
    /// (anchored to the lower-right of the FOV, similar to a wrist display).
    ///
    /// Inspector wiring:
    ///   toastPrefab     — world-space Canvas with Message (TMP_Text),
    ///                     Accept (Button), Decline (Button), Icon (Image)
    ///   stackRoot       — empty Transform child of this GO; toasts are parented here
    ///   toastOffset     — position in camera-local space (e.g. right 0.3 / down -0.2 / forward 0.5)
    ///   autoExpireSecs  — seconds before an unactioned toast auto-dismisses
    ///
    /// Notifications are pushed by subscribing to NotificationManager.OnNotificationReceived.
    /// </summary>
    public sealed class NotificationToast : MonoBehaviour
    {
        [Header("Prefab / layout")]
        [SerializeField] private GameObject toastPrefab;
        [SerializeField] private Transform  stackRoot;
        [SerializeField] private Vector3    toastOffset  = new(0.3f, -0.2f, 0.5f);
        [SerializeField] private float      toastSpacing = 0.12f;

        [Header("Behaviour")]
        [SerializeField] private float autoExpireSecs = 30f;
        [SerializeField] private int   maxToasts      = 5;

        private NotificationManager _notifs;

        private readonly LinkedList<ActiveToast> _toasts = new();

        private sealed class ActiveToast
        {
            public GameObject    Go;
            public PendingNotification Note;
            public float         Elapsed;
        }

        private void Awake()
        {
            _notifs = SLApplication.Instance?.Notifications
                   ?? FindObjectOfType<NotificationManager>();
        }

        private void OnEnable()
        {
            if (_notifs != null)
                _notifs.OnNotificationReceived += Push;
        }

        private void OnDisable()
        {
            if (_notifs != null)
                _notifs.OnNotificationReceived -= Push;
        }

        private void Update()
        {
            var cam = Camera.main;
            if (cam == null) return;

            // Anchor stack root to camera-local offset
            if (stackRoot != null)
            {
                stackRoot.position = cam.transform.TransformPoint(toastOffset);
                stackRoot.rotation = cam.transform.rotation;
            }

            // Age toasts and expire old ones
            var node = _toasts.First;
            while (node != null)
            {
                var next = node.Next;
                node.Value.Elapsed += Time.deltaTime;
                if (node.Value.Elapsed >= autoExpireSecs)
                    DismissNode(node);
                node = next;
            }
        }

        // ── Push ──────────────────────────────────────────────────────────────

        private void Push(PendingNotification note)
        {
            // Drop oldest if at cap
            if (_toasts.Count >= maxToasts)
                DismissNode(_toasts.Last);

            if (toastPrefab == null) return;

            var parent = stackRoot != null ? stackRoot : transform;
            var go = Instantiate(toastPrefab, parent);

            // Stack below previous toasts
            int index = _toasts.Count;
            go.transform.localPosition = Vector3.down * (index * toastSpacing);
            go.transform.localRotation = Quaternion.identity;

            // Populate text
            var msgLabel = go.transform.Find("Message")?.GetComponent<TMP_Text>();
            if (msgLabel != null) msgLabel.text = FormatMessage(note);

            // Icon tint by type
            var icon = go.transform.Find("Icon")?.GetComponent<Image>();
            if (icon != null) icon.color = TypeColour(note.Type);

            // Buttons
            var acceptBtn  = go.transform.Find("Accept")?.GetComponent<Button>();
            var declineBtn = go.transform.Find("Decline")?.GetComponent<Button>();

            var active = new ActiveToast { Go = go, Note = note };
            var ln     = _toasts.AddFirst(active);

            if (acceptBtn != null)
                acceptBtn.onClick.AddListener(() =>
                {
                    note.OnAccept?.Invoke();
                    _notifs?.Dismiss(note);
                    DismissNode(ln);
                });

            if (declineBtn != null)
                declineBtn.onClick.AddListener(() =>
                {
                    note.OnDecline?.Invoke();
                    _notifs?.Dismiss(note);
                    DismissNode(ln);
                });
        }

        private void DismissNode(LinkedListNode<ActiveToast> node)
        {
            if (node?.Value == null) return;
            _notifs?.Dismiss(node.Value.Note);
            if (node.Value.Go != null)
                Destroy(node.Value.Go);
            _toasts.Remove(node);
            RebuildStackPositions();
        }

        private void RebuildStackPositions()
        {
            int i = 0;
            foreach (var t in _toasts)
            {
                if (t.Go != null)
                    t.Go.transform.localPosition = Vector3.down * (i * toastSpacing);
                i++;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string FormatMessage(PendingNotification note) =>
            $"[{NotifLabel(note.Type)}]\n{note.Message}";

        private static string NotifLabel(NotificationType t) => t switch
        {
            NotificationType.FriendRequest    => "Friend Request",
            NotificationType.TeleportLure     => "Teleport Offer",
            NotificationType.InventoryOffer   => "Inventory Offer",
            NotificationType.GroupInvitation  => "Group Invite",
            NotificationType.ScriptPermission => "Script Request",
            _                                 => "Notification"
        };

        private static Color TypeColour(NotificationType t) => t switch
        {
            NotificationType.FriendRequest    => new Color(0.4f, 1f, 0.5f),
            NotificationType.TeleportLure     => new Color(0.4f, 0.7f, 1f),
            NotificationType.InventoryOffer   => new Color(1f, 0.85f, 0.4f),
            NotificationType.GroupInvitation  => new Color(0.8f, 0.5f, 1f),
            NotificationType.ScriptPermission => new Color(1f, 0.5f, 0.4f),
            _                                 => Color.white
        };
    }
}
