using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using YC.Domain.CityStyles;
using YC.Domain.State;

namespace YC.Domain.SpecialActions
{
    public static class SpecialActionDatabase
    {
        public const string MilitaryIndustrialArea = "special_action.city_style.military_industrial_area";
        public const string MobilizationSupportSystem = "special_action.city_style.mobilization_support_system";
        public const string CompositePowerSystem = "special_action.city_style.composite_power_system";
        public const string SourceStoneIndustrialHub = "special_action.city_style.source_stone_industrial_hub";
        public const string EfficientMobileManagementSystem = "special_action.city_style.efficient_mobile_management_system";

        public const int ExpectedDefinitionCount = 5;

        private static readonly IReadOnlyList<string> StableIds = Array.AsReadOnly(new[]
        {
            MilitaryIndustrialArea,
            MobilizationSupportSystem,
            CompositePowerSystem,
            SourceStoneIndustrialHub,
            EfficientMobileManagementSystem
        });

        private static readonly object SyncRoot = new object();
        private static IReadOnlyDictionary<string, SpecialActionDefinition> definitions =
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

        public static void Initialize(IEnumerable<SpecialActionDefinition> sourceDefinitions)
        {
            if (sourceDefinitions == null)
            {
                throw new ArgumentNullException(nameof(sourceDefinitions));
            }

            var next = new Dictionary<string, SpecialActionDefinition>(StringComparer.Ordinal);
            foreach (var source in sourceDefinitions)
            {
                ValidateDefinition(source);
                if (next.ContainsKey(source.SpecialActionId))
                {
                    throw new InvalidOperationException("特殊行动目录包含重复 ID：" + source.SpecialActionId);
                }

                next.Add(source.SpecialActionId, source.Clone());
            }

            ValidateDefinitionSet(next);
            var nextSnapshot = new ReadOnlyDictionary<string, SpecialActionDefinition>(next);
            lock (SyncRoot)
            {
                if (injectedDefinitions)
                {
                    if (DefinitionSetsEqual(definitions, next))
                    {
                        return;
                    }

                    throw new InvalidOperationException(
                        "SpecialActionDatabase 已使用不同的完整特殊行动目录初始化，禁止覆盖。");
                }

                definitions = nextSnapshot;
                injectedDefinitions = true;
            }
        }

        public static IReadOnlyList<SpecialActionDefinition> All
        {
            get
            {
                var snapshot = GetInitializedSnapshot();
                var result = new List<SpecialActionDefinition>(StableIds.Count);
                for (var i = 0; i < StableIds.Count; i++)
                {
                    result.Add(snapshot[StableIds[i]].Clone());
                }

                return result.AsReadOnly();
            }
        }

        public static bool TryGet(string specialActionId, out SpecialActionDefinition definition)
        {
            var snapshot = GetInitializedSnapshot();
            SpecialActionDefinition stored;
            if (string.IsNullOrEmpty(specialActionId) || !snapshot.TryGetValue(specialActionId, out stored))
            {
                definition = null;
                return false;
            }

            definition = stored.Clone();
            return true;
        }

        public static SpecialActionDefinition Get(string specialActionId)
        {
            SpecialActionDefinition definition;
            return TryGet(specialActionId, out definition) ? definition : null;
        }

        private static IReadOnlyDictionary<string, SpecialActionDefinition> GetInitializedSnapshot()
        {
            lock (SyncRoot)
            {
                if (!injectedDefinitions)
                {
                    throw new InvalidOperationException(
                        "SpecialActionDatabase 未初始化：必须先由 CityStyleSpecialActionCatalogBootstrap 注入完整目录。");
                }

                return definitions;
            }
        }

        private static void ValidateDefinitionSet(
            IReadOnlyDictionary<string, SpecialActionDefinition> candidates)
        {
            if (candidates.Count != ExpectedDefinitionCount)
            {
                throw new InvalidOperationException(
                    "特殊行动目录必须精确包含 5 项，实际 " + candidates.Count + " 项。");
            }

            for (var i = 0; i < StableIds.Count; i++)
            {
                if (!candidates.ContainsKey(StableIds[i]))
                {
                    throw new InvalidOperationException("特殊行动目录缺少稳定 ID：" + StableIds[i]);
                }
            }
        }

