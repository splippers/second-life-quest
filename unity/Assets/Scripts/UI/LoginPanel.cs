using TMPro;
using SLQuest.Network;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// World-space login form. Credentials are collected here and forwarded to
    /// <see cref="LoginManager"/>. Passwords are never stored to disk.
    /// </summary>
    public sealed class LoginPanel : MonoBehaviour
    {
        [Header("Fields")]
        [SerializeField] private TMP_InputField firstNameField;
        [SerializeField] private TMP_InputField lastNameField;
        [SerializeField] private TMP_InputField passwordField;
        [SerializeField] private TMP_Dropdown   gridDropdown;
        [SerializeField] private TMP_InputField customGridField;
        [SerializeField] private Toggle         rememberMeToggle;

        [Header("Actions")]
        [SerializeField] private Button loginButton;
        [SerializeField] private Button cancelButton;
        [SerializeField] private TMP_Text statusLabel;

        private LoginManager _loginManager;

        private void Awake()
        {
            _loginManager = SLApplication.Instance?.Login
                         ?? FindObjectOfType<LoginManager>();

            passwordField.contentType = TMP_InputField.ContentType.Password;
            passwordField.ForceLabelUpdate();

            loginButton.onClick.AddListener(OnLoginClicked);
            cancelButton.onClick.AddListener(OnCancelClicked);
            gridDropdown.onValueChanged.AddListener(OnGridChanged);
        }

        private void Start()
        {
            Core.EventBus.Subscribe<LoginSucceededEvent>(OnSuccess);
            Core.EventBus.Subscribe<LoginFailedEvent>(OnFailed);

            // Restore saved username (not password)
            if (PlayerPrefs.HasKey("SLQ_FirstName"))
                firstNameField.text = PlayerPrefs.GetString("SLQ_FirstName");
            if (PlayerPrefs.HasKey("SLQ_LastName"))
                lastNameField.text = PlayerPrefs.GetString("SLQ_LastName");

            UpdateGridFieldVisibility();
        }

        private void OnDestroy()
        {
            Core.EventBus.Unsubscribe<LoginSucceededEvent>(OnSuccess);
            Core.EventBus.Unsubscribe<LoginFailedEvent>(OnFailed);
        }

        private void OnLoginClicked()
        {
            var creds = new LoginCredentials
            {
                firstName       = firstNameField.text.Trim(),
                lastName        = lastNameField.text.Trim(),
                password        = passwordField.text,
                startLocation   = "last",
                grid            = (GridPreset)gridDropdown.value,
                customGridUri   = customGridField.text.Trim()
            };

            if (!LoginManager.ValidateCredentials(creds, out var error))
            {
                ShowStatus(error, isError: true);
                return;
            }

            if (rememberMeToggle.isOn)
            {
                PlayerPrefs.SetString("SLQ_FirstName", creds.firstName);
                PlayerPrefs.SetString("SLQ_LastName",  creds.lastName);
                PlayerPrefs.Save();
            }

            ShowStatus("Connecting...", isError: false);
            SetInteractive(false);
            _loginManager.Login(creds);
        }

        private void OnCancelClicked()
        {
            _loginManager.Logout();
            SetInteractive(true);
            ShowStatus(string.Empty, false);
        }

        private void OnGridChanged(int _) => UpdateGridFieldVisibility();

        private void UpdateGridFieldVisibility()
        {
            bool custom = gridDropdown.value == (int)GridPreset.Custom;
            customGridField.gameObject.SetActive(custom);
        }

        private void OnSuccess(LoginSucceededEvent _)
        {
            ShowStatus("Connected!", isError: false);
            // VRUIManager will hide this panel automatically
        }

        private void OnFailed(LoginFailedEvent evt)
        {
            ShowStatus($"Login failed: {evt.Reason}", isError: true);
            SetInteractive(true);
        }

        private void ShowStatus(string msg, bool isError)
        {
            statusLabel.text  = msg;
            statusLabel.color = isError ? Color.red : Color.white;
        }

        private void SetInteractive(bool enabled)
        {
            firstNameField.interactable = enabled;
            lastNameField.interactable  = enabled;
            passwordField.interactable  = enabled;
            gridDropdown.interactable   = enabled;
            loginButton.interactable    = enabled;
        }
    }
}
