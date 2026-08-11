using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class EffectDialogResourceRowView : MonoBehaviour
    {
        [SerializeField] private Text label;
        [SerializeField] private Button decreaseButton;
        [SerializeField] private Text decreaseLabel;
        [SerializeField] private Text valueText;
        [SerializeField] private Button increaseButton;
        [SerializeField] private Text increaseLabel;

        public Text Label => label;
        public Button DecreaseButton => decreaseButton;
        public Text DecreaseLabel => decreaseLabel;
        public Text ValueText => valueText;
        public Button IncreaseButton => increaseButton;
        public Text IncreaseLabel => increaseLabel;

        public bool TryValidateConfiguration(out string reason)
        {
            if (label == null || decreaseButton == null || decreaseLabel == null ||
                valueText == null || increaseButton == null || increaseLabel == null)
            {
                reason = "资源分配行模板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
