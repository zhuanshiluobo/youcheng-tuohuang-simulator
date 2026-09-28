using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class GameplayPromptView : MonoBehaviour
    {
        [Tooltip("启用旧版提示框自动尺寸及滑入动画；默认仅淡入淡出，不改手动布局。")]
        [SerializeField] private bool useRuntimeLayout;
        public bool UseRuntimeLayout => useRuntimeLayout;
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
