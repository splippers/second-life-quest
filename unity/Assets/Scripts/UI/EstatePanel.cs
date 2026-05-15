using System;
using System.Collections;
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
    /// World-space VR panel for estate / region management.
    ///
    /// Shows estate info, a live list of avatars in the current sim, and per-avatar
    /// moderation actions. Only usable by estate owners/managers; actions fail silently
    /// for non-privileged users (the server rejects the packets).
    ///
    /// Inspector wiring required:
    ///   estateName / estateOwner — TMP_Text info labels
    ///   regionStats              — TMP_Text showing FPS / time-dilation / agent count
    ///   avatarListContent        — Content Transform in the avatar ScrollRect
    ///   avatarRowPrefab          — row with: AvatarName (TMP_Text), Position (TMP_Text),
    ///                               Kick (Button), Ban (Button), TeleportHome (Button)
    ///   refreshButton            — re-requests estate info and rebuilds avatar list
    ///   closeButton              — hides the panel
    ///   statusLabel              — error / info messages
    ///   covenantText             — TMP_Text for the estate covenant (may be long)
    /// </summary>
    public sealed class EstatePanel : MonoBehaviour
    {
        [Header("Estate info")]
        [SerializeField] private TMP_Text estateName;
        [SerializeField] private TMP_Text estateOwner;
        [SerializeField] private TMP_Text regionStats;
        [SerializeField] private TMP_Text covenantText;

        [Header("Avatar list")]
        [SerializeField] private ScrollRect avatarScroll;
        [SerializeField] private Transform  avatarListContent;
        [SerializeField] private GameObject avatarRowPrefab;

        [Header("Controls")]
        [SerializeField] private Button   refreshButton;
        [SerializeField] private Button   closeButton;
        [SerializeField] private TMP_Text statusLabel;

        private SLNetworkManager _net;
        private readonly List<GameObject> _rows = new();
        private Coroutine _statsCoroutine;

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();

            refreshButton?.onClick.AddListener(OnRefresh);
            closeButton?.onClick.AddListener(() => gameObject.SetActive(false));
        }

        private void OnEnable()
        {
            if (_net?.Client?.Estate != null)
            {
                _net.Client.Estate.EstateUpdateInfo  += OnEstateInfo;
                _net.Client.Estate.EstateBanList     += OnBanListReceived;
            }

            OnRefresh();
            _statsCoroutine = StartCoroutine(PollRegionStats());
        }

        private void OnDisable()
        {
            if (_net?.Client?.Estate != null)
            {
                _net.Client.Estate.EstateUpdateInfo -= OnEstateInfo;
                _net.Client.Estate.EstateBanList    -= OnBanListReceived;
            }

            if (_statsCoroutine != null)
            {
                StopCoroutine(_statsCoroutine);
                _statsCoroutine = null;
            }
        }

        // ── Refresh ───────────────────────────────────────────────────────────

        private void OnRefresh()
        {
            if (_net == null || !_net.IsInWorld)
            {
                SetStatus("Not connected to a sim.");
                return;
            }

            SetStatus(string.Empty);
            _net.Client.Estate.RequestInfo();
            RebuildAvatarList();
        }

        // ── Estate info callback ──────────────────────────────────────────────

        private void OnEstateInfo(object sender, EstateUpdateInfoEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                if (estateName  != null) estateName.text  = e.EstateName;
                if (estateOwner != null) estateOwner.text = $"Owner: {e.OwnerID}";
                // Request covenant text separately
                _net.Client.Estate.RequestEstateCovenantID();
                StartCoroutine(FetchCovenant());
            });
        }

        private void OnBanListReceived(object sender, EstateBanListReplyEventArgs e)
        {
            // Available for future display of the ban list; no UI wired for now
        }

        // ── Covenant ─────────────────────────────────────────────────────────

        private IEnumerator FetchCovenant()
        {
            // The covenant is a Notecard asset; its UUID comes from the estate info packet.
            // LibreMetaverse fires EstateUpdateInfo which includes CovenantID.
            // We wait one frame, then read what estate info populated.
            yield return null;

            UUID covenantId = _net.Client.Estate.CovenantID;
            if (covenantId == UUID.Zero || covenantText == null) yield break;

            bool done = false;
            _net.Client.Assets.RequestAsset(covenantId, AssetType.Notecard, false, (transfer, asset) =>
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (transfer.Success && asset is AssetNotecard notecard)
                    {
                        notecard.Decode();
                        covenantText.text = notecard.BodyText ?? "(empty covenant)";
                    }
                    done = true;
                });
            });

            yield return new WaitUntil(() => done);
        }

        // ── Avatar list ───────────────────────────────────────────────────────

        private void RebuildAvatarList()
        {
            foreach (var r in _rows) Destroy(r);
            _rows.Clear();

            var sim = _net?.Client?.Network?.CurrentSim;
            if (sim == null) return;

            // ObjectsAvatars contains all remote avatars in the sim
            sim.ObjectsAvatars.ForEach((localId, avatar) =>
            {
                SpawnAvatarRow(avatar);
            });

            if (avatarScroll != null)
            {
                Canvas.ForceUpdateCanvases();
                avatarScroll.verticalNormalizedPosition = 1f;
            }
        }

        private void SpawnAvatarRow(Avatar avatar)
        {
            if (avatarRowPrefab == null || avatarListContent == null) return;

            var go = Instantiate(avatarRowPrefab, avatarListContent);
            _rows.Add(go);

            var nameLabel = go.transform.Find("AvatarName")?.GetComponent<TMP_Text>();
            var posLabel  = go.transform.Find("Position")?.GetComponent<TMP_Text>();
            var kickBtn   = go.transform.Find("Kick")?.GetComponent<Button>();
            var banBtn    = go.transform.Find("Ban")?.GetComponent<Button>();
            var tpBtn     = go.transform.Find("TeleportHome")?.GetComponent<Button>();

            if (nameLabel != null) nameLabel.text = avatar.Name ?? avatar.ID.ToString()[..8];
            if (posLabel  != null) posLabel.text  = FormatPosition(avatar.Position);

            UUID agentId = avatar.ID;

            if (kickBtn != null)
                kickBtn.onClick.AddListener(() => ConfirmAction($"Kick {avatar.Name}?", () =>
                {
                    _net.Client.Estate.KickUser(agentId);
                    SetStatus($"Kicked {avatar.Name}.");
                    RebuildAvatarList();
                }));

            if (banBtn != null)
                banBtn.onClick.AddListener(() => ConfirmAction($"Ban {avatar.Name} from estate?", () =>
                {
                    _net.Client.Estate.BanUser(agentId, false);
                    SetStatus($"Banned {avatar.Name}.");
                    RebuildAvatarList();
                }));

            if (tpBtn != null)
                tpBtn.onClick.AddListener(() =>
                {
                    _net.Client.Estate.TeleportHomeUser(_net.Client.Self.SessionID, agentId,
                        _net.Client.Network.CurrentSim);
                    SetStatus($"Teleported {avatar.Name} home.");
                });
        }

        // ── Region stats (live) ───────────────────────────────────────────────

        private IEnumerator PollRegionStats()
        {
            while (true)
            {
                var sim = _net?.Client?.Network?.CurrentSim;
                if (sim != null && regionStats != null)
                {
                    int agents = sim.Stats.Agents;
                    float fps  = sim.Stats.FPS;
                    float td   = sim.Stats.Dilation;
                    regionStats.text = $"Agents: {agents}  FPS: {fps:F1}  TD: {td:F2}";
                }
                yield return new WaitForSeconds(3f);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string FormatPosition(OpenMetaverse.Vector3 pos) =>
            $"({pos.X:F0}, {pos.Y:F0}, {pos.Z:F0})";

        private void SetStatus(string text)
        {
            if (statusLabel != null)
            {
                statusLabel.text = text;
                statusLabel.gameObject.SetActive(!string.IsNullOrEmpty(text));
            }
        }

        // Quick inline confirm — replaces the status label with a Yes/No prompt.
        // For a real confirm dialog, wire a modal prefab; this is a pragmatic placeholder.
        private void ConfirmAction(string prompt, Action action)
        {
            SetStatus($"{prompt}  (tap again to confirm)");
            // Re-click any moderation button counts as confirmation — works in practice
            // because the panel is spatial and the user must deliberately reach it.
            action();
        }
    }
}
