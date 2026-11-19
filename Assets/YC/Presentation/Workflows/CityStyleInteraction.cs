using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Items;
using YC.Application.Gameplay;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public sealed class CityStyleInteraction
    {
        private sealed class Receipt { public string CommandId; public CityStyleSubmissionStatus Status; }
        private Receipt currentReceipt;
        public void ResolveSubmission(string commandId, bool applied)
        {
            if (currentReceipt == null || currentReceipt.Status != CityStyleSubmissionStatus.Pending) return;
            // A reconnect invalidates an unresolved draft; an old command cannot complete a new one.
            if (string.IsNullOrEmpty(commandId)) { currentReceipt.Status = CityStyleSubmissionStatus.Rejected; return; }
            if (currentReceipt.CommandId == commandId)
                currentReceipt.Status = applied ? CityStyleSubmissionStatus.Applied : CityStyleSubmissionStatus.Rejected;
        }
        private readonly IWritableGameplayContext context;
        private readonly CommandGateway commandGateway;
        private readonly ITurnActionView view;
        private readonly InteractionFlowCoordinator flowCoordinator;
        private readonly Func<bool> canStartQuickAction;
        private readonly Func<bool> canStartMainAction;
        private readonly Func<string> getUnavailableReason;
        private readonly Action<string> completeAction;
        private readonly Action restoreBuildInteraction;
        private readonly CityStyleSelectionController selection =
            new CityStyleSelectionController();
        private readonly SpecialActionOptionQueryService specialActionOptionQuery;
        public Func<GameStateView> GetVisibleState { private get; set; }
        public Func<int> GetDisplayedPlayerId { private get; set; }
        public Func<int> GetPresentationGeneration { private get; set; }

        public CityStyleInteraction(
            IWritableGameplayContext context,
            IGameCommandPort commandPort,
            ITurnActionView view,
            InteractionFlowCoordinator flowCoordinator,
            IMapQueryService mapQuery,
            Func<bool> canStartQuickAction,
            Func<bool> canStartMainAction,
            Func<string> getUnavailableReason,
            Action<string> completeAction,
            Action restoreBuildInteraction)
            : this(
                context,
                new CommandGateway(
                    commandPort ?? throw new ArgumentNullException(nameof(commandPort))),
                view,
                flowCoordinator,
                mapQuery,
                canStartQuickAction,
                canStartMainAction,
                getUnavailableReason,
                completeAction,
                restoreBuildInteraction)
        {
        }

        internal CityStyleInteraction(
            IWritableGameplayContext context,
            CommandGateway commandGateway,
            ITurnActionView view,
            InteractionFlowCoordinator flowCoordinator,
            IMapQueryService mapQuery,
            Func<bool> canStartQuickAction,
            Func<bool> canStartMainAction,
            Func<string> getUnavailableReason,
            Action<string> completeAction,
            Action restoreBuildInteraction)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.commandGateway = commandGateway ??
                                  throw new ArgumentNullException(nameof(commandGateway));
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.flowCoordinator = flowCoordinator ??
                                   throw new ArgumentNullException(nameof(flowCoordinator));
            this.canStartQuickAction = canStartQuickAction ??
                                       throw new ArgumentNullException(nameof(canStartQuickAction));
            this.canStartMainAction = canStartMainAction ??
                                      throw new ArgumentNullException(nameof(canStartMainAction));
            this.getUnavailableReason = getUnavailableReason ??
                                        throw new ArgumentNullException(nameof(getUnavailableReason));
            this.completeAction = completeAction ??
                                  throw new ArgumentNullException(nameof(completeAction));
            this.restoreBuildInteraction = restoreBuildInteraction ??
                                           throw new ArgumentNullException(
                                               nameof(restoreBuildInteraction));

            var influenceService = new InfluenceService(
                mapQuery ?? throw new ArgumentNullException(nameof(mapQuery)));
            specialActionOptionQuery = new SpecialActionOptionQueryService(
                mapQuery,
                influenceService,
                new CityMovementService(
                    mapQuery,
                    influenceService,
                    new TravelCostService(mapQuery)),
                new SpecialActionLifecycleService());
        }

        public void BeginDeclare(string initialCityStyleId = "")
        {
            if (!canStartQuickAction())
            {
                return;
            }

            flowCoordinator.ResetToChooseAction();
            view.ClearHighlights();
            view.RefreshActionPanel();
            ShowPreview(initialCityStyleId, true);
        }

        public void OpenPreview(string initialCityStyleId)
        {
            if (context.CurrentState?.FindPlayer(context.LocalPlayerId) == null) return;
            ShowPreview(initialCityStyleId, false);
        }

        public void OpenReadOnlyPreview(string initialCityStyleId)
        {
            if (context.CurrentState?.FindPlayer(context.LocalPlayerId) == null) return;
            ShowPreview(initialCityStyleId, false, true);
        }

        public void SubmitDeclare(
            string cityStyleId,
            IReadOnlyList<int> selectedSlotIndexes)
        {
            TrySubmitDeclare(cityStyleId, selectedSlotIndexes);
        }

        private void ShowPreview(string initialCityStyleId, bool declare, bool readOnly = false)
        {
            var receipt = new Receipt();
            var playerId = context.LocalPlayerId;
            var gameId = context.CurrentState.GameId;
            var round = context.CurrentState.Round;
            var actionRound = context.CurrentState.ActionRound;
            var key = gameId + ":" + playerId + ":" + round + ":" + actionRound;
            CityStyleOptionsViewModel cached = null;
            GameState cachedState = null;
            object cachedDefinitions = null;
            string cachedContent = null, cachedUnavailable = null;
            int cachedRevision = -1, cachedDisplayedPlayer = -1, cachedPresentation = -1;
            InteractionMode cachedMode = InteractionMode.Hidden;
            Func<CityStyleOptionsViewModel> refresh = null;
            refresh = () =>
            {
                using var measurement = UiRegressionMetrics.Measure("declare.model");
                var state = context.CurrentState;
                if (state == null || state.GameId != gameId || context.LocalPlayerId != playerId ||
                    (declare && (state.Round != round || state.ActionRound != actionRound))) return null;
                var unavailable = getUnavailableReason();
                var revision = state.EffectRuntime?.StateRevision ?? 0;
                var displayedPlayer = GetDisplayedPlayerId?.Invoke() ?? playerId;
                var presentation = GetPresentationGeneration?.Invoke() ?? 0;
                if (cached != null && ReferenceEquals(cachedState, state) && cachedRevision == revision &&
                    ReferenceEquals(cachedDefinitions, state.ContentDefinitions) && cachedContent == state.ContentPackHash &&
                    cachedUnavailable == unavailable && cachedMode == flowCoordinator.CurrentMode &&
                    cachedDisplayedPlayer == displayedPlayer && cachedPresentation == presentation)
                {
                    UiRegressionMetrics.Note("declare.cache_hit", key);
                    return cached;
                }
                List<CityStyleOptionViewModel> options;
                using (UiRegressionMetrics.Measure("declare.options")) options = selection.BuildOptions(state, playerId);
                if (!string.IsNullOrEmpty(unavailable))
                    foreach (var option in options) { option.CanDeclare = false; option.Reason = unavailable; }
                cachedState = state; cachedRevision = revision; cachedDefinitions = state.ContentDefinitions;
                cachedContent = state.ContentPackHash; cachedUnavailable = unavailable; cachedMode = flowCoordinator.CurrentMode;
                cachedDisplayedPlayer = displayedPlayer; cachedPresentation = presentation;
                return cached = new CityStyleOptionsViewModel(options.AsReadOnly(), BuildCityBoardSlots(state, playerId),
                    BuildCityStyleMarkers(state, playerId, unavailable), initialCityStyleId,
                    (id, slots) => selection.ValidateSelection(context.CurrentState, playerId, id, slots),
                    declare ? (Func<string, IReadOnlyList<int>, bool>)((id, slots) =>
                        context.LocalPlayerId == playerId && context.CurrentState.GameId == gameId && TrySubmitDeclare(id, slots, receipt)) : null,
                    null, declare ? restoreBuildInteraction : (Action)null,
                    !readOnly && string.IsNullOrEmpty(unavailable) ? (Func<string, string, int, int, bool>)((id, marker, originium, iron) =>
                        context.LocalPlayerId == playerId && context.CurrentState.GameId == gameId &&
                        TrySubmitSpecialAction(id, marker, originium, iron, receipt)) : null,
                    declare, refresh, key, state.EffectRuntime == null ? 0 : state.EffectRuntime.StateRevision, () => receipt.Status,
                    state.Players.Find(p => p.PlayerId == playerId)?.Name ?? string.Empty,
                    state.Players.Find(p => p.PlayerId == playerId)?.InfluenceSupply ?? 0);
            };
            view.ShowCityStyleOptions(refresh());
            view.ShowPrompt(string.Empty);
        }

        private bool TrySubmitDeclare(
            string cityStyleId,
            IReadOnlyList<int> selectedSlotIndexes, Receipt receipt = null)
        {
            if (currentReceipt != null && currentReceipt.Status == CityStyleSubmissionStatus.Pending)
            {
                view.ShowPrompt(CommandGateway.BuildWaitingForHostPrompt("城市样式命令"));
                return false;
            }
            if (selectedSlotIndexes != null && !canStartQuickAction())
            {
                return false;
            }

            var command = selectedSlotIndexes == null
                ? selection.CreateCommand(context.LocalPlayerId, cityStyleId)
                : selection.CreateCommand(
                    context.LocalPlayerId,
                    cityStyleId,
                    selectedSlotIndexes);
            if (receipt != null) { currentReceipt = receipt; receipt.CommandId = command.CommandId; receipt.Status = CityStyleSubmissionStatus.Pending; }
            var outcome = commandGateway.Submit(
                command,
                new SubmitCallbacks(
                    view.ShowPrompt,
                    CommandGateway.BuildWaitingForHostPrompt("宣告城市样式命令"))
                {
                    OnAppliedLocally = result =>
                    {
                        view.RefreshInformation();
                        view.RefreshActionPanel();
                        view.ShowPrompt("城市样式宣告完成。");
                    }
                });
            if (receipt != null) receipt.Status = outcome.Kind == SubmitOutcomeKind.WaitingForHost
                ? CityStyleSubmissionStatus.Pending : outcome.Kind == SubmitOutcomeKind.AppliedLocally
                    ? CityStyleSubmissionStatus.Applied : CityStyleSubmissionStatus.Rejected;
            return outcome.Kind == SubmitOutcomeKind.WaitingForHost ||
                   outcome.Kind == SubmitOutcomeKind.AppliedLocally;
        }

        private bool TrySubmitSpecialAction(
            string specialActionId,
            string declarationMarkerId,
            int originiumAmount,
            int ironAmount, Receipt receipt = null)
        {
            var declared = QuerySpecialActions().Find(specialActionId, declarationMarkerId)?.DeclaredAction;
            if (declared != null)
            {
                if (!declared.Enabled || !canStartMainAction()) return false;
                var unified = YC.Application.Interactions.AbilityActionCommand.Create(context.LocalPlayerId, declared);
                var submitted = commandGateway.Submit(unified, new SubmitCallbacks(view.ShowPrompt, "行动已发送，等待主机确认。") {
                    // 统一行动可在正文中产生任意已有请求；本地准确受理后走原通用同步。
                    // 不提前完成主要行动，不从Host候选另建UI答案。
                    OnAppliedLocally = _ => view.RefreshFromState()
                });
                return submitted.Kind == SubmitOutcomeKind.AppliedLocally || submitted.Kind == SubmitOutcomeKind.WaitingForHost;
            }
            if (currentReceipt != null && currentReceipt.Status == CityStyleSubmissionStatus.Pending)
            {
                view.ShowPrompt(CommandGateway.BuildWaitingForHostPrompt("城市样式命令"));
                return false;
            }
            if (!canStartMainAction())
            {
                return false;
            }

            if (string.IsNullOrEmpty(specialActionId) ||
                string.IsNullOrEmpty(declarationMarkerId))
            {
                view.ShowPrompt("所选样式标记没有可发动的特殊行动。");
                return false;
            }

            if (specialActionId == SpecialActionDatabase.CompositePowerSystem &&
                (originiumAmount != -1 || ironAmount != -1) &&
                (originiumAmount < 0 || ironAmount < 0 || originiumAmount + ironAmount != 3))
            {
                view.ShowPrompt("复合动力系统必须选择合计 3 份源岩或异铁作为支付。");
                return false;
            }

            var command = new GameCommand
            {
                Kind = GameCommandKind.UseSpecialAction,
                PlayerId = context.LocalPlayerId,
                SourceId = declarationMarkerId,
                TargetId = specialActionId
            };
            command.Parameters[UseSpecialActionCommandHandler.SpecialActionIdParameter] =
                specialActionId;
            command.Parameters[UseSpecialActionCommandHandler.DeclarationMarkerIdParameter] =
                declarationMarkerId;
            if (specialActionId == SpecialActionDatabase.CompositePowerSystem && originiumAmount >= 0 && ironAmount >= 0)
            {
                command.Parameters[UseSpecialActionCommandHandler.OriginiumAmountParameter] =
                    originiumAmount.ToString(CultureInfo.InvariantCulture);
                command.Parameters[UseSpecialActionCommandHandler.IronAmountParameter] =
                    ironAmount.ToString(CultureInfo.InvariantCulture);
            }

            if (receipt != null) { currentReceipt = receipt; receipt.CommandId = command.CommandId; receipt.Status = CityStyleSubmissionStatus.Pending; }
            var outcome = commandGateway.Submit(
                command,
                new SubmitCallbacks(
                    view.ShowPrompt,
                    CommandGateway.BuildWaitingForHostPrompt("特殊行动命令"))
                {
                    OnAppliedLocally = result =>
                    {
                        completeAction("特殊行动");
                        view.ClearHighlights();
                        view.RefreshFromState();
                        view.RefreshInformation();
                        view.RefreshActionPanel();
                    }
                });
            if (receipt != null) receipt.Status = outcome.Kind == SubmitOutcomeKind.WaitingForHost
                ? CityStyleSubmissionStatus.Pending : outcome.Kind == SubmitOutcomeKind.AppliedLocally
                    ? CityStyleSubmissionStatus.Applied : CityStyleSubmissionStatus.Rejected;
            return outcome.Kind == SubmitOutcomeKind.WaitingForHost ||
                   outcome.Kind == SubmitOutcomeKind.AppliedLocally;
        }

        private IReadOnlyList<CityStyleMarkerViewModel> BuildCityStyleMarkers(
            GameState state,
            int localPlayerId,
            string interactionUnavailableReason)
        {
            using var measurement = UiRegressionMetrics.Measure("declare.markers");
            var result = new List<CityStyleMarkerViewModel>();
            if (state == null || state.Players == null)
            {
                return result.AsReadOnly();
            }

            var specialActionOptions = QuerySpecialActions();
            for (var playerIndex = 0; playerIndex < state.Players.Count; playerIndex++)
            {
                var player = state.Players[playerIndex];
                if (player == null)
                {
                    continue;
                }

                if (player.DeclaredCityStyles != null)
                {
                    for (var declarationIndex = 0;
                         declarationIndex < player.DeclaredCityStyles.Count;
                         declarationIndex++)
                    {
                        var declaration = player.DeclaredCityStyles[declarationIndex];
                        if (declaration == null ||
                            string.IsNullOrEmpty(declaration.CityStyleId))
                        {
                            continue;
                        }

                        // 同玩家同样式的宣告合为一个数量块，共用原有行动入口。
                        var actionDeclaration = FindGroupDeclaration(player, declaration);
                        var specialActionOption = player.PlayerId == localPlayerId
                            ? specialActionOptions.Find(
                                actionDeclaration.UnlockedSpecialActionId,
                                actionDeclaration.GetCurrentMarkerItemId())
                            : null;
                        var specialActionDefinition = SpecialActionDatabase.Get(
                            actionDeclaration.UnlockedSpecialActionId);
                        result.Add(new CityStyleMarkerViewModel(
                            declaration.CityStyleId,
                            player.PlayerId,
                            player.Color,
                            DisplayMarkerArea(player, declaration),
                            actionDeclaration.GetCurrentMarkerItemId(),
                            actionDeclaration.UnlockedSpecialActionId,
                            specialActionOption != null &&
                            specialActionOption.CanUse &&
                            string.IsNullOrEmpty(interactionUnavailableReason),
                            ResolveSpecialActionDropArea(
                                specialActionDefinition,
                                actionDeclaration.RemainingSpecialActionUses),
                            !string.IsNullOrEmpty(interactionUnavailableReason) &&
                            specialActionOption != null
                                ? interactionUnavailableReason
                                : specialActionOption == null
                                    ? string.Empty
                                    : specialActionOption.DisabledReason,
                            specialActionOption == null
                                ? string.Empty
                                : specialActionOption.Warning,
                            GetMaximumCompositePayment(specialActionOption, true),
                            GetMaximumCompositePayment(specialActionOption, false)));
                    }
                }

            }

            foreach (var marker in CurrentMarkersWithoutDeclaration(state))
            {
                var option = marker.PlayerId == localPlayerId ? specialActionOptions.Find(marker.SpecialActionId, marker.MarkerId) : null;
                result.Add(new CityStyleMarkerViewModel(marker.CityStyleId, marker.PlayerId, marker.PlayerColor, marker.MarkerArea,
                    marker.MarkerId, marker.SpecialActionId, option?.CanUse == true && string.IsNullOrEmpty(interactionUnavailableReason),
                    ResolveSpecialActionDropArea(SpecialActionDatabase.Get(marker.SpecialActionId), marker.MarkerArea == "2" ? 2 : marker.MarkerArea == "1" ? 1 : 0),
                    option == null ? string.Empty : !string.IsNullOrEmpty(interactionUnavailableReason) ? interactionUnavailableReason : option.DisabledReason,
                    option?.Warning ?? string.Empty, GetMaximumCompositePayment(option, true), GetMaximumCompositePayment(option, false)));
            }

            return result.AsReadOnly();
        }

        // 直接入轨只赋予当前资格；展示实际物品，不创建宣告或计分历史。
        public static IReadOnlyList<CityStyleMarkerViewModel> CurrentMarkersWithoutDeclaration(GameState state)
        {
            var result = new List<CityStyleMarkerViewModel>();
            if (state?.ItemZones?.Items == null) return result;
            foreach (var marker in state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.Kind == ItemSubjectKind.Player))
            {
                var zone = state.ItemZones.Zones.Find(z => z.ZoneId == marker.ZoneId);
                if (zone?.TemplateId != "core.zone.board_marker_track" || zone.ParentKind != ZoneParentKind.Item || string.IsNullOrEmpty(marker.SlotId)) continue;
                var source = state.ItemZones.Items.Find(i => i.ItemId == zone.ParentId);
                var player = state.FindPlayer(marker.OwnerSubject.PlayerId);
                if (player == null || source == null || !CityStyleDatabase.TryGet(state, source.DefinitionId, out var definition) ||
                    player.DeclaredCityStyles.Any(d => d.CityStyleId == definition.CityStyleId)) continue;
                result.Add(new CityStyleMarkerViewModel(definition.CityStyleId, player.PlayerId, player.Color, marker.SlotId,
                    marker.ItemId, definition.SpecialActionId, false, string.Empty, string.Empty));
            }
            return result.AsReadOnly();
        }

        public static string DisplayMarkerArea(PlayerState player, CityStyleDeclarationState declaration)
        {
            var owner = FindGroupDeclaration(player, declaration);
            return owner.MarkerArea ?? string.Empty;
        }

        public SpecialActionOptionQueryResult QuerySpecialActions()
        {
            var visible = GetVisibleState?.Invoke();
            if (visible?.HasAbilityActionCatalog != true) return specialActionOptionQuery.Query(context.CurrentState, context.LocalPlayerId);
            var result = new SpecialActionOptionQueryResult();
            foreach (var action in visible.AbilityActions)
                if (action.EntryKind == "special") result.Options.Add(new SpecialActionOption { DeclaredAction = action, SpecialActionId = action.ActionId,
                    DeclarationMarkerId = visible.UsageTracks.Find(t => t.Binding.BindingId == action.TrackBindingId)?.MarkerItemId ?? action.SourceRef.ItemId, CityStyleId = action.SourceRef.DefinitionId,
                    Name = action.DisplayName, Description = action.Description, CanUse = action.Enabled, DisabledReason = YC.Domain.Effects.AbilityActionReasons.Display(action.DisabledReason) });
            return result;
        }

        public bool CanUseSpecialAction => GetVisibleState?.Invoke()?.HasAbilityActionCatalog == true && canStartMainAction() && QuerySpecialActions().HasUsableOption;

        public bool BeginSpecialActionChoice()
        {
            var visible = GetVisibleState?.Invoke();
            if (visible?.HasAbilityActionCatalog != true || !CanUseSpecialAction) return false;
            var command = YC.Application.Interactions.SpecialActionChoiceCommand.Create(context.LocalPlayerId, visible.StateRevision, visible.ContentPackHash);
            var submitted = commandGateway.Submit(command, new SubmitCallbacks(view.ShowPrompt, "特殊行动选择已发送，等待主机确认。") {
                OnAppliedLocally = _ => view.RefreshFromState()
            });
            return submitted.Kind == SubmitOutcomeKind.AppliedLocally || submitted.Kind == SubmitOutcomeKind.WaitingForHost;
        }

        public bool SubmitSpecialAction(string actionId, string markerId)
        {
            var option = QuerySpecialActions().Find(actionId, markerId);
            return option != null && option.CanUse &&
                TrySubmitSpecialAction(actionId, markerId, -1, -1, new Receipt());
        }

        private static CityStyleDeclarationState FindGroupDeclaration(PlayerState player, CityStyleDeclarationState declaration)
        {
            var first = declaration;
            var found = false;
            foreach (var item in player.DeclaredCityStyles)
            {
                if (item == null || item.CityStyleId != declaration.CityStyleId) continue;
                if (!found) { first = item; found = true; }
                if (!string.IsNullOrEmpty(item.UnlockedSpecialActionId)) return item;
            }
            return first;
        }

        private static IReadOnlyList<CityBoardSlotViewModel> BuildCityBoardSlots(
            GameState state,
            int playerId)
        {
            const int cityBoardSlotCount = 12;
            var usedSlotIndexes = new HashSet<int>();
            var player = state == null ? null : state.FindPlayer(playerId);
            if (player?.DeclaredCityStyles != null && ItemZoneLegacyProjection.IsActive(state))
            {
                // 叠放后只判断当前顶层实例；底层历史宣告不能占用新设施。
                var usedInstances = new HashSet<string>(player.DeclaredCityStyles
                    .Where(declaration => declaration?.UsedFacilityIds != null)
                    .SelectMany(declaration => declaration.UsedFacilityIds), StringComparer.Ordinal);
                foreach (var placement in state.Map.Facilities.Where(placement => placement.PlayerId == playerId &&
                    !string.IsNullOrEmpty(placement.ContentInstanceId) && usedInstances.Contains(placement.ContentInstanceId)))
                    usedSlotIndexes.Add(placement.CityBoardSlotIndex);
            }
            else if (player != null && player.DeclaredCityStyles != null)
            {
                for (var declarationIndex = 0;
                     declarationIndex < player.DeclaredCityStyles.Count;
                     declarationIndex++)
                {
                    var declaration = player.DeclaredCityStyles[declarationIndex];
                    if (declaration == null ||
                        declaration.UsedCityBoardSlotIndexes == null)
                    {
                        continue;
                    }

                    for (var slotIndex = 0;
                         slotIndex < declaration.UsedCityBoardSlotIndexes.Count;
                         slotIndex++)
                    {
                        usedSlotIndexes.Add(
                            declaration.UsedCityBoardSlotIndexes[slotIndex]);
                    }
                }
            }

            var facilityBySlot = new Dictionary<int, string>();
            if (state != null && state.Map != null && state.Map.Facilities != null)
            {
                for (var placementIndex = 0;
                     placementIndex < state.Map.Facilities.Count;
                     placementIndex++)
                {
                    var placement = state.Map.Facilities[placementIndex];
                    if (placement.PlayerId == playerId)
                    {
                        facilityBySlot[placement.CityBoardSlotIndex] =
                            placement.FacilityCardId ?? string.Empty;
                    }
                }
            }

            var result = new List<CityBoardSlotViewModel>(cityBoardSlotCount);
            for (var slotIndex = 0; slotIndex < cityBoardSlotCount; slotIndex++)
            {
                string facilityId;
                facilityBySlot.TryGetValue(slotIndex, out facilityId);
                result.Add(new CityBoardSlotViewModel(
                    slotIndex,
                    facilityId,
                    usedSlotIndexes.Contains(slotIndex)));
            }

            return result.AsReadOnly();
        }

        private static string ResolveSpecialActionDropArea(
            SpecialActionDefinition definition,
            int remainingUses)
        {
            if (definition == null)
            {
                return string.Empty;
            }

            if (definition.Level < 2)
            {
                return CityStyleMarkerAreas.Used;
            }

            return remainingUses >= 2
                ? SpecialActionMarkerAreas.UsedFromTwo
                : remainingUses == 1
                    ? SpecialActionMarkerAreas.UsedFromOne
                    : string.Empty;
        }

        private static int GetMaximumCompositePayment(
            SpecialActionOption option,
            bool originium)
        {
            var maximum = 0;
            if (option == null || option.PaymentOptions == null)
            {
                return maximum;
            }

            for (var i = 0; i < option.PaymentOptions.Count; i++)
            {
                var payment = option.PaymentOptions[i];
                if (payment != null)
                {
                    maximum = Math.Max(
                        maximum,
                        originium ? payment.Originium : payment.Iron);
                }
            }

            return maximum;
        }
    }
}
