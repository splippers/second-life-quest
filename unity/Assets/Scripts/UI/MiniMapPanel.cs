using System;
using System.Collections;
using System.Collections.Generic;
using OpenMetaverse;
using TMPro;
using SLQuest.Core;
using SLQuest.Network;
using SLQuest.Social;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// 2D overhead mini-map for the current region.
    ///
    /// Fetches the region tile from map.secondlife.com, then overlays dots for:
    ///   self (white), friends (green), other avatars (yellow).
    /// Updates on a configurable interval and can be wrist-mounted or
    /// spawned as a detachable world-space panel via VRUIManager.
    ///
    /// Inspector wiring:
    ///   mapImage        — RawImage for the 256x256 region tile
    ///   dotRoot         — Transform; dot prefabs are parented here
    ///   selfDotPrefab   — white dot (can have a heading notch child)
    ///   friendDotPrefab — green dot
    ///   agentDotPrefab  — yellow dot
    ///   regionLabel     — TMP_Text (region name + grid coords)
    ///   agentCountLabel — TMP_Text ("N avatars")
    ///   closeButton     — hides the panel
    /// </summary>
    public sealed class MiniMapPanel : MonoBehaviour
    {
        [Header("Map display")]
        [SerializeField] private RawImage  mapImage;
        [SerializeField] private Transform dotRoot;
        [SerializeField] private float     mapSizeM  = 256f;
        [SerializeField] private float     mapSizePx = 256f;

        [Header("Dot prefabs")]
        [SerializeField] private GameObject selfDotPrefab;
        [SerializeField] private GameObject friendDotPrefab;
        [SerializeField] private GameObject agentDotPrefab;

        [Header("Labels / controls")]
        [SerializeField] private TMP_Text regionLabel;
        [SerializeField] private TMP_Text agentCountLabel;
        [SerializeField] private Button   closeButton;

        [Header("Refresh")]
        [SerializeField] private float updateIntervalSec = 2f;

        private SLNetworkManager _net;
        private FriendManager    _friends;

        private GameObject _selfDot;
        private readonly Dictionary<UUID, GameObject> _agentDots = new();

        private string    _lastRegionName;
        private Coroutine _refreshCoroutine;

        private void Awake()
        {
            _net     = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
            _friends = SLApplication.Instance?.Friends ?? FindObjectOfType<FriendManager>();
            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));
        }

        private void OnEnable()
        {
            if (_selfDot == null && selfDotPrefab != null && dotRoot != null)
                _selfDot = Instantiate(selfDotPrefab, dotRoot);

            _refreshCoroutine = StartCoroutine(RefreshLoop());
        }

        private void OnDisable()
        {
            if (_refreshCoroutine != null)
            {
                StopCoroutine(_refreshCoroutine);
                _refreshCoroutine = null;
            }
            ClearAgentDots();
        }

        // ── Refresh loop ──────────────────────────────────────────────────────

        private IEnumerator RefreshLoop()
        {
            while (true)
            {
                var sim = _net?.Client?.Network?.CurrentSim;
                if (sim != null)
                {
                    UpdateRegionTile(sim);
                    UpdateDots(sim);
                    UpdateLabels(sim);
                }
                yield return new WaitForSeconds(updateIntervalSec);
            }
        }

        // ── Region map tile ───────────────────────────────────────────────────

        private void UpdateRegionTile(Simulator sim)
        {
            if (sim.Name == _lastRegionName) return;
            _lastRegionName = sim.Name;

            ulong handle = sim.Handle;
            uint  gridX  = (uint)(handle >> 32) / 256;
            uint  gridY  = (uint)(handle & 0xFFFF_FFFF) / 256;

            string url = $"https://map.secondlife.com/map-1-{gridX}-{gridY}-objects.jpg";
            StartCoroutine(FetchMapTile(url));

            if (regionLabel != null)
                regionLabel.text = $"{sim.Name}  ({gridX},{gridY})";
        }

        private IEnumerator FetchMapTile(string url)
        {
            using var req = UnityWebRequestTexture.GetTexture(url);
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success && mapImage != null)
                mapImage.texture = DownloadHandlerTexture.GetContent(req);
        }

        // ── Dot placement ─────────────────────────────────────────────────────

        private void UpdateDots(Simulator sim)
        {
            // Self dot
            if (_selfDot != null && _net?.Client?.Self != null)
            {
                var pos = _net.Client.Self.SimPosition;
                PlaceDot(_selfDot, pos.X, pos.Y);

                // Derive yaw from sim rotation quaternion
                var q = _net.Client.Self.SimRotation;
                float yaw = (float)Math.Atan2(
                    2.0 * (q.W * q.Z + q.X * q.Y),
                    1.0 - 2.0 * (q.Y * q.Y + q.Z * q.Z));
                _selfDot.transform.localRotation = Quaternion.Euler(0f, 0f, -yaw * Mathf.Rad2Deg);
            }

            // Remote avatars
            var seen = new HashSet<UUID>();
            sim.ObjectsAvatars.ForEach((localId, avatar) =>
            {
                if (avatar.ID == _net.Client.Self.AgentID) return;
                seen.Add(avatar.ID);

                if (!_agentDots.TryGetValue(avatar.ID, out var dot))
                {
                    bool isFriend = _friends?.Friends.ContainsKey(avatar.ID) == true;
                    var  prefab   = isFriend ? friendDotPrefab : agentDotPrefab;
                    if (prefab == null || dotRoot == null) return;
                    dot = Instantiate(prefab, dotRoot);
                    _agentDots[avatar.ID] = dot;
                }

                PlaceDot(dot, avatar.Position.X, avatar.Position.Y);
            });

            // Remove stale dots
            var stale = new List<UUID>();
            foreach (var kvp in _agentDots)
                if (!seen.Contains(kvp.Key)) stale.Add(kvp.Key);
            foreach (var id in stale)
            {
                Destroy(_agentDots[id]);
                _agentDots.Remove(id);
            }
        }

        private void PlaceDot(GameObject dot, float simX, float simY)
        {
            float px = (simX / mapSizeM - 0.5f) * mapSizePx;
            float py = (simY / mapSizeM - 0.5f) * mapSizePx;
            dot.transform.localPosition = new Vector3(px, py, 0f);
        }

        private void UpdateLabels(Simulator sim)
        {
            if (agentCountLabel != null)
                agentCountLabel.text = $"{sim.Stats.Agents} avatars";
        }

        private void ClearAgentDots()
        {
            foreach (var d in _agentDots.Values)
                if (d != null) Destroy(d);
            _agentDots.Clear();
        }
    }
}
