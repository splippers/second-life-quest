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
    /// World-space teleport panel: search regions by name and maintain a recent
    /// destinations list.
    ///
    /// Search uses <c>Client.Grid.RequestMapRegion(name, callback)</c> to resolve
    /// a region name to a handle, then <c>Client.Self.Teleport(handle, center)</c>.
    ///
    /// Inspector wiring:
    ///   searchInput     — TMP_InputField for region name
    ///   searchButton    — triggers search
    ///   resultContent   — Content Transform for search results ScrollRect
    ///   resultRowPrefab — row: RegionName (TMP_Text), GoButton (Button)
    ///   historyContent  — Content Transform for recent teleports
    ///   historyRowPrefab— same structure as resultRowPrefab
    ///   statusLabel     — feedback text
    ///   closeButton     — hides panel
    /// </summary>
    public sealed class TeleportPanel : MonoBehaviour
    {
        [Header("Search")]
        [SerializeField] private TMP_InputField searchInput;
        [SerializeField] private Button         searchButton;
        [SerializeField] private Transform      resultContent;
        [SerializeField] private GameObject     resultRowPrefab;

        [Header("History")]
        [SerializeField] private Transform  historyContent;
        [SerializeField] private GameObject historyRowPrefab;

        [Header("Controls")]
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private Button   closeButton;

        private SLNetworkManager _net;
        private readonly List<GameObject>    _resultRows = new();
        private readonly List<GameObject>    _historyRows = new();
        private readonly List<(string Name, ulong Handle)> _history = new();
        private const int MaxHistory = 12;

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();

            searchButton?.onClick.AddListener(OnSearch);
            searchInput?.onSubmit.AddListener(_ => OnSearch());
            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));
        }

        private void OnEnable()
        {
            EventBus.Subscribe<TeleportEvent>(OnTeleported);
        }

        private void OnDisable()
        {
            EventBus.Unsubscribe<TeleportEvent>(OnTeleported);
        }

        // ── Search ────────────────────────────────────────────────────────────

        private void OnSearch()
        {
            string query = searchInput != null ? searchInput.text.Trim() : string.Empty;
            if (string.IsNullOrEmpty(query)) return;

            ClearRows(_resultRows);
            SetStatus("Searching…");

            _net.Client.Grid.RequestMapRegion(query, GridLayerType.Objects,
                (region, success) =>
                {
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        SetStatus(string.Empty);
                        if (!success || region.Name == string.Empty)
                        {
                            SetStatus($"Region '{query}' not found.");
                            return;
                        }
                        SpawnResultRow(region.Name, region.RegionHandle, resultContent, _resultRows);
                    });
                });
        }

        // ── Teleport ──────────────────────────────────────────────────────────

        private void TeleportTo(string regionName, ulong handle)
        {
            SetStatus($"Teleporting to {regionName}…");
            var center = new OpenMetaverse.Vector3(128f, 128f, 25f);
            _net.Client.Self.Teleport(handle, center);
            gameObject.SetActive(false);
        }

        private void OnTeleported(TeleportEvent e)
        {
            // Find handle for the region we just landed in
            var sim = _net?.Client?.Network?.CurrentSim;
            if (sim == null) return;

            string name = sim.Name;
            ulong handle = sim.Handle;

            _history.RemoveAll(h => h.Name == name);
            _history.Insert(0, (name, handle));
            if (_history.Count > MaxHistory)
                _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);

            if (gameObject.activeSelf)
                RebuildHistory();
        }

        // ── Row factories ─────────────────────────────────────────────────────

        private void SpawnResultRow(string regionName, ulong handle,
                                    Transform container, List<GameObject> pool)
        {
            if (resultRowPrefab == null || container == null) return;

            var go = Instantiate(resultRowPrefab, container);
            pool.Add(go);

            var lbl = go.transform.Find("RegionName")?.GetComponent<TMP_Text>();
            var btn = go.transform.Find("GoButton")?.GetComponent<Button>();

            if (lbl != null) lbl.text = regionName;
            if (btn != null) btn.onClick.AddListener(() => TeleportTo(regionName, handle));
        }

        private void RebuildHistory()
        {
            ClearRows(_historyRows);
            foreach (var (name, handle) in _history)
            {
                var go = Instantiate(historyRowPrefab != null ? historyRowPrefab : resultRowPrefab,
                                     historyContent);
                _historyRows.Add(go);

                var lbl = go.transform.Find("RegionName")?.GetComponent<TMP_Text>();
                var btn = go.transform.Find("GoButton")?.GetComponent<Button>();
                string n = name; ulong h = handle;

                if (lbl != null) lbl.text = n;
                if (btn != null) btn.onClick.AddListener(() => TeleportTo(n, h));
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void ClearRows(List<GameObject> rows)
        {
            foreach (var r in rows) Destroy(r);
            rows.Clear();
        }

        private void SetStatus(string text)
        {
            if (statusLabel == null) return;
            statusLabel.text = text;
            statusLabel.gameObject.SetActive(!string.IsNullOrEmpty(text));
        }
    }
}
