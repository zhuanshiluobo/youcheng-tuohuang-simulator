namespace YC.Domain.Cards
{
    public enum CharacterCardEffectKind
    {
        Unsupported,
        CannotTradeChannel,
        CannotRequisition,
        LiskarmSecurityProtocol,
        LiskarmControlPosition,
        ElysiumLogistics,
        ElysiumNavigation,
        TexasSpecialDelivery,
        TexasRemoveAndDoubleMove,
        TinManEstablishPrestige,
        TinManDeepPlanning
    }

    public sealed class CharacterCardDefinition
    {
        public string CardId = string.Empty;
        public string TemplateId = string.Empty;
        public string Name = string.Empty;
        public CharacterCardEffectKind StrategyEffect;
        public CharacterCardEffectKind TacticEffect;
    }

    public static class CharacterEffectModes
    {
        public const string Strategy = "strategy";
        public const string Tactic = "tactic";
        public const string Both = "both";
    }

    public static class CharacterEffectOrders
    {
        public const string StrategyFirst = "strategy-first";
        public const string TacticFirst = "tactic-first";
    }

    public static class CharacterEffectParameterKeys
    {
        public const string ResourceType = "resourceType";
        public const string FacilityCardId = "facilityCardId";
        public const string Choice = "choice";
        public const string SourceInfluenceSlotId = "sourceInfluenceSlotId";
        public const string TargetInfluenceSlotId = "targetInfluenceSlotId";
        public const string PlacementSlotId1 = "placementSlotId1";
        public const string PlacementSlotId2 = "placementSlotId2";
        public const string TargetLocationId = "targetLocationId";
        public const string RemovalTargetInfluenceSlotId = "removalTargetInfluenceSlotId";
        public const string MoveSourceSlotId1 = "moveSourceSlotId1";
        public const string MoveTargetSlotId1 = "moveTargetSlotId1";
        public const string MoveSourceSlotId2 = "moveSourceSlotId2";
        public const string MoveTargetSlotId2 = "moveTargetSlotId2";
        public const string TinManPurchasePureOriginium12 = "tinMan.purchasePureOriginium12";
        public const string TinManPurchasePureOriginium15 = "tinMan.purchasePureOriginium15";
        public const string SaleOriginium = "sale.originium";
        public const string SaleOriginiumShard = "sale.originium-shard";
        public const string SaleIron = "sale.iron";
        public const string SalePureOriginium = "sale.pure-originium";
        public const string OfferSecondEffect = "character.offer-second-effect";
    }

    public static class CharacterEffectChoiceIds
    {
        public const string GainGold = "gain-gold";
        public const string MoveInfluence = "move-influence";
        public const string ContinueSecondEffect = "continue-second-effect";
        public const string FinishCharacterUse = "finish-character-use";
    }

    public static class CharacterPendingChoiceTypes
    {
        public const string TinManDiscard = "character.tin-man.discard";
        public const string LiskarmCleanupRemoval = "character.liskarm.cleanup-removal";
        public const string SecondEffectDecision = "character.second-effect.decision";
        public const string SecondEffectExecution = "character.second-effect.execution";
    }
}
