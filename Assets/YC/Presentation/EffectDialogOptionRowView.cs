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

        public Button Button => button;
        public Image Background => background;
        public Text Label => label;
        public LayoutElement LayoutElement => layoutElement;

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
