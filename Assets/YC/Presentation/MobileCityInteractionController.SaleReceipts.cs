using System;
using YC.Domain.State;
using YC.Application.Sessions;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    public sealed partial class MobileCityInteractionController
    {
        private static readonly Unity.Profiling.ProfilerMarker PlayerProjectionMarker = new Unity.Profiling.ProfilerMarker("YC.PlayerProjection");
        private static readonly Unity.Profiling.ProfilerMarker GameplaySynchronizeMarker = new Unity.Profiling.ProfilerMarker("YC.GameplaySynchronize");
        private static readonly Unity.Profiling.ProfilerMarker GameplayUiRefreshMarker = new Unity.Profiling.ProfilerMarker("YC.GameplayUiRefresh");
        private string lastPresentedSaleReceipt = string.Empty;
        private int visibleReadDepth;
        private GameState visibleReadState;
        private GameStateView visibleReadProjection;
        private int visibleReadRevision = -1, visibleReadPlayer = -1;
        private GameStateView uiReadView;
        private GameState uiReadState;
        private GameState uiReadSimulationState;
        private GameSession uiReadSession;
        private GameSession lastSimulationUiSession;
        private GameState lastSimulationUiState;
        private GameStateView lastSimulationUiView;
        private string lastSimulationUiGame;
        private string lastSimulationUiContent;
        private bool lastSimulationUiReady, simulationUiResultRequiresSynchronization;
        private bool lastSimulationUiCanSubmit;
        private ActionPanelViewModel lastActionPanelPermissionModel;
        private int lastSimulationUiRevision = -1, lastSimulationUiViewer = -1;

        private bool RefreshSimulationPermissionOnly()
        {
            if (commandSubmission?.UsesDeterministicSimulation != true) return false;
            var hasResult = simulationUiResultRequiresSynchronization;
            simulationUiResultRequiresSynchronization = false;
            var view = VisibleSaleState();
            if (view == null) { lastSimulationUiView = null; return false; }
            var ready = commandSubmission.IsSimulationReady;
            var unchanged = IsSameSimulationUiContext(view, ready, hasResult);
            var canSubmit = CanSubmitGameplayCommand;
            var permissionChanged = lastSimulationUiCanSubmit != canSubmit;
            lastSimulationUiSession = session;
            lastSimulationUiState = commandSubmission.PublishedState;
            lastSimulationUiView = view;
            lastSimulationUiGame = view.GameId;
            lastSimulationUiContent = view.ContentPackHash;
            lastSimulationUiReady = ready;
            lastSimulationUiRevision = view.StateRevision;
            lastSimulationUiViewer = localPlayerId;
            lastSimulationUiCanSubmit = canSubmit;
            if (!unchanged) return false;
            using var permissionTiming = new UiExecutionTimingScope(session.ExecutionTimings, "ui.permission");
            // 核验前缀或相同许可通知无需重画；保留准确结果和上下文改变的完整同步。
            if (!permissionChanged) return true;
            RefreshCharacterViewerSubmissionAvailability(canSubmit);
            if (NeedsExistingInteractionPermissionRefresh())
            {
                // 这些宿主尚无独立许可入口，保原刷新以免丢失开启/撤回或草稿。
                RefreshAllFromState();
                RefreshCurrentRequestEndButton();
                return true;
            }
            ApplySubmissionPermission(lastActionPanelPermissionModel);
            RefreshCurrentRequestEndButton();
            return true;
        }

        private bool IsSameSimulationUiContext(GameStateView view, bool ready, bool hasResult) =>
            view != null && ReferenceEquals(lastSimulationUiSession, session) &&
            ReferenceEquals(lastSimulationUiState, commandSubmission?.PublishedState) && ReferenceEquals(lastSimulationUiView, view) &&
            lastSimulationUiGame == view.GameId && lastSimulationUiRevision == view.StateRevision &&
            lastSimulationUiContent == view.ContentPackHash && lastSimulationUiReady == ready &&
            lastSimulationUiViewer == localPlayerId && !viewerChanged && !hasResult;

        private bool NeedsExistingInteractionPermissionRefresh() =>
            lastActionPanelPermissionModel == null ||
            interactionRequestRouter?.CurrentRequest != null && !IsCharacterViewerUseRequest(interactionRequestRouter.CurrentRequest) || buildDialogActive ||
            characterCoverChoiceDialog?.IsShowing == true || characterUsePage?.IsShowing == true ||
            characterCardEffectChoiceDialog?.IsShowing == true || specialActionUsePage?.IsShowing == true ||
            eventChoiceDialog?.IsShowing == true || (VisibleSaleState()?.CharacterSettlementOptions?.Count ?? 0) > 0;

        private readonly struct UiExecutionTimingScope : IDisposable
        {
            private readonly ExecutionTimingCounters counters;
            private readonly string phase;
            private readonly long started;
            public UiExecutionTimingScope(ExecutionTimingCounters counters, string phase)
            {
                this.counters = counters?.Enabled == true ? counters : null;
                this.phase = phase;
                started = this.counters != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            }
            public void Dispose()
            {
                if (counters != null) counters.AddSample(phase, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }
        }

        private bool CanSubmitGameplayCommand => UiReadState() != null && gameplayAdapter?.CanSubmitCommands == true;
        private bool HasLocalOperationPermission(GameState state) => state != null &&
            (commandSubmission?.UsesDeterministicSimulation == true
                ? CanSubmitGameplayCommand : state.CurrentPlayerId == localPlayerId);

        // 旧展示端口仍需要 GameState；此副本只来自本人 View，不替换或裁剪模拟会话的 State。
        private GameState UiReadState()
        {
            if (session == null) return null;
            if (ShouldControlCurrentPlayerLocally()) return session.State;
            var view = VisibleSaleState();
            var simulation = commandSubmission?.UsesDeterministicSimulation == true ? commandSubmission.PublishedState : session.State;
            if (view == null || simulation == null) return null;
            if (ReferenceEquals(uiReadView, view) && ReferenceEquals(uiReadSession, session) &&
                ReferenceEquals(uiReadSimulationState, simulation)) return uiReadState;
            var projected = PlayerUiReadStateFactory.Create(simulation, view, localPlayerId);
            uiReadSession = session;
            uiReadView = view;
            uiReadSimulationState = simulation;
            uiReadState = projected;
            return projected;
        }

        private void ApplySubmissionPermission(ActionPanelViewModel model)
        {
            if (model == null || actionPanel == null) return;
            var allowed = CanSubmitGameplayCommand;
            actionPanel.SetButtonStates(allowed && model.CanUseCharacter, model.CanInspectCityStyles,
                allowed && model.CanDeploy, allowed && model.CanDispatch, allowed && model.CanExplore,
                allowed && model.CanMoveCity, allowed && model.CanBuild || model.CanViewFacilitySupply,
                allowed && model.CanEndAction, allowed && model.CanUseSpecialAction);
            var endButton = gameplayInteractionHud?.Frame?.EndActionButton;
            if (!allowed && endButton != null) endButton.interactable = false;
        }

        // 仅在一次同步调用栈内复用；退出事务即失效，不依赖未发布的内部修订合同。
        private VisibleReadScope BeginVisibleRead() => new VisibleReadScope(this);
        private readonly struct VisibleReadScope : IDisposable
        {
            private readonly MobileCityInteractionController owner;
            public VisibleReadScope(MobileCityInteractionController owner)
            {
                this.owner = owner;
                if (owner.visibleReadDepth++ == 0) owner.InvalidateVisibleRead();
            }
            public void Dispose()
            {
                if (--owner.visibleReadDepth == 0) owner.InvalidateVisibleRead();
            }
        }
        private void InvalidateVisibleRead()
        {
            visibleReadProjection = null;
            visibleReadState = null;
            visibleReadRevision = visibleReadPlayer = -1;
        }

        private Func<bool> CreateVisibleSnapshotGuard(GameStateView snapshot)
        {
            var boundSession = session;
            var simulated = commandSubmission?.UsesDeterministicSimulation == true;
            var boundState = simulated ? commandSubmission.PublishedState : session?.State;
            var boundView = simulated ? commandSubmission.PublishedView : session?.View;
            var viewer = localPlayerId;
            var gameId = snapshot?.GameId;
            var revision = snapshot?.StateRevision ?? -1;
            if (!ShouldControlCurrentPlayerLocally())
                return () => snapshot != null && ReferenceEquals(session, boundSession) &&
                    ReferenceEquals(simulated ? commandSubmission?.PublishedState : session?.State, boundState) && viewer == localPlayerId &&
                    snapshot.ViewerRole == GameStateViewerRole.Player && snapshot.ViewerPlayerId == viewer &&
                    VisibleSaleState()?.GameId == gameId && VisibleSaleState()?.StateRevision == revision &&
                    VisibleSaleState()?.ContentPackHash == snapshot.ContentPackHash;
            return () => snapshot != null && ReferenceEquals(session, boundSession) &&
                viewer == localPlayerId && snapshot.ViewerRole == GameStateViewerRole.Player &&
                snapshot.ViewerPlayerId == viewer &&
                (boundView != null
                    ? ReferenceEquals(session?.View, boundView) && ReferenceEquals(snapshot, boundView) &&
                        boundView.GameId == gameId && boundView.StateRevision == revision
                    : session?.View == null && boundState != null && ReferenceEquals(session?.State, boundState) &&
                        boundState.GameId == gameId && (boundState.EffectRuntime?.StateRevision ?? 0) == revision);
        }

        private GameStateView VisibleSaleState()
        {
            if (commandSubmission?.UsesDeterministicSimulation == true) return commandSubmission.PublishedView;
            if (session?.State == null) return null;
            if (session.View != null)
                return session.View.ViewerRole == GameStateViewerRole.Player && session.View.ViewerPlayerId == localPlayerId &&
                    session.View.GameId == session.State.GameId && session.View.ContentPackHash == session.State.ContentPackHash &&
                    session.View.StateRevision == (session.State.EffectRuntime?.StateRevision ?? 0)
                    ? session.View : null;
            if (!ShouldControlCurrentPlayerLocally()) return null;
            var state = session.State;
            if (state == null) return null;
            if (visibleReadDepth == 0)
            {
                using (PlayerProjectionMarker.Auto()) return session.ProjectFor(GameStateViewer.Player(localPlayerId));
            }
            if (visibleReadProjection == null || !ReferenceEquals(visibleReadState, state) ||
                visibleReadRevision != (state.EffectRuntime?.StateRevision ?? 0) || visibleReadPlayer != localPlayerId)
            {
                visibleReadState = state;
                visibleReadRevision = state.EffectRuntime?.StateRevision ?? 0;
                visibleReadPlayer = localPlayerId;
                using (PlayerProjectionMarker.Auto()) visibleReadProjection = session.ProjectFor(GameStateViewer.Player(localPlayerId));
            }
            return visibleReadProjection;
        }

        private void PresentLatestResourceSaleReceipt()
        {
            var visible = VisibleSaleState();
            var receipt = ResourceSaleReceiptProjection.Latest(visible, localPlayerId);
            if (receipt == null) return;
            var key = visible.GameId + "|" + localPlayerId + "|" + receipt.EffectId;
            if (key == lastPresentedSaleReceipt) return;
            lastPresentedSaleReceipt = key;
            if (SaleReceiptNotification.ShouldSuppress(visible, localPlayerId, receipt)) return;
            SetPrompt(string.Format(gameplayInteractionHud.DialogRegistry.EffectDialogLayoutProfile.SaleReceiptSummaryFormat,
                receipt.Revenue));
        }

        private static class SaleReceiptNotification
        {
            internal static bool ShouldSuppress(GameStateView visible, int playerId, ResourceSaleReceiptProjection.Receipt receipt)
            {
                if (receipt == null || receipt.Finished == true || visible == null ||
                    visible.ViewerRole != GameStateViewerRole.Player || visible.ViewerPlayerId != playerId) return true;
                if (visible.Interactions == null) return false;
                foreach (var request in visible.Interactions)
                    if (request != null && request.Status == "open" && request.AnsweringPlayerId == playerId &&
                        request.Kind == "lua.choice" && request.PromptKey == "effect.resource.sell" &&
                        request.AnswerSchema == "candidate_id_array" && request.Presentation?.Prompt != null) return true;
                return false;
            }
        }
    }

    // 纯数据构造与 Unity 控件生命周期分开；实际控制器和隐私检查使用同一个入口。
    internal static class PlayerUiReadStateFactory
    {
        internal static GameState Create(GameState simulation, GameStateView view, int viewer)
        {
            if (simulation == null || view == null || view.ViewerRole != GameStateViewerRole.Player ||
                view.ViewerPlayerId != viewer || view.GameId != simulation.GameId ||
                view.ContentPackHash != simulation.ContentPackHash ||
                view.StateRevision != (simulation.EffectRuntime?.StateRevision ?? 0)) return null;
            var projected = GameStateViewProjector.ToClientState(view);
            projected.EffectRuntime = new EffectRuntimeState
            {
                StateRevision = view.StateRevision,
                ActiveMainNodeId = view.ActiveMainNodeId,
                Status = view.IsPausedFault ? EffectRuntimeStatus.PausedFault : EffectRuntimeStatus.Active
            };
            projected.EffectRuntime.InteractionRequests.AddRange(
                VisibleInteractionRequestSource.Read(null, view, viewer));
            projected.ContentDefinitions = GameStateCloneService.DeepClone(simulation.ContentDefinitions);
            return projected;
        }
    }
}
