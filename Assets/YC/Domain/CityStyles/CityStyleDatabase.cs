using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using YC.Domain.Facilities;
using YC.Domain.SpecialActions;
using YC.Domain.State;

namespace YC.Domain.CityStyles
{
    public static class CityStyleDatabase
    {
        public const string MilitaryIndustrialArea = "city_style_military_industrial_area";
        public const string MobilizationSupportSystem = "city_style_mobilization_support_system";
        public const string CompositePowerSystem = "city_style_composite_power_system";
        public const string MaterialRelayStation = "city_style_material_relay_station";
        public const string SourceStoneIndustrialHub = "city_style_source_stone_industrial_hub";
        public const string EfficientMobileManagementSystem = "city_style_efficient_mobile_management_system";

        public const int ExpectedDefinitionCount = 6;

        private static readonly IReadOnlyList<string> StableIds = Array.AsReadOnly(new[]
        {
            MilitaryIndustrialArea,
            MobilizationSupportSystem,
            CompositePowerSystem,
            MaterialRelayStation,
            SourceStoneIndustrialHub,
            EfficientMobileManagementSystem
        });

        private static readonly object SyncRoot = new object();
        private static IReadOnlyDictionary<string, CityStyleDefinition> definitions =
            CreateEmptySnapshot();
        private static bool injectedDefinitions;

        public static bool IsInitialized
        {
            get
            {
                lock (SyncRoot)
                {
                    return injectedDefinitions;
                }
            }
        }

        public static IReadOnlyList<string> DefaultSupplyIds
        {
            get
            {
                EnsureInitialized();
                return StableIds;
            }
        }

        public static IReadOnlyList<string> PresentationSupplyIds
        {
            get
            {
                EnsureInitialized();
                return StableIds;
            }
        }

        public static IReadOnlyList<CityStyleDefinition> All
        {
            get
            {
                var snapshot = GetInitializedSnapshot();
                var result = new List<CityStyleDefinition>(StableIds.Count);
                for (var i = 0; i < StableIds.Count; i++)
                {
                    result.Add(CloneDefinition(snapshot[StableIds[i]]));
                }

                return result.AsReadOnly();
            }
        }

        public static void Initialize(IEnumerable<CityStyleDefinition> sourceDefinitions)
        {
            if (sourceDefinitions == null)
            {
                throw new ArgumentNullException(nameof(sourceDefinitions));
            }

            var next = new Dictionary<string, CityStyleDefinition>(StringComparer.Ordinal);
            foreach (var source in sourceDefinitions)
            {
                ValidateDefinition(source);
                if (next.ContainsKey(source.CityStyleId))
                {
                    throw new InvalidOperationException("城市样式目录包含重复 ID：" + source.CityStyleId);
                }

                next.Add(source.CityStyleId, CloneDefinition(source));
            }

            ValidateDefinitionSet(next);
            var nextSnapshot = new ReadOnlyDictionary<string, CityStyleDefinition>(next);
            lock (SyncRoot)
            {
                if (injectedDefinitions)
                {
                    if (DefinitionSetsEqual(definitions, next))
                    {
                        return;
                    }

                    throw new InvalidOperationException(
                        "CityStyleDatabase 已使用不同的完整城市样式目录初始化，禁止覆盖。");
                }

                definitions = nextSnapshot;
                injectedDefinitions = true;
            }
        }

        public static bool TryGet(string cityStyleId, out CityStyleDefinition definition)
        {
            var snapshot = GetInitializedSnapshot();
            CityStyleDefinition stored;
            if (string.IsNullOrEmpty(cityStyleId) || !snapshot.TryGetValue(cityStyleId, out stored))
            {
                definition = null;
                return false;
            }

            definition = CloneDefinition(stored);
            return true;
        }

        public static CityStyleDefinition Get(string cityStyleId)
        {
            CityStyleDefinition definition;
            return TryGet(cityStyleId, out definition) ? definition : null;
        }

        private static void EnsureInitialized()
        {
            lock (SyncRoot)
            {
                if (!injectedDefinitions)
                {
                    throw new InvalidOperationException(
                        "CityStyleDatabase 未初始化：必须先由 CityStyleSpecialActionCatalogBootstrap 注入完整目录。");
                }
            }
        }

        private static IReadOnlyDictionary<string, CityStyleDefinition> GetInitializedSnapshot()
        {
            lock (SyncRoot)
            {
                if (!injectedDefinitions)
                {
                    throw new InvalidOperationException(
                        "CityStyleDatabase 未初始化：必须先由 CityStyleSpecialActionCatalogBootstrap 注入完整目录。");
                }

                return definitions;
            }
        }

        private static void ValidateDefinitionSet(
            IReadOnlyDictionary<string, CityStyleDefinition> candidates)
        {
            if (candidates.Count != ExpectedDefinitionCount)
            {
                throw new InvalidOperationException(
                    "城市样式目录必须精确包含 6 项，实际 " + candidates.Count + " 项。");
            }

            for (var i = 0; i < StableIds.Count; i++)
            {
                if (!candidates.ContainsKey(StableIds[i]))
                {
                    throw new InvalidOperationException("城市样式目录缺少稳定 ID：" + StableIds[i]);
                }
            }
        }

