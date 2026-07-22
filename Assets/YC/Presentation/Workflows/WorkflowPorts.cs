using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Rules;
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

        void ShowBuildFacilityDraft(BuildFacilityDraftViewModel viewModel);

        void HideBuildFacilityDraft();

        void ShowCityStyleOptions(CityStyleOptionsViewModel viewModel);
    }

    public sealed class BuildFacilityDraftViewModel
    {
        public BuildFacilityDraftViewModel(
            BuildFacilityDraftPhase phase,
            IReadOnlyList<BuildFacilityOptionQueryResult> options,
            BuildFacilityOptionQueryResult selectedOption,
            FacilityCardDefinition facility,
            int cityBoardSlotIndex,
            string paymentMode,
            string errorMessage,
            IReadOnlyList<int> legalSlotIndexes,
            Action<string> beginDrag,
            Action beginGhostDrag,
            Action<int> drop,
            Action rejectDrop,
            Action escape,
            Action<string> selectPayment,
            Action back,
            Action confirm,
            Action cancel)
        {
            Phase = phase;
            Options = options ?? new List<BuildFacilityOptionQueryResult>().AsReadOnly();
            SelectedOption = selectedOption;
            Facility = facility;
            CityBoardSlotIndex = cityBoardSlotIndex;
            PaymentMode = paymentMode ?? string.Empty;
            ErrorMessage = errorMessage ?? string.Empty;
            LegalSlotIndexes = legalSlotIndexes ?? new List<int>().AsReadOnly();
            BeginDrag = beginDrag;
            BeginGhostDrag = beginGhostDrag;
            Drop = drop;
            RejectDrop = rejectDrop;
            Escape = escape;
            SelectPayment = selectPayment;
            Back = back;
            Confirm = confirm;
            Cancel = cancel;
        }

        public BuildFacilityDraftPhase Phase { get; private set; }
        public IReadOnlyList<BuildFacilityOptionQueryResult> Options { get; private set; }
        public BuildFacilityOptionQueryResult SelectedOption { get; private set; }
        public FacilityCardDefinition Facility { get; private set; }
        public int CityBoardSlotIndex { get; private set; }
        public string PaymentMode { get; private set; }
        public string ErrorMessage { get; private set; }
        public IReadOnlyList<int> LegalSlotIndexes { get; private set; }
        public Action<string> BeginDrag { get; private set; }
        public Action BeginGhostDrag { get; private set; }
        public Action<int> Drop { get; private set; }
        public Action RejectDrop { get; private set; }
        public Action Escape { get; private set; }
        public Action<string> SelectPayment { get; private set; }
        public Action Back { get; private set; }
        public Action Confirm { get; private set; }
        public Action Cancel { get; private set; }
    }

    public sealed class BuildFacilityAvailabilityViewModel
    {
        public BuildFacilityAvailabilityViewModel(
            IReadOnlyList<string> draggableFacilityIds,
            IReadOnlyList<int> legalSlotIndexes,
            bool usesSpecialBuild,
            string unavailableMessage)
        {
            DraggableFacilityIds = draggableFacilityIds ?? new List<string>().AsReadOnly();
            LegalSlotIndexes = legalSlotIndexes ?? new List<int>().AsReadOnly();
            UsesSpecialBuild = usesSpecialBuild;
            UnavailableMessage = unavailableMessage ?? string.Empty;
        }

        public IReadOnlyList<string> DraggableFacilityIds { get; private set; }

        public IReadOnlyList<int> LegalSlotIndexes { get; private set; }

        public bool UsesSpecialBuild { get; private set; }

        public string UnavailableMessage { get; private set; }
    }

    public sealed class CityStyleOptionsViewModel
    {
        public CityStyleOptionsViewModel(
            IReadOnlyList<CityStyleOptionViewModel> options,
            Action<string> select,
            Action cancel)
            : this(
                options,
                null,
                null,
                string.Empty,
                null,
                null,
                select,
                cancel)
        {
        }

        public CityStyleOptionsViewModel(
            IReadOnlyList<CityStyleOptionViewModel> options,
            IReadOnlyList<CityBoardSlotViewModel> cityBoardSlots,
            IReadOnlyList<CityStyleMarkerViewModel> cityStyleMarkers,
            string initialCityStyleId,
            Func<string, IReadOnlyList<int>, CityStyleSelectionValidationViewModel> validateSelection,
            Func<string, IReadOnlyList<int>, bool> confirmSelection,
            Action<string> select,
            Action cancel,
            Func<string, string, int, int, bool> tryUseSpecialAction = null)
        {
            Options = options ?? new List<CityStyleOptionViewModel>().AsReadOnly();
            CityBoardSlots = cityBoardSlots ?? new List<CityBoardSlotViewModel>().AsReadOnly();
            CityStyleMarkers = cityStyleMarkers ?? new List<CityStyleMarkerViewModel>().AsReadOnly();
            InitialCityStyleId = initialCityStyleId ?? string.Empty;
            ValidateSelection = validateSelection;
            ConfirmSelection = confirmSelection;
            Select = select;
            Cancel = cancel;
            TryUseSpecialAction = tryUseSpecialAction;
        }

        public IReadOnlyList<CityStyleOptionViewModel> Options { get; private set; }

        public IReadOnlyList<CityBoardSlotViewModel> CityBoardSlots { get; private set; }

        public IReadOnlyList<CityStyleMarkerViewModel> CityStyleMarkers { get; private set; }

        public string InitialCityStyleId { get; private set; }

        public Func<string, IReadOnlyList<int>, CityStyleSelectionValidationViewModel> ValidateSelection { get; private set; }

        public Func<string, IReadOnlyList<int>, bool> ConfirmSelection { get; private set; }

        public Action<string> Select { get; private set; }

        public Action Cancel { get; private set; }

        public Func<string, string, int, int, bool> TryUseSpecialAction { get; private set; }
    }

    public sealed class CityBoardSlotViewModel
    {
        public CityBoardSlotViewModel(int slotIndex, string facilityId, bool used)
        {
            SlotIndex = slotIndex;
            FacilityId = facilityId ?? string.Empty;
            Used = used;
        }

        public int SlotIndex { get; private set; }

        public string FacilityId { get; private set; }

        public bool Used { get; private set; }
    }

    public sealed class CityStyleMarkerViewModel
    {
        public CityStyleMarkerViewModel(
            string cityStyleId,
            int playerId,
            PlayerColor playerColor,
            string markerArea)
            : this(
                cityStyleId,
                playerId,
                playerColor,
                markerArea,
                string.Empty,
                string.Empty,
                false,
                string.Empty,
                string.Empty)
        {
        }

        public CityStyleMarkerViewModel(
            string cityStyleId,
            int playerId,
            PlayerColor playerColor,
            string markerArea,
            string markerId,
            string specialActionId,
            bool canDragForSpecialAction,
            string legalDropArea,
            string specialActionDisabledReason,
            string specialActionWarning = "",
            int maximumOriginiumPayment = 0,
            int maximumIronPayment = 0)
        {
            CityStyleId = cityStyleId ?? string.Empty;
            PlayerId = playerId;
            PlayerColor = playerColor;
            MarkerArea = markerArea ?? string.Empty;
            MarkerId = markerId ?? string.Empty;
            SpecialActionId = specialActionId ?? string.Empty;
            CanDragForSpecialAction = canDragForSpecialAction;
            LegalDropArea = legalDropArea ?? string.Empty;
            SpecialActionDisabledReason = specialActionDisabledReason ?? string.Empty;
            SpecialActionWarning = specialActionWarning ?? string.Empty;
            MaximumOriginiumPayment = Math.Max(0, maximumOriginiumPayment);
            MaximumIronPayment = Math.Max(0, maximumIronPayment);
        }

        public string CityStyleId { get; private set; }

        public int PlayerId { get; private set; }

        public PlayerColor PlayerColor { get; private set; }

        public string MarkerArea { get; private set; }

        public string MarkerId { get; private set; }

        public string SpecialActionId { get; private set; }

        public bool CanDragForSpecialAction { get; private set; }

        public string LegalDropArea { get; private set; }

        public string SpecialActionDisabledReason { get; private set; }

        public string SpecialActionWarning { get; private set; }

        public int MaximumOriginiumPayment { get; private set; }

        public int MaximumIronPayment { get; private set; }
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
