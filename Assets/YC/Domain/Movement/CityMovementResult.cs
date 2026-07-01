using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Movement
{
    public sealed class CityMovementResult
    {
        public bool Succeeded { get; private set; }
        public ValidationResult Validation { get; private set; }
        public string SourceLocationId { get; private set; }
        public string TargetLocationId { get; private set; }
        public string RouteId { get; private set; }
        public int RemovedInfluenceCount { get; private set; }
        public InfluenceOperationResult SourceInfluencePlacement { get; private set; }
        public bool HasEventCard { get; private set; }
        public string EventCardId { get; private set; }
        public EventColor EventColor { get; private set; }
        public int SelectedOptionIndex { get; private set; }
        public ResourceSet EventReward { get; private set; }

        private CityMovementResult(
            bool succeeded,
            ValidationResult validation,
            string sourceLocationId,
            string targetLocationId,
            string routeId,
            int removedInfluenceCount,
            InfluenceOperationResult sourceInfluencePlacement,
            bool hasEventCard,
            string eventCardId,
            EventColor eventColor,
            int selectedOptionIndex,
            ResourceSet eventReward)
        {
            Succeeded = succeeded;
            Validation = validation;
            SourceLocationId = sourceLocationId;
            TargetLocationId = targetLocationId;
            RouteId = routeId;
            RemovedInfluenceCount = removedInfluenceCount;
            SourceInfluencePlacement = sourceInfluencePlacement;
            HasEventCard = hasEventCard;
            EventCardId = eventCardId;
            EventColor = eventColor;
            SelectedOptionIndex = selectedOptionIndex;
            EventReward = eventReward;
        }

        public static CityMovementResult Success(
            string sourceLocationId,
            string targetLocationId,
            string routeId,
            int removedInfluenceCount,
            InfluenceOperationResult sourceInfluencePlacement,
            bool hasEventCard = false,
            string eventCardId = "",
            EventColor eventColor = EventColor.Green,
            int selectedOptionIndex = -1,
            ResourceSet eventReward = null)
        {
            return new CityMovementResult(
                true,
                ValidationResult.Success,
                sourceLocationId,
                targetLocationId,
                routeId,
                removedInfluenceCount,
                sourceInfluencePlacement,
                hasEventCard,
                eventCardId,
                eventColor,
                selectedOptionIndex,
                eventReward);
        }

        public static CityMovementResult Failure(ValidationResult validation)
        {
            return new CityMovementResult(false, validation, string.Empty, string.Empty, string.Empty, 0, null, false, string.Empty, EventColor.Green, -1, null);
        }
    }
}
