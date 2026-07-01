using YC.Domain.Commands;

namespace YC.Domain.Facilities
{
    public sealed class BuildFacilityResult
    {
        private BuildFacilityResult(bool succeeded, ValidationResult validation, FacilityCardDefinition facility, int cityBoardSlotIndex, string paymentMode)
        {
            Succeeded = succeeded;
            Validation = validation;
            Facility = facility;
            CityBoardSlotIndex = cityBoardSlotIndex;
            PaymentMode = paymentMode ?? string.Empty;
        }

        public bool Succeeded { get; private set; }
        public ValidationResult Validation { get; private set; }
        public FacilityCardDefinition Facility { get; private set; }
        public int CityBoardSlotIndex { get; private set; }
        public string PaymentMode { get; private set; }

        public static BuildFacilityResult Success(FacilityCardDefinition facility, int cityBoardSlotIndex, string paymentMode)
        {
            return new BuildFacilityResult(true, ValidationResult.Success, facility, cityBoardSlotIndex, paymentMode);
        }

        public static BuildFacilityResult Failure(ValidationResult validation)
        {
            return new BuildFacilityResult(false, validation, null, -1, string.Empty);
        }
    }
}
