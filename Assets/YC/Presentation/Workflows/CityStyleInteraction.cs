using System;
using System.Collections.Generic;
using System.Globalization;
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
            ShowPreview(initialCityStyleId, string.Empty);
        }

        public void OpenPreview(string initialCityStyleId)
        {
            ShowPreview(initialCityStyleId, getUnavailableReason());
        }

        public void SubmitDeclare(
            string cityStyleId,
            IReadOnlyList<int> selectedSlotIndexes)
        {
            TrySubmitDeclare(cityStyleId, selectedSlotIndexes);
        }

        private void ShowPreview(string initialCityStyleId, string unavailableReason)
        {
            var options = selection.BuildOptions(
                context.CurrentState,
                context.LocalPlayerId);
            if (!string.IsNullOrEmpty(unavailableReason))
            {
                for (var i = 0; i < options.Count; i++)
                {
                    options[i].CanDeclare = false;
                    options[i].Reason = unavailableReason;
                }
            }

            view.ShowCityStyleOptions(new CityStyleOptionsViewModel(
                options.AsReadOnly(),
                BuildCityBoardSlots(context.CurrentState, context.LocalPlayerId),
                BuildCityStyleMarkers(
                    context.CurrentState,
                    context.LocalPlayerId,
                    unavailableReason),
                initialCityStyleId,
                (cityStyleId, selectedSlotIndexes) => selection.ValidateSelection(
                    context.CurrentState,
                    context.LocalPlayerId,
                    cityStyleId,
                    selectedSlotIndexes),
                TrySubmitDeclare,
                null,
                restoreBuildInteraction,
                TrySubmitSpecialAction));
            view.ShowPrompt(string.Empty);
        }

        private bool TrySubmitDeclare(
            string cityStyleId,
            IReadOnlyList<int> selectedSlotIndexes)
        {
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
            return outcome.Kind == SubmitOutcomeKind.WaitingForHost ||
                   outcome.Kind == SubmitOutcomeKind.AppliedLocally;
        }

        private bool TrySubmitSpecialAction(
            string specialActionId,
            string declarationMarkerId,
            int originiumAmount,
            int ironAmount)
        {
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
                (originiumAmount < 0 ||
                 ironAmount < 0 ||
                 originiumAmount + ironAmount != 3))
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
            if (specialActionId == SpecialActionDatabase.CompositePowerSystem)
            {
                command.Parameters[UseSpecialActionCommandHandler.OriginiumAmountParameter] =
                    originiumAmount.ToString(CultureInfo.InvariantCulture);
                command.Parameters[UseSpecialActionCommandHandler.IronAmountParameter] =
                    ironAmount.ToString(CultureInfo.InvariantCulture);
            }

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
            return outcome.Kind == SubmitOutcomeKind.WaitingForHost ||
                   outcome.Kind == SubmitOutcomeKind.AppliedLocally;
        }

        private IReadOnlyList<CityStyleMarkerViewModel> BuildCityStyleMarkers(
            GameState state,
            int localPlayerId,
            string interactionUnavailableReason)
        {
            var result = new List<CityStyleMarkerViewModel>();
            if (state == null || state.Players == null)
            {
                return result.AsReadOnly();
            }

            var specialActionOptions = specialActionOptionQuery.Query(state, localPlayerId);
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

                        var specialActionOption = player.PlayerId == localPlayerId
                            ? specialActionOptions.Find(
                                declaration.UnlockedSpecialActionId,
                                declaration.InfluenceMarkerId)
                            : null;
                        var specialActionDefinition = SpecialActionDatabase.Get(
                            declaration.UnlockedSpecialActionId);
                        result.Add(new CityStyleMarkerViewModel(
                            declaration.CityStyleId,
                            player.PlayerId,
                            player.Color,
                            string.IsNullOrEmpty(declaration.MarkerArea)
                                ? CityStyleMarkerAreas.Declared
                                : declaration.MarkerArea,
                            declaration.InfluenceMarkerId,
                            declaration.UnlockedSpecialActionId,
                            specialActionOption != null &&
                            specialActionOption.CanUse &&
                            string.IsNullOrEmpty(interactionUnavailableReason),
                            ResolveSpecialActionDropArea(
                                specialActionDefinition,
                                declaration.RemainingSpecialActionUses),
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

            return result.AsReadOnly();
        }

        private static IReadOnlyList<CityBoardSlotViewModel> BuildCityBoardSlots(
            GameState state,
            int playerId)
        {
            const int cityBoardSlotCount = 12;
            var usedSlotIndexes = new HashSet<int>();
            var player = state == null ? null : state.FindPlayer(playerId);
            if (player != null && player.DeclaredCityStyles != null)
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
