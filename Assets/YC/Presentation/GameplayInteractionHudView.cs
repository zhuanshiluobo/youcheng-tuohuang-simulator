using UnityEngine;
using System.Collections;
using YC.Presentation.Workflows;

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
        [SerializeField] private GameplayExpansionUi expansionUi;

        private bool interactionTraceFramePending;
        private WaitForEndOfFrame interactionTraceFrameEnd;
        internal void ObserveInteractionTraceFrame()
        {
            if (!InteractionLatencyTrace.Enabled || interactionTraceFramePending || !isActiveAndEnabled) return;
            interactionTraceFramePending = true;
            try { StartCoroutine(ObserveInteractionTraceFrameEnd()); }
            catch { interactionTraceFramePending = false; InteractionLatencyTrace.Stop("trace.frame_observation_failed"); }
        }
        private IEnumerator ObserveInteractionTraceFrameEnd()
        {
            if (interactionTraceFrameEnd == null) interactionTraceFrameEnd = new WaitForEndOfFrame();
            yield return interactionTraceFrameEnd;
            interactionTraceFramePending = false;
            // 仅帧尾机会及真实按钮属性，不冒称GPU像素已经呈现。
            InteractionLatencyTrace.Point("ui.frame_end_opportunity", value:
                MobileCityInteractionController.ButtonTraceFlags(frame?.EndActionButton) |
                MobileCityInteractionController.ButtonTraceFlags(actionPanelView?.EndRoundButton) << 4);
        }

        public Canvas Canvas => canvas;
        public GameplayHudFrame Frame => frame;
        public TabletopCanvasLayout TabletopCanvas => tabletopCanvas;
        public ActionPanelView ActionPanelView => actionPanelView;
        public CharacterHandPanel CharacterHandPanel => characterHandPanel;
        public BuildInfoPanel BuildInfoPanel => buildInfoPanel;
        public GameplayDialogRegistry DialogRegistry => dialogRegistry;
        public MobileCityInteractionController CityInteractionController => cityInteractionController;
        public GameplayMainModules MainModules => mainModules;
        public GameplayExpansionUi ExpansionUi => expansionUi;
        public void ConfigureGameplayPresentation(YC.Domain.Presentation.IGameplayTextCatalogSnapshot catalog,
            System.Collections.Generic.IReadOnlyDictionary<string, string> definitionNames)
            => frame.ConfigureGameplayPresentation(catalog, definitionNames);
        public bool TryValidateConfiguration(out string reason)
        {
            if (canvas == null || frame == null || tabletopCanvas == null || actionPanelView == null ||
                characterHandPanel == null || buildInfoPanel == null ||
                dialogRegistry == null || mainModules == null || expansionUi == null)
            {
                reason = "交互 HUD 总 View 引用不完整。";
                return false;
            }

            if (!frame.TryValidateConfiguration(out reason) ||
                !tabletopCanvas.TryValidateConfiguration(out reason) ||
                !actionPanelView.TryValidateConfiguration(out reason) ||
                !characterHandPanel.TryValidateConfiguration(out reason) ||
                !mainModules.TryValidateConfiguration(out reason) || !expansionUi.TryValidateConfiguration(out reason) ||
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
