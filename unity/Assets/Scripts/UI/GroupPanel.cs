using System.Collections.Generic;
using OpenMetaverse;
using TMPro;
using SLQuest.Chat;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space panel for group membership management.
    ///
    /// On open, calls Client.Groups.RequestCurrentGroups(); populates when the
    /// CurrentGroups event fires. Provides per-group actions: open group chat
    /// (via GroupChatManager), activate (set as active group), and leave.
    ///
    /// Inspector wiring:
    ///   groupListContent  — Content Transform inside the groups ScrollRect
    ///   groupRowPrefab    — row with: GroupName (TMP_Text), RoleTitle (TMP_Text),
    ///                        ChatButton (Button), ActivateButton (Button),
    ///                        LeaveButton (Button)
    ///   activeGroupLabel  — TMP_Text showing current active group name
    ///   statusLabel       — shows "Loading…" / errors
    ///   refreshButton     — re-requests group list
    ///   closeButton       — hides the panel
    /// </summary>
    public sealed class GroupPanel : MonoBehaviour
    {
        [Header("List")]
        [SerializeField] private ScrollRect groupScroll;
        [SerializeField] private Transform  groupListContent;
        [SerializeField] private GameObject groupRowPrefab;

        [Header("Labels / controls")]
        [SerializeField] private TMP_Text activeGroupLabel;
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private Button   refreshButton;
        [SerializeField] private Button   closeButton;

        private SLNetworkManager _net;
        private GroupChatManager _groupChats;

        private readonly List<GameObject>     _rows      = new();
        private readonly Dictionary<UUID, Group> _groups = new();

        private void Awake()
        {
            _net        = SLApplication.Instance?.Network    ?? FindObjectOfType<SLNetworkManager>();
            _groupChats = SLApplication.Instance?.GroupChats ?? FindObjectOfType<GroupChatManager>();

            refreshButton?.onClick.AddListener(OnRefresh);
            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));
        }

        private void OnEnable()
        {
            _net.Client.Groups.CurrentGroups += OnCurrentGroups;
            OnRefresh();
        }

        private void OnDisable()
        {
            if (_net?.Client?.Groups != null)
                _net.Client.Groups.CurrentGroups -= OnCurrentGroups;
        }

        // ── Refresh ───────────────────────────────────────────────────────────

        private void OnRefresh()
        {
            SetStatus("Loading…");
            ClearRows();
            _net?.Client?.Groups?.RequestCurrentGroups();
        }

        private void OnCurrentGroups(object sender, CurrentGroupsEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                _groups.Clear();
                foreach (var kvp in e.Groups)
                    _groups[kvp.Key] = kvp.Value;

                ClearRows();
                SetStatus(string.Empty);

                foreach (var group in _groups.Values)
                    SpawnRow(group);

                UpdateActiveGroupLabel();

                if (groupScroll != null)
                {
                    Canvas.ForceUpdateCanvases();
                    groupScroll.verticalNormalizedPosition = 1f;
                }
            });
        }

        // ── Row factory ───────────────────────────────────────────────────────

        private void SpawnRow(Group group)
        {
            if (groupRowPrefab == null || groupListContent == null) return;

            var go = Instantiate(groupRowPrefab, groupListContent);
            _rows.Add(go);

            var nameLabel  = go.transform.Find("GroupName")?.GetComponent<TMP_Text>();
            var titleLabel = go.transform.Find("RoleTitle")?.GetComponent<TMP_Text>();
            var chatBtn    = go.transform.Find("ChatButton")?.GetComponent<Button>();
            var activateBtn= go.transform.Find("ActivateButton")?.GetComponent<Button>();
            var leaveBtn   = go.transform.Find("LeaveButton")?.GetComponent<Button>();

            if (nameLabel  != null) nameLabel.text  = group.Name;
            if (titleLabel != null) titleLabel.text  = group.Title;

            UUID groupId = group.ID;

            if (chatBtn != null)
                chatBtn.onClick.AddListener(() => _groupChats?.JoinGroupChat(groupId));

            if (activateBtn != null)
                activateBtn.onClick.AddListener(() =>
                {
                    _net.Client.Groups.ActivateGroup(groupId);
                    UpdateActiveGroupLabel();
                });

            if (leaveBtn != null)
                leaveBtn.onClick.AddListener(() => ConfirmLeave(group));
        }

        // ── Actions ───────────────────────────────────────────────────────────

        private void ConfirmLeave(Group group)
        {
            SetStatus($"Leaving {group.Name}…");
            _net.Client.Groups.LeaveGroup(group.ID);
            _groups.Remove(group.ID);

            // Rebuild after brief delay to let server process
            Invoke(nameof(OnRefresh), 2f);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void UpdateActiveGroupLabel()
        {
            if (activeGroupLabel == null) return;

            UUID activeId = _net?.Client?.Self?.ActiveGroup ?? UUID.Zero;
            if (activeId != UUID.Zero && _groups.TryGetValue(activeId, out var active))
                activeGroupLabel.text = $"Active: {active.Name}";
            else
                activeGroupLabel.text = "Active: (none)";
        }

        private void ClearRows()
        {
            foreach (var r in _rows) Destroy(r);
            _rows.Clear();
        }

        private void SetStatus(string text)
        {
            if (statusLabel != null)
            {
                statusLabel.text = text;
                statusLabel.gameObject.SetActive(!string.IsNullOrEmpty(text));
            }
        }
    }
}
