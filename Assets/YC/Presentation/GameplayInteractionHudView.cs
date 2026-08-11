using UnityEngine;

namespace YC.Presentation
{
    public sealed class GameplayInteractionHudView : MonoBehaviour
    {
        [SerializeField] private Canvas canvas;
        [SerializeField] private GameplayPromptView promptView;
        [SerializeField] private ActionPanelView actionPanelView;
        [SerializeField] private ExpandableInfoPanel infoPanel;
        [SerializeField] private BuildInfoPanel buildInfoPanel;
        [SerializeField] private GameplayDialogRegistry dialogRegistry;
        [SerializeField] private MobileCityInteractionController cityInteractionController;

        public Canvas Canvas => canvas;
        public GameplayPromptView PromptView => promptView;
        public ActionPanelView ActionPanelView => actionPanelView;
        public ExpandableInfoPanel InfoPanel => infoPanel;
        public BuildInfoPanel BuildInfoPanel => buildInfoPanel;
        public GameplayDialogRegistry DialogRegistry => dialogRegistry;
        public MobileCityInteractionController CityInteractionController => cityInteractionController;
        public bool TryValidateConfiguration(out string reason)
        {
            if (canvas == null || promptView == null || actionPanelView == null ||
                infoPanel == null || buildInfoPanel == null || dialogRegistry == null)
            {
                reason = "交互 HUD 总 View 引用不完整。";
                return false;
            }

            if (!promptView.TryValidateConfiguration(out reason) ||
                !actionPanelView.TryValidateConfiguration(out reason) ||
                infoPanel.View == null ||
                !infoPanel.View.IsBoundTo(infoPanel) ||
                !infoPanel.View.TryValidateConfiguration(out reason) ||
                buildInfoPanel.View == null ||
                !buildInfoPanel.View.IsBoundTo(buildInfoPanel) ||
                !buildInfoPanel.View.TryValidateConfiguration(out reason) ||
                !dialogRegistry.TryValidateConfiguration(out reason))
            {
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public bool IsBoundTo(MobileCityInteractionController controller)
        {
            return controller != null && cityInteractionController == controller;
        }

        public static bool TryValidateSceneBinding(
            GameplayInteractionHudView view,
            MobileCityInteractionController controller,
            ExpandableInfoPanel configuredInfoPanel,
            BuildInfoPanel configuredBuildInfoPanel,
            out string reason)
        {
            if (view == null)
            {
                reason = "缺少 GameplayInteractionHudView 场景引用。";
                return false;
            }

            if (!view.TryValidateConfiguration(out reason))
            {
                return false;
            }

            if (!view.IsBoundTo(controller))
            {
                reason = "HUD 未双向绑定当前 MobileCityInteractionController。";
                return false;
            }

            if (configuredInfoPanel == null || configuredBuildInfoPanel == null ||
                view.InfoPanel != configuredInfoPanel ||
                view.BuildInfoPanel != configuredBuildInfoPanel)
            {
                reason = "MobileCity 与 HUD 的信息面板序列化引用不一致。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
