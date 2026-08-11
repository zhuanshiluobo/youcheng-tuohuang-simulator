using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Domain.CityStyles;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;

namespace YC.Presentation
{
    public sealed class CityStyleSpecialActionDefinitionSet
    {
        public CityStyleSpecialActionDefinitionSet(
            IReadOnlyList<CityStyleDefinition> cityStyles,
            IReadOnlyList<SpecialActionDefinition> specialActions)
        {
            CityStyles = cityStyles;
            SpecialActions = specialActions;
        }

        public IReadOnlyList<CityStyleDefinition> CityStyles { get; }
        public IReadOnlyList<SpecialActionDefinition> SpecialActions { get; }
    }

    [CreateAssetMenu(
        fileName = "CityStyleSpecialActionCatalog",
        menuName = "YC/City Style and Special Action Catalog")]
    public sealed class CityStyleSpecialActionCatalog : ScriptableObject
    {
        public const int ExpectedCityStyleCount = 6;
        public const int ExpectedSpecialActionCount = 5;

        [Serializable]
        private sealed class SerializedResourceSet
        {
            public int originium;
            public int originiumShard;
            public int iron;
            public int pureOriginium;
            public int goldVoucher;

            public ResourceSet CreateResourceSet()
            {
                return new ResourceSet
                {
                    Originium = originium,
                    OriginiumShard = originiumShard,
                    Iron = iron,
                    PureOriginium = pureOriginium,
                    GoldVoucher = goldVoucher
                };
            }

            public bool TryValidate(string label, out string reason)
            {
                if (originium < 0 || originiumShard < 0 || iron < 0 ||
                    pureOriginium < 0 || goldVoucher < 0)
                {
                    reason = label + " 包含负数资源。";
                    return false;
                }

                reason = string.Empty;
                return true;
            }
        }

        [Serializable]
        private sealed class SerializedPatternCell
        {
            public int rowOffset;
            public int columnOffset;
            public List<string> allowedFacilityColors = new List<string>();

            public CityStylePatternCell CreatePatternCell()
            {
                return new CityStylePatternCell
                {
                    RowOffset = rowOffset,
                    ColumnOffset = columnOffset,
                    AllowedFacilityColors = new List<string>(allowedFacilityColors)
                };
            }

            public bool TryValidate(string label, out string reason)
            {
                if (allowedFacilityColors == null || allowedFacilityColors.Count == 0)
                {
                    reason = label + " 没有可用设施颜色。";
                    return false;
                }

                var colors = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < allowedFacilityColors.Count; i++)
                {
                    var color = allowedFacilityColors[i];
                    if ((color != "blue" && color != "yellow" && color != "red") ||
                        !colors.Add(color))
                    {
                        reason = label + " 包含无效或重复设施颜色。";
                        return false;
                    }
                }

                reason = string.Empty;
                return true;
            }
        }

        [Serializable]
        private sealed class SerializedRequirement
        {
            public int requiredFacilityCount;
            public List<string> requiredEffectTypes = new List<string>();
            public List<ResourceType> requiredResourceTypes = new List<ResourceType>();
            public List<int> requiredCityBoardSlotIndexes = new List<int>();
            public List<SerializedPatternCell> requiredPatternCells = new List<SerializedPatternCell>();
            public bool requireSameCityBoardRow;

            public CityStyleRequirement CreateRequirement()
            {
                var result = new CityStyleRequirement
                {
                    RequiredFacilityCount = requiredFacilityCount,
                    RequiredEffectTypes = new List<string>(requiredEffectTypes),
                    RequiredResourceTypes = new List<ResourceType>(requiredResourceTypes),
                    RequiredCityBoardSlotIndexes = new List<int>(requiredCityBoardSlotIndexes),
                    RequireSameCityBoardRow = requireSameCityBoardRow
                };

                for (var i = 0; i < requiredPatternCells.Count; i++)
                {
                    result.RequiredPatternCells.Add(requiredPatternCells[i].CreatePatternCell());
                }

                return result;
            }

