using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class EffectDialogActionButtonView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private Image background;
        [SerializeField] private Text label;

        public Button Button => button;
        public Image Background => background;
        public Text Label => label;

        public bool TryValidateConfiguration(out string reason)
        {
            if (button == null || background == null || label == null)
            {
                reason = "效果对话框动作按钮引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
