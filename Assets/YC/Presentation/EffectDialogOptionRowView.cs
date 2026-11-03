using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class EffectDialogOptionRowView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private Image background;
        [SerializeField] private Text label;
        [SerializeField] private LayoutElement layoutElement;
        [SerializeField] private Image selectedIndicator;
        [SerializeField] private GameObject legacyActionIndicator;
        private GameplayTextHostStandard labelStandard;

        private void Awake() => CaptureStandards();
        private void CaptureStandards()
        {
            if (label != null && labelStandard == null) { labelStandard = new GameplayTextHostStandard(label); labelStandard.AttachDocument(); }
        }
        private void RestoreStandards()
        { CaptureStandards(); labelStandard?.Apply(true); }
        private void LateUpdate() => RestoreStandards();

        public Button Button => button;
        public Image Background => background;
        public Text Label => label;
        public LayoutElement LayoutElement => layoutElement;
        public void SetSelected(bool selected)
        {
            if (legacyActionIndicator != null) legacyActionIndicator.SetActive(false);
            if (selectedIndicator != null) selectedIndicator.enabled = selected;
        }
        public bool TryValidateConfiguration(out string reason)
        {
            if (button == null || background == null || label == null || layoutElement == null)
            {
                reason = "效果选项模板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
