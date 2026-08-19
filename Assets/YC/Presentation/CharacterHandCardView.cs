using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class CharacterHandCardView : MonoBehaviour
    {
        [SerializeField] private RectTransform root;
        [SerializeField] private RawImage image;
        [SerializeField] private Button button;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private Outline outline;
        [SerializeField] private CardPointerInteraction pointerInteraction;

        public RectTransform Root => root;
        public RawImage Image => image;
        public Button Button => button;
        public CanvasGroup CanvasGroup => canvasGroup;
        public Outline Outline => outline;
        public CardPointerInteraction PointerInteraction => pointerInteraction;

        public bool TryValidateConfiguration(out string reason)
        {
            if (root == null || image == null || button == null || canvasGroup == null ||
                outline == null || pointerInteraction == null)
            {
                reason = "手牌卡牌模板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
