using YC.Domain.Facilities;

namespace YC.Domain.CityStyles
{
    public sealed class CityStyleFacilityCandidate
    {
        public string FacilityId = string.Empty;
        public int CityBoardSlotIndex = -1;
        public FacilityCardDefinition Definition;
    }
}
