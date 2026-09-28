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

        private void OnEnable()
        {
            // 旧预览动画将子 Canvas 的 sortingOrder 写成 0/200。
            // 预览必须继承手牌面板（或弃牌页）的层级，不能穿到主界面后方或弹窗前方。
            var previewCanvas = image != null ? image.GetComponent<Canvas>() : null;
            if (previewCanvas != null)
            {
                previewCanvas.overrideSorting = false;
            }
        }

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
