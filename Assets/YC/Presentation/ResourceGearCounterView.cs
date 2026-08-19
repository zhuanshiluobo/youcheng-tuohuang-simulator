using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Rules;

namespace YC.Presentation
{
    public sealed class ResourceGearCounterView : MonoBehaviour
    {
        [SerializeField] private ResourceType resourceType;
        [SerializeField] private Image resourceIcon;
        [SerializeField] private Text resourceName;
        [SerializeField] private RectTransform mainGear;
        [SerializeField] private RectTransform idlerGear;
        [SerializeField] private Text tensDigitText;
        [SerializeField] private Text onesDigitText;
        [SerializeField] private Image tensGearCover;
        [SerializeField] private Image onesGearCover;

        public ResourceType ResourceType => resourceType;
        public Image ResourceIcon => resourceIcon;
        public Text ResourceName => resourceName;
        public RectTransform MainGear => mainGear;
        public RectTransform IdlerGear => idlerGear;
        public Text TensDigitText => tensDigitText;
        public Text OnesDigitText => onesDigitText;
        public Image TensGearCover => tensGearCover;
        public Image OnesGearCover => onesGearCover;

        public bool TryValidateConfiguration(out string reason)
        {
            if (resourceIcon == null || resourceIcon.sprite == null || resourceName == null ||
                mainGear == null || idlerGear == null ||
                tensDigitText == null || onesDigitText == null ||
                tensGearCover == null || onesGearCover == null ||
                tensGearCover.sprite == null || onesGearCover.sprite == null)
            {
                reason = "实体资源计数列的牌窗、十位或个位齿轮引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
