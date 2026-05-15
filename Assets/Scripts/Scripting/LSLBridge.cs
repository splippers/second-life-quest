using System;
using System.Collections.Generic;
using OpenMetaverse;
using SLQuest.Core;
using UnityEngine;

namespace SLQuest.Scripting
{
    /// <summary>
    /// LSL script integration bridge.
    ///
    /// Second Life scripts (LSL) run server-side; the viewer never executes LSL
    /// directly. This bridge handles the viewer-side consequences of script events:
    ///   - llDialog / llTextBox → UI prompt panels
    ///   - llRequestPermissions → permission request dialogs
    ///   - llSay / llShout / llWhisper on non-zero channels → already handled via chat
    ///   - llGiveInventory → inventory acceptance prompt
    ///   - CONTROL_* permission → capture avatar control input for scripted vehicles
    ///   - Camera control (llSetCameraParams) → apply camera overrides
    ///   - llMapDestination → open map at target
    ///   - llLoadURL → open browser or in-world browser panel
    /// </summary>
    public sealed class LSLBridge : MonoBehaviour
    {
        private SLNetworkManager _net;

        // Active control permissions granted by scripts
        private UUID _controlPermObjectId = UUID.Zero;
        private bool _cameraControlActive;

        public event Action<string, List<string>, UUID, int> OnScriptDialog;
        public event Action<string, UUID>                    OnInventoryOffer;
        public event Action<string, string>                  OnURLLoad;

        private void Awake()
        {
            _net = SLApplication.Instance?.Network ?? FindObjectOfType<SLNetworkManager>();
        }

        private void Start()
        {
            var self = _net.Client.Self;
            self.ScriptDialog         += OnDialog;
            self.ScriptControlChange  += OnControlChange;
            self.ScriptQuestion       += OnScriptQuestion;
            self.LoadURL              += OnLoadURL;
            self.MoneyBalance         += OnMoneyBalance;
            _net.Client.Inventory.InventoryObjectOffered += OnInventoryObjectOffered;
        }

        private void OnDestroy()
        {
            if (_net?.Client?.Self == null) return;
            var self = _net.Client.Self;
            self.ScriptDialog        -= OnDialog;
            self.ScriptControlChange -= OnControlChange;
            self.ScriptQuestion      -= OnScriptQuestion;
            self.LoadURL             -= OnLoadURL;
            self.MoneyBalance        -= OnMoneyBalance;
            _net.Client.Inventory.InventoryObjectOffered -= OnInventoryObjectOffered;
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private void OnDialog(object sender, ScriptDialogEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                Debug.Log($"[LSL] Script dialog from {e.ObjectName}: {e.Message}");
                OnScriptDialog?.Invoke(e.Message, e.Buttons, e.ObjectID, e.Channel);
            });
        }

        private void OnControlChange(object sender, ScriptControlEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                _controlPermObjectId = e.Controls != 0 ? e.ObjectID : UUID.Zero;
                Debug.Log($"[LSL] Control permission: {e.Controls} from {e.ObjectID}");
            });
        }

        private void OnScriptQuestion(object sender, ScriptQuestionEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                // Auto-grant benign permissions; prompt user for camera/control/track perms
                var dangerous = ScriptPermission.ControlCamera |
                                ScriptPermission.TakeControls  |
                                ScriptPermission.TrackCamera;

                if ((e.Questions & dangerous) == 0)
                {
                    _net.Client.Self.ScriptQuestionReply(_net.Client.Network.CurrentSim,
                        e.ItemID, e.TaskID, e.Questions);
                }
                else
                {
                    Debug.Log($"[LSL] Script requesting elevated permissions: {e.Questions}");
                    // TODO: surface a VR permission dialog to the user
                }
            });
        }

        private void OnLoadURL(object sender, LoadUrlEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                Debug.Log($"[LSL] LoadURL: {e.URL}");
                OnURLLoad?.Invoke(e.ObjectName, e.URL);
                // Open URL in Android's default browser
                Application.OpenURL(e.URL);
            });
        }

        private void OnMoneyBalance(object sender, BalanceEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                Debug.Log($"[LSL] Balance: L${e.Balance}");
                // UI layer can subscribe to OnMoneyBalance if needed
            });
        }

        private void OnInventoryObjectOffered(object sender, InventoryObjectOfferedEventArgs e)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                OnInventoryOffer?.Invoke(e.Offer.FromAgentName, e.Offer.Message);
                // Auto-accept by returning true — production should show a dialog
                e.Accept = true;
            });
        }

        // ── Reply helpers ─────────────────────────────────────────────────────

        public void ReplyDialog(UUID objectId, int channel, string button)
        {
            _net.Client.Self.ReplyToScriptDialog(channel, 0, button, objectId);
        }
    }
}
