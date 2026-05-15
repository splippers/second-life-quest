using System.Collections.Generic;
using TMPro;
using SLQuest.Chat;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// Displays the chat history and allows sending local chat and IMs.
    /// History is a ScrollRect with pooled text elements for performance.
    /// </summary>
    public sealed class ChatPanel : MonoBehaviour
    {
        [SerializeField] private ScrollRect  scrollRect;
        [SerializeField] private Transform   contentRoot;
        [SerializeField] private TMP_Text    messageRowPrefab;
        [SerializeField] private TMP_InputField inputField;
        [SerializeField] private Button      sendButton;
        [SerializeField] private int         maxRows = 200;

        [Header("Colours")]
        [SerializeField] private Color localChatColour = Color.white;
        [SerializeField] private Color imColour        = new(0.7f, 0.9f, 1f);
        [SerializeField] private Color systemColour    = new(0.8f, 0.8f, 0.4f);

        private ChatManager _chat;
        private readonly Queue<TMP_Text> _rows = new();

        private void Awake()
        {
            _chat = SLApplication.Instance?.Chat ?? FindObjectOfType<ChatManager>();
            sendButton.onClick.AddListener(OnSend);
            inputField.onEndEdit.AddListener(OnEndEdit);
        }

        private void Start()
        {
            if (_chat == null) return;

            // Show existing history
            foreach (var e in _chat.History) AppendRow(e);
            _chat.OnNewEntry += AppendRow;
        }

        private void OnDestroy()
        {
            if (_chat != null) _chat.OnNewEntry -= AppendRow;
        }

        private void AppendRow(ChatEntry entry)
        {
            if (messageRowPrefab == null) return;

            var row = Instantiate(messageRowPrefab, contentRoot);
            row.text  = entry.ToString();
            row.color = entry.Source switch
            {
                ChatSource.IM     => imColour,
                ChatSource.System => systemColour,
                _                  => localChatColour
            };

            _rows.Enqueue(row);
            if (_rows.Count > maxRows)
                Destroy(_rows.Dequeue().gameObject);

            // Scroll to bottom on next frame
            Canvas.ForceUpdateCanvases();
            scrollRect.verticalNormalizedPosition = 0f;
        }

        private void OnSend()
        {
            var text = inputField.text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (text.StartsWith("/me ", System.StringComparison.OrdinalIgnoreCase))
                _chat?.Say(text[4..], 0, OpenMetaverse.ChatType.Emote);
            else if (text.StartsWith("/shout ", System.StringComparison.OrdinalIgnoreCase))
                _chat?.Say(text[7..], 0, OpenMetaverse.ChatType.Shout);
            else if (text.StartsWith("/whisper ", System.StringComparison.OrdinalIgnoreCase))
                _chat?.Say(text[9..], 0, OpenMetaverse.ChatType.Whisper);
            else
                _chat?.Say(text);

            inputField.text = string.Empty;
        }

        private void OnEndEdit(string text)
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                OnSend();
        }
    }
}
