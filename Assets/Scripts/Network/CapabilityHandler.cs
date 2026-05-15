using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using OpenMetaverse;
using SLQuest.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace SLQuest.Network
{
    /// <summary>
    /// Wraps the Second Life HTTP capability system.
    /// Caps are per-sim HTTPS endpoints returned during login/region handshake.
    /// Use these instead of UDP packets for heavy data transfer (mesh, texture, etc.)
    /// </summary>
    public sealed class CapabilityHandler : MonoBehaviour
    {
        private SLNetworkManager _net;
        private readonly Dictionary<string, Uri> _caps = new(StringComparer.OrdinalIgnoreCase);

        private void Awake()
        {
            _net = SLApplication.Instance?.Network
                ?? FindObjectOfType<SLNetworkManager>();
        }

        private void Start()
        {
            EventBus.Subscribe<SimConnectedEvent>(OnSimConnected);
        }

        private void OnDestroy()
        {
            EventBus.Unsubscribe<SimConnectedEvent>(OnSimConnected);
        }

        private void OnSimConnected(SimConnectedEvent evt)
        {
            var sim = evt.Simulator as Simulator;
            if (sim == null) return;
            RefreshCaps(sim);
        }

        private void RefreshCaps(Simulator sim)
        {
            _caps.Clear();
            if (sim.Caps?.CapabilityURI("Seed") is Uri seed)
                StartCoroutine(FetchSeedCaps(seed));
        }

        private IEnumerator FetchSeedCaps(Uri seedUri)
        {
            // Request the standard capability list
            var requestedCaps = new List<string>
            {
                "GetTexture", "GetMesh", "GetMesh2", "GetDisplayNames",
                "AvatarPickerSearch", "UpdateAgentInformation", "UpdateAgentLanguage",
                "EventQueueGet", "ChatSessionRequest", "CreateInventoryCategory",
                "CopyInventoryFromNotecard", "InventoryAPIv3", "LibraryAPIv3",
                "FetchInventory2", "FetchInventoryDescendents2", "FetchLib2",
                "FetchLibDescendents2", "ObjectMedia", "ObjectMediaNavigate",
                "ObjectAdd", "ProductInfoRequest", "ProvisionVoiceAccountRequest",
                "ViewerStartAuction", "ViewerAsset", "AgentState", "AttachmentResources",
                "RenderMaterials", "ServerReleaseNotes", "SimulatorFeatures"
            };

            string body = OpenMetaverse.StructuredData.OSDParser
                .SerializeJsonString(
                    OpenMetaverse.StructuredData.OSD.FromVector(
                        requestedCaps.ConvertAll(c =>
                            (OpenMetaverse.StructuredData.OSD)OpenMetaverse.StructuredData.OSD.FromString(c))));

            using var req = new UnityWebRequest(seedUri.ToString(), "POST");
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/llsd+json");

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Caps] Seed request failed: {req.error}");
                yield break;
            }

            try
            {
                var parsed = OpenMetaverse.StructuredData.OSDParser.DeserializeJson(req.downloadHandler.text);
                if (parsed is OpenMetaverse.StructuredData.OSDMap map)
                {
                    foreach (var kvp in map)
                    {
                        if (Uri.TryCreate(kvp.Value.AsString(), UriKind.Absolute, out var uri))
                            _caps[kvp.Key] = uri;
                    }
                    Debug.Log($"[Caps] Loaded {_caps.Count} capabilities");
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public bool TryGetCap(string name, out Uri uri) => _caps.TryGetValue(name, out uri);

        public bool HasCap(string name) => _caps.ContainsKey(name);
    }
}
