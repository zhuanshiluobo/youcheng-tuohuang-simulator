using System;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    [Serializable]
    public sealed class FacilityCardDefinition
    {
        public string FacilityId = string.Empty;
        public string Name = string.Empty;
        public int Score;
        public ResourceSet ResourceCost = new ResourceSet();
        public int GoldVoucherCost;
        public bool Unique;
        public string EffectType = string.Empty;
        public ResourceSet OnBuiltReward = new ResourceSet();
    }
}
