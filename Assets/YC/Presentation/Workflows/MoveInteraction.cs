using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public sealed class MoveInteraction : InteractionBase, IMapConfirmationScope, IMapTargetPreparation
    {
        private enum MoveStage
        {
            Inactive,
            SelectingTarget
        }

        private readonly IWritableGameplayContext context;
        private readonly CommandGateway commandGateway;
        private readonly ITurnActionView view;
        private readonly InteractionFlowCoordinator flowCoordinator;
        private readonly IMapQueryService mapQuery;
        private readonly Action activateWorkflowOwner;
        private readonly Func<bool> canStartMainAction;
        private readonly Action<string> completeAction;
        private readonly ActionPaymentPreparation paymentPreparation;
        private MoveStage stage;
        private int selectionVersion;
        private string inFlightCommandId = string.Empty;
        private GameState draftState;
        private string draftGameId;
        private string draftCityLocation, draftContentHash;
        private int draftPlayerId, draftRound, draftActionRound, draftRevision;
        private GamePhase draftPhase;
        private int selectionRevision;
        private string confirmedTarget, preselectedTarget;
        private PreparedActionPaymentView observedPreparation;
        public Func<int> ObservedStateRevision { private get; set; }
        public Func<GameStateView> GetVisibleState { set => paymentPreparation.GetVisibleState = value; }
        public bool HasPreparedPayment => paymentPreparation.HasPreparation;
        public bool CanResumePreparedDraft => IsActive && IsDraftScopeCurrent() &&
            (paymentPreparation.IsInFlight || ActionPaymentPreparation.SameIdentity(observedPreparation, paymentPreparation.Current));
        public bool CanCancelPreparedPayment => paymentPreparation.CanCancel;
        public bool TryCancelPreparedPayment(Action afterApplied) => paymentPreparation.Cancel(() => { CancelLocal(); afterApplied?.Invoke(); });

        public bool IsSubmissionInFlight => !string.IsNullOrEmpty(inFlightCommandId) || paymentPreparation.IsInFlight;
        private bool HasBlockingChoice => ActionPaymentPreparation.HasBlockingChoice(context.CurrentState, context.LocalPlayerId);

        public string MapConfirmationScope
        {
            get
            {
                var state = context.CurrentState;
                var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
                if (!IsActive || !IsDraftScopeCurrent() || !string.IsNullOrEmpty(inFlightCommandId) || player == null || HasBlockingChoice)
                    return string.Empty;
                return BuildConfirmationScope();
            }
        }
        private string BuildConfirmationScope()
        {
            var state = context.CurrentState;
            var player = state.FindPlayer(context.LocalPlayerId);
            return Id + ":" + selectionVersion + ":" + context.LocalPlayerId + ":" +
                    state.GameId + ":" + state.ContentPackHash + ":" +
                    state.Round + ":" + state.Phase + ":" + state.ActionRound + ":" +
                    player.CityLocationId + ":" + selectionRevision;
        }
        public bool RetainsMapConfirmation(string scope, string target) => IsActive && IsDraftScopeCurrent() &&
            string.IsNullOrEmpty(inFlightCommandId) && !string.IsNullOrEmpty(preselectedTarget) &&
            target == WorkflowHighlightTargetKind.Location + ":" + preselectedTarget && scope == BuildConfirmationScope() &&
            (paymentPreparation.IsInFlight || ActionPaymentPreparation.SameIdentity(observedPreparation, paymentPreparation.Current)) &&
            IsLegalPreparedTarget(preselectedTarget);
        private bool IsLegalPreparedTarget(string targetId)
        {
            var player = context.CurrentState?.FindPlayer(context.LocalPlayerId);
            if (player == null || string.IsNullOrEmpty(player.CityLocationId)) return false;
            foreach (var location in mapQuery.GetAdjacentLocations(player.CityLocationId))
                if (location.LocationId == targetId) return !IsOccupiedByAnotherCity(targetId) &&
                    !RedZoneAccessRule.IsClosed(context.CurrentState, mapQuery.Map, location);
            return false;
        }

        public MoveInteraction(
            IWritableGameplayContext context,
            IGameCommandPort commandPort,
            ITurnActionView view,
            InteractionFlowCoordinator flowCoordinator,
            IMapQueryService mapQuery,
            Action activateWorkflowOwner,
            Func<bool> canStartMainAction,
            Action<string> completeAction)
            : this(
                context,
                new CommandGateway(
                    commandPort ?? throw new ArgumentNullException(nameof(commandPort))),
                view,
                flowCoordinator,
                mapQuery,
                activateWorkflowOwner,
                canStartMainAction,
                completeAction)
        {
        }

        internal MoveInteraction(
            IWritableGameplayContext context,
            CommandGateway commandGateway,
            ITurnActionView view,
            InteractionFlowCoordinator flowCoordinator,
            IMapQueryService mapQuery,
            Action activateWorkflowOwner,
            Func<bool> canStartMainAction,
            Action<string> completeAction)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.commandGateway = commandGateway ??
                                  throw new ArgumentNullException(nameof(commandGateway));
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.flowCoordinator = flowCoordinator ??
                                   throw new ArgumentNullException(nameof(flowCoordinator));
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.activateWorkflowOwner = activateWorkflowOwner ??
                                         throw new ArgumentNullException(
                                             nameof(activateWorkflowOwner));
            this.canStartMainAction = canStartMainAction ??
                                      throw new ArgumentNullException(nameof(canStartMainAction));
            this.completeAction = completeAction ??
                                  throw new ArgumentNullException(nameof(completeAction));
            paymentPreparation = new ActionPaymentPreparation(context, commandGateway, "city_move", OnPaymentPreparationChanged, view.ShowPrompt);
        }

        public override string Id
        {
            get { return "move-city"; }
        }

        public override InteractionPriority Priority
        {
            get { return InteractionPriority.ActiveAction; }
        }

        public override bool IsActive
        {
            get { return stage == MoveStage.SelectingTarget; }
        }

        public InteractionMode Mode
        {
            get
            {
                return IsActive
                    ? InteractionMode.Busy
                    : InteractionMode.ChooseAction;
            }
        }

        public bool IsSelectingMoveTarget
        {
            get { return IsActive; }
        }

        public bool IsAwaitingInitialPlacement
        {
            get
            {
                var state = context.CurrentState;
                if (state == null || state.Phase != GamePhase.Entrance)
                {
                    return false;
                }

                var player = state.FindPlayer(context.LocalPlayerId);
                return player == null || string.IsNullOrEmpty(player.CityLocationId);
            }
        }

        public override InteractionResult OnLocationClicked(string locationId)
        {
            if (!IsActive) return InteractionResult.Passthrough;
            // 统一路由已完成首击预选和二次合法性校验，这里只处理确认后的业务命令。
            if (paymentPreparation.IsInFlight)
            {
                confirmedTarget = locationId;
                return InteractionResult.Consumed;
            }
            Move(locationId);
            return InteractionResult.Consumed;
        }

        public void OnMapTargetPreselected(WorkflowHighlightTargetKind kind, string targetId)
        {
            if (kind != WorkflowHighlightTargetKind.Location || !IsActive || !IsDraftScopeCurrent() ||
                !string.IsNullOrEmpty(inFlightCommandId) || !HasMoveTarget(targetId)) return;
            confirmedTarget = null;
            preselectedTarget = targetId;
            if (!paymentPreparation.IsInFlight && paymentPreparation.Current?.TargetId != targetId)
                PrepareTarget(targetId);
        }

        private void PrepareTarget(string targetId)
        {
            var current = paymentPreparation.Current;
            if (current != null && !string.IsNullOrEmpty(current.TargetId) && current.TargetId != targetId)
                paymentPreparation.Cancel(() => paymentPreparation.Prepare(targetId));
            else paymentPreparation.Prepare(targetId);
        }

        private void OnPaymentPreparationChanged()
        {
            if (!IsActive || !IsDraftScopeCurrent()) return;
            if (!paymentPreparation.LastResolvedApplied)
            {
                confirmedTarget = preselectedTarget = null;
                selectionVersion++;
                RestorePresentation();
                return;
            }
            draftRevision = CurrentRevision();
            observedPreparation = paymentPreparation.Current;
            if (paymentPreparation.LastResolvedOperation == "cancel_action") return;
            view.RefreshFromState();
            if (!string.IsNullOrEmpty(preselectedTarget) && paymentPreparation.Current != null &&
                paymentPreparation.Current.TargetId != preselectedTarget)
            {
                PrepareTarget(preselectedTarget);
                return;
            }
            var target = confirmedTarget; confirmedTarget = null;
            if (!string.IsNullOrEmpty(target) && paymentPreparation.Current?.CanConfirm == true &&
                paymentPreparation.Current.TargetId == target && HasMoveTarget(target)) Move(target);
            else RestorePresentation();
        }

        public override InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionResult OnMobileCityClicked()
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionResult OnEscape()
        {
            return IsSubmissionInFlight ? InteractionResult.Consumed : InteractionResult.Passthrough;
        }

        public override InteractionPresentation BuildPresentation()
        {
            if (!IsActive)
            {
                return InteractionPresentation.Empty;
            }

            return new InteractionPresentation(
                BuildMoveTargetHighlights(),
                IsSubmissionInFlight
                    ? CommandGateway.BuildWaitingForHostPrompt("\u79fb\u52a8\u547d\u4ee4")
                    : "城市移动：预计花费" + new TravelCostService(mapQuery).GetCityMoveBaseCost().OriginiumShard + "源石碎片",
                InteractionMode.Busy);
        }

        public void Activate()
        {
            if (IsSubmissionInFlight) return;
            InitializeDraft();
            PresentMoveTargets();
            paymentPreparation.Prepare(string.Empty);
        }

        public bool TryRestorePreparedDraft()
        {
            var state = context.CurrentState;
            if (IsActive || IsSubmissionInFlight || state == null || state.CurrentPlayerId != context.LocalPlayerId ||
                (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2) ||
                string.IsNullOrEmpty(state.FindPlayer(context.LocalPlayerId)?.CityLocationId)) return false;
            var prepared = paymentPreparation.Current;
            if (prepared == null) return false;
            InitializeDraft();
            // 付款身份来自已恢复的本人投影；重建界面草稿不重新准备、取消或确认付款。
            observedPreparation = prepared;
            preselectedTarget = prepared.TargetId;
            return true;
        }

        private void InitializeDraft()
        {
            draftState = context.CurrentState; draftGameId = draftState?.GameId;
            draftCityLocation = draftState?.FindPlayer(context.LocalPlayerId)?.CityLocationId;
            draftContentHash = draftState?.ContentPackHash;
            draftPlayerId = context.LocalPlayerId; draftRound = draftState?.Round ?? 0;
            draftActionRound = draftState?.ActionRound ?? 0; draftPhase = draftState == null ? default : draftState.Phase;
            draftRevision = CurrentRevision();
            selectionRevision = draftRevision; confirmedTarget = preselectedTarget = null; observedPreparation = null;
            selectionVersion++;
            stage = MoveStage.SelectingTarget;
        }

        public override void Cancel()
        {
            if (IsSubmissionInFlight) return;
            if (paymentPreparation.HasPreparation) { paymentPreparation.Cancel(CancelLocal); return; }
            CancelLocal();
        }

        public void BeginShutdown()
        {
            paymentPreparation.BeginShutdown();
            inFlightCommandId = string.Empty;
            CancelLocal();
        }

        private void CancelLocal()
        {
            selectionVersion++;
            confirmedTarget = preselectedTarget = null;
            observedPreparation = null;
            draftState = null; draftGameId = null;
            stage = MoveStage.Inactive;
            view.ClearHighlights();
        }

        public void Begin()
        {
            if (IsSubmissionInFlight) return;
            if (IsActive)
            {
                if (paymentPreparation.HasPreparation) paymentPreparation.Cancel(() =>
                    { CancelLocal(); flowCoordinator.ResetToChooseAction(); view.RefreshActionPanel(); });
                else { CancelLocal(); flowCoordinator.ResetToChooseAction(); view.RefreshActionPanel(); }
                return;
            }

            if (!canStartMainAction())
            {
                return;
            }

            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            if (player == null || string.IsNullOrEmpty(player.CityLocationId))
            {
                view.ShowPrompt("\u73a9\u5bb6\u57ce\u5e02\u4e0d\u5728\u573a\u4e0a\u3002");
                return;
            }

            activateWorkflowOwner();
            view.RefreshActionPanel();
            view.ShowPrompt(
                "\u57ce\u5e02\u79fb\u52a8\uff1a\u9009\u62e9\u4e00\u4e2a\u9ad8\u4eae\u8d44\u6e90\u70b9\u3002");
        }

        public void Move(string locationId)
        {
            if (IsSubmissionInFlight || !IsActive || !IsDraftCurrent() || HasBlockingChoice ||
                !(paymentPreparation.HasPreparation ? paymentPreparation.Current?.CanConfirm == true : canStartMainAction()) ||
                !HasMoveTarget(locationId)) return;
            var command = new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = draftPlayerId,
                TargetId = CityMoveCandidateQueryService.CandidatePrefix + (locationId ?? string.Empty)
            };
            if (paymentPreparation.Current?.TargetId != locationId || !paymentPreparation.BindFinalCommand(command)) return;
            inFlightCommandId = command.CommandId;
            view.ClearHighlights();
            try { commandGateway.Submit(
                command,
                new SubmitCallbacks(
                    view.ShowPrompt,
                    CommandGateway.BuildWaitingForHostPrompt("\u79fb\u52a8\u547d\u4ee4"))
                {
                    BeforeRejectedPrompt = _ => NotifyCommandSettled(command.CommandId),
                    OnAppliedLocally = result => CompleteAppliedMove(command.CommandId)
                }); }
            catch { NotifyCommandSettled(command.CommandId); throw; }
        }

        public override void NotifyCommandSettled(string commandId)
        {
            if (!IsSubmissionInFlight || string.IsNullOrEmpty(commandId) || commandId != inFlightCommandId)
                return;
            inFlightCommandId = string.Empty;
            selectionVersion++;
            if (!IsDraftCurrent()) { Cancel(); return; }
            RestorePresentation();
        }
        public override void NotifyInteractionCommandResolved(string gameId, string commandId, bool accepted, CommandErrorCode errorCode)
        {
            if (paymentPreparation.NotifyResolved(gameId, commandId, accepted)) return;
            if (!IsSubmissionInFlight || commandId != inFlightCommandId || gameId != draftGameId ||
                context.CurrentState?.GameId != draftGameId || context.LocalPlayerId != draftPlayerId) return;
            if (!accepted) { NotifyCommandSettled(commandId); return; }
            CompleteAppliedMove(commandId);
        }
        private void CompleteAppliedMove(string commandId)
        {
            if (inFlightCommandId != commandId || string.IsNullOrEmpty(commandId)) return;
            // 准确结果可能在 Submit 返回前同步到达；结果通知与返回回调共用一次收尾。
            inFlightCommandId = string.Empty;
            paymentPreparation.ResetAfterCommit(); CancelLocal();
            if (context.CurrentState.HasPendingChoice())
            {
                flowCoordinator.SetMode(InteractionMode.Busy);
                view.RefreshFromState();
                view.ShowPendingChoice();
                return;
            }
            completeAction("\u57ce\u5e02\u79fb\u52a8");
        }
        private int CurrentRevision() => ObservedStateRevision?.Invoke() ?? context.CurrentState?.EffectRuntime?.StateRevision ?? 0;
        private bool IsDraftCurrent()
        {
            // 保存替代计划与必要响应只改变同笔准备的投影；不重签请求或把返回视为付款意图。
            if (IsDraftScopeCurrent() && ActionPaymentPreparation.SameIdentity(observedPreparation, paymentPreparation.Current))
                draftRevision = CurrentRevision();
            return IsDraftScopeCurrent() && CurrentRevision() == draftRevision;
        }
        private bool IsDraftScopeCurrent()
        {
            var current = context.CurrentState;
            return draftState != null && current != null && current.GameId == draftGameId && current.ContentPackHash == draftContentHash &&
                current.FindPlayer(draftPlayerId)?.CityLocationId == draftCityLocation &&
                context.LocalPlayerId == draftPlayerId && current.CurrentPlayerId == draftPlayerId && current.Round == draftRound &&
                current.ActionRound == draftActionRound && current.Phase == draftPhase;
        }
        private bool HasMoveTarget(string locationId)
        {
            foreach (var highlight in BuildMoveTargetHighlights()) if (highlight.TargetId == locationId) return true;
            return false;
        }

        public void RestorePresentation()
        {
            if (IsActive)
            {
                PresentMoveTargets();
            }
        }

        public bool IsLocalPlayersTurn()
        {
            var state = context.CurrentState;
            return state == null || state.CurrentPlayerId == context.LocalPlayerId;
        }

        public void PlaceInitialCity(string locationId)
        {
            commandGateway.Submit(
                new GameCommand
                {
                    Kind = GameCommandKind.ChooseInitialLocation,
                    PlayerId = context.LocalPlayerId,
                    TargetId = locationId ?? string.Empty
                },
                new SubmitCallbacks(
                    view.ShowPrompt,
                    CommandGateway.BuildWaitingForHostPrompt("\u5165\u573a\u547d\u4ee4"))
                {
                    OnAppliedLocally = result => view.RefreshFromState()
                });
        }

        public IReadOnlyList<WorkflowHighlight> BuildInitialPlacementHighlights()
        {
            var result = new List<WorkflowHighlight>();
            if (!IsAwaitingInitialPlacement || !IsLocalPlayersTurn())
            {
                return result;
            }

            var locations = mapQuery.Map.Locations;
            for (var i = 0; i < locations.Count; i++)
            {
                var locationId = locations[i].LocationId;
                if (CanUseInitialPlacementLocation(locationId))
                {
                    result.Add(new WorkflowHighlight(
                        WorkflowHighlightTargetKind.Location,
                        locationId,
                        WorkflowHighlightSemantic.InitialPlacement));
                }
            }

            return result;
        }

        private void PresentMoveTargets()
        {
            view.SetHighlights(BuildMoveTargetHighlights());
        }

        private IReadOnlyList<WorkflowHighlight> BuildMoveTargetHighlights()
        {
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            var highlights = new List<WorkflowHighlight>();
            if (!(IsDraftCurrent() || paymentPreparation.IsInFlight && IsDraftScopeCurrent()) || player == null || string.IsNullOrEmpty(player.CityLocationId) ||
                !string.IsNullOrEmpty(inFlightCommandId) || HasBlockingChoice || !IsLocalPlayersTurn() ||
                (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2))
            {
                return highlights;
            }

            var adjacent = mapQuery.GetAdjacentLocations(player.CityLocationId);
            for (var i = 0; i < adjacent.Count; i++)
            {
                var target = adjacent[i];
                if (IsOccupiedByAnotherCity(target.LocationId) ||
                    RedZoneAccessRule.IsClosed(state, mapQuery.Map, target))
                {
                    continue;
                }

                highlights.Add(new WorkflowHighlight(
                    WorkflowHighlightTargetKind.Location,
                    target.LocationId,
                    WorkflowHighlightSemantic.MoveTarget));
            }

            return highlights;
        }

        private bool CanUseInitialPlacementLocation(string locationId)
        {
            MapLocationDefinition location;
            try
            {
                location = mapQuery.GetLocation(locationId);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (!location.CanDockCity ||
                (mapQuery.Map.MapId == StaticMapDefinitions.ThreePlayerMapId &&
                 location.EventColor != EventColor.Green) ||
                (mapQuery.Map.MapId == StaticMapDefinitions.FourPlayerMapId &&
                 !StaticMapDefinitions.FourPlayerInitialLocationIds.Contains(locationId)))
            {
                return false;
            }

            return !IsOccupiedByAnotherCity(locationId);
        }

        private bool IsOccupiedByAnotherCity(string locationId)
        {
            var state = context.CurrentState;
            if (state == null)
            {
                return false;
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (player.PlayerId != context.LocalPlayerId &&
                    player.CityLocationId == locationId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
