using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class GameplayPromptView : MonoBehaviour
    {
        [SerializeField] private Canvas canvas;
        [SerializeField] private RectTransform panelTransform;
        [SerializeField] private Text promptText;
        [SerializeField] private CanvasGroup canvasGroup;

        public Canvas Canvas => canvas;
        public RectTransform PanelTransform => panelTransform;
        public Text PromptText => promptText;
        public CanvasGroup CanvasGroup => canvasGroup;

        public bool TryValidateConfiguration(out string reason)
        {
            if (canvas == null || panelTransform == null || promptText == null || canvasGroup == null)
            {
                reason = "交互提示 View 引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