        public static bool TryValidateDefinition(
            SpecialActionDefinition definition,
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

        private static void ValidateDefinition(SpecialActionDefinition definition)
        {
            if (definition == null)
            {
                throw new InvalidOperationException("特殊行动目录包含空定义。");
            }

            var expectedCityStyleId = GetExpectedCityStyleId(definition.SpecialActionId);
            if (expectedCityStyleId == null)
            {
                throw new InvalidOperationException("特殊行动目录包含未知稳定 ID：" + definition.SpecialActionId);
            }

            if (definition.CityStyleId != expectedCityStyleId)
            {
                throw new InvalidOperationException(definition.SpecialActionId + " 的城市样式映射不正确。");
            }

            if (string.IsNullOrWhiteSpace(definition.Name) ||
                string.IsNullOrWhiteSpace(definition.Description) ||
                (definition.Level != 1 && definition.Level != 2) ||
                !Enum.IsDefined(typeof(SpecialActionEffectKind), definition.EffectKind) ||
                definition.FixedCost == null ||
                definition.FlexibleOriginiumAndIronCost < 0 ||
                definition.MaximumTargetCount < 0 || definition.FreeMoveCount < 0 ||
                definition.ExtraMainActionCount < 0)
            {
                throw new InvalidOperationException(definition.SpecialActionId + " 缺少必需字段或数值无效。");
            }

            ValidateResourceSet(definition.FixedCost, definition.SpecialActionId + " 的固定成本");
            ValidateSemanticContract(definition);
        }

        private static void ValidateSemanticContract(SpecialActionDefinition definition)
        {
            switch (definition.SpecialActionId)
            {
                case MilitaryIndustrialArea:
                    ValidateExpectedContract(
                        definition,
                        1,
                        SpecialActionEffectKind.DeployInfluence,
                        0, 0, 0, 0, 0,
                        0, 3, 0, 0,
                        false);
                    break;
                case MobilizationSupportSystem:
                    ValidateExpectedContract(
                        definition,
                        1,
                        SpecialActionEffectKind.ReplaceInfluence,
                        0, 0, 0, 0, 0,
                        0, 1, 0, 0,
                        false);
                    break;
                case CompositePowerSystem:
                    ValidateExpectedContract(
                        definition,
                        1,
                        SpecialActionEffectKind.CompositePowerMove,
                        0, 1, 0, 0, 0,
                        3, 1, 1, 0,
                        false);
                    break;
                case SourceStoneIndustrialHub:
                    ValidateExpectedContract(
                        definition,
                        2,
                        SpecialActionEffectKind.GrantExtraMainActions,
                        0, 0, 0, 0, 6,
                        0, 0, 0, 2,
                        true);
                    break;
                case EfficientMobileManagementSystem:
                    ValidateExpectedContract(
                        definition,
                        2,
                        SpecialActionEffectKind.ConsecutiveFreeMoves,
                        0, 3, 0, 0, 0,
                        0, 0, 2, 0,
                        true);
                    break;
            }
        }

        private static void ValidateExpectedContract(
            SpecialActionDefinition definition,
            int level,
            SpecialActionEffectKind effectKind,
            int originium,
            int originiumShard,
            int iron,
            int pureOriginium,
            int goldVoucher,
            int flexibleOriginiumAndIronCost,
            int maximumTargetCount,
            int freeMoveCount,
            int extraMainActionCount,
            bool locksCharacterCard)
        {
            var cost = definition.FixedCost;
            if (definition.Level != level || definition.EffectKind != effectKind ||
                cost.Originium != originium || cost.OriginiumShard != originiumShard ||
                cost.Iron != iron || cost.PureOriginium != pureOriginium ||
                cost.GoldVoucher != goldVoucher ||
                definition.FlexibleOriginiumAndIronCost != flexibleOriginiumAndIronCost ||
                definition.MaximumTargetCount != maximumTargetCount ||
                definition.FreeMoveCount != freeMoveCount ||
                definition.ExtraMainActionCount != extraMainActionCount ||
                definition.LocksCharacterCard != locksCharacterCard)
            {
                throw new InvalidOperationException(
                    definition.SpecialActionId + " 的稳定行为语义或参数与契约不一致。");
            }
        }

        private static string GetExpectedCityStyleId(string specialActionId)
        {
            switch (specialActionId)
            {
                case MilitaryIndustrialArea:
                    return CityStyleDatabase.MilitaryIndustrialArea;
                case MobilizationSupportSystem:
                    return CityStyleDatabase.MobilizationSupportSystem;
                case CompositePowerSystem:
                    return CityStyleDatabase.CompositePowerSystem;
                case SourceStoneIndustrialHub:
                    return CityStyleDatabase.SourceStoneIndustrialHub;
                case EfficientMobileManagementSystem:
                    return CityStyleDatabase.EfficientMobileManagementSystem;
                default:
                    return null;
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

        private static bool DefinitionSetsEqual(
            IReadOnlyDictionary<string, SpecialActionDefinition> left,
            IReadOnlyDictionary<string, SpecialActionDefinition> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach (var pair in left)
            {
                SpecialActionDefinition candidate;
                if (!right.TryGetValue(pair.Key, out candidate) ||
                    !DefinitionsEqual(pair.Value, candidate))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool DefinitionsEqual(
            SpecialActionDefinition left,
            SpecialActionDefinition right)
        {
            return left.SpecialActionId == right.SpecialActionId &&
                   left.CityStyleId == right.CityStyleId &&
                   left.Name == right.Name && left.Description == right.Description &&
                   left.Level == right.Level && left.EffectKind == right.EffectKind &&
                   ResourceSetsEqual(left.FixedCost, right.FixedCost) &&
                   left.FlexibleOriginiumAndIronCost == right.FlexibleOriginiumAndIronCost &&
                   left.MaximumTargetCount == right.MaximumTargetCount &&
                   left.FreeMoveCount == right.FreeMoveCount &&
                   left.ExtraMainActionCount == right.ExtraMainActionCount &&
                   left.LocksCharacterCard == right.LocksCharacterCard;
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

        private static IReadOnlyDictionary<string, SpecialActionDefinition> CreateEmptySnapshot()
        {
            return new ReadOnlyDictionary<string, SpecialActionDefinition>(
                new Dictionary<string, SpecialActionDefinition>(StringComparer.Ordinal));
        }
    }
}
