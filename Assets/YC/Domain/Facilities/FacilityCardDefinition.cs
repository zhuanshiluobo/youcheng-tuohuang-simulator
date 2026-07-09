using System;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    [Serializable]
    public sealed class FacilityCardDefinition
    {
        public string FacilityId = string.Empty;
        public string Name = string.Empty;
        public string Color = string.Empty;
        public int Score;
        public ResourceSet ResourceCost = new ResourceSet();
        public int GoldVoucherCost;
        public bool Unique;
        public string UniqueGroupId = string.Empty;
        public string EffectType = string.Empty;
        public string Description = string.Empty;
        public string EffectText = string.Empty;
        public string ManifestId = string.Empty;
        public bool ReserveOnly;
        public ResourceSet OnBuiltReward = new ResourceSet();
        public string ImageRelativePath = string.Empty;
    }
}
