using UnityEngine;

namespace YC.Presentation
{
    public sealed class GameplayInteractionHudView : MonoBehaviour
    {
        [SerializeField] private Canvas canvas;
        [SerializeField] private GameplayHudFrame frame;
        [SerializeField] private TabletopCanvasLayout tabletopCanvas;
        [SerializeField] private ActionPanelView actionPanelView;
        [SerializeField] private CharacterHandPanel characterHandPanel;
        [SerializeField] private BuildInfoPanel buildInfoPanel;
        [SerializeField] private GameplayDialogRegistry dialogRegistry;
        [SerializeField] private MobileCityInteractionController cityInteractionController;
        [SerializeField] private GameplayMainModules mainModules;

        public Canvas Canvas => canvas;
        public GameplayHudFrame Frame => frame;
        public TabletopCanvasLayout TabletopCanvas => tabletopCanvas;
        public ActionPanelView ActionPanelView => actionPanelView;
        public CharacterHandPanel CharacterHandPanel => characterHandPanel;
        public BuildInfoPanel BuildInfoPanel => buildInfoPanel;
        public GameplayDialogRegistry DialogRegistry => dialogRegistry;
        public MobileCityInteractionController CityInteractionController => cityInteractionController;
        public GameplayMainModules MainModules => mainModules;
        public bool TryValidateConfiguration(out string reason)
        {
            if (canvas == null || frame == null || tabletopCanvas == null || actionPanelView == null ||
                characterHandPanel == null || buildInfoPanel == null ||
                dialogRegistry == null || mainModules == null)
            {
                reason = "交互 HUD 总 View 引用不完整。";
                return false;
            }

            if (!frame.TryValidateConfiguration(out reason) ||
                !tabletopCanvas.TryValidateConfiguration(out reason) ||
                !actionPanelView.TryValidateConfiguration(out reason) ||
                !characterHandPanel.TryValidateConfiguration(out reason) ||
                !mainModules.TryValidateConfiguration(out reason) ||
                (GetComponent<GameplayInformationPages>() != null &&
                 !GetComponent<GameplayInformationPages>().TryValidateConfiguration(out reason)) ||
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

            if (configuredBuildInfoPanel == null ||
                view.BuildInfoPanel != configuredBuildInfoPanel)
            {
                reason = "MobileCity 与 HUD 的建造面板序列化引用不一致。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
