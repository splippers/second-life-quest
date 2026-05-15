using TMPro;
using SLQuest.Core;
using SLQuest.World;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space panel for landmark management.
    ///
    /// Inspector wiring:
    ///   listContent       — Content Transform inside a ScrollRect
    ///   rowPrefab         — row with: LandmarkName (TMP_Text), GoButton (Button)
    ///   createButton      — creates a landmark at current position
    ///   landmarkNameInput — TMP_InputField for new landmark name
    ///   statusLabel       — loading/error feedback
    ///   refreshButton     — re-scans inventory
    ///   closeButton       — hides panel
    /// </summary>
    public sealed class LandmarkPanel : MonoBehaviour
    {
        [Header("List")]
        [SerializeField] private Transform  listContent;
        [SerializeField] private GameObject rowPrefab;

        [Header("Create")]
        [SerializeField] private TMP_InputField landmarkNameInput;
        [SerializeField] private Button         createButton;

        [Header("Controls")]
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private Button   refreshButton;
        [SerializeField] private Button   closeButton;

        private LandmarkManager _lm;
        private readonly System.Collections.Generic.List<GameObject> _rows = new();

        private void Awake()
        {
            _lm = FindObjectOfType<LandmarkManager>();

            createButton?.onClick.AddListener(OnCreate);
            refreshButton?.onClick.AddListener(() => { SetStatus("Refreshing…"); _lm?.Refresh(); });
            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));
        }

        private void OnEnable()
        {
            if (_lm != null) _lm.OnListChanged += Rebuild;
            Rebuild();
        }

        private void OnDisable()
        {
            if (_lm != null) _lm.OnListChanged -= Rebuild;
        }

        // ── Build list ────────────────────────────────────────────────────────

        private void Rebuild()
        {
            ClearRows();
            SetStatus(string.Empty);

            if (_lm == null) return;

            if (_lm.Landmarks.Count == 0)
            {
                SetStatus("No landmarks.");
                return;
            }

            foreach (var entry in _lm.Landmarks)
                SpawnRow(entry);
        }

        private void SpawnRow(LandmarkEntry entry)
        {
            if (rowPrefab == null || listContent == null) return;

            var go = Instantiate(rowPrefab, listContent);
            _rows.Add(go);

            var nameLabel = go.transform.Find("LandmarkName")?.GetComponent<TMP_Text>();
            var goBtn     = go.transform.Find("GoButton")?.GetComponent<Button>();

            if (nameLabel != null)
                nameLabel.text = string.IsNullOrEmpty(entry.Region)
                    ? entry.Name
                    : $"{entry.Name}\n<size=70%>{entry.Region}</size>";

            if (goBtn != null)
                goBtn.onClick.AddListener(() =>
                {
                    SetStatus($"Teleporting to {entry.Name}…");
                    _lm.TeleportTo(entry);
                    gameObject.SetActive(false);
                });
        }

        // ── Create ────────────────────────────────────────────────────────────

        private void OnCreate()
        {
            string lmName = landmarkNameInput != null ? landmarkNameInput.text.Trim() : string.Empty;
            if (string.IsNullOrEmpty(lmName))
            {
                SetStatus("Enter a name first.");
                return;
            }

            SetStatus($"Creating '{lmName}'…");
            _lm?.CreateLandmark(lmName);

            if (landmarkNameInput != null) landmarkNameInput.text = string.Empty;
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
