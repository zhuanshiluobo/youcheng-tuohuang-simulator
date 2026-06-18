using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Exploration
{
    public sealed class ExplorationResult
    {
        private ExplorationResult(
            bool succeeded,
            ValidationResult validation,
            string sourceLocationId,
            string targetLocationId,
            string eventCardId,
            EventColor eventColor,
            int selectedOptionIndex,
            ResourceSet reward,
            IReadOnlyList<ExplorationTravelPayment> payments,
            InfluenceOperationResult influencePlacement)
        {
            Succeeded = succeeded;
            Validation = validation;
            SourceLocationId = sourceLocationId ?? string.Empty;
            TargetLocationId = targetLocationId ?? string.Empty;
            EventCardId = eventCardId ?? string.Empty;
            EventColor = eventColor;
            SelectedOptionIndex = selectedOptionIndex;
            Reward = reward;
            Payments = payments ?? new List<ExplorationTravelPayment>().AsReadOnly();
            InfluencePlacement = influencePlacement;
        }

        public bool Succeeded { get; private set; }
        public ValidationResult Validation { get; private set; }
        public string SourceLocationId { get; private set; }
        public string TargetLocationId { get; private set; }
        public string EventCardId { get; private set; }
        public EventColor EventColor { get; private set; }
        public int SelectedOptionIndex { get; private set; }
        public ResourceSet Reward { get; private set; }
        public IReadOnlyList<ExplorationTravelPayment> Payments { get; private set; }
        public InfluenceOperationResult InfluencePlacement { get; private set; }

        public static ExplorationResult Success(
            string sourceLocationId,
            string targetLocationId,
            string eventCardId,
            EventColor eventColor,
            int selectedOptionIndex,
            ResourceSet reward,
            IReadOnlyList<ExplorationTravelPayment> payments,
            InfluenceOperationResult influencePlacement)
        {
            return new ExplorationResult(
                true,
                ValidationResult.Success,
                sourceLocationId,
                targetLocationId,
                eventCardId,
                eventColor,
                selectedOptionIndex,
                reward,
                payments,
                influencePlacement);
        }

        public static ExplorationResult Failure(ValidationResult validation)
        {
            return new ExplorationResult(
                false,
                validation,
                string.Empty,
                string.Empty,
                string.Empty,
                EventColor.Green,
                -1,
                null,
                new List<ExplorationTravelPayment>().AsReadOnly(),
                null);
        }
    }
}
