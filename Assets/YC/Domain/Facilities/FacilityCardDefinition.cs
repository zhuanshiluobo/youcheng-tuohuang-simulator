using System;
using System.Collections.Generic;
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
        public bool HasEntryEffect;
        public List<string> Keywords = new List<string>();
        public string EffectId = string.Empty;
        public string EffectType = string.Empty;
        public string Description = string.Empty;
        public string EffectText = string.Empty;
        public string ManifestId = string.Empty;
        public bool ReserveOnly;
        public ResourceSet OnBuiltReward = new ResourceSet();
    }

    public static class FacilityCardKeywords
    {
        public const string Entry = "entry";
        public const string Unique = "unique";
        public const string Free = "free";
    }

    public static class FacilityCardEffectIds
    {
        public const string UniqueOnly = "unique_only";
        public const string CopyAdjacentEntryEffect = "copy_adjacent_entry_effect";
        public const string ClaimStartMarkerAtCleanup = "claim_start_marker_at_cleanup";
        public const string BuildAdditionalFacility = "build_additional_facility";
        public const string BuildExtensionHub = "build_extension_hub";
        public const string GainOriginiumShardSix = "gain_originium_shard_6";
        public const string GainGoldPerCoreAdjacentFacility = "gain_gold_per_core_adjacent_facility";
        public const string GainIronFour = "gain_iron_4";
        public const string SellResources = "sell_resources";
        public const string DiscountOriginiumByFacilityColor = "discount_originium_by_facility_color";
        public const string FreeCityMoveAndDeployRouteInfluence = "free_city_move_and_deploy_route_influence";
        public const string GainOriginiumSeven = "gain_originium_7";
        public const string ChooseFiveBasicResources = "choose_five_basic_resources";
        public const string ReplaceOneInfluence = "replace_one_influence";
        public const string DeployTwoInfluences = "deploy_two_influences";
        public const string RemoveThenDispatchOrExplore = "remove_then_dispatch_or_explore";
        public const string SetupCoreCommandTower = "setup_core_command_tower";
        public const string ReserveExtensionHub = "reserve_extension_hub";
        public const string EnterpriseOffice = "enterprise_office";
    }
}
