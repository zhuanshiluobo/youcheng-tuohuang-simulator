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
        [SerializeField] private InputField valueInput;
        [SerializeField] private Image resourceIcon;
        [SerializeField] private Text draftPreview;
        [SerializeField] private string[] resourceNames;
        [SerializeField] private Sprite[] resourceIcons;
        [SerializeField] private string inventoryPreviewFormat = "库存 {0} / 剩余 {1}";
        [SerializeField] private string salePreviewFormat = "库存 {0} → {1} / 本批 {2} 金券";
        public InputField ValueInput => valueInput;

        public void RenderDraft(string resource, int inventory, int amount, int? price)
        {
            if (resourceIcon != null)
            {
                var index = resourceNames == null ? -1 : System.Array.IndexOf(resourceNames, resource);
                resourceIcon.sprite = index < 0 || resourceIcons == null || index >= resourceIcons.Length ? null : resourceIcons[index];
                resourceIcon.gameObject.SetActive(resourceIcon.sprite != null);
            }
            if (draftPreview != null) draftPreview.text = string.Format(price.HasValue ? salePreviewFormat : inventoryPreviewFormat,
                inventory, Mathf.Max(0, inventory - amount), price.GetValueOrDefault() * amount);
        }

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
