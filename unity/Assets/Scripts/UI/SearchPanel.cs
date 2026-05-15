using System.Collections.Generic;
using OpenMetaverse;
using TMPro;
using SLQuest.Core;
using SLQuest.Network;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space search panel covering People, Places, and Events searches
    /// via the LibreMetaverse Directory API.
    ///
    /// Inspector wiring:
    ///   tabPeople / tabPlaces / tabEvents — Toggle buttons for each tab
    ///   queryInput       — TMP_InputField
    ///   searchButton     — triggers search
    ///   resultContent    — ScrollRect Content Transform
    ///   resultRowPrefab  — row: ResultName (TMP_Text), SubText (TMP_Text), ActionButton (Button)
    ///   statusLabel      — loading / error feedback
    ///   closeButton
    /// </summary>
    public sealed class SearchPanel : MonoBehaviour
    {
        public enum SearchTab { People, Places, Events }

        [Header("Tabs")]
        [SerializeField] private Toggle tabPeople;
        [SerializeField] private Toggle tabPlaces;
        [SerializeField] private Toggle tabEvents;

        [Header("Query")]
        [SerializeField] private TMP_InputField queryInput;
        [SerializeField] private Button         searchButton;

        [Header("Results")]
        [SerializeField] private Transform  resultContent;
        [SerializeField] private GameObject resultRowPrefab;

        [Header("Controls")]
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private Button   closeButton;

        private SLNetworkManager  _net;
        private SearchTab         _activeTab = SearchTab.People;
        private readonly List<GameObject> _rows = new();

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();

            searchButton?.onClick.AddListener(OnSearch);
            queryInput?.onSubmit.AddListener(_ => OnSearch());
            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));

            tabPeople?.onValueChanged.AddListener(on => { if (on) _activeTab = SearchTab.People; });
            tabPlaces?.onValueChanged.AddListener(on => { if (on) _activeTab = SearchTab.Places; });
            tabEvents?.onValueChanged.AddListener(on => { if (on) _activeTab = SearchTab.Events; });
        }

        private void OnEnable()
        {
            if (_net?.Client?.Directory != null)
            {
                _net.Client.Directory.DirPeopleReply  += OnPeopleReply;
                _net.Client.Directory.DirPlacesReply  += OnPlacesReply;
                _net.Client.Directory.DirEventsReply  += OnEventsReply;
            }
        }

        private void OnDisable()
        {
            if (_net?.Client?.Directory != null)
            {
                _net.Client.Directory.DirPeopleReply  -= OnPeopleReply;
                _net.Client.Directory.DirPlacesReply  -= OnPlacesReply;
                _net.Client.Directory.DirEventsReply  -= OnEventsReply;
            }
        }

        // ── Search dispatch ───────────────────────────────────────────────────

        private void OnSearch()
        {
            string query = queryInput != null ? queryInput.text.Trim() : string.Empty;
            if (string.IsNullOrEmpty(query)) return;

            ClearRows();
            SetStatus("Searching…");

            switch (_activeTab)
            {
                case SearchTab.People:
                    _net.Client.Directory.StartPeopleSearch(query, 0);
                    break;
                case SearchTab.Places:
                    _net.Client.Directory.StartPlacesSearch(query, 0, DirectoryManager.DirFindFlags.DwellSort, UUID.Zero);
                    break;
                case SearchTab.Events:
                    _net.Client.Directory.StartEventsSearch(query, DirectoryManager.EventFlags.All, 0);
                    break;
            }
        }

        // ── Result handlers ───────────────────────────────────────────────────

        private void OnPeopleReply(object sender, DirPeopleReplyEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                SetStatus(string.Empty);
                if (e.MatchedPeople == null || e.MatchedPeople.Count == 0)
                { SetStatus("No results."); return; }

                foreach (var person in e.MatchedPeople)
                {
                    UUID id   = person.AgentID;
                    string nm = person.FirstName + " " + person.LastName;
                    SpawnRow(nm, string.Empty, "IM", () =>
                        VRUIManager.Instance?.ShowIM(id, nm));
                }
            });
        }

        private void OnPlacesReply(object sender, DirPlacesReplyEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                SetStatus(string.Empty);
                if (e.MatchedParcels == null || e.MatchedParcels.Count == 0)
                { SetStatus("No results."); return; }

                foreach (var parcel in e.MatchedParcels)
                {
                    string nm    = parcel.Name;
                    string dwell = $"Dwell: {parcel.Dwell:F0}";
                    // Places don't have direct teleport — open map instead
                    SpawnRow(nm, dwell, "Map", () => VRUIManager.Instance?.ShowMap());
                }
            });
        }

        private void OnEventsReply(object sender, DirEventsReplyEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                SetStatus(string.Empty);
                if (e.MatchedEvents == null || e.MatchedEvents.Count == 0)
                { SetStatus("No results."); return; }

                foreach (var ev in e.MatchedEvents)
                {
                    string nm   = ev.Name;
                    string when = ev.Date;
                    uint evid   = ev.ID;
                    SpawnRow(nm, when, "Go", () => TeleportToEvent(evid));
                }
            });
        }

        // ── Row factory ───────────────────────────────────────────────────────

        private void SpawnRow(string name, string subText, string actionLabel, System.Action action)
        {
            if (resultRowPrefab == null || resultContent == null) return;

            var go       = Instantiate(resultRowPrefab, resultContent);
            var nameLbl  = go.transform.Find("ResultName")?.GetComponent<TMP_Text>();
            var subLbl   = go.transform.Find("SubText")?.GetComponent<TMP_Text>();
            var btn      = go.transform.Find("ActionButton")?.GetComponent<Button>();
            var btnLbl   = btn?.GetComponentInChildren<TMP_Text>();

            if (nameLbl != null)  nameLbl.text  = name;
            if (subLbl  != null)  subLbl.text   = subText;
            if (btnLbl  != null)  btnLbl.text   = actionLabel;
            if (btn     != null)  btn.onClick.AddListener(() => action?.Invoke());

            _rows.Add(go);
        }

        // ── Actions ───────────────────────────────────────────────────────────

        private void TeleportToEvent(uint eventId)
        {
            _net.Client.Directory.EventInfoRequest(eventId);
            // Could subscribe DirEventInfoReply here to get sim + pos, then teleport
            // For now just open the teleport panel so the user can type the region
            VRUIManager.Instance?.ShowTeleport();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void ClearRows()
        {
            foreach (var r in _rows) Destroy(r);
            _rows.Clear();
        }

        private void SetStatus(string text)
        {
            if (statusLabel == null) return;
            statusLabel.text = text;
            statusLabel.gameObject.SetActive(!string.IsNullOrEmpty(text));
        }
    }
}
