using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space on-screen keyboard for Meta Quest 3.
    ///
    /// Spawns as a floating panel when a TMP_InputField is selected.
    /// Uses a grid of Button prefabs generated at runtime from the layout arrays.
    /// Supports shift (caps), backspace, space, and enter.
    ///
    /// Usage: call <see cref="Open(TMP_InputField, Action)"/> from any panel that
    /// needs text entry, or wire it to TMP_InputField.onSelect via
    /// <see cref="VRKeyboardActivator"/>.
    ///
    /// Inspector wiring:
    ///   keyButtonPrefab  — Button with TMP_Text child "Label"
    ///   keyContainer     — GridLayoutGroup Transform for key buttons
    ///   currentTextLabel — TMP_Text showing the text being typed (optional)
    ///   closeButton      — hides keyboard without submitting
    /// </summary>
    public sealed class VRKeyboard : MonoBehaviour
    {
        public static VRKeyboard Instance { get; private set; }

        [SerializeField] private GameObject keyButtonPrefab;
        [SerializeField] private Transform  keyContainer;
        [SerializeField] private TMP_Text   currentTextLabel;
        [SerializeField] private Button     closeButton;

        private TMP_InputField _target;
        private Action         _onSubmit;
        private bool           _shift;

        // QWERTY rows
        private static readonly string[] Row1 = { "q","w","e","r","t","y","u","i","o","p" };
        private static readonly string[] Row2 = { "a","s","d","f","g","h","j","k","l" };
        private static readonly string[] Row3 = { "z","x","c","v","b","n","m" };

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            closeButton?.onClick.AddListener(Close);
            BuildKeys();
            gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void Open(TMP_InputField target, Action onSubmit = null)
        {
            _target   = target;
            _onSubmit = onSubmit;
            _shift    = false;
            gameObject.SetActive(true);
            RefreshLabel();
            PositionInFrontOfCamera();
        }

        public void Close()
        {
            _target = null;
            gameObject.SetActive(false);
        }

        // ── Key layout ────────────────────────────────────────────────────────

        private void BuildKeys()
        {
            if (keyContainer == null || keyButtonPrefab == null) return;

            foreach (var k in Row1) SpawnKey(k);
            SpawnSpecial("⌫", OnBackspace);
            foreach (var k in Row2) SpawnKey(k);
            SpawnSpecial("↑", OnShift);
            foreach (var k in Row3) SpawnKey(k);
            SpawnSpecial("Space", OnSpace, wide: true);
            SpawnSpecial("↵", OnEnter);
        }

        private void SpawnKey(string lower)
        {
            var go  = Instantiate(keyButtonPrefab, keyContainer);
            var lbl = go.GetComponentInChildren<TMP_Text>();
            var btn = go.GetComponent<Button>();
            string k = lower;
            if (lbl != null) lbl.text = k.ToUpperInvariant();
            btn?.onClick.AddListener(() =>
            {
                if (_target == null) return;
                string ch = _shift ? k.ToUpperInvariant() : k;
                _target.text += ch;
                if (_shift) { _shift = false; RefreshShiftVisual(); }
                RefreshLabel();
            });
        }

        private void SpawnSpecial(string label, Action action, bool wide = false)
        {
            var go  = Instantiate(keyButtonPrefab, keyContainer);
            var lbl = go.GetComponentInChildren<TMP_Text>();
            var btn = go.GetComponent<Button>();
            var rt  = go.GetComponent<RectTransform>();

            if (lbl != null) lbl.text = label;
            if (wide && rt != null)
            {
                var size = rt.sizeDelta;
                rt.sizeDelta = new Vector2(size.x * 3f, size.y);
            }
            btn?.onClick.AddListener(() => action?.Invoke());
        }

        // ── Special key handlers ──────────────────────────────────────────────

        private void OnBackspace()
        {
            if (_target == null) return;
            if (_target.text.Length > 0)
                _target.text = _target.text[..^1];
            RefreshLabel();
        }

        private void OnShift()
        {
            _shift = !_shift;
            RefreshShiftVisual();
        }

        private void OnSpace()
        {
            if (_target == null) return;
            _target.text += ' ';
            RefreshLabel();
        }

        private void OnEnter()
        {
            _onSubmit?.Invoke();
            Close();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void RefreshLabel()
        {
            if (currentTextLabel != null && _target != null)
                currentTextLabel.text = _target.text;
        }

        private void RefreshShiftVisual()
        {
            // Rebuild key labels to show correct case — find all buttons in container
            if (keyContainer == null) return;
            int rowIdx = 0;
            string[][] rows = { Row1, Row2, Row3 };
            int keyIdx = 0;
            foreach (Transform child in keyContainer)
            {
                if (rowIdx >= rows.Length) break;
                var lbl = child.GetComponentInChildren<TMP_Text>();
                if (lbl == null) continue;
                string k = rows[rowIdx][keyIdx];
                if (lbl.text == k || lbl.text == k.ToUpperInvariant())
                {
                    lbl.text = _shift ? k.ToUpperInvariant() : k;
                    keyIdx++;
                    if (keyIdx >= rows[rowIdx].Length) { rowIdx++; keyIdx = 0; }
                }
            }
        }

        private void PositionInFrontOfCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            transform.position = cam.transform.TransformPoint(new Vector3(0f, -0.3f, 0.8f));
            transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
        }
    }
}
