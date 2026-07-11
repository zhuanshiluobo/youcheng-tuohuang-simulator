using System;
using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public interface IGameplayContext
    {
        GameState CurrentState { get; }

        int LocalPlayerId { get; }
    }

    public interface IWritableGameplayContext : IGameplayContext
    {
        bool ControlsCurrentPlayerLocally { get; }

        void SetLocalPlayerId(int playerId);
    }

    public interface IGameCommandPort
    {
        WorkflowSubmissionResult Submit(GameCommand command);
    }

    public sealed class WorkflowSubmissionResult
    {
        public WorkflowSubmissionResult(CommandResult commandResult, bool appliedLocally)
        {
            CommandResult = commandResult ?? throw new ArgumentNullException(nameof(commandResult));
            AppliedLocally = appliedLocally;
        }

        public CommandResult CommandResult { get; private set; }

        public bool AppliedLocally { get; private set; }
    }

    public interface IInteractionView
    {
        void ShowPrompt(string message);

        void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights);

        void ClearHighlights();
    }

    public interface IResourceCollectionView : IInteractionView
    {
        void ShowRoutePaymentOptions(
            string routeId,
            int cost,
            IReadOnlyList<int> recipientPlayerIds);

        string GetPlayerDisplayName(int playerId);

        void RefreshSelectionView();

        void RefreshFromState();
    }

    public interface IInfluenceActionView : IInteractionView
    {
        void SetInteractionMode(InteractionMode mode);

        void ShowDispatchDecision(DispatchDecisionViewModel viewModel);

        void HideDispatchDecision();

        void RefreshInfluencePreview(
            bool hasPendingFirstMove,
            string firstSourceSlotId,
            string firstTargetSlotId);

        void RefreshActionPanel();

        void CompleteAction(string actionName);
    }

    public interface IExplorationEventView : IInteractionView
    {
        void SetInteractionMode(InteractionMode mode);

        void ShowExplorePathOptions(ExplorePathOptionsViewModel viewModel);

        void ShowExplorePaymentOptions(ExplorePaymentOptionsViewModel viewModel);

        void ShowEventCardOptions(EventCardOptionsViewModel viewModel);

        void CollapseEventOptions();

        void HideEventOptions();

        string GetPlayerDisplayName(int playerId);

        void RefreshActionPanel();

        void RefreshResourceAndInfluence();

        void RefreshFromState();

        void CompleteAction(string actionName);
    }

    public interface ITurnActionView : IInteractionView
    {
        void RefreshFromState();

        void RefreshInformation();

        void RefreshActionPanel();

        void ShowPendingChoice();

        void CompleteMainActionPresentation(string actionName);

        void ShowBuildFacilityOptions(BuildFacilityOptionsViewModel viewModel);

        void ShowCityStyleOptions(CityStyleOptionsViewModel viewModel);
    }

    public sealed class BuildFacilityOptionsViewModel
    {
        public BuildFacilityOptionsViewModel(
            IReadOnlyList<string> facilityIds,
            PlayerState player,
            int cityBoardSlotIndex,
            Action<string, string> select,
            Action cancel)
        {
            FacilityIds = facilityIds ?? new List<string>().AsReadOnly();
            Player = player;
            CityBoardSlotIndex = cityBoardSlotIndex;
            Select = select;
            Cancel = cancel;
        }

        public IReadOnlyList<string> FacilityIds { get; private set; }

        public PlayerState Player { get; private set; }

        public int CityBoardSlotIndex { get; private set; }

        public Action<string, string> Select { get; private set; }

        public Action Cancel { get; private set; }
    }

    public sealed class CityStyleOptionsViewModel
    {
        public CityStyleOptionsViewModel(
            IReadOnlyList<CityStyleOptionViewModel> options,
            Action<string> select,
            Action cancel)
        {
            Options = options ?? new List<CityStyleOptionViewModel>().AsReadOnly();
            Select = select;
            Cancel = cancel;
        }

        public IReadOnlyList<CityStyleOptionViewModel> Options { get; private set; }

        public Action<string> Select { get; private set; }

        public Action Cancel { get; private set; }
    }

    public sealed class ExplorePathOptionsViewModel
    {
        public ExplorePathOptionsViewModel(
            IReadOnlyList<ExplorePathChoice> choices,
            Action<int> selectPath)
        {
            Choices = choices ?? new List<ExplorePathChoice>().AsReadOnly();
            SelectPath = selectPath;
        }

        public IReadOnlyList<ExplorePathChoice> Choices { get; private set; }

        public Action<int> SelectPath { get; private set; }
    }

    public sealed class ExplorePaymentOptionsViewModel
    {
        public ExplorePaymentOptionsViewModel(
            IReadOnlyList<ExplorePaymentChoice> choices,
            IReadOnlyDictionary<string, int> recipientsByRouteId,
            Action<string, int> selectRecipient,
            Action confirm)
        {
            Choices = choices ?? new List<ExplorePaymentChoice>().AsReadOnly();
            RecipientsByRouteId = recipientsByRouteId ??
                                  new Dictionary<string, int>();
            SelectRecipient = selectRecipient;
            Confirm = confirm;
        }

        public IReadOnlyList<ExplorePaymentChoice> Choices { get; private set; }

        public IReadOnlyDictionary<string, int> RecipientsByRouteId { get; private set; }

        public Action<string, int> SelectRecipient { get; private set; }

        public Action Confirm { get; private set; }
    }

    public sealed class EventCardOptionsViewModel
    {
        public EventCardOptionsViewModel(
            EventCardDefinition card,
            string metadataLabel,
            IReadOnlyList<ExplorePaymentChoice> paymentChoices,
            IReadOnlyDictionary<string, int> recipientsByRouteId,
            Action<int> selectChoice,
            Action<string, int> selectRecipient)
        {
            Card = card;
            MetadataLabel = metadataLabel ?? string.Empty;
            PaymentChoices = paymentChoices ?? new List<ExplorePaymentChoice>().AsReadOnly();
            RecipientsByRouteId = recipientsByRouteId ??
                                  new Dictionary<string, int>();
            SelectChoice = selectChoice;
            SelectRecipient = selectRecipient;
        }

        public EventCardDefinition Card { get; private set; }

        public string MetadataLabel { get; private set; }

        public IReadOnlyList<ExplorePaymentChoice> PaymentChoices { get; private set; }

        public IReadOnlyDictionary<string, int> RecipientsByRouteId { get; private set; }

        public Action<int> SelectChoice { get; private set; }

        public Action<string, int> SelectRecipient { get; private set; }
    }

    public sealed class DispatchDecisionViewModel
    {
        public DispatchDecisionViewModel(
            string title,
            string message,
            string continueLabel,
            string finishLabel,
            Action continueAction,
            Action finishAction)
        {
            Title = title ?? string.Empty;
            Message = message ?? string.Empty;
            ContinueLabel = continueLabel ?? string.Empty;
            FinishLabel = finishLabel ?? string.Empty;
            ContinueAction = continueAction;
            FinishAction = finishAction;
        }

        public string Title { get; private set; }

        public string Message { get; private set; }

        public string ContinueLabel { get; private set; }

        public string FinishLabel { get; private set; }

        public Action ContinueAction { get; private set; }

        public Action FinishAction { get; private set; }
    }

    public enum WorkflowHighlightTargetKind
    {
        Location,
        Route,
        InfluenceSlot
    }

    public enum WorkflowHighlightSemantic
    {
        MoveTarget,
        ExploreTarget,
        DeployTarget,
        DispatchSource,
        DispatchTarget,
        EventInfluenceTarget,
        CollectionCandidate,
        CollectionSelected,
        CollectionPaymentRequired,
        InitialPlacement
    }

    public sealed class WorkflowHighlight
    {
        public WorkflowHighlight(
            WorkflowHighlightTargetKind targetKind,
            string targetId,
            WorkflowHighlightSemantic semantic)
        {
            TargetKind = targetKind;
            TargetId = targetId ?? string.Empty;
            Semantic = semantic;
        }

        public WorkflowHighlightTargetKind TargetKind { get; private set; }

        public string TargetId { get; private set; }

        public WorkflowHighlightSemantic Semantic { get; private set; }
    }
}