        public static bool TryValidateDefinition(
            CityStyleDefinition definition,
            out string reason)
        {
            try
            {
                ValidateDefinition(definition);
                reason = string.Empty;
                return true;
            }
            catch (InvalidOperationException exception)
            {
                reason = exception.Message;
                return false;
            }
        }

        private static void ValidateDefinition(CityStyleDefinition definition)
        {
            if (definition == null)
            {
                throw new InvalidOperationException("城市样式目录包含空定义。");
            }

            var expectedActionId = GetExpectedSpecialActionId(definition.CityStyleId);
            if (expectedActionId == null)
            {
                throw new InvalidOperationException("城市样式目录包含未知稳定 ID：" + definition.CityStyleId);
            }

            if (definition.SpecialActionId != expectedActionId)
            {
                throw new InvalidOperationException(definition.CityStyleId + " 的特殊行动映射不正确。");
            }

            if (string.IsNullOrWhiteSpace(definition.Name) ||
                string.IsNullOrWhiteSpace(definition.Description) ||
                definition.Level <= 0 || definition.Score < 0 ||
                definition.MaxDeclarationsPerPlayer <= 0 ||
                definition.DeclarationReward == null ||
                definition.DeclarationRequirement == null)
            {
                throw new InvalidOperationException(definition.CityStyleId + " 缺少必需字段或数值无效。");
            }

            ValidateResourceSet(definition.DeclarationReward, definition.CityStyleId + " 的声明奖励");
            ValidateRequirement(definition.CityStyleId, definition.DeclarationRequirement);
        }

        private static string GetExpectedSpecialActionId(string cityStyleId)
        {
            switch (cityStyleId)
            {
                case MilitaryIndustrialArea:
                    return SpecialActionDatabase.MilitaryIndustrialArea;
                case MobilizationSupportSystem:
                    return SpecialActionDatabase.MobilizationSupportSystem;
                case CompositePowerSystem:
                    return SpecialActionDatabase.CompositePowerSystem;
                case MaterialRelayStation:
                    return string.Empty;
                case SourceStoneIndustrialHub:
                    return SpecialActionDatabase.SourceStoneIndustrialHub;
                case EfficientMobileManagementSystem:
                    return SpecialActionDatabase.EfficientMobileManagementSystem;
                default:
                    return null;
            }
        }

        private static void ValidateRequirement(string id, CityStyleRequirement requirement)
        {
            if (requirement.RequiredFacilityCount <= 0 ||
                requirement.RequiredEffectTypes == null ||
                requirement.RequiredResourceTypes == null ||
                requirement.RequiredCityBoardSlotIndexes == null ||
                requirement.RequiredPatternCells == null ||
                requirement.RequiredPatternCells.Count != requirement.RequiredFacilityCount)
            {
                throw new InvalidOperationException(id + " 的声明条件无效。");
            }

            var effectTypes = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < requirement.RequiredEffectTypes.Count; i++)
            {
                var effectType = requirement.RequiredEffectTypes[i];
                if (string.IsNullOrWhiteSpace(effectType) || !effectTypes.Add(effectType))
                {
                    throw new InvalidOperationException(id + " 包含空或重复的必需效果类型。");
                }
            }

            var resourceTypes = new HashSet<YC.Domain.Rules.ResourceType>();
            for (var i = 0; i < requirement.RequiredResourceTypes.Count; i++)
            {
                var resourceType = requirement.RequiredResourceTypes[i];
                if (!Enum.IsDefined(typeof(YC.Domain.Rules.ResourceType), resourceType) ||
                    !resourceTypes.Add(resourceType))
                {
                    throw new InvalidOperationException(id + " 包含非法或重复的必需资源类型。");
                }
            }

            var slotIndexes = new HashSet<int>();
            for (var i = 0; i < requirement.RequiredCityBoardSlotIndexes.Count; i++)
            {
                var slotIndex = requirement.RequiredCityBoardSlotIndexes[i];
                if (slotIndex < 0 || slotIndex >= BuildFacilityService.CityBoardSlotCount ||
                    !slotIndexes.Add(slotIndex))
                {
                    throw new InvalidOperationException(id + " 包含越界或重复的城市面板槽位。");
                }
            }

