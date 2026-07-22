using System;
using YC.Domain.State;

namespace YC.Domain.SpecialActions
{
    public enum SpecialActionEffectKind
    {
        DeployInfluence,
        ReplaceInfluence,
        CompositePowerMove,
        GrantExtraMainActions,
        ConsecutiveFreeMoves
    }

    [Serializable]
    public sealed class SpecialActionDefinition
    {
        public string SpecialActionId = string.Empty;
        public string CityStyleId = string.Empty;
        public string Name = string.Empty;
        public string Description = string.Empty;
        public int Level;
        public SpecialActionEffectKind EffectKind;
        public ResourceSet FixedCost = new ResourceSet();
        public int FlexibleOriginiumAndIronCost;
        public int MaximumTargetCount;
        public int FreeMoveCount;
        public int ExtraMainActionCount;
        public bool LocksCharacterCard;

        public SpecialActionDefinition Clone()
        {
            return new SpecialActionDefinition
            {
                SpecialActionId = SpecialActionId,
                CityStyleId = CityStyleId,
                Name = Name,
                Description = Description,
                Level = Level,
                EffectKind = EffectKind,
                FixedCost = FixedCost == null ? new ResourceSet() : FixedCost.Clone(),
                FlexibleOriginiumAndIronCost = FlexibleOriginiumAndIronCost,
                MaximumTargetCount = MaximumTargetCount,
                FreeMoveCount = FreeMoveCount,
                ExtraMainActionCount = ExtraMainActionCount,
                LocksCharacterCard = LocksCharacterCard
            };
        }
    }
}
