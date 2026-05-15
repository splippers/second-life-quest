using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using SLQuest.Core;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space VR panel for browsing the Second Life Marketplace.
    ///
    /// Fetches the Marketplace search JSON API, renders results as a scrollable
    /// list, and opens item URLs in the Quest system browser via Application.OpenURL.
    ///
    /// Inspector wiring required:
    ///   searchInput      — TMP_InputField for the search query
    ///   searchButton     — triggers a new search
    ///   resultsContent   — Content Transform inside the results ScrollRect
    ///   resultRowPrefab  — row with: Name (TMP_Text), Price (TMP_Text),
    ///                       Creator (TMP_Text), Thumbnail (RawImage), View (Button)
    ///   pageLabel        — shows "Page N of M"
    ///   prevButton       — page back
    ///   nextButton       — page forward
    ///   statusLabel      — shows loading / error messages
    ///   closeButton      — hides the panel
    /// </summary>
    public sealed class MarketplacePanel : MonoBehaviour
    {
        private const string BASE_URL      = "https://marketplace.secondlife.com";
        private const string SEARCH_URL    = BASE_URL + "/products/search.json";
        private const int    PER_PAGE      = 20;

        [Header("Search")]
        [SerializeField] private TMP_InputField searchInput;
        [SerializeField] private Button         searchButton;

        [Header("Results")]
        [SerializeField] private ScrollRect  resultsScroll;
        [SerializeField] private Transform   resultsContent;
        [SerializeField] private GameObject  resultRowPrefab;

        [Header("Pagination")]
        [SerializeField] private TMP_Text pageLabel;
        [SerializeField] private Button   prevButton;
        [SerializeField] private Button   nextButton;

        [Header("Status / chrome")]
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private Button   closeButton;

        private int    _currentPage = 1;
        private int    _totalPages  = 1;
        private string _lastQuery   = string.Empty;

        private readonly List<GameObject> _rows = new();

        private void Awake()
        {
            searchButton?.onClick.AddListener(OnSearch);
            searchInput?.onSubmit.AddListener(_ => OnSearch());
            prevButton?.onClick.AddListener(OnPrev);
            nextButton?.onClick.AddListener(OnNext);
            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));

            searchInput?.onEndEdit.AddListener(_ =>
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    OnSearch();
            });

            SetStatus("Enter a search term above.");
            UpdatePaginationButtons();
        }

        // ── Search ────────────────────────────────────────────────────────────

        private void OnSearch()
        {
            string q = searchInput != null ? searchInput.text.Trim() : string.Empty;
            if (string.IsNullOrEmpty(q)) return;
            _lastQuery   = q;
            _currentPage = 1;
            StartCoroutine(FetchResults());
        }

        private void OnPrev()
        {
            if (_currentPage <= 1) return;
            _currentPage--;
            StartCoroutine(FetchResults());
        }

        private void OnNext()
        {
            if (_currentPage >= _totalPages) return;
            _currentPage++;
            StartCoroutine(FetchResults());
        }

        private IEnumerator FetchResults()
        {
            SetStatus("Searching…");
            ClearRows();

            string url = $"{SEARCH_URL}?q={UnityWebRequest.EscapeURL(_lastQuery)}" +
                         $"&page={_currentPage}&per_page={PER_PAGE}";

            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("Accept", "application/json");
            req.SetRequestHeader("User-Agent", "SLQuest/0.1");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                SetStatus($"Error: {req.error}");
                yield break;
            }

            MarketplaceSearchResult result;
            try { result = JsonUtility.FromJson<MarketplaceSearchResult>(req.downloadHandler.text); }
            catch (Exception ex)
            {
                SetStatus($"Parse error: {ex.Message}");
                yield break;
            }

            if (result?.products == null || result.products.Count == 0)
            {
                SetStatus("No results.");
                yield break;
            }

            // Calculate pagination
            int total = result.search?.total ?? result.products.Count;
            _totalPages = Math.Max(1, (int)Math.Ceiling(total / (float)PER_PAGE));
            UpdatePaginationButtons();
            if (pageLabel != null)
                pageLabel.text = $"Page {_currentPage} of {_totalPages}  ({total:N0} items)";

            SetStatus(string.Empty);

            foreach (var product in result.products)
                SpawnRow(product);

            if (resultsScroll != null)
            {
                Canvas.ForceUpdateCanvases();
                resultsScroll.verticalNormalizedPosition = 1f;
            }
        }

        // ── Row factory ───────────────────────────────────────────────────────

        private void SpawnRow(MarketplaceProduct p)
        {
            if (resultRowPrefab == null || resultsContent == null) return;

            var go = Instantiate(resultRowPrefab, resultsContent);
            _rows.Add(go);

            var nameLabel    = go.transform.Find("Name")?.GetComponent<TMP_Text>();
            var priceLabel   = go.transform.Find("Price")?.GetComponent<TMP_Text>();
            var creatorLabel = go.transform.Find("Creator")?.GetComponent<TMP_Text>();
            var viewButton   = go.transform.Find("View")?.GetComponent<Button>();
            var thumbnail    = go.transform.Find("Thumbnail")?.GetComponent<RawImage>();

            if (nameLabel    != null) nameLabel.text    = p.name;
            if (priceLabel   != null) priceLabel.text   = p.price_display ?? "Free";
            if (creatorLabel != null) creatorLabel.text = p.creator_name;

            string itemUrl = p.url?.StartsWith("http", StringComparison.Ordinal) == true
                ? p.url
                : BASE_URL + p.url;

            if (viewButton != null)
                viewButton.onClick.AddListener(() => Application.OpenURL(itemUrl));

            // Async thumbnail load
            if (thumbnail != null && !string.IsNullOrEmpty(p.main_image?.url))
                StartCoroutine(LoadThumbnail(p.main_image.url, thumbnail));
        }

        private IEnumerator LoadThumbnail(string url, RawImage target)
        {
            using var req = UnityWebRequestTexture.GetTexture(url);
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success && target != null)
                target.texture = DownloadHandlerTexture.GetContent(req);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

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

        private void UpdatePaginationButtons()
        {
            if (prevButton != null) prevButton.interactable = _currentPage > 1;
            if (nextButton != null) nextButton.interactable = _currentPage < _totalPages;
        }
    }

    // ── JSON model ────────────────────────────────────────────────────────────
    //
    // JsonUtility requires concrete classes with [Serializable]; no generics.

    [Serializable]
    internal sealed class MarketplaceSearchResult
    {
        public List<MarketplaceProduct> products;
        public MarketplaceSearchMeta    search;
    }

    [Serializable]
    internal sealed class MarketplaceSearchMeta
    {
        public int total;
        public int page;
        public int per_page;
    }

    [Serializable]
    internal sealed class MarketplaceProduct
    {
        public int              id;
        public string           name;
        public string           description;
        public string           price_display;
        public string           creator_name;
        public string           url;
        public MarketplaceImage main_image;
    }

    [Serializable]
    internal sealed class MarketplaceImage
    {
        public string url;
    }
}
