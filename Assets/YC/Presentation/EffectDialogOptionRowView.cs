using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class EffectDialogOptionRowView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private Image background;
        [SerializeField] private Text label;
        [SerializeField] private Text sourceLabel;
        [SerializeField] private LayoutElement layoutElement;
        [SerializeField] private Image selectedIndicator;
        [SerializeField] private GameObject legacyActionIndicator;

        public Button Button => button;
        public Image Background => background;
        public Text Label => label;
        public LayoutElement LayoutElement => layoutElement;
        public void SetSelected(bool selected)
        {
            if (legacyActionIndicator != null) legacyActionIndicator.SetActive(false);
            if (selectedIndicator != null) selectedIndicator.enabled = selected;
        }
        public void ResetSelection() => SetSelected(false);
        public void SetSourceAndDescription(string source, string description)
        {
            sourceLabel.text = source ?? string.Empty;
            label.text = description ?? string.Empty;
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
