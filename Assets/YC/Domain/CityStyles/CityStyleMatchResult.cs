using System.Collections.Generic;
using YC.Domain.Commands;

namespace YC.Domain.CityStyles
{
    public sealed class CityStyleMatchResult
    {
        private CityStyleMatchResult(bool succeeded, ValidationResult validation)
        {
            Succeeded = succeeded;
            Validation = validation;
            UsedFacilityIds = new List<string>();
            UsedCityBoardSlotIndexes = new List<int>();
        }

        public bool Succeeded { get; private set; }
        public ValidationResult Validation { get; private set; }
        public List<string> UsedFacilityIds { get; private set; }
        public List<int> UsedCityBoardSlotIndexes { get; private set; }
        public int RotationDegrees { get; private set; }

        public static CityStyleMatchResult Success(
            IEnumerable<CityStyleFacilityCandidate> facilities,
            int rotationDegrees = 0)
        {
            var result = new CityStyleMatchResult(true, ValidationResult.Success);
            result.RotationDegrees = rotationDegrees;
            if (facilities == null)
            {
                return result;
            }

            foreach (var facility in facilities)
            {
                result.UsedFacilityIds.Add(facility.FacilityId);
                result.UsedCityBoardSlotIndexes.Add(facility.CityBoardSlotIndex);
            }

            return result;
        }

        public static CityStyleMatchResult Failure(ValidationResult validation)
        {
            return new CityStyleMatchResult(false, validation);
        }
    }
}
