using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    public static class FacilityCardDatabase
    {
        public const string CoreCommandTower = "reserve_001";
        public const string BoroughAdministrativeDistrict = "building_001";
        public const string AffiliatedEnergyFacility = "building_004";
        public const string FederalOffice = "building_007";
        public const string SimpleEngineeringCamp = "building_010";
        public const string LogisticsHub = "building_012";
        public const string SourceStoneRefinery = "building_014";
        public const string UrbanizedArea = "building_015";
        public const string IronRefinery = "building_018";
        public const string TradeDistrict = "building_019";
        public const string CityIndustrialDistrict = "building_022";
        public const string HighPerformancePowerFacility = "building_025";
        public const string OriginiumPurificationPlant = "building_027";
        public const string MiningPowerShovel = "building_029";
        public const string MercenaryCommand = "building_034";
        public const string EscortDispatchCenter = "building_037";
        public const string EquipmentWarehouse = "building_039";
        public const string ExtensionHubBlue = "reserve_002";
        public const string ExtensionHubYellow = "reserve_003";
        public const string ExtensionHubRed = "reserve_004";
        public const string EnterpriseOffice = "facility_enterprise_office";

        public const int ExpectedDefinitionCount = 46;
        public const int ExpectedDefaultSupplyCount = 41;
        public const int ExpectedReserveCount = 4;

        private static readonly Dictionary<string, FacilityCardDefinition> Definitions =
            new Dictionary<string, FacilityCardDefinition>(StringComparer.Ordinal);
        private static IReadOnlyList<string> defaultSupplyIds = Array.Empty<string>();
        private static IReadOnlyList<string> reserveIds = Array.Empty<string>();
        private static bool injectedDefinitions;

        public static bool IsInitialized => injectedDefinitions;

        public static void Initialize(IEnumerable<FacilityCardDefinition> sourceDefinitions)
        {
            if (sourceDefinitions == null)
            {
                throw new ArgumentNullException(nameof(sourceDefinitions));
            }

            var next = new Dictionary<string, FacilityCardDefinition>(StringComparer.Ordinal);
            foreach (var source in sourceDefinitions)
            {
                ValidateInjectedDefinition(source);
                if (next.ContainsKey(source.FacilityId))
                {
                    throw new InvalidOperationException("设施目录包含重复 ID：" + source.FacilityId);
                }

                next.Add(source.FacilityId, CloneInjectedDefinition(source));
            }

            ValidateInjectedDefinitionSet(next);
            if (injectedDefinitions)
            {
                if (InjectedDefinitionSetsEqual(Definitions, next))
                {
                    return;
                }

                throw new InvalidOperationException("FacilityCardDatabase 已使用不同的完整设施目录初始化，禁止覆盖。");
            }

            Definitions.Clear();
            foreach (var pair in next)
            {
                Definitions.Add(pair.Key, pair.Value);
            }

            defaultSupplyIds = BuildDefaultSupplyIds(next);
            reserveIds = BuildReserveIds(next);
            injectedDefinitions = true;
        }

        public static IReadOnlyList<string> DefaultSupplyIds
        {
            get
            {
                EnsureInitialized();
                return defaultSupplyIds;
            }
        }

        public static IReadOnlyList<string> ReserveIds
        {
            get
            {
                EnsureInitialized();
                return reserveIds;
            }
        }

        public static bool PlayerHasBuiltUniqueFacility(PlayerState player, FacilityCardDefinition facility)
        {
            EnsureInitialized();
            if (player == null || facility == null || !facility.Unique)
            {
                return false;
            }

            var uniqueGroupId = GetUniqueGroupId(facility);
            for (var i = 0; i < player.BuiltFacilityIds.Count; i++)
            {
                FacilityCardDefinition builtFacility;
                if (!TryGet(player.BuiltFacilityIds[i], out builtFacility) ||
                    !builtFacility.Unique)
                {
                    continue;
                }

                if (GetUniqueGroupId(builtFacility) == uniqueGroupId)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TryGet(string facilityId, out FacilityCardDefinition definition)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(facilityId))
            {
                definition = null;
                return false;
            }

            return Definitions.TryGetValue(facilityId, out definition);
        }

        public static FacilityCardDefinition Get(string facilityId)
        {
            EnsureInitialized();
            FacilityCardDefinition definition;
            return TryGet(facilityId, out definition) ? definition : null;
        }

        private static void EnsureInitialized()
        {
            if (!injectedDefinitions)
            {
                throw new InvalidOperationException(
                    "FacilityCardDatabase 未初始化：必须先由 FacilityCatalogBootstrap 注入完整设施目录。");
            }
        }

        private static IReadOnlyList<string> BuildDefaultSupplyIds(
            IReadOnlyDictionary<string, FacilityCardDefinition> definitions)
        {
            var result = new List<string>(ExpectedDefaultSupplyCount);
            for (var index = 1; index <= ExpectedDefaultSupplyCount; index++)
            {
                var id = "building_" + index.ToString("000");
                if (!definitions.TryGetValue(id, out var definition) || definition.ReserveOnly)
                {
                    throw new InvalidOperationException("默认供应设施缺失或分类错误：" + id);
                }

                result.Add(id);
            }

            return result.AsReadOnly();
        }

        private static IReadOnlyList<string> BuildReserveIds(
            IReadOnlyDictionary<string, FacilityCardDefinition> definitions)
        {
            var result = new List<string>(ExpectedReserveCount);
            for (var index = 1; index <= ExpectedReserveCount; index++)
            {
                var id = "reserve_" + index.ToString("000");
                if (!definitions.TryGetValue(id, out var definition) || !definition.ReserveOnly)
                {
                    throw new InvalidOperationException("reserve 设施缺失或分类错误：" + id);
                }

                result.Add(id);
            }

            return result.AsReadOnly();
        }

        private static void ValidateInjectedDefinitionSet(
            IReadOnlyDictionary<string, FacilityCardDefinition> candidates)
        {
            if (candidates.Count != ExpectedDefinitionCount)
            {
                throw new InvalidOperationException(
                    "设施目录必须精确包含 " + ExpectedDefinitionCount + " 项，实际 " + candidates.Count + " 项。");
            }

            for (var index = 1; index <= ExpectedDefaultSupplyCount; index++)
            {
                var id = "building_" + index.ToString("000");
                if (!candidates.TryGetValue(id, out var definition) || definition.ReserveOnly)
                {
                    throw new InvalidOperationException("默认供应设施缺失或分类错误：" + id);
                }
            }

            for (var index = 1; index <= ExpectedReserveCount; index++)
            {
                var id = "reserve_" + index.ToString("000");
                if (!candidates.TryGetValue(id, out var definition) || !definition.ReserveOnly)
                {
                    throw new InvalidOperationException("reserve 设施缺失或分类错误：" + id);
                }
            }

            if (!candidates.TryGetValue(EnterpriseOffice, out var enterprise) || enterprise.ReserveOnly)
            {
                throw new InvalidOperationException("缺少非 reserve 的企业办事处补充定义。");
            }
        }

        private static void ValidateInjectedDefinition(FacilityCardDefinition definition)
        {
            if (definition == null)
            {
                throw new InvalidOperationException("设施目录包含空定义。");
            }

            if (string.IsNullOrEmpty(definition.FacilityId) ||
                definition.ManifestId != definition.FacilityId ||
                string.IsNullOrEmpty(definition.Name) ||
                string.IsNullOrEmpty(definition.Color) ||
                string.IsNullOrEmpty(definition.EffectId) ||
                string.IsNullOrEmpty(definition.EffectType))
            {
                throw new InvalidOperationException(definition.FacilityId + " 缺少必需字段或 ManifestId 不一致。");
            }

            if (definition.ResourceCost == null || definition.OnBuiltReward == null || definition.Keywords == null)
            {
                throw new InvalidOperationException(definition.FacilityId + " 缺少资源或关键词结构。");
            }

            ValidateInjectedResourceSet(definition.ResourceCost, definition.FacilityId + " 的建造成本");
            ValidateInjectedResourceSet(definition.OnBuiltReward, definition.FacilityId + " 的建造奖励");
            if (definition.GoldVoucherCost < 0)
            {
                throw new InvalidOperationException(definition.FacilityId + " 的金券成本不能为负数。");
            }

            var keywordSet = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < definition.Keywords.Count; i++)
            {
                if (string.IsNullOrEmpty(definition.Keywords[i]) || !keywordSet.Add(definition.Keywords[i]))
                {
                    throw new InvalidOperationException(definition.FacilityId + " 包含空或重复关键词。");
                }
            }

            var unique = keywordSet.Contains(FacilityCardKeywords.Unique);
            if (definition.Unique != unique || definition.UniqueGroupId != (unique ? definition.Name : string.Empty))
            {
                throw new InvalidOperationException(definition.FacilityId + " 的唯一设施字段未由关键词稳定推导。");
            }

            if (!KnownEffectIds.Contains(definition.EffectId))
            {
                throw new InvalidOperationException(
                    definition.FacilityId + " 使用未知 effectId：" + definition.EffectId);
            }

            var hasEntryKeyword = keywordSet.Contains(FacilityCardKeywords.Entry);
            if (definition.HasEntryEffect != hasEntryKeyword)
            {
                throw new InvalidOperationException(
                    definition.FacilityId + " 的 HasEntryEffect 与 entry 关键词不一致。");
            }

            ValidateOnBuiltRewardContract(definition);
        }

        private static readonly HashSet<string> KnownEffectIds = new HashSet<string>(StringComparer.Ordinal)
        {
            FacilityCardEffectIds.UniqueOnly,
            FacilityCardEffectIds.CopyAdjacentEntryEffect,
            FacilityCardEffectIds.ClaimStartMarkerAtCleanup,
            FacilityCardEffectIds.BuildAdditionalFacility,
            FacilityCardEffectIds.BuildExtensionHub,
            FacilityCardEffectIds.GainOriginiumShardSix,
            FacilityCardEffectIds.GainGoldPerCoreAdjacentFacility,
            FacilityCardEffectIds.GainIronFour,
            FacilityCardEffectIds.SellResources,
            FacilityCardEffectIds.DiscountOriginiumByFacilityColor,
            FacilityCardEffectIds.FreeCityMoveAndDeployRouteInfluence,
            FacilityCardEffectIds.GainOriginiumSeven,
            FacilityCardEffectIds.ChooseFiveBasicResources,
            FacilityCardEffectIds.ReplaceOneInfluence,
            FacilityCardEffectIds.DeployTwoInfluences,
            FacilityCardEffectIds.RemoveThenDispatchOrExplore,
            FacilityCardEffectIds.SetupCoreCommandTower,
            FacilityCardEffectIds.ReserveExtensionHub,
            FacilityCardEffectIds.EnterpriseOffice
        };

        private static void ValidateOnBuiltRewardContract(FacilityCardDefinition definition)
        {
            ResourceSet expected;
            switch (definition.EffectId)
            {
                case FacilityCardEffectIds.GainOriginiumShardSix:
                    expected = new ResourceSet { OriginiumShard = 6 };
                    break;
                case FacilityCardEffectIds.GainIronFour:
                    expected = new ResourceSet { Iron = 4 };
                    break;
                case FacilityCardEffectIds.GainOriginiumSeven:
                    expected = new ResourceSet { Originium = 7 };
                    break;
                default:
                    expected = new ResourceSet();
                    break;
            }

            if (!InjectedResourceSetsEqual(definition.OnBuiltReward, expected))
            {
                throw new InvalidOperationException(
                    definition.FacilityId + " 的 OnBuiltReward 与 effectId 契约不一致：" + definition.EffectId);
            }
        }

        private static void ValidateInjectedResourceSet(ResourceSet value, string label)
        {
            if (value.Originium < 0 || value.OriginiumShard < 0 || value.Iron < 0 ||
                value.PureOriginium < 0 || value.GoldVoucher < 0)
            {
                throw new InvalidOperationException(label + " 包含负数资源。");
            }
        }

        private static FacilityCardDefinition CloneInjectedDefinition(FacilityCardDefinition source)
        {
            return new FacilityCardDefinition
            {
                FacilityId = source.FacilityId,
                ManifestId = source.ManifestId,
                Name = source.Name,
                Color = source.Color,
                Score = source.Score,
                ResourceCost = source.ResourceCost.Clone(),
                GoldVoucherCost = source.GoldVoucherCost,
                Unique = source.Unique,
                UniqueGroupId = source.UniqueGroupId,
                HasEntryEffect = source.HasEntryEffect,
                Keywords = new List<string>(source.Keywords),
                EffectId = source.EffectId,
                EffectType = source.EffectType,
                Description = source.Description,
                EffectText = source.EffectText,
                ReserveOnly = source.ReserveOnly,
                OnBuiltReward = source.OnBuiltReward.Clone()
            };
        }

        private static bool InjectedDefinitionSetsEqual(
            IReadOnlyDictionary<string, FacilityCardDefinition> left,
            IReadOnlyDictionary<string, FacilityCardDefinition> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach (var pair in left)
            {
                if (!right.TryGetValue(pair.Key, out var candidate) ||
                    !InjectedDefinitionsEqual(pair.Value, candidate))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool InjectedDefinitionsEqual(
            FacilityCardDefinition left,
            FacilityCardDefinition right)
        {
            if (left.FacilityId != right.FacilityId || left.ManifestId != right.ManifestId ||
                left.Name != right.Name || left.Color != right.Color || left.Score != right.Score ||
                left.GoldVoucherCost != right.GoldVoucherCost || left.Unique != right.Unique ||
                left.UniqueGroupId != right.UniqueGroupId || left.HasEntryEffect != right.HasEntryEffect ||
                left.EffectId != right.EffectId || left.EffectType != right.EffectType ||
                left.Description != right.Description || left.EffectText != right.EffectText ||
                left.ReserveOnly != right.ReserveOnly ||
                !InjectedResourceSetsEqual(left.ResourceCost, right.ResourceCost) ||
                !InjectedResourceSetsEqual(left.OnBuiltReward, right.OnBuiltReward) ||
                left.Keywords.Count != right.Keywords.Count)
            {
                return false;
            }

            for (var i = 0; i < left.Keywords.Count; i++)
            {
                if (left.Keywords[i] != right.Keywords[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool InjectedResourceSetsEqual(ResourceSet left, ResourceSet right)
        {
            return left.Originium == right.Originium &&
                   left.OriginiumShard == right.OriginiumShard &&
                   left.Iron == right.Iron &&
                   left.PureOriginium == right.PureOriginium &&
                   left.GoldVoucher == right.GoldVoucher;
        }

        private static string GetUniqueGroupId(FacilityCardDefinition facility)
        {
            return string.IsNullOrEmpty(facility.UniqueGroupId)
                ? facility.FacilityId
                : facility.UniqueGroupId;
        }
    }
}
