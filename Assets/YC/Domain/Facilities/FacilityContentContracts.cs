using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    /// <summary>
    /// 设施内容的行为族。这里描述可复用的规则形状，不描述某一张设施牌。
    /// 牌面 ID 只在内容目录中出现，运行时路由使用行为族和稳定实例 ID。
    /// </summary>
    public static class FacilityBehaviorFamilies
    {
        public const string None = "none";
        public const string CleanupStartMarker = "cleanup_start_marker";
        public const string UniqueOnly = "unique_only";
        public const string ReplayAdjacentEntry = "replay_adjacent_entry";
        public const string RegisterCleanupMarker = "register_cleanup_marker";
        public const string AdditionalBuild = "additional_build";
        public const string ExtensionHubBuild = "extension_hub_build";
        public const string ResourceReward = "resource_reward";
        public const string GoldPerAdjacentCore = "gold_per_adjacent_core";
        public const string ResourceSale = "resource_sale";
        public const string OriginiumDiscount = "originium_discount";
        public const string FreeMoveAndRouteInfluence = "free_move_route_influence";
        public const string ResourceChoice = "resource_choice";
        public const string ReplaceOrDeployInfluence = "replace_or_deploy_influence";
        public const string DeployInfluences = "deploy_influences";
        public const string RemoveAndDispatchOrExplore = "remove_dispatch_or_explore";
        public const string Setup = "setup";
        public const string Reserve = "reserve";
        public const string Enterprise = "enterprise";
    }

    /// <summary>
    /// 将设施目录中的 effectId 映射到行为族。该表是内容协议，不允许按具体
    /// facilityId 分支；新增同形状设施只需复用已有 effectId。
    /// </summary>
    public static class FacilityBehaviorFamilyResolver
    {
        public static string Resolve(FacilityCardDefinition definition)
        {
            return definition == null ? FacilityBehaviorFamilies.None : Resolve(definition.EffectId);
        }

        public static string Resolve(string effectId)
        {
            switch (effectId ?? string.Empty)
            {
                case FacilityCardEffectIds.UniqueOnly: return FacilityBehaviorFamilies.UniqueOnly;
                case FacilityCardEffectIds.CopyAdjacentEntryEffect: return FacilityBehaviorFamilies.ReplayAdjacentEntry;
                case FacilityCardEffectIds.ClaimStartMarkerAtCleanup: return FacilityBehaviorFamilies.RegisterCleanupMarker;
                case FacilityCardEffectIds.BuildAdditionalFacility: return FacilityBehaviorFamilies.AdditionalBuild;
                case FacilityCardEffectIds.BuildExtensionHub: return FacilityBehaviorFamilies.ExtensionHubBuild;
                case FacilityCardEffectIds.GainOriginiumShardSix:
                case FacilityCardEffectIds.GainIronFour:
                case FacilityCardEffectIds.GainOriginiumSeven: return FacilityBehaviorFamilies.ResourceReward;
                case FacilityCardEffectIds.GainGoldPerCoreAdjacentFacility: return FacilityBehaviorFamilies.GoldPerAdjacentCore;
                case FacilityCardEffectIds.SellResources: return FacilityBehaviorFamilies.ResourceSale;
                case FacilityCardEffectIds.DiscountOriginiumByFacilityColor: return FacilityBehaviorFamilies.OriginiumDiscount;
                case FacilityCardEffectIds.FreeCityMoveAndDeployRouteInfluence: return FacilityBehaviorFamilies.FreeMoveAndRouteInfluence;
                case FacilityCardEffectIds.ChooseFiveBasicResources: return FacilityBehaviorFamilies.ResourceChoice;
                case FacilityCardEffectIds.ReplaceOneInfluence: return FacilityBehaviorFamilies.ReplaceOrDeployInfluence;
                case FacilityCardEffectIds.DeployTwoInfluences: return FacilityBehaviorFamilies.DeployInfluences;
                case FacilityCardEffectIds.RemoveThenDispatchOrExplore: return FacilityBehaviorFamilies.RemoveAndDispatchOrExplore;
                case FacilityCardEffectIds.SetupCoreCommandTower: return FacilityBehaviorFamilies.Setup;
                case FacilityCardEffectIds.ReserveExtensionHub: return FacilityBehaviorFamilies.Reserve;
                case FacilityCardEffectIds.EnterpriseOffice: return FacilityBehaviorFamilies.Enterprise;
                default: return FacilityBehaviorFamilies.None;
            }
        }
    }

    /// <summary>
    /// 设施内容注册项。它是运行时索引，不进入 GameState；版本、handler 和订阅
    /// 标识会随 Effect/Event receipt 持久化，保证恢复时仍能定位同一内容契约。
    /// </summary>
    public sealed class FacilityContentRegistration
    {
        public const string DefinitionVersion = "1.0.0";

        public string FacilityId = string.Empty;
        public string EffectId = string.Empty;
        public string BehaviorFamily = FacilityBehaviorFamilies.None;
        public string EventType = FacilityEntryEventTypeIds.Activated;
        public string RouteKey = string.Empty;
        public string HandlerId = string.Empty;
        public string SubscriptionId = string.Empty;
        public string DefinitionHash = string.Empty;
        public string DefinitionVersionValue = DefinitionVersion;
        public bool HasEntryEffect;

        public bool IsValid()
        {
            return !string.IsNullOrEmpty(FacilityId) &&
                   !string.IsNullOrEmpty(EffectId) &&
                   !string.IsNullOrEmpty(BehaviorFamily) &&
                   !string.IsNullOrEmpty(EventType) &&
                   !string.IsNullOrEmpty(RouteKey) &&
                   !string.IsNullOrEmpty(HandlerId) &&
                   !string.IsNullOrEmpty(SubscriptionId) &&
                   !string.IsNullOrEmpty(DefinitionHash) &&
                   !string.IsNullOrEmpty(DefinitionVersionValue);
        }
    }

    public static class FacilityContentRegistrationCatalog
    {
        public static FacilityContentRegistration Create(FacilityCardDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            string family = FacilityBehaviorFamilyResolver.Resolve(definition);
            string routeKey = definition.EffectId ?? string.Empty;
            return new FacilityContentRegistration
            {
                FacilityId = definition.FacilityId ?? string.Empty,
                EffectId = routeKey,
                BehaviorFamily = family,
                RouteKey = routeKey,
                HandlerId = "lua.facility." + routeKey,
                SubscriptionId = "lua:facility:" + routeKey,
                HasEntryEffect = definition.HasEntryEffect,
                DefinitionHash = StableIdFactory.Create("facility-content", definition.FacilityId ?? string.Empty,
                    routeKey, family)
            };
        }

        public static IList<FacilityContentRegistration> CreateAll()
        {
            var result = new List<FacilityContentRegistration>();
            if (!FacilityCardDatabase.IsInitialized) return result;
            foreach (var definition in FacilityCardDatabase.All) result.Add(Create(definition));
            return result;
        }
    }

    public static class FacilityEntryEventTypeIds
    {
        public const string Activated = "FacilityEntryEffectActivated";
    }

    public static class FacilityEntryEffectTypeIds
    {
        public const string Activate = "effect.facility.entry";
        public const string Behavior = "effect.facility.behavior";
        public const string Build = "effect.facility.build";
        public const string InteractionType = "facility.entry.choice";
    }
}
