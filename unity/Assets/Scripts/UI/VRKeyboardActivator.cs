using TMPro;
using UnityEngine;

namespace SLQuest.UI
{
    /// <summary>
    /// Attach to any GameObject with a <see cref="TMP_InputField"/> to
    /// automatically open the <see cref="VRKeyboard"/> when the field is
    /// selected and close it when deselected.
    /// </summary>
    [RequireComponent(typeof(TMP_InputField))]
    public sealed class VRKeyboardActivator : MonoBehaviour
    {
        private TMP_InputField _field;

        private void Awake()
        {
            _field = GetComponent<TMP_InputField>();
        }

        private void OnEnable()
        {
            _field.onSelect.AddListener(OnSelect);
            _field.onDeselect.AddListener(OnDeselect);
        }

        private void OnDisable()
        {
            _field.onSelect.RemoveListener(OnSelect);
            _field.onDeselect.RemoveListener(OnDeselect);
        }

        private void OnSelect(string _)
        {
            VRKeyboard.Instance?.Open(_field);
        }

        private void OnDeselect(string _)
        {
            if (VRKeyboard.Instance != null && VRKeyboard.Instance.gameObject.activeSelf)
                VRKeyboard.Instance.Close();
        }
    }
}