            var offsets = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < requirement.RequiredPatternCells.Count; i++)
            {
                var cell = requirement.RequiredPatternCells[i];
                if (cell == null || cell.AllowedFacilityColors == null ||
                    cell.AllowedFacilityColors.Count == 0 ||
                    !offsets.Add(cell.RowOffset + ":" + cell.ColumnOffset))
                {
                    throw new InvalidOperationException(id + " 包含无效或重复的样式格。");
                }

                var colors = new HashSet<string>(StringComparer.Ordinal);
                for (var colorIndex = 0; colorIndex < cell.AllowedFacilityColors.Count; colorIndex++)
                {
                    var color = cell.AllowedFacilityColors[colorIndex];
                    if ((color != "blue" && color != "yellow" && color != "red") ||
                        !colors.Add(color))
                    {
                        throw new InvalidOperationException(id + " 包含无效或重复的设施颜色。");
                    }
                }
            }
        }

        private static void ValidateResourceSet(ResourceSet value, string label)
        {
            if (value.Originium < 0 || value.OriginiumShard < 0 || value.Iron < 0 ||
                value.PureOriginium < 0 || value.GoldVoucher < 0)
            {
                throw new InvalidOperationException(label + " 包含负数资源。");
            }
        }

        private static CityStyleDefinition CloneDefinition(CityStyleDefinition source)
        {
            var requirement = source.DeclarationRequirement;
            var requirementCopy = new CityStyleRequirement
            {
                RequiredFacilityCount = requirement.RequiredFacilityCount,
                RequiredEffectTypes = new List<string>(requirement.RequiredEffectTypes),
                RequiredResourceTypes = new List<YC.Domain.Rules.ResourceType>(
                    requirement.RequiredResourceTypes),
                RequiredCityBoardSlotIndexes = new List<int>(requirement.RequiredCityBoardSlotIndexes),
                RequireSameCityBoardRow = requirement.RequireSameCityBoardRow
            };

            for (var i = 0; i < requirement.RequiredPatternCells.Count; i++)
            {
                var cell = requirement.RequiredPatternCells[i];
                requirementCopy.RequiredPatternCells.Add(new CityStylePatternCell
                {
                    RowOffset = cell.RowOffset,
                    ColumnOffset = cell.ColumnOffset,
                    AllowedFacilityColors = new List<string>(cell.AllowedFacilityColors)
                });
            }

            return new CityStyleDefinition
            {
                CityStyleId = source.CityStyleId,
                Name = source.Name,
                Level = source.Level,
                Score = source.Score,
                Description = source.Description,
                MaxDeclarationsPerPlayer = source.MaxDeclarationsPerPlayer,
                SpecialActionId = source.SpecialActionId,
                DeclarationReward = source.DeclarationReward.Clone(),
                DeclarationRequirement = requirementCopy
            };
        }

        private static bool DefinitionSetsEqual(
            IReadOnlyDictionary<string, CityStyleDefinition> left,
            IReadOnlyDictionary<string, CityStyleDefinition> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach (var pair in left)
            {
                CityStyleDefinition candidate;
                if (!right.TryGetValue(pair.Key, out candidate) ||
                    !DefinitionsEqual(pair.Value, candidate))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool DefinitionsEqual(CityStyleDefinition left, CityStyleDefinition right)
        {
            var leftRequirement = left.DeclarationRequirement;
            var rightRequirement = right.DeclarationRequirement;
            if (left.CityStyleId != right.CityStyleId || left.Name != right.Name ||
                left.Level != right.Level || left.Score != right.Score ||
                left.Description != right.Description ||
                left.MaxDeclarationsPerPlayer != right.MaxDeclarationsPerPlayer ||
                left.SpecialActionId != right.SpecialActionId ||
                !ResourceSetsEqual(left.DeclarationReward, right.DeclarationReward) ||
                leftRequirement.RequiredFacilityCount != rightRequirement.RequiredFacilityCount ||
                leftRequirement.RequireSameCityBoardRow != rightRequirement.RequireSameCityBoardRow ||
                !ListsEqual(leftRequirement.RequiredEffectTypes, rightRequirement.RequiredEffectTypes) ||
                !ListsEqual(leftRequirement.RequiredResourceTypes, rightRequirement.RequiredResourceTypes) ||
                !ListsEqual(leftRequirement.RequiredCityBoardSlotIndexes, rightRequirement.RequiredCityBoardSlotIndexes) ||
                leftRequirement.RequiredPatternCells.Count != rightRequirement.RequiredPatternCells.Count)
            {
                return false;
            }

            for (var i = 0; i < leftRequirement.RequiredPatternCells.Count; i++)
            {
                var leftCell = leftRequirement.RequiredPatternCells[i];
                var rightCell = rightRequirement.RequiredPatternCells[i];
                if (leftCell.RowOffset != rightCell.RowOffset ||
                    leftCell.ColumnOffset != rightCell.ColumnOffset ||
                    !ListsEqual(leftCell.AllowedFacilityColors, rightCell.AllowedFacilityColors))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ListsEqual<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            var comparer = EqualityComparer<T>.Default;
            for (var i = 0; i < left.Count; i++)
            {
                if (!comparer.Equals(left[i], right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ResourceSetsEqual(ResourceSet left, ResourceSet right)
        {
            return left.Originium == right.Originium &&
                   left.OriginiumShard == right.OriginiumShard &&
                   left.Iron == right.Iron &&
                   left.PureOriginium == right.PureOriginium &&
                   left.GoldVoucher == right.GoldVoucher;
        }

        internal static void ResetForTests()
        {
            lock (SyncRoot)
            {
                definitions = CreateEmptySnapshot();
                injectedDefinitions = false;
            }
        }

        private static IReadOnlyDictionary<string, CityStyleDefinition> CreateEmptySnapshot()
        {
            return new ReadOnlyDictionary<string, CityStyleDefinition>(
                new Dictionary<string, CityStyleDefinition>(StringComparer.Ordinal));
        }
    }
}
