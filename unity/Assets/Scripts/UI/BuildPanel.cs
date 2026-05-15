using TMPro;
using SLQuest.Building;
using UnityEngine;
using UnityEngine.UI;

namespace SLQuest.UI
{
    /// <summary>
    /// HUD panel for the build toolset: tool picker, prim properties editor,
    /// texture picker, and link/unlink buttons.
    /// </summary>
    public sealed class BuildPanel : MonoBehaviour
    {
        [Header("Tools")]
        [SerializeField] private Button selectBtn;
        [SerializeField] private Button moveBtn;
        [SerializeField] private Button rotateBtn;
        [SerializeField] private Button scaleBtn;
        [SerializeField] private Button createBtn;

        [Header("Properties")]
        [SerializeField] private TMP_InputField nameField;
        [SerializeField] private TMP_InputField descField;
        [SerializeField] private TMP_Text positionLabel;
        [SerializeField] private TMP_Text sizeLabel;

        [Header("Actions")]
        [SerializeField] private Button linkBtn;
        [SerializeField] private Button unlinkBtn;
        [SerializeField] private Button deleteBtn;
        [SerializeField] private Button closeBtn;

        private BuildingManager _bm;

        private void Awake()
        {
            _bm = SLApplication.Instance?.Building ?? FindObjectOfType<BuildingManager>();

            selectBtn.onClick.AddListener(() => _bm?.SetTool(BuildTool.Select));
            moveBtn.onClick.AddListener(()   => _bm?.SetTool(BuildTool.Move));
            rotateBtn.onClick.AddListener(() => _bm?.SetTool(BuildTool.Rotate));
            scaleBtn.onClick.AddListener(()  => _bm?.SetTool(BuildTool.Scale));
            createBtn.onClick.AddListener(() => SpawnAtReticle());

            nameField.onEndEdit.AddListener(v => _bm?.SetName(v));
            descField.onEndEdit.AddListener(v => _bm?.SetDescription(v));

            linkBtn.onClick.AddListener(()   => _bm?.LinkSelection());
            unlinkBtn.onClick.AddListener(() => _bm?.UnlinkSelected());
            deleteBtn.onClick.AddListener(() => _bm?.DeleteSelected());
            closeBtn.onClick.AddListener(()  => VRUIManager.Instance?.HidePanel(this));
        }

        private void Start()
        {
            if (_bm == null) return;
            _bm.OnSelectionChanged += OnSelectionChanged;
            _bm.OnPropertiesUpdated += OnPropertiesUpdated;
        }

        private void OnDestroy()
        {
            if (_bm != null)
            {
                _bm.OnSelectionChanged  -= OnSelectionChanged;
                _bm.OnPropertiesUpdated -= OnPropertiesUpdated;
            }
        }

        private void OnSelectionChanged(World.SLPrimitive prim)
        {
            bool hasSel = prim != null;
            moveBtn.interactable   = hasSel;
            rotateBtn.interactable = hasSel;
            scaleBtn.interactable  = hasSel;
            deleteBtn.interactable = hasSel;
            nameField.interactable = hasSel;
            descField.interactable = hasSel;

            if (!hasSel)
            {
                nameField.text    = string.Empty;
                descField.text    = string.Empty;
                positionLabel.text = "—";
                sizeLabel.text     = "—";
            }
        }

        private void OnPropertiesUpdated(OpenMetaverse.Primitive.ObjectProperties props)
        {
            if (props == null) return;
            nameField.text = props.Name;
            descField.text = props.Description;
        }

        private void Update()
        {
            var sel = _bm?.SelectedPrim;
            if (sel == null) return;
            var t = sel.transform;
            positionLabel.text = $"Pos: {t.position.x:F2}, {t.position.y:F2}, {t.position.z:F2}";
            sizeLabel.text     = $"Size: {t.localScale.x:F2}, {t.localScale.y:F2}, {t.localScale.z:F2}";
        }

        private void SpawnAtReticle()
        {
            var cam = Camera.main;
            if (cam == null || _bm == null) return;
            var spawnPos = cam.transform.position + cam.transform.forward * 3f;
            _bm.RezBox(spawnPos);
        }
    }
}
