using System;
using System.Collections.Generic;
using OpenMetaverse;
using TMPro;
using SLQuest.Core;
using SLQuest.Scripting;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space VR panel for LSL llDialog prompts.
    ///
    /// Subscribes to LSLBridge.OnScriptDialog; each incoming dialog spawns a
    /// self-contained dialog card that positions itself in the player's FOV.
    /// Multiple dialogs stack. Button press sends ReplyToScriptDialog and
    /// destroys the card.
    ///
    /// Inspector wiring:
    ///   dialogCardPrefab — world-space Canvas with:
    ///                       ObjectName (TMP_Text)
    ///                       Message    (TMP_Text)
    ///                       ButtonContainer (Transform) — buttons are parented here
    ///                       buttonPrefab (Button + TMP_Text child "Label")
    ///                       CloseButton (Button) — dismiss without replying
    ///   cardSpawnOffset  — camera-local position for the first card
    ///   cardSpacing      — vertical spacing when multiple cards are stacked
    /// </summary>
    public sealed class ScriptDialogPanel : MonoBehaviour
    {
        [Header("Prefabs / layout")]
        [SerializeField] private GameObject dialogCardPrefab;
        [SerializeField] private GameObject buttonPrefab;
        [SerializeField] private Vector3    cardSpawnOffset = new(0f, 0f, 1.2f);
        [SerializeField] private float      cardSpacing     = 0.35f;
        [SerializeField] private int        maxCards        = 4;

        private LSLBridge _lsl;
        private readonly LinkedList<GameObject> _cards = new();

        private void Awake()
        {
            _lsl = SLApplication.Instance?.LSL ?? FindObjectOfType<LSLBridge>();
        }

        private void OnEnable()
        {
            if (_lsl != null) _lsl.OnScriptDialog += ShowDialog;
        }

        private void OnDisable()
        {
            if (_lsl != null) _lsl.OnScriptDialog -= ShowDialog;
        }

        // ── Dialog display ────────────────────────────────────────────────────

        private void ShowDialog(string message, List<string> buttons, UUID objectId, int channel)
        {
            if (dialogCardPrefab == null) return;

            // Evict oldest if at cap
            if (_cards.Count >= maxCards)
                DismissCard(_cards.Last);

            var cam = Camera.main;
            Vector3 worldPos = cam != null
                ? cam.transform.TransformPoint(cardSpawnOffset + Vector3.down * (_cards.Count * cardSpacing))
                : Vector3.zero;

            var card = Instantiate(dialogCardPrefab);
            card.transform.position = worldPos;
            if (cam != null)
                card.transform.rotation = Quaternion.LookRotation(
                    card.transform.position - cam.transform.position);

            var ln = _cards.AddFirst(card);

            // Populate labels
            var msgLabel  = card.transform.Find("Message")?.GetComponent<TMP_Text>();
            var nameLabel = card.transform.Find("ObjectName")?.GetComponent<TMP_Text>();
            if (msgLabel  != null) msgLabel.text  = message;
            // objectId → name is async; leave blank for now (LSLBridge already logged it)

            // Spawn buttons
            var container = card.transform.Find("ButtonContainer");
            if (container != null && buttonPrefab != null)
            {
                foreach (string btn in buttons)
                {
                    string label = btn; // capture
                    var go = Instantiate(buttonPrefab, container);
                    var txt = go.GetComponentInChildren<TMP_Text>();
                    if (txt != null) txt.text = label;

                    go.GetComponent<Button>()?.onClick.AddListener(() =>
                    {
                        _lsl?.ReplyDialog(objectId, channel, label);
                        DismissCard(ln);
                    });
                }
            }

            // Close / ignore button
            var closeBtn = card.transform.Find("CloseButton")?.GetComponent<Button>();
            closeBtn?.onClick.AddListener(() => DismissCard(ln));
        }

        private void DismissCard(LinkedListNode<GameObject> node)
        {
            if (node?.Value != null)
                Destroy(node.Value);
            _cards.Remove(node);
            RebuildStackPositions();
        }

        private void RebuildStackPositions()
        {
            var cam = Camera.main;
            if (cam == null) return;
            int i = 0;
            foreach (var card in _cards)
            {
                if (card != null)
                    card.transform.position = cam.transform.TransformPoint(
                        cardSpawnOffset + Vector3.down * (i * cardSpacing));
                i++;
            }
        }
    }
}