            public bool TryValidate(string label, out string reason)
            {
                if (requiredFacilityCount <= 0 || requiredEffectTypes == null ||
                    requiredResourceTypes == null || requiredCityBoardSlotIndexes == null ||
                    requiredPatternCells == null ||
                    requiredPatternCells.Count != requiredFacilityCount)
                {
                    reason = label + " 的声明条件结构或设施数量无效。";
                    return false;
                }

                var offsets = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < requiredPatternCells.Count; i++)
                {
                    var cell = requiredPatternCells[i];
                    if (cell == null ||
                        !offsets.Add(cell.rowOffset + ":" + cell.columnOffset))
                    {
                        reason = label + " 包含空或重复的样式格。";
                        return false;
                    }

                    if (!cell.TryValidate(label + " 的样式格 " + i, out reason))
                    {
                        return false;
                    }
                }

                for (var i = 0; i < requiredResourceTypes.Count; i++)
                {
                    if (!Enum.IsDefined(typeof(ResourceType), requiredResourceTypes[i]))
                    {
                        reason = label + " 包含未知资源类型。";
                        return false;
                    }
                }

                reason = string.Empty;
                return true;
            }
        }

        [Serializable]
        private sealed class SerializedCityStyleDefinition
        {
            public string cityStyleId = string.Empty;
            public string name = string.Empty;
            public int level;
            public int score;
            public string description = string.Empty;
            public int maxDeclarationsPerPlayer = 1;
            public string specialActionId = string.Empty;
            public SerializedResourceSet declarationReward = new SerializedResourceSet();
            public SerializedRequirement declarationRequirement = new SerializedRequirement();

            public CityStyleDefinition CreateDefinition()
            {
                return new CityStyleDefinition
                {
                    CityStyleId = cityStyleId,
                    Name = name,
                    Level = level,
                    Score = score,
                    Description = description,
                    MaxDeclarationsPerPlayer = maxDeclarationsPerPlayer,
                    SpecialActionId = specialActionId,
                    DeclarationReward = declarationReward.CreateResourceSet(),
                    DeclarationRequirement = declarationRequirement.CreateRequirement()
                };
            }

            public bool TryValidate(out string reason)
            {
                if (string.IsNullOrEmpty(cityStyleId) || string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(description) || level <= 0 || score < 0 ||
                    maxDeclarationsPerPlayer <= 0 || declarationReward == null ||
                    declarationRequirement == null)
                {
                    reason = cityStyleId + " 缺少必需字段或数值无效。";
                    return false;
                }

                if (!declarationReward.TryValidate(cityStyleId + " 的声明奖励", out reason) ||
                    !declarationRequirement.TryValidate(cityStyleId, out reason))
                {
                    return false;
                }

                return CityStyleDatabase.TryValidateDefinition(CreateDefinition(), out reason);
            }
        }

        [Serializable]
        private sealed class SerializedSpecialActionDefinition
        {
            public string specialActionId = string.Empty;
            public string cityStyleId = string.Empty;
            public string name = string.Empty;
            public string description = string.Empty;
            public int level;
            public SpecialActionEffectKind effectKind;
            public SerializedResourceSet fixedCost = new SerializedResourceSet();
            public int flexibleOriginiumAndIronCost;
            public int maximumTargetCount;
            public int freeMoveCount;
            public int extraMainActionCount;
            public bool locksCharacterCard;

            public SpecialActionDefinition CreateDefinition()
            {
                return new SpecialActionDefinition
                {
                    SpecialActionId = specialActionId,
                    CityStyleId = cityStyleId,
                    Name = name,
                    Description = description,
                    Level = level,
                    EffectKind = effectKind,
                    FixedCost = fixedCost.CreateResourceSet(),
                    FlexibleOriginiumAndIronCost = flexibleOriginiumAndIronCost,
                    MaximumTargetCount = maximumTargetCount,
                    FreeMoveCount = freeMoveCount,
                    ExtraMainActionCount = extraMainActionCount,
                    LocksCharacterCard = locksCharacterCard
                };
            }

            public bool TryValidate(out string reason)
            {
                if (string.IsNullOrEmpty(specialActionId) || string.IsNullOrEmpty(cityStyleId) ||
                    string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description) ||
                    (level != 1 && level != 2) ||
                    !Enum.IsDefined(typeof(SpecialActionEffectKind), effectKind) ||
                    fixedCost == null || flexibleOriginiumAndIronCost < 0 ||
                    maximumTargetCount < 0 || freeMoveCount < 0 || extraMainActionCount < 0)
                {
                    reason = specialActionId + " 缺少必需字段或数值无效。";
                    return false;
                }

                if (!fixedCost.TryValidate(specialActionId + " 的固定成本", out reason))
                {
                    return false;
                }

                return SpecialActionDatabase.TryValidateDefinition(CreateDefinition(), out reason);
            }
        }

        [SerializeField]
        private SerializedCityStyleDefinition[] cityStyles =
            Array.Empty<SerializedCityStyleDefinition>();
        [SerializeField]
        private SerializedSpecialActionDefinition[] specialActions =
            Array.Empty<SerializedSpecialActionDefinition>();

        public int CityStyleCount => cityStyles == null ? 0 : cityStyles.Length;
        public int SpecialActionCount => specialActions == null ? 0 : specialActions.Length;

        public CityStyleSpecialActionDefinitionSet CreateDefinitions()
        {
            if (!TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException("CityStyleSpecialActionCatalog 无效：" + reason);
            }

            var cityStyleDefinitions = new List<CityStyleDefinition>(cityStyles.Length);
            for (var i = 0; i < cityStyles.Length; i++)
            {
                cityStyleDefinitions.Add(cityStyles[i].CreateDefinition());
            }

            var specialActionDefinitions = new List<SpecialActionDefinition>(specialActions.Length);
            for (var i = 0; i < specialActions.Length; i++)
            {
                specialActionDefinitions.Add(specialActions[i].CreateDefinition());
            }

            return new CityStyleSpecialActionDefinitionSet(
                cityStyleDefinitions.AsReadOnly(),
                specialActionDefinitions.AsReadOnly());
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (cityStyles == null || cityStyles.Length != ExpectedCityStyleCount)
            {
                reason = "城市样式定义数量必须为 6。";
                return false;
            }

            if (specialActions == null || specialActions.Length != ExpectedSpecialActionCount)
            {
                reason = "特殊行动定义数量必须为 5。";
                return false;
            }

            var cityStylesById = new Dictionary<string, SerializedCityStyleDefinition>(StringComparer.Ordinal);
            for (var i = 0; i < cityStyles.Length; i++)
            {
                var definition = cityStyles[i];
                if (definition == null)
                {
                    reason = "城市样式定义存在空记录。";
                    return false;
                }

                if (!definition.TryValidate(out reason))
                {
                    return false;
                }

                var expectedActionId = ExpectedSpecialActionId(definition.cityStyleId);
                if (expectedActionId == null || definition.specialActionId != expectedActionId)
                {
                    reason = definition.cityStyleId + " 不是稳定 ID 或特殊行动映射不正确。";
                    return false;
                }

                if (cityStylesById.ContainsKey(definition.cityStyleId))
                {
                    reason = "城市样式定义包含重复 ID：" + definition.cityStyleId;
                    return false;
                }

                cityStylesById.Add(definition.cityStyleId, definition);
            }

            var specialActionsById = new Dictionary<string, SerializedSpecialActionDefinition>(StringComparer.Ordinal);
            for (var i = 0; i < specialActions.Length; i++)
            {
                var definition = specialActions[i];
                if (definition == null)
                {
                    reason = "特殊行动定义存在空记录。";
                    return false;
                }

                if (!definition.TryValidate(out reason))
                {
                    return false;
                }

                var expectedCityStyleId = ExpectedCityStyleId(definition.specialActionId);
                if (expectedCityStyleId == null || definition.cityStyleId != expectedCityStyleId)
                {
                    reason = definition.specialActionId + " 不是稳定 ID 或城市样式映射不正确。";
                    return false;
                }

                if (specialActionsById.ContainsKey(definition.specialActionId))
                {
                    reason = "特殊行动定义包含重复 ID：" + definition.specialActionId;
                    return false;
                }

                specialActionsById.Add(definition.specialActionId, definition);
            }

            if (!ContainsEveryStableId(cityStylesById, specialActionsById, out reason))
            {
                return false;
            }

            foreach (var action in specialActionsById.Values)
            {
                SerializedCityStyleDefinition style;
                if (!cityStylesById.TryGetValue(action.cityStyleId, out style) ||
                    style.specialActionId != action.specialActionId)
                {
                    reason = action.specialActionId + " 未与城市样式形成双向映射。";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (string.IsNullOrEmpty(UnityEditor.AssetDatabase.GetAssetPath(this)))
            {
                return;
            }

            if (!TryValidateConfiguration(out var reason))
            {
                Debug.LogError("[CityStyleSpecialActionCatalog] " + reason, this);
            }
        }
#endif

        private void OnEnable()
        {
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(UnityEditor.AssetDatabase.GetAssetPath(this)))
            {
                return;
            }
#endif
            if (!TryValidateConfiguration(out var reason))
            {
                Debug.LogError("[CityStyleSpecialActionCatalog] " + reason, this);
            }
        }

        private static bool ContainsEveryStableId(
            IReadOnlyDictionary<string, SerializedCityStyleDefinition> cityStyleDefinitions,
            IReadOnlyDictionary<string, SerializedSpecialActionDefinition> specialActionDefinitions,
            out string reason)
        {
            var cityIds = new[]
            {
                CityStyleDatabase.MilitaryIndustrialArea,
                CityStyleDatabase.MobilizationSupportSystem,
                CityStyleDatabase.CompositePowerSystem,
                CityStyleDatabase.MaterialRelayStation,
                CityStyleDatabase.SourceStoneIndustrialHub,
                CityStyleDatabase.EfficientMobileManagementSystem
            };
            for (var i = 0; i < cityIds.Length; i++)
            {
                if (!cityStyleDefinitions.ContainsKey(cityIds[i]))
                {
                    reason = "城市样式目录缺少稳定 ID：" + cityIds[i];
                    return false;
                }
            }

            var actionIds = new[]
            {
                SpecialActionDatabase.MilitaryIndustrialArea,
                SpecialActionDatabase.MobilizationSupportSystem,
                SpecialActionDatabase.CompositePowerSystem,
                SpecialActionDatabase.SourceStoneIndustrialHub,
                SpecialActionDatabase.EfficientMobileManagementSystem
            };
            for (var i = 0; i < actionIds.Length; i++)
            {
                if (!specialActionDefinitions.ContainsKey(actionIds[i]))
                {
                    reason = "特殊行动目录缺少稳定 ID：" + actionIds[i];
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        private static string ExpectedSpecialActionId(string cityStyleId)
        {
            switch (cityStyleId)
            {
                case CityStyleDatabase.MilitaryIndustrialArea:
                    return SpecialActionDatabase.MilitaryIndustrialArea;
                case CityStyleDatabase.MobilizationSupportSystem:
                    return SpecialActionDatabase.MobilizationSupportSystem;
                case CityStyleDatabase.CompositePowerSystem:
                    return SpecialActionDatabase.CompositePowerSystem;
                case CityStyleDatabase.MaterialRelayStation:
                    return string.Empty;
                case CityStyleDatabase.SourceStoneIndustrialHub:
                    return SpecialActionDatabase.SourceStoneIndustrialHub;
                case CityStyleDatabase.EfficientMobileManagementSystem:
                    return SpecialActionDatabase.EfficientMobileManagementSystem;
                default:
                    return null;
            }
        }

        private static string ExpectedCityStyleId(string specialActionId)
        {
            switch (specialActionId)
            {
                case SpecialActionDatabase.MilitaryIndustrialArea:
                    return CityStyleDatabase.MilitaryIndustrialArea;
                case SpecialActionDatabase.MobilizationSupportSystem:
                    return CityStyleDatabase.MobilizationSupportSystem;
                case SpecialActionDatabase.CompositePowerSystem:
                    return CityStyleDatabase.CompositePowerSystem;
                case SpecialActionDatabase.SourceStoneIndustrialHub:
                    return CityStyleDatabase.SourceStoneIndustrialHub;
                case SpecialActionDatabase.EfficientMobileManagementSystem:
                    return CityStyleDatabase.EfficientMobileManagementSystem;
                default:
                    return null;
            }
        }
    }
}
