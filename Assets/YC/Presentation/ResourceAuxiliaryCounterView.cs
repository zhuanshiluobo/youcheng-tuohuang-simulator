using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Rules;

namespace YC.Presentation
{
    public sealed class ResourceAuxiliaryCounterView : MonoBehaviour
    {
        [SerializeField] private ResourceType resourceType;
        [SerializeField] private RectTransform contentRoot;
        [SerializeField] private Image resourceIcon;
        [SerializeField] private Text resourceName;
        [SerializeField] private Text amountText;
        [SerializeField] private Image flashOverlay;

        public ResourceType ResourceType => resourceType;
        public RectTransform ContentRoot => contentRoot;
        public Image ResourceIcon => resourceIcon;
        public Text ResourceName => resourceName;
        public Text AmountText => amountText;
        public Image FlashOverlay => flashOverlay;

        public bool TryValidateConfiguration(out string reason)
        {
            if (contentRoot == null || resourceIcon == null || resourceIcon.sprite == null ||
                resourceName == null || amountText == null || flashOverlay == null)
            {
                reason = "独立实体资源槽固定引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
