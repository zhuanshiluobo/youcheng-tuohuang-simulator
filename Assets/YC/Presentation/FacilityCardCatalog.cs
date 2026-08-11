using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Presentation
{
    [CreateAssetMenu(fileName = "FacilityCardCatalog", menuName = "YC/Facility Card Catalog")]
    public sealed class FacilityCardCatalog : ScriptableObject
    {
        public const int ExpectedDefinitionCount = 46;
        public const int ExpectedDefaultSupplyCount = 41;
        public const int ExpectedReserveCount = 4;
        public const int ExpectedSupplementalCount = 1;

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

#if UNITY_EDITOR
            public static SerializedResourceSet From(ResourceSet value)
            {
                value = value ?? new ResourceSet();
                return new SerializedResourceSet
                {
                    originium = value.Originium,
                    originiumShard = value.OriginiumShard,
                    iron = value.Iron,
                    pureOriginium = value.PureOriginium,
                    goldVoucher = value.GoldVoucher
                };
            }
#endif

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
        private sealed class SerializedDefinition
        {
            public string facilityId = string.Empty;
            public string manifestId = string.Empty;
            public string name = string.Empty;
            public string color = string.Empty;
            public int score;
            public SerializedResourceSet resourceCost = new SerializedResourceSet();
            public int goldVoucherCost;
            public bool defaultSupply;
            public bool reserveOnly;
            public bool hasEntryEffect;
            public List<string> keywords = new List<string>();
            public string effectId = string.Empty;
            public string effectType = string.Empty;
            public string description = string.Empty;
            public string effectText = string.Empty;
            public SerializedResourceSet onBuiltReward = new SerializedResourceSet();

            public FacilityCardDefinition CreateDefinition()
            {
                var keywordCopy = keywords == null
                    ? new List<string>()
                    : new List<string>(keywords);
                var unique = keywordCopy.Contains(FacilityCardKeywords.Unique);
                return new FacilityCardDefinition
                {
                    FacilityId = facilityId,
                    ManifestId = manifestId,
                    Name = name,
                    Color = color,
                    Score = score,
                    ResourceCost = resourceCost.CreateResourceSet(),
                    GoldVoucherCost = goldVoucherCost,
                    Unique = unique,
                    UniqueGroupId = unique ? name : string.Empty,
                    HasEntryEffect = hasEntryEffect,
                    Keywords = keywordCopy,
                    EffectId = effectId,
                    EffectType = effectType,
                    Description = description,
                    EffectText = effectText,
                    ReserveOnly = reserveOnly,
                    OnBuiltReward = onBuiltReward.CreateResourceSet()
                };
            }

#if UNITY_EDITOR
            public static SerializedDefinition From(
                FacilityCardDefinition value,
                bool isDefaultSupply)
            {
                return new SerializedDefinition
                {
                    facilityId = value.FacilityId ?? string.Empty,
                    manifestId = value.ManifestId ?? string.Empty,
                    name = value.Name ?? string.Empty,
                    color = value.Color ?? string.Empty,
                    score = value.Score,
                    resourceCost = SerializedResourceSet.From(value.ResourceCost),
                    goldVoucherCost = value.GoldVoucherCost,
                    defaultSupply = isDefaultSupply,
                    reserveOnly = value.ReserveOnly,
                    hasEntryEffect = value.HasEntryEffect,
                    keywords = value.Keywords == null
                        ? new List<string>()
                        : new List<string>(value.Keywords),
                    effectId = value.EffectId ?? string.Empty,
                    effectType = value.EffectType ?? string.Empty,
                    description = value.Description ?? string.Empty,
                    effectText = value.EffectText ?? string.Empty,
                    onBuiltReward = SerializedResourceSet.From(value.OnBuiltReward)
                };
            }
#endif

            public bool TryValidate(out string reason)
            {
                if (string.IsNullOrEmpty(facilityId) || string.IsNullOrEmpty(manifestId))
                {
                    reason = "设施定义缺少 facilityId 或 manifestId。";
                    return false;
                }

                if (manifestId != facilityId)
                {
                    reason = facilityId + " 的 manifestId 不一致。";
                    return false;
                }

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(color) ||
                    string.IsNullOrEmpty(effectId) || string.IsNullOrEmpty(effectType))
                {
                    reason = facilityId + " 缺少必需文本字段。";
                    return false;
                }

                if (resourceCost == null || onBuiltReward == null || keywords == null)
                {
                    reason = facilityId + " 缺少资源或关键词结构。";
                    return false;
                }

                if (goldVoucherCost < 0)
                {
                    reason = facilityId + " 的金券成本不能为负数。";
                    return false;
                }

                if (!resourceCost.TryValidate(facilityId + " 的建造成本", out reason) ||
                    !onBuiltReward.TryValidate(facilityId + " 的建造奖励", out reason))
                {
                    return false;
                }

                var keywordSet = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < keywords.Count; i++)
                {
                    if (string.IsNullOrEmpty(keywords[i]) || !keywordSet.Add(keywords[i]))
                    {
                        reason = facilityId + " 包含空或重复关键词。";
                        return false;
                    }
                }

                reason = string.Empty;
                return true;
            }
        }

        [SerializeField] private string sourceSha256 = string.Empty;
        [SerializeField] private SerializedDefinition[] definitions = Array.Empty<SerializedDefinition>();

        public string SourceSha256 => sourceSha256;
        public int DefinitionCount => definitions == null ? 0 : definitions.Length;

        public IReadOnlyList<FacilityCardDefinition> CreateDefinitions()
        {
            if (!TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException("FacilityCardCatalog 无效：" + reason);
            }

            var result = new List<FacilityCardDefinition>(definitions.Length);
            for (var i = 0; i < definitions.Length; i++)
            {
                result.Add(definitions[i].CreateDefinition());
            }

            return result.AsReadOnly();
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (!IsSha256(sourceSha256))
            {
                reason = "源 JSON SHA-256 无效。";
                return false;
            }

            if (definitions == null || definitions.Length != ExpectedDefinitionCount)
            {
                reason = "设施定义数量必须为 " + ExpectedDefinitionCount + "。";
                return false;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var defaultCount = 0;
            var reserveCount = 0;
            var supplementalCount = 0;
            for (var i = 0; i < definitions.Length; i++)
            {
                var definition = definitions[i];
                if (definition == null)
                {
                    reason = "设施定义存在空记录。";
                    return false;
                }

                if (!definition.TryValidate(out reason))
                {
                    return false;
                }

                if (!ids.Add(definition.facilityId))
                {
                    reason = "设施定义 ID 重复：" + definition.facilityId;
                    return false;
                }

                if (definition.defaultSupply)
                {
                    defaultCount++;
                }

                if (definition.reserveOnly)
                {
                    reserveCount++;
                }

                if (!definition.defaultSupply && !definition.reserveOnly)
                {
                    supplementalCount++;
                }

                var buildingId = definition.facilityId.StartsWith("building_", StringComparison.Ordinal);
                var reserveId = definition.facilityId.StartsWith("reserve_", StringComparison.Ordinal);
                var supplementalId = definition.facilityId == FacilityCardDatabase.EnterpriseOffice;
                if ((buildingId && (!definition.defaultSupply || definition.reserveOnly)) ||
                    (reserveId && (definition.defaultSupply || !definition.reserveOnly)) ||
                    (supplementalId && (definition.defaultSupply || definition.reserveOnly)) ||
                    (!buildingId && !reserveId && !supplementalId))
                {
                    reason = "设施分类与稳定 ID 不一致：" + definition.facilityId;
                    return false;
                }
            }

            if (defaultCount != ExpectedDefaultSupplyCount || reserveCount != ExpectedReserveCount ||
                supplementalCount != ExpectedSupplementalCount)
            {
                reason = "设施分类数量错误：default=" + defaultCount + "，reserve=" + reserveCount +
                         "，supplemental=" + supplementalCount + "。";
                return false;
            }

            for (var index = 1; index <= ExpectedDefaultSupplyCount; index++)
            {
                if (!ids.Contains("building_" + index.ToString("000")))
                {
                    reason = "缺少默认设施 ID：building_" + index.ToString("000");
                    return false;
                }
            }

            for (var index = 1; index <= ExpectedReserveCount; index++)
            {
                if (!ids.Contains("reserve_" + index.ToString("000")))
                {
                    reason = "缺少 reserve 设施 ID：reserve_" + index.ToString("000");
                    return false;
                }
            }

            if (!ids.Contains(FacilityCardDatabase.EnterpriseOffice))
            {
                reason = "缺少企业办事处补充定义。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            string sourceHash,
            IReadOnlyList<FacilityCardDefinition> sourceDefinitions,
            ISet<string> defaultSupplyIds)
        {
            if (sourceDefinitions == null)
            {
                throw new ArgumentNullException(nameof(sourceDefinitions));
            }

            sourceSha256 = (sourceHash ?? string.Empty).ToUpperInvariant();
            definitions = new SerializedDefinition[sourceDefinitions.Count];
            for (var i = 0; i < sourceDefinitions.Count; i++)
            {
                var definition = sourceDefinitions[i];
                if (definition == null)
                {
                    throw new InvalidOperationException("Editor 输入包含空设施定义。");
                }

                definitions[i] = SerializedDefinition.From(
                    definition,
                    defaultSupplyIds != null && defaultSupplyIds.Contains(definition.FacilityId));
            }
        }

        private void OnValidate()
        {
            if (!TryValidateConfiguration(out var reason))
            {
                Debug.LogError("[FacilityCardCatalog] " + reason, this);
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
                Debug.LogError("[FacilityCardCatalog] " + reason, this);
            }
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var character = value[i];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'A' && character <= 'F') ||
                      (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
