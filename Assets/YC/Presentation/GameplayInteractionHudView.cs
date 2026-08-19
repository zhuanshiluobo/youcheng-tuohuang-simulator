using UnityEngine;

namespace YC.Presentation
{
    public sealed class GameplayInteractionHudView : MonoBehaviour
    {
        [SerializeField] private Canvas canvas;
        [SerializeField] private TabletopCanvasLayout tabletopCanvas;
        [SerializeField] private GameplayPromptView promptView;
        [SerializeField] private ActionPanelView actionPanelView;
        [SerializeField] private CharacterHandPanel characterHandPanel;
        [SerializeField] private ResourceCounterBoard resourceCounterBoard;
        [SerializeField] private BuildInfoPanel buildInfoPanel;
        [SerializeField] private GameplayDialogRegistry dialogRegistry;
        [SerializeField] private MobileCityInteractionController cityInteractionController;

        public Canvas Canvas => canvas;
        public TabletopCanvasLayout TabletopCanvas => tabletopCanvas;
        public GameplayPromptView PromptView => promptView;
        public ActionPanelView ActionPanelView => actionPanelView;
        public CharacterHandPanel CharacterHandPanel => characterHandPanel;
        public ResourceCounterBoard ResourceCounterBoard => resourceCounterBoard;
        public BuildInfoPanel BuildInfoPanel => buildInfoPanel;
        public GameplayDialogRegistry DialogRegistry => dialogRegistry;
        public MobileCityInteractionController CityInteractionController => cityInteractionController;
        public bool TryValidateConfiguration(out string reason)
        {
            if (canvas == null || tabletopCanvas == null || promptView == null || actionPanelView == null ||
                characterHandPanel == null || resourceCounterBoard == null || buildInfoPanel == null || dialogRegistry == null)
            {
                reason = "交互 HUD 总 View 引用不完整。";
                return false;
            }

            if (!tabletopCanvas.TryValidateConfiguration(out reason) ||
                !promptView.TryValidateConfiguration(out reason) ||
                !actionPanelView.TryValidateConfiguration(out reason) ||
                !characterHandPanel.TryValidateConfiguration(out reason) ||
                !resourceCounterBoard.TryValidateConfiguration(out reason) ||
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
            ResourceCounterBoard configuredResourceCounterBoard,
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

            if (configuredResourceCounterBoard == null || configuredBuildInfoPanel == null ||
                view.ResourceCounterBoard != configuredResourceCounterBoard ||
                view.BuildInfoPanel != configuredBuildInfoPanel)
            {
                reason = "MobileCity 与 HUD 的资源卡板/建造面板序列化引用不一致。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
