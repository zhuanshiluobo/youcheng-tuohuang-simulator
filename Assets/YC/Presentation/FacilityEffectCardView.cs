using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class FacilityEffectCardView : MonoBehaviour
    {
        [SerializeField] private RectTransform cardRect;
        [SerializeField] private Image background;
        [SerializeField] private Button button;
        [SerializeField] private Outline outline;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private RawImage cardImage;
        [SerializeField] private Text fallbackLabel;
        [SerializeField] private CardPointerInteraction pointerInteraction;

        public RectTransform CardRect => cardRect;
        public Image Background => background;
        public Button Button => button;
        public Outline Outline => outline;
        public CanvasGroup CanvasGroup => canvasGroup;
        public RawImage CardImage => cardImage;
        public Text FallbackLabel => fallbackLabel;
        public CardPointerInteraction PointerInteraction => pointerInteraction;

        public bool TryValidateConfiguration(out string reason)
        {
            if (cardRect == null || background == null || button == null || outline == null ||
                canvasGroup == null || cardImage == null || fallbackLabel == null || pointerInteraction == null)
            {
                reason = "设施效果卡模板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
