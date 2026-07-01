using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.State;

namespace YC.Domain.Harvest
{
    [Serializable]
    public sealed class ResourceCollectionResult
    {
        public bool Succeeded { get; private set; }
        public ValidationResult Validation { get; private set; }
        public List<string> LocationIds { get; private set; }
        public List<ExplorationTravelPayment> Payments { get; private set; }
        public ResourceSet Reward { get; private set; }

        private ResourceCollectionResult(
            bool succeeded,
            ValidationResult validation,
            List<string> locationIds,
            List<ExplorationTravelPayment> payments,
            ResourceSet reward)
        {
            Succeeded = succeeded;
            Validation = validation;
            LocationIds = locationIds ?? new List<string>();
            Payments = payments ?? new List<ExplorationTravelPayment>();
            Reward = reward ?? new ResourceSet();
        }

        public static ResourceCollectionResult Failure(ValidationResult validation)
        {
            return new ResourceCollectionResult(false, validation, null, null, null);
        }

        public static ResourceCollectionResult Success(
            List<string> locationIds,
            List<ExplorationTravelPayment> payments,
            ResourceSet reward)
        {
            return new ResourceCollectionResult(
                true,
                ValidationResult.Success,
                locationIds,
                payments,
                reward);
        }
    }
}
