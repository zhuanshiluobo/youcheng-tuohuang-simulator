using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.CardFlows;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Presentation.Workflows
{
    public sealed class ExplorationEventPresenter : IInteractionWorkflow
    {
        private readonly IGameplayContext context;
        private readonly IGameCommandPort commandPort;
        private readonly IExplorationEventView view;
        private readonly IMapQueryService mapQuery;
        private readonly ExplorationService explorationService;
        private readonly InfluenceService influenceService;
        private readonly RouteTollService routeTollService;
        private readonly EventEffectResolver eventEffectResolver;
        private readonly ExplorePathSelectionController pathSelection;
        private readonly ExplorePaymentRecipientSelectionController paymentSelection;
        private readonly EventOptionSelectionController optionSelection;
        private readonly EventInfluenceTargetSelectionController influenceTargetSelection;
        private readonly HashSet<string> selectableInfluenceSlotIds =
            new HashSet<string>(StringComparer.Ordinal);

        private InteractionMode currentMode = InteractionMode.ChooseAction;
        private int lastInfluenceInputFrame = -1;
        private int syntheticInputFrame = int.MinValue;

        public ExplorationEventPresenter(
            IGameplayContext context,
            IGameCommandPort commandPort,
            IExplorationEventView view,
            IMapQueryService mapQuery,
            ExplorationService explorationService,
            InfluenceService influenceService)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.explorationService = explorationService ??
                                      throw new ArgumentNullException(nameof(explorationService));
            this.influenceService = influenceService ??
                                    throw new ArgumentNullException(nameof(influenceService));
            routeTollService = new RouteTollService(mapQuery);
            eventEffectResolver = new EventEffectResolver(mapQuery, influenceService);
            pathSelection = new ExplorePathSelectionController();
            paymentSelection = new ExplorePaymentRecipientSelectionController();
            optionSelection = new EventOptionSelectionController();
            influenceTargetSelection = new EventInfluenceTargetSelectionController();
        }

        public InteractionMode Mode
        {
            get { return currentMode; }
        }

        public bool IsSelectingInfluenceTarget
        {
            get { return influenceTargetSelection.IsSelecting; }
        }

        public string TargetLocationId
        {
            get { return pathSelection.TargetLocationId; }
        }

        public MapPath SelectedPath
        {
            get { return pathSelection.SelectedPath; }
        }

        public IReadOnlyList<ExplorePathChoice> PathChoices
        {
            get { return pathSelection.PathChoices; }
        }

        public IReadOnlyDictionary<string, int> PaymentRecipients
        {
            get { return paymentSelection.RecipientsByRouteId; }
        }

        public IReadOnlyList<string> SelectedInfluenceSlotIds
        {
            get { return influenceTargetSelection.SelectedSlotIds; }
        }

        public void Activate()
        {
            BeginTargetSelection();
        }

        public void BeginTargetSelection()
        {
            ClearWorkflowState();
            view.HideEventOptions();
            SetMode(InteractionMode.ResolvingExploreTarget);

            var highlights = BuildExplorableLocationHighlights();
            view.SetHighlights(highlights);
            view.RefreshActionPanel();
            view.ShowPrompt(highlights.Count == 0
                ? "\u5f53\u524d\u6ca1\u6709\u53ef\u63a2\u7d22\u8d44\u6e90\u70b9\u3002"
                : "\u63a2\u7d22\uff1a\u9009\u62e9\u4e00\u4e2a\u9ad8\u4eae\u8d44\u6e90\u70b9\u3002");
        }

        public bool TryEstimateGoldVoucherCost(
            string locationId,
            out int estimatedCost,
            out string errorPrompt)
        {
            estimatedCost = 0;
            errorPrompt = string.Empty;

            IReadOnlyList<MapPath> paths;
            if (!TryFindPaths(locationId, out paths))
            {
                errorPrompt = "\u6ca1\u6709\u53ef\u5230\u8fbe\u8be5\u63a2\u7d22\u76ee\u6807\u7684\u8def\u7ebf\u3002";
                return false;
            }

            estimatedCost = CalculateGoldVoucherCost(paths[0]);
            return true;
        }

        public void SelectTarget(string locationId)
        {
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            if (player == null || string.IsNullOrEmpty(player.CityLocationId))
            {
                view.ShowPrompt("\u73a9\u5bb6\u57ce\u5e02\u4e0d\u5728\u573a\u4e0a\u3002");
                return;
            }

            IReadOnlyList<MapPath> paths;
            if (!TryFindPaths(locationId, out paths))
            {
                view.ShowPrompt("\u6ca1\u6709\u53ef\u5230\u8fbe\u8be5\u63a2\u7d22\u76ee\u6807\u7684\u8def\u7ebf\u3002");
                return;
            }

            pathSelection.BeginTarget(locationId);
            paymentSelection.Clear();
            pathSelection.SetPathChoices(paths, BuildPathChoiceLabel);
            view.ClearHighlights();
            view.RefreshActionPanel();

            if (ShouldPromptForPathChoice(paths))
            {
                ShowPathOptions();
                return;
            }

            SelectPath(paths[0]);
        }

        public void BeginWithPathAndCard(
            string locationId,
            MapPath path,
            EventCardDefinition card)
        {
            pathSelection.BeginWithPath(locationId, path);
            paymentSelection.Clear();
            BuildPaymentChoices(path);
            view.ClearHighlights();
            view.RefreshActionPanel();
            ShowEventCard(card);
        }

        public void SelectPathChoice(int choiceIndex)
        {
            if (!pathSelection.TrySelectPathChoice(choiceIndex))
            {
                view.ShowPrompt("\u63a2\u7d22\u8def\u7ebf\u9009\u9879\u65e0\u6548\u3002");
                return;
            }

            SelectPath(pathSelection.SelectedPath);
        }

        public void SelectPaymentRecipient(string routeId, int recipientPlayerId)
        {
            if (!paymentSelection.SelectRecipient(routeId, recipientPlayerId))
            {
                view.ShowPrompt("\u8fc7\u8def\u8d39\u63a5\u6536\u65b9\u65e0\u6548\u3002");
                return;
            }

            ShowPaymentOptions();
        }

        public void ConfirmExploreStart()
        {
            SubmitExploreStart();
        }

        public void ShowPendingChoice()
        {
            var pendingChoice = GetPendingChoice();
            if (pendingChoice == null || pendingChoice.PlayerId != context.LocalPlayerId)
            {
                return;
            }

            var card = EventCardDatabase.Get(pendingChoice.CardId);
            if (card == null)
            {
                view.ShowPrompt("\u5f85\u5904\u7406\u4e8b\u4ef6\u5361\u4e0d\u5b58\u5728\u3002");
                return;
            }

            ShowEventCard(card);
        }

        public void SelectEventChoice(int choiceIndex)
        {
            EventOptionSelection selection;
            if (!optionSelection.TrySelectChoice(choiceIndex, out selection))
            {
                view.ShowPrompt("\u4e8b\u4ef6\u9009\u9879\u65e0\u6548\u3002");
                return;
            }

            var card = optionSelection.PendingEventCard;
            if (selection.RequiresInfluenceTargets)
            {
                BeginInfluenceTargetSelection(card, selection.ChoiceIndex);
                return;
            }

            SubmitSelectedEventChoice(selection.ChoiceIndex);
        }

        public bool SelectInfluenceAtLocation(string locationId, int inputFrame)
        {
            if (!TryAcceptInfluenceInputFrame(inputFrame))
            {
                return false;
            }

            if (!influenceTargetSelection.IsSelecting || string.IsNullOrEmpty(locationId))
            {
                return false;
            }

            MapLocationDefinition location;
            try
            {
                location = mapQuery.GetLocation(locationId);
            }
            catch (ArgumentException)
            {
                return false;
            }

            for (var i = 0; i < location.InfluenceSlotCount; i++)
            {
                var slotId = InfluenceService.GetLocationSlotId(locationId, i);
                if (selectableInfluenceSlotIds.Contains(slotId))
                {
                    SelectInfluenceSlotCore(slotId);
                    return true;
                }
            }

            for (var routeIndex = 0; routeIndex < mapQuery.Map.Routes.Count; routeIndex++)
            {
                var route = mapQuery.Map.Routes[routeIndex];
                if (!RouteTouchesLocation(route, locationId))
                {
                    continue;
                }

                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    var slotId = InfluenceService.GetRouteSlotId(route.RouteId, slotIndex);
                    if (selectableInfluenceSlotIds.Contains(slotId))
                    {
                        SelectInfluenceSlotCore(slotId);
                        return true;
                    }
                }
            }

            view.ShowPrompt("\u8bf7\u9009\u62e9\u9ad8\u4eae\u7684\u5f71\u54cd\u529b\u69fd\u4f4d\u3002");
            return false;
        }

        public bool SelectInfluenceAtLocation(string locationId)
        {
            return SelectInfluenceAtLocation(locationId, NextSyntheticInputFrame());
        }

        public bool SelectInfluenceSlot(string slotId, int inputFrame)
        {
            if (!TryAcceptInfluenceInputFrame(inputFrame))
            {
                return false;
            }

            return SelectInfluenceSlotCore(slotId);
        }

        public bool SelectInfluenceSlot(string slotId)
        {
            return SelectInfluenceSlot(slotId, NextSyntheticInputFrame());
        }

        public void RestorePresentation()
        {
            switch (currentMode)
            {
                case InteractionMode.ResolvingExploreTarget:
                    view.SetHighlights(BuildExplorableLocationHighlights());
                    view.ShowPrompt(GetCurrentPrompt());
                    break;
                case InteractionMode.ResolvingEventInfluenceTarget:
                    PresentInfluenceTargets();
                    view.ShowPrompt(GetCurrentPrompt());
                    break;
            }
        }

        public string GetCurrentPrompt()
        {
            switch (currentMode)
            {
                case InteractionMode.ResolvingExploreTarget:
                    return "\u63a2\u7d22\uff1a\u9009\u62e9\u4e00\u4e2a\u9ad8\u4eae\u8d44\u6e90\u70b9\u3002";
                case InteractionMode.ResolvingEventInfluenceTarget:
                    return "\u8bf7\u9009\u62e9\u4e8b\u4ef6\u6548\u679c\u8981\u653e\u7f6e\u5f71\u54cd\u529b\u7684\u69fd\u4f4d\u3002";
                default:
                    return string.Empty;
            }
        }

        public void Cancel()
        {
            ClearWorkflowState();
            view.HideEventOptions();
            view.ClearHighlights();
            SetMode(InteractionMode.ChooseAction);
            view.RefreshActionPanel();
        }

        private void SelectPath(MapPath path)
        {
            pathSelection.SelectPath(path);
            BuildPaymentChoices(path);
            if (paymentSelection.HasChoices)
            {
                ShowPaymentOptions();
                return;
            }

            SubmitExploreStart();
        }

        private void ShowPathOptions()
        {
            SetMode(InteractionMode.PendingChoice);
            view.ShowExplorePathOptions(new ExplorePathOptionsViewModel(
                pathSelection.PathChoices,
                SelectPathChoice));
            view.ShowPrompt("\u5b58\u5728\u591a\u6761\u8def\u8d39\u63a5\u6536\u65b9\u4e0d\u540c\u7684\u6700\u4f18\u8def\u7ebf\uff0c\u8bf7\u9009\u62e9\u3002");
        }

        private void ShowPaymentOptions()
        {
            SetMode(InteractionMode.PendingChoice);
            view.ShowExplorePaymentOptions(new ExplorePaymentOptionsViewModel(
                paymentSelection.Choices,
                paymentSelection.RecipientsByRouteId,
                SelectPaymentRecipient,
                ConfirmExploreStart));
            view.ShowPrompt("\u9009\u62e9\u6bcf\u6761\u8def\u7ebf\u7684\u8fc7\u8def\u8d39\u63a5\u6536\u8005\uff0c\u7136\u540e\u786e\u8ba4\u63a2\u7d22\u3002");
        }

        private void ShowEventCard(EventCardDefinition card)
        {
            if (card == null || card.ChoiceRewards.Count == 0)
            {
                return;
            }

            optionSelection.Begin(card);
            SetMode(InteractionMode.PendingChoice);
            view.ShowEventCardOptions(new EventCardOptionsViewModel(
                card,
                BuildEventCardMetadataLabel(card),
                paymentSelection.Choices,
                paymentSelection.RecipientsByRouteId,
                SelectEventChoice,
                SelectPaymentRecipient));
            view.ShowPrompt("\u8bf7\u9009\u62e9\u4e8b\u4ef6\u724c\u7684\u4e00\u4e2a\u9009\u9879\u3002");
        }

        private void BeginInfluenceTargetSelection(EventCardDefinition card, int choiceIndex)
        {
            influenceTargetSelection.Begin(choiceIndex);
            selectableInfluenceSlotIds.Clear();
            lastInfluenceInputFrame = -1;
            view.CollapseEventOptions();
            SetMode(InteractionMode.ResolvingEventInfluenceTarget);
            view.ClearHighlights();

            var count = PresentInfluenceTargets();
            view.RefreshActionPanel();
            if (count > 0)
            {
                view.ShowPrompt(GetCurrentPrompt());
                return;
            }

            influenceTargetSelection.Clear();
            selectableInfluenceSlotIds.Clear();
            SetMode(InteractionMode.PendingChoice);
            ShowEventCard(card);
            view.ShowPrompt("\u6ca1\u6709\u53ef\u7528\u4e8e\u8be5\u4e8b\u4ef6\u6548\u679c\u7684\u5f71\u54cd\u529b\u69fd\u4f4d\u3002");
        }

        private bool SelectInfluenceSlotCore(string slotId)
        {
            var card = GetPendingEventCardForTargetSelection();
            if (card == null || !influenceTargetSelection.IsSelecting)
            {
                view.ShowPrompt("\u5f53\u524d\u6ca1\u6709\u5f85\u5904\u7406\u7684\u4e8b\u4ef6\u5f71\u54cd\u529b\u76ee\u6807\u3002");
                return false;
            }

            if (influenceTargetSelection.ContainsSlot(slotId))
            {
                view.ShowPrompt("\u4e0d\u80fd\u91cd\u590d\u9009\u62e9\u540c\u4e00\u4e2a\u5f71\u54cd\u529b\u69fd\u4f4d\u3002");
                return false;
            }

            if (!selectableInfluenceSlotIds.Contains(slotId))
            {
                view.ShowPrompt("\u8bf7\u9009\u62e9\u9ad8\u4eae\u7684\u5f71\u54cd\u529b\u69fd\u4f4d\u3002");
                return false;
            }

            bool completed;
            if (!influenceTargetSelection.TrySelectSlot(card, slotId, out completed))
            {
                return false;
            }

            if (!completed)
            {
                view.ClearHighlights();
                PresentInfluenceTargets();
                view.RefreshActionPanel();
                view.ShowPrompt("\u7ee7\u7eed\u9009\u62e9\u4e8b\u4ef6\u6548\u679c\u7684\u5f71\u54cd\u529b\u69fd\u4f4d\u3002");
                return true;
            }

            SubmitSelectedEventChoice(influenceTargetSelection.ChoiceIndex);
            return true;
        }

        private void SubmitSelectedEventChoice(int choiceIndex)
        {
            var pendingChoice = GetPendingChoice();
            if (pendingChoice != null &&
                pendingChoice.ChoiceType == ExploreLocationCommandHandler.ExploreEventChoiceType)
            {
                SubmitResolvePendingChoice(
                    choiceIndex,
                    ExploreLocationCommandHandler.EventInfluenceSlotIdsParameter,
                    "\u63a2\u7d22\u4e8b\u4ef6\u9009\u62e9\u5df2\u53d1\u9001\u7ed9\u4e3b\u673a\uff0c\u7b49\u5f85\u786e\u8ba4\u3002",
                    "\u63a2\u7d22");
                return;
            }

            if (pendingChoice != null &&
                pendingChoice.ChoiceType == MoveCityCommandHandler.MoveCityEventChoiceType)
            {
                SubmitResolvePendingChoice(
                    choiceIndex,
                    MoveCityCommandHandler.EventInfluenceSlotIdsParameter,
                    "\u57ce\u5e02\u79fb\u52a8\u4e8b\u4ef6\u9009\u62e9\u5df2\u53d1\u9001\u7ed9\u4e3b\u673a\uff0c\u7b49\u5f85\u786e\u8ba4\u3002",
                    "\u57ce\u5e02\u79fb\u52a8");
                return;
            }

            if (pathSelection.HasTarget)
            {
                SubmitExploreChoice(choiceIndex);
                return;
            }

            SubmitEntranceEventChoice(choiceIndex);
        }

        private void SubmitExploreStart()
        {
            if (pathSelection.SelectedPath == null)
            {
                view.ShowPrompt("\u7f3a\u5c11\u63a2\u7d22\u8def\u7ebf\u3002");
                return;
            }

            var command = BuildExploreCommand();
            var submission = commandPort.Submit(command);
            if (!submission.CommandResult.Succeeded)
            {
                view.ShowPrompt(submission.CommandResult.Validation.Reason);
                return;
            }

            if (!submission.AppliedLocally)
            {
                view.ShowPrompt("\u63a2\u7d22\u547d\u4ee4\u5df2\u53d1\u9001\u7ed9\u4e3b\u673a\uff0c\u7b49\u5f85\u786e\u8ba4\u3002");
                return;
            }

            view.HideEventOptions();
            ClearWorkflowState();
            view.RefreshResourceAndInfluence();
            var pendingChoice = GetPendingChoice();
            if (pendingChoice != null && pendingChoice.PlayerId == context.LocalPlayerId)
            {
                ShowPendingChoice();
                return;
            }

            SetMode(InteractionMode.ChooseAction);
            view.RefreshActionPanel();
            view.ShowPrompt("\u63a2\u7d22\u5df2\u5f00\u59cb\u3002");
        }

        private void SubmitResolvePendingChoice(
            int choiceIndex,
            string influenceParameterName,
            string waitingPrompt,
            string completedActionName)
        {
            var pendingChoice = GetPendingChoice();
            var command = new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = context.LocalPlayerId,
                TargetId = pendingChoice == null ? string.Empty : pendingChoice.TargetId,
                OptionIds = new List<string> { choiceIndex.ToString() }
            };
            influenceTargetSelection.AddCommandParameter(command, influenceParameterName);
            var submission = commandPort.Submit(command);
            if (!submission.CommandResult.Succeeded)
            {
                view.ShowPrompt(submission.CommandResult.Validation.Reason);
                return;
            }

            if (!submission.AppliedLocally)
            {
                view.ShowPrompt(waitingPrompt);
                return;
            }

            CompleteAppliedAction(completedActionName);
        }

        private void SubmitExploreChoice(int choiceIndex)
        {
            if (pathSelection.SelectedPath == null)
            {
                view.ShowPrompt("\u7f3a\u5c11\u63a2\u7d22\u8def\u7ebf\u3002");
                return;
            }

            var command = BuildExploreCommand();
            command.Parameters[ExploreLocationCommandHandler.EventOptionIdParameter] = choiceIndex.ToString();
            influenceTargetSelection.AddCommandParameter(
                command,
                ExploreLocationCommandHandler.EventInfluenceSlotIdsParameter);
            var submission = commandPort.Submit(command);
            if (!submission.CommandResult.Succeeded)
            {
                view.ShowPrompt(submission.CommandResult.Validation.Reason);
                return;
            }

            if (!submission.AppliedLocally)
            {
                view.ShowPrompt("\u63a2\u7d22\u547d\u4ee4\u5df2\u53d1\u9001\u7ed9\u4e3b\u673a\uff0c\u7b49\u5f85\u786e\u8ba4\u3002");
                return;
            }

            CompleteAppliedAction("\u63a2\u7d22");
        }

        private void SubmitEntranceEventChoice(int choiceIndex)
        {
            var command = new GameCommand
            {
                Kind = GameCommandKind.ResolveEntranceEvent,
                PlayerId = context.LocalPlayerId,
                OptionIds = new List<string> { choiceIndex.ToString() }
            };
            influenceTargetSelection.AddCommandParameter(
                command,
                ExploreLocationCommandHandler.EventInfluenceSlotIdsParameter);
            var submission = commandPort.Submit(command);
            if (!submission.CommandResult.Succeeded)
            {
                view.ShowPrompt(submission.CommandResult.Validation.Reason);
                return;
            }

            if (!submission.AppliedLocally)
            {
                view.ShowPrompt("\u4e8b\u4ef6\u9009\u62e9\u5df2\u53d1\u9001\u7ed9\u4e3b\u673a\uff0c\u7b49\u5f85\u786e\u8ba4\u3002");
                return;
            }

            view.HideEventOptions();
            ClearWorkflowState();
            view.ClearHighlights();
            SetMode(InteractionMode.ChooseAction);
            view.RefreshFromState();
        }

        private GameCommand BuildExploreCommand()
        {
            var command = new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = context.LocalPlayerId,
                TargetId = pathSelection.TargetLocationId
            };
            command.Parameters[ExploreLocationCommandHandler.PathLocationIdsParameter] =
                EncodeIds(pathSelection.SelectedPath.LocationIds);
            command.Parameters[ExploreLocationCommandHandler.RouteIdsParameter] =
                EncodeIds(pathSelection.SelectedPath.RouteIds);
            var encodedPayments = paymentSelection.EncodeRecipients();
            if (!string.IsNullOrEmpty(encodedPayments))
            {
                command.Parameters[ExploreLocationCommandHandler.PaymentRecipientsParameter] = encodedPayments;
            }

            return command;
        }

        private void CompleteAppliedAction(string actionName)
        {
            view.HideEventOptions();
            ClearWorkflowState();
            view.ClearHighlights();
            view.RefreshResourceAndInfluence();
            SetMode(InteractionMode.ChooseAction);
            view.CompleteAction(actionName);
        }

        private List<WorkflowHighlight> BuildExplorableLocationHighlights()
        {
            var highlights = new List<WorkflowHighlight>();
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            if (state == null || player == null || string.IsNullOrEmpty(player.CityLocationId))
            {
                return highlights;
            }

            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var locationId = mapQuery.Map.Locations[i].LocationId;
                if (locationId == player.CityLocationId || !CanExploreLocation(locationId))
                {
                    continue;
                }

                highlights.Add(new WorkflowHighlight(
                    WorkflowHighlightTargetKind.Location,
                    locationId,
                    WorkflowHighlightSemantic.ExploreTarget));
            }

            return highlights;
        }

        private bool CanExploreLocation(string locationId)
        {
            IReadOnlyList<MapPath> paths;
            if (!TryFindPaths(locationId, out paths))
            {
                return false;
            }

            var state = context.CurrentState;
            for (var i = 0; i < paths.Count; i++)
            {
                var recipients = BuildDefaultPaymentRecipients(paths[i]);
                var validation = explorationService.CanExplore(
                    state,
                    context.LocalPlayerId,
                    locationId,
                    paths[i],
                    -1,
                    string.Empty,
                    recipients,
                    null,
                    false);
                if (validation.IsValid)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryFindPaths(string locationId, out IReadOnlyList<MapPath> paths)
        {
            paths = null;
            var state = context.CurrentState;
            if (state == null || string.IsNullOrEmpty(locationId))
            {
                return false;
            }

            try
            {
                paths = explorationService.FindDefaultPathChoices(
                    state,
                    context.LocalPlayerId,
                    locationId);
            }
            catch (ArgumentException)
            {
                return false;
            }

            return paths != null && paths.Count > 0;
        }

        private void BuildPaymentChoices(MapPath path)
        {
            var state = context.CurrentState;
            var playerId = context.LocalPlayerId;
            paymentSelection.BuildForPathWithPaymentKeys(
                path,
                routeId => routeTollService.GetRoutePaymentKey(
                    routeId,
                    RouteTollPaymentKeyMode.SharedRegion),
                routeId => routeTollService.HasPaymentKeyInfluenceOwnedBy(
                    state,
                    routeTollService.GetRoutePaymentKey(routeId, RouteTollPaymentKeyMode.SharedRegion),
                    playerId,
                    RouteTollPaymentKeyMode.SharedRegion),
                routeId => routeTollService.GetOpponentInfluenceOwnersOnPaymentKey(
                    state,
                    routeTollService.GetRoutePaymentKey(routeId, RouteTollPaymentKeyMode.SharedRegion),
                    playerId,
                    RouteTollPaymentKeyMode.SharedRegion));
        }

        private Dictionary<string, int> BuildDefaultPaymentRecipients(MapPath path)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (path == null)
            {
                return result;
            }

            var state = context.CurrentState;
            var paidKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                var routeId = path.RouteIds[i];
                var key = routeTollService.GetRoutePaymentKey(
                    routeId,
                    RouteTollPaymentKeyMode.SharedRegion);
                if (!paidKeys.Add(key) ||
                    !routeTollService.IsPaymentRequired(
                        state,
                        routeId,
                        context.LocalPlayerId,
                        RouteTollPaymentKeyMode.SharedRegion))
                {
                    continue;
                }

                var owners = routeTollService.GetOpponentInfluenceOwnersOnPaymentKey(
                    state,
                    key,
                    context.LocalPlayerId,
                    RouteTollPaymentKeyMode.SharedRegion);
                if (owners.Count > 0)
                {
                    result[routeId] = owners[0];
                }
            }

            return result;
        }

        private int CalculateGoldVoucherCost(MapPath path)
        {
            if (path == null)
            {
                return 0;
            }

            var total = 0;
            var paidKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                var routeId = path.RouteIds[i];
                var key = routeTollService.GetRoutePaymentKey(
                    routeId,
                    RouteTollPaymentKeyMode.SharedRegion);
                if (paidKeys.Add(key) &&
                    routeTollService.IsPaymentRequired(
                        context.CurrentState,
                        routeId,
                        context.LocalPlayerId,
                        RouteTollPaymentKeyMode.SharedRegion))
                {
                    total += ExplorationService.RouteCostGoldVoucher;
                }
            }

            return total;
        }

        private bool ShouldPromptForPathChoice(IReadOnlyList<MapPath> paths)
        {
            if (paths == null || paths.Count <= 1)
            {
                return false;
            }

            var signatures = new HashSet<string>(StringComparer.Ordinal);
            var hasOpponentToll = false;
            for (var i = 0; i < paths.Count; i++)
            {
                var signature = BuildOpponentRecipientSignature(paths[i]);
                if (!string.IsNullOrEmpty(signature))
                {
                    hasOpponentToll = true;
                }

                signatures.Add(signature);
            }

            return hasOpponentToll && signatures.Count > 1;
        }

        private string BuildOpponentRecipientSignature(MapPath path)
        {
            if (path == null)
            {
                return string.Empty;
            }

            var signatures = new List<string>();
            var paidKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                var routeId = path.RouteIds[i];
                var key = routeTollService.GetRoutePaymentKey(
                    routeId,
                    RouteTollPaymentKeyMode.SharedRegion);
                if (!paidKeys.Add(key) ||
                    !routeTollService.IsPaymentRequired(
                        context.CurrentState,
                        routeId,
                        context.LocalPlayerId,
                        RouteTollPaymentKeyMode.SharedRegion))
                {
                    continue;
                }

                var owners = routeTollService.GetOpponentInfluenceOwnersOnPaymentKey(
                    context.CurrentState,
                    key,
                    context.LocalPlayerId,
                    RouteTollPaymentKeyMode.SharedRegion);
                owners.Sort();
                if (owners.Count > 0)
                {
                    signatures.Add(string.Join(",", owners));
                }
            }

            return string.Join("|", signatures);
        }

        private string BuildPathChoiceLabel(MapPath path, int index)
        {
            return "\u8def\u7ebf " + (index + 1) + "\uff1a" + EncodeIds(path.LocationIds) +
                   "\uff1b\u8def\u8d39\uff1a" + BuildPathPaymentLabel(path);
        }

        private string BuildPathPaymentLabel(MapPath path)
        {
            var systemTollCount = 0;
            var recipientNames = new List<string>();
            var paidKeys = new HashSet<string>(StringComparer.Ordinal);
            if (path != null)
            {
                for (var i = 0; i < path.RouteIds.Count; i++)
                {
                    var routeId = path.RouteIds[i];
                    var key = routeTollService.GetRoutePaymentKey(
                        routeId,
                        RouteTollPaymentKeyMode.SharedRegion);
                    if (!paidKeys.Add(key) ||
                        !routeTollService.IsPaymentRequired(
                            context.CurrentState,
                            routeId,
                            context.LocalPlayerId,
                            RouteTollPaymentKeyMode.SharedRegion))
                    {
                        continue;
                    }

                    var owners = routeTollService.GetOpponentInfluenceOwnersOnPaymentKey(
                        context.CurrentState,
                        key,
                        context.LocalPlayerId,
                        RouteTollPaymentKeyMode.SharedRegion);
                    if (owners.Count == 0)
                    {
                        systemTollCount += 1;
                        continue;
                    }

                    owners.Sort();
                    for (var ownerIndex = 0; ownerIndex < owners.Count; ownerIndex++)
                    {
                        var name = view.GetPlayerDisplayName(owners[ownerIndex]);
                        if (!recipientNames.Contains(name))
                        {
                            recipientNames.Add(name);
                        }
                    }
                }
            }

            if (systemTollCount > 0)
            {
                recipientNames.Add("\u7cfb\u7edf");
            }

            return recipientNames.Count == 0 ? "\u65e0" : string.Join("\u3001", recipientNames);
        }

        private int PresentInfluenceTargets()
        {
            selectableInfluenceSlotIds.Clear();
            var card = GetPendingEventCardForTargetSelection();
            if (card == null || !influenceTargetSelection.IsSelecting)
            {
                view.ClearHighlights();
                return 0;
            }

            var allSlotIds = BuildAllInfluenceSlotIds();
            for (var i = 0; i < allSlotIds.Count; i++)
            {
                var slotId = allSlotIds[i];
                if (!influenceTargetSelection.ContainsSlot(slotId) &&
                    CanCompleteEventInfluenceSelection(card, slotId, allSlotIds))
                {
                    selectableInfluenceSlotIds.Add(slotId);
                }
            }

            var highlights = new List<WorkflowHighlight>();
            foreach (var slotId in selectableInfluenceSlotIds)
            {
                highlights.Add(new WorkflowHighlight(
                    WorkflowHighlightTargetKind.InfluenceSlot,
                    slotId,
                    WorkflowHighlightSemantic.EventInfluenceTarget));
            }

            view.SetHighlights(highlights);
            return highlights.Count;
        }

        private bool CanCompleteEventInfluenceSelection(
            EventCardDefinition card,
            string candidateSlotId,
            IReadOnlyList<string> allSlotIds)
        {
            var choiceIndex = influenceTargetSelection.ChoiceIndex;
            if (choiceIndex < 0 || choiceIndex >= card.ChoicePendingEffects.Count)
            {
                return false;
            }

            var requiredCount = EventInfluenceTargetSelectionController.GetRequiredSlotCount(
                card,
                choiceIndex);
            var selected = new List<string>(influenceTargetSelection.SelectedSlotIds);
            selected.Add(candidateSlotId);
            if (selected.Count > requiredCount)
            {
                return false;
            }

            var reserved = BuildReservedInfluenceSlots();
            return CanCompleteInfluenceSlots(
                card.ChoicePendingEffects[choiceIndex],
                GetEventOriginLocationId(),
                selected,
                allSlotIds,
                reserved,
                requiredCount);
        }

        private bool CanCompleteInfluenceSlots(
            IReadOnlyList<EventEffect> effects,
            string originLocationId,
            List<string> selected,
            IReadOnlyList<string> allSlotIds,
            IReadOnlyList<string> reservedSlotIds,
            int requiredCount)
        {
            if (selected.Count >= requiredCount)
            {
                return eventEffectResolver.Validate(
                    context.CurrentState,
                    context.LocalPlayerId,
                    effects,
                    originLocationId,
                    selected,
                    reservedSlotIds).IsValid;
            }

            for (var i = 0; i < allSlotIds.Count; i++)
            {
                var slotId = allSlotIds[i];
                if (selected.Contains(slotId) || Contains(reservedSlotIds, slotId))
                {
                    continue;
                }

                selected.Add(slotId);
                if (CanCompleteInfluenceSlots(
                        effects,
                        originLocationId,
                        selected,
                        allSlotIds,
                        reservedSlotIds,
                        requiredCount))
                {
                    selected.RemoveAt(selected.Count - 1);
                    return true;
                }

                selected.RemoveAt(selected.Count - 1);
            }

            return false;
        }

        private List<string> BuildAllInfluenceSlotIds()
        {
            var result = new List<string>();
            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var location = mapQuery.Map.Locations[i];
                for (var slotIndex = 0; slotIndex < location.InfluenceSlotCount; slotIndex++)
                {
                    result.Add(InfluenceService.GetLocationSlotId(location.LocationId, slotIndex));
                }
            }

            for (var i = 0; i < mapQuery.Map.Routes.Count; i++)
            {
                var route = mapQuery.Map.Routes[i];
                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    result.Add(InfluenceService.GetRouteSlotId(route.RouteId, slotIndex));
                }
            }

            return result;
        }

        private IReadOnlyList<string> BuildReservedInfluenceSlots()
        {
            var result = new List<string>();
            if (!pathSelection.HasTarget)
            {
                return result.AsReadOnly();
            }

            var originLocationId = pathSelection.TargetLocationId;
            MapLocationDefinition location;
            try
            {
                location = mapQuery.GetLocation(originLocationId);
            }
            catch (ArgumentException)
            {
                return result.AsReadOnly();
            }

            for (var i = 0; i < location.InfluenceSlotCount; i++)
            {
                var slotId = InfluenceService.GetLocationSlotId(originLocationId, i);
                if (influenceService.FindInfluence(context.CurrentState, slotId) == null)
                {
                    result.Add(slotId);
                    break;
                }
            }

            return result.AsReadOnly();
        }

        private EventCardDefinition GetPendingEventCardForTargetSelection()
        {
            var pendingChoice = GetPendingChoice();
            if (pendingChoice != null)
            {
                return EventCardDatabase.Get(pendingChoice.CardId);
            }

            return PeekExploreEventCard(pathSelection.TargetLocationId) ?? optionSelection.PendingEventCard;
        }

        private EventCardDefinition PeekExploreEventCard(string locationId)
        {
            var state = context.CurrentState;
            if (state == null || string.IsNullOrEmpty(locationId))
            {
                return null;
            }

            var color = StaticMapDefinitions.GetEventColor(locationId);
            var cardId = new EventDeckService().Peek(state.Decks, color);
            return EventCardDatabase.Get(cardId);
        }

        private string GetEventOriginLocationId()
        {
            var pendingChoice = GetPendingChoice();
            return pendingChoice == null ? pathSelection.TargetLocationId : pendingChoice.TargetId;
        }

        private PendingCardChoiceView GetPendingChoice()
        {
            return CardFlowStateAdapter.GetPendingChoiceView(context.CurrentState);
        }

        private string BuildEventCardMetadataLabel(EventCardDefinition card)
        {
            var targetId = pathSelection.TargetLocationId;
            var pendingChoice = GetPendingChoice();
            if (string.IsNullOrEmpty(targetId) && pendingChoice != null)
            {
                targetId = pendingChoice.TargetId;
            }

            var cardInfo = card == null
                ? string.Empty
                : GetEventColorDisplayName(card.Color) + "    " +
                  GetResourceTypeDisplayName(card.ResourceType) + " * " + card.ResourceAmount;
            return string.IsNullOrEmpty(targetId)
                ? cardInfo
                : "\u6240\u5c5e\u8d44\u6e90\u70b9\uff1a" + targetId + "    " + cardInfo;
        }

        private static string GetEventColorDisplayName(EventColor color)
        {
            switch (color)
            {
                case EventColor.Green:
                    return "\u7eff\u8272\u533a\u57df";
                case EventColor.Yellow:
                    return "\u9ec4\u8272\u533a\u57df";
                case EventColor.Red:
                    return "\u7ea2\u8272\u533a\u57df";
                default:
                    return color.ToString();
            }
        }

        private static string GetResourceTypeDisplayName(ResourceType resourceType)
        {
            switch (resourceType)
            {
                case ResourceType.Originium:
                    return "\u6e90\u5ca9";
                case ResourceType.OriginiumShard:
                    return "\u6e90\u77f3\u788e\u7247";
                case ResourceType.Iron:
                    return "\u5f02\u94c1";
                case ResourceType.PureOriginium:
                    return "\u81f3\u7eaf\u6e90\u77f3";
                case ResourceType.GoldVoucher:
                    return "\u91d1\u5238";
                default:
                    return resourceType.ToString();
            }
        }

        private void ClearWorkflowState()
        {
            pathSelection.Clear();
            paymentSelection.Clear();
            optionSelection.Clear();
            influenceTargetSelection.Clear();
            selectableInfluenceSlotIds.Clear();
            lastInfluenceInputFrame = -1;
        }

        private void SetMode(InteractionMode mode)
        {
            currentMode = mode;
            view.SetInteractionMode(mode);
        }

        private bool TryAcceptInfluenceInputFrame(int inputFrame)
        {
            if (lastInfluenceInputFrame == inputFrame)
            {
                return false;
            }

            lastInfluenceInputFrame = inputFrame;
            return true;
        }

        private int NextSyntheticInputFrame()
        {
            var result = syntheticInputFrame;
            syntheticInputFrame += 1;
            return result;
        }

        private static bool RouteTouchesLocation(MapRouteDefinition route, string locationId)
        {
            if (route == null || string.IsNullOrEmpty(locationId))
            {
                return false;
            }

            return route.FromLocationId == locationId ||
                   route.ToLocationId == locationId ||
                   (route.CoveredLocationIds != null && route.CoveredLocationIds.Contains(locationId));
        }

        private static bool Contains(IReadOnlyList<string> values, string value)
        {
            if (values == null)
            {
                return false;
            }

            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] == value)
                {
                    return true;
                }
            }

            return false;
        }

        private static string EncodeIds(IReadOnlyList<string> ids)
        {
            return ids == null || ids.Count == 0 ? string.Empty : string.Join(",", ids);
        }
    }
}
