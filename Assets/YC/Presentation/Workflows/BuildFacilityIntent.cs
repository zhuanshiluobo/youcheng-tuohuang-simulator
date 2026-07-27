namespace YC.Presentation.Workflows
{
    public abstract class BuildFacilityIntent
    {
        private BuildFacilityIntent()
        {
        }

        public sealed class BeginDrag : BuildFacilityIntent
        {
            public BeginDrag(string facilityId)
            {
                FacilityId = facilityId ?? string.Empty;
            }

            public string FacilityId { get; private set; }
        }

        public sealed class BeginGhostDrag : BuildFacilityIntent
        {
        }

        public sealed class Drop : BuildFacilityIntent
        {
            public Drop(int cityBoardSlotIndex)
            {
                CityBoardSlotIndex = cityBoardSlotIndex;
            }

            public int CityBoardSlotIndex { get; private set; }
        }

        public sealed class RejectDrop : BuildFacilityIntent
        {
        }

        public sealed class Escape : BuildFacilityIntent
        {
        }

        public sealed class SelectPayment : BuildFacilityIntent
        {
            public SelectPayment(string paymentMode)
            {
                PaymentMode = paymentMode ?? string.Empty;
            }

            public string PaymentMode { get; private set; }
        }

        public sealed class Back : BuildFacilityIntent
        {
        }

        public sealed class Confirm : BuildFacilityIntent
        {
        }

        public sealed class Cancel : BuildFacilityIntent
        {
        }
    }
}
