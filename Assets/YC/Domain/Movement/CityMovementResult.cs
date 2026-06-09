using YC.Domain.Commands;
using YC.Domain.Influence;

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

        private CityMovementResult(
            bool succeeded,
            ValidationResult validation,
            string sourceLocationId,
            string targetLocationId,
            string routeId,
            int removedInfluenceCount,
            InfluenceOperationResult sourceInfluencePlacement)
        {
            Succeeded = succeeded;
            Validation = validation;
            SourceLocationId = sourceLocationId;
            TargetLocationId = targetLocationId;
            RouteId = routeId;
            RemovedInfluenceCount = removedInfluenceCount;
            SourceInfluencePlacement = sourceInfluencePlacement;
        }

        public static CityMovementResult Success(
            string sourceLocationId,
            string targetLocationId,
            string routeId,
            int removedInfluenceCount,
            InfluenceOperationResult sourceInfluencePlacement)
        {
            return new CityMovementResult(
                true,
                ValidationResult.Success,
                sourceLocationId,
                targetLocationId,
                routeId,
                removedInfluenceCount,
                sourceInfluencePlacement);
        }

        public static CityMovementResult Failure(ValidationResult validation)
        {
            return new CityMovementResult(false, validation, string.Empty, string.Empty, string.Empty, 0, null);
        }
    }
}
