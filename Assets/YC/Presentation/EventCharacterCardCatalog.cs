using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation
{
    [CreateAssetMenu(
        fileName = "EventCharacterCardCatalog",
        menuName = "YC/Event & Character Card Catalog")]
    public sealed class EventCharacterCardCatalog : ScriptableObject
    {
        public const int ExpectedEventDefinitionCount = 22;
        public const int ExpectedCharacterDefinitionCount = 5;

        private static readonly string[] CanonicalEventIds =
        {
            "event_green_01", "event_green_02", "event_green_03",
            "event_green_04", "event_green_05", "event_green_06",
            "event_red_01", "event_red_02", "event_red_03",
            "event_red_04", "event_red_05", "event_red_06",
            "event_yellow_01", "event_yellow_02", "event_yellow_03",
            "event_yellow_04", "event_yellow_05", "event_yellow_06",
            "event_yellow_07", "event_yellow_08", "event_yellow_09",
            "event_yellow_10"
        };

        private static readonly string[] CanonicalCharacterIds =
        {
            CharacterCardDatabase.Liskarm,
            CharacterCardDatabase.Elysium,
            CharacterCardDatabase.Texas,
            CharacterCardDatabase.Cannot,
            CharacterCardDatabase.TinMan
        };

        [Serializable]
        private sealed class SerializedResourceSet
        {
            public int originium;
            public int originiumShard;
            public int iron;
            public int pureOriginium;
            public int goldVoucher;

            public ResourceSet Create()
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
        }

        [Serializable]
        private sealed class SerializedEventEffect
        {
            public EventEffectKind kind;
            public ResourceType resourceType;
            public int amount;
            public EventEffectTargetScope targetScope;
            public ResourceType costResourceType;
            public int costAmount;

            public EventEffect Create()
            {
                return new EventEffect
                {
                    Kind = kind,
                    ResourceType = resourceType,
                    Amount = amount,
                    TargetScope = targetScope,
                    CostResourceType = costResourceType,
                    CostAmount = costAmount
                };
            }

            public bool TryValidate(string label, out string reason)
            {
                if (!Enum.IsDefined(typeof(EventEffectKind), kind) ||
                    !Enum.IsDefined(typeof(ResourceType), resourceType) ||
                    !Enum.IsDefined(typeof(EventEffectTargetScope), targetScope) ||
                    !Enum.IsDefined(typeof(ResourceType), costResourceType) ||
                    kind == EventEffectKind.None || amount <= 0 || costAmount < 0)
                {
                    reason = label + " 字段无效。";
                    return false;
                }

                if (kind == EventEffectKind.GainScore && targetScope != EventEffectTargetScope.Self)
                {
                    reason = label + " 的分数目标必须为 Self。";
                    return false;
                }

                if (kind == EventEffectKind.GrantResource &&
                    targetScope != EventEffectTargetScope.Self &&
                    targetScope != EventEffectTargetScope.Opponents)
                {
                    reason = label + " 的资源目标无效。";
                    return false;
                }

                if (kind == EventEffectKind.PlaceInfluence &&
                    targetScope != EventEffectTargetScope.None &&
                    targetScope != EventEffectTargetScope.CurrentLocationOrAdjacentRoute &&
                    targetScope != EventEffectTargetScope.AdjacentRoute)
                {
                    reason = label + " 的影响力目标无效。";
                    return false;
                }

                reason = string.Empty;
                return true;
            }

#if UNITY_EDITOR
            public static SerializedEventEffect From(EventEffect value)
            {
                return new SerializedEventEffect
                {
                    kind = value.Kind,
                    resourceType = value.ResourceType,
                    amount = value.Amount,
                    targetScope = value.TargetScope,
                    costResourceType = value.CostResourceType,
                    costAmount = value.CostAmount
                };
            }
#endif
        }

        [Serializable]
        private sealed class SerializedEventChoice
        {
            public string description = string.Empty;
            public SerializedResourceSet reward = new SerializedResourceSet();
            public SerializedEventEffect[] pendingEffects = Array.Empty<SerializedEventEffect>();

            public bool TryValidate(string cardId, int choiceIndex, out string reason)
            {
                var label = cardId + " 的选项 " + choiceIndex;
                if (string.IsNullOrEmpty(description) || reward == null || pendingEffects == null)
                {
                    reason = label + " 结构不完整。";
                    return false;
                }

                if (!reward.TryValidate(label + " 奖励", out reason))
                {
                    return false;
                }

                for (var i = 0; i < pendingEffects.Length; i++)
                {
                    if (pendingEffects[i] == null)
                    {
                        reason = label + " 包含空 effect。";
                        return false;
                    }

                    if (!pendingEffects[i].TryValidate(label + " 的 effect " + i, out reason))
                    {
                        return false;
                    }
                }

                reason = string.Empty;
                return true;
            }

#if UNITY_EDITOR
            public static SerializedEventChoice From(
                string description,
                ResourceSet reward,
                IReadOnlyList<EventEffect> pendingEffects)
            {
                var effects = pendingEffects == null
                    ? Array.Empty<SerializedEventEffect>()
                    : new SerializedEventEffect[pendingEffects.Count];
                if (pendingEffects != null)
                {
                    for (var i = 0; i < pendingEffects.Count; i++)
                    {
                        if (pendingEffects[i] == null)
                        {
                            throw new InvalidOperationException("Editor 输入包含空 event effect。");
                        }

                        effects[i] = SerializedEventEffect.From(pendingEffects[i]);
                    }
                }

                return new SerializedEventChoice
                {
                    description = description ?? string.Empty,
                    reward = SerializedResourceSet.From(reward),
                    pendingEffects = effects
                };
            }
#endif
        }

        [Serializable]
        private sealed class SerializedEventDefinition
        {
            public string cardId = string.Empty;
            public string name = string.Empty;
            [TextArea] public string description = string.Empty;
            public EventColor color;
            public ResourceType resourceType;
            public int resourceAmount;
            public ResourceType representativeResourceType;
            public int representativeResourceAmount;
            public SerializedEventChoice[] choices = Array.Empty<SerializedEventChoice>();

            public EventCardDefinition Create()
            {
                var result = new EventCardDefinition
                {
                    CardId = cardId,
                    Name = name,
                    Description = description,
                    Color = color,
                    ResourceType = resourceType,
                    ResourceAmount = resourceAmount,
                    RepresentativeResourceType = representativeResourceType,
                    RepresentativeResourceAmount = representativeResourceAmount
                };

                for (var i = 0; i < choices.Length; i++)
                {
                    var choice = choices[i];
                    result.ChoiceDescriptions.Add(choice.description);
                    result.ChoiceRewards.Add(choice.reward.Create());
                    var effects = new List<EventEffect>(choice.pendingEffects.Length);
                    for (var effectIndex = 0; effectIndex < choice.pendingEffects.Length; effectIndex++)
                    {
                        effects.Add(choice.pendingEffects[effectIndex].Create());
                    }

                    result.ChoicePendingEffects.Add(effects);
                }

                return result;
            }

            public bool TryValidate(out string reason)
            {
                if (string.IsNullOrEmpty(cardId) || string.IsNullOrEmpty(name) ||
                    string.IsNullOrEmpty(description) || !Enum.IsDefined(typeof(EventColor), color) ||
                    !Enum.IsDefined(typeof(ResourceType), resourceType) || resourceAmount <= 0 ||
                    !Enum.IsDefined(typeof(ResourceType), representativeResourceType) ||
                    representativeResourceAmount <= 0 || choices == null ||
                    choices.Length < 2 || choices.Length > 3)
                {
                    reason = cardId + " 的事件卡字段无效。";
                    return false;
                }

                for (var i = 0; i < choices.Length; i++)
                {
                    if (choices[i] == null)
                    {
                        reason = cardId + " 包含空选项。";
                        return false;
                    }

                    if (!choices[i].TryValidate(cardId, i, out reason))
                    {
                        return false;
                    }
                }

                reason = string.Empty;
                return true;
            }

#if UNITY_EDITOR
            public static SerializedEventDefinition From(EventCardDefinition value)
            {
                var choices = new SerializedEventChoice[value.ChoiceDescriptions.Count];
                for (var i = 0; i < choices.Length; i++)
                {
                    choices[i] = SerializedEventChoice.From(
                        value.ChoiceDescriptions[i],
                        value.ChoiceRewards[i],
                        value.ChoicePendingEffects[i]);
                }

                return new SerializedEventDefinition
                {
                    cardId = value.CardId ?? string.Empty,
                    name = value.Name ?? string.Empty,
                    description = value.Description ?? string.Empty,
                    color = value.Color,
                    resourceType = value.ResourceType,
                    resourceAmount = value.ResourceAmount,
                    representativeResourceType = value.RepresentativeResourceType,
                    representativeResourceAmount = value.RepresentativeResourceAmount,
                    choices = choices
                };
            }
#endif
        }

        [Serializable]
        private sealed class SerializedCharacterDefinition
        {
            public string templateId = string.Empty;
            public string cardId = string.Empty;
            public string name = string.Empty;
            public CharacterCardEffectKind strategyEffect;
            public CharacterCardEffectKind tacticEffect;

            public CharacterCardDefinition Create()
            {
                return new CharacterCardDefinition
                {
                    TemplateId = templateId,
                    CardId = cardId,
                    Name = name,
                    StrategyEffect = strategyEffect,
                    TacticEffect = tacticEffect
                };
            }

            public bool TryValidate(out string reason)
            {
                if (string.IsNullOrEmpty(templateId) || cardId != templateId ||
                    string.IsNullOrEmpty(name) ||
                    !Enum.IsDefined(typeof(CharacterCardEffectKind), strategyEffect) ||
                    !Enum.IsDefined(typeof(CharacterCardEffectKind), tacticEffect) ||
                    strategyEffect == CharacterCardEffectKind.Unsupported ||
                    tacticEffect == CharacterCardEffectKind.Unsupported ||
                    strategyEffect == tacticEffect)
                {
                    reason = templateId + " 的角色卡字段无效。";
                    return false;
                }

                reason = string.Empty;
                return true;
            }

#if UNITY_EDITOR
            public static SerializedCharacterDefinition From(CharacterCardDefinition value)
            {
                return new SerializedCharacterDefinition
                {
                    templateId = value.TemplateId ?? string.Empty,
                    cardId = value.CardId ?? string.Empty,
                    name = value.Name ?? string.Empty,
                    strategyEffect = value.StrategyEffect,
                    tacticEffect = value.TacticEffect
                };
            }
#endif
        }

        [SerializeField] private string sourceSha256 = string.Empty;
        [SerializeField] private SerializedEventDefinition[] eventDefinitions =
            Array.Empty<SerializedEventDefinition>();
        [SerializeField] private SerializedCharacterDefinition[] characterDefinitions =
            Array.Empty<SerializedCharacterDefinition>();

        public string SourceSha256 => sourceSha256;
        public int EventDefinitionCount => eventDefinitions == null ? 0 : eventDefinitions.Length;
        public int CharacterDefinitionCount => characterDefinitions == null ? 0 : characterDefinitions.Length;

        public IReadOnlyList<EventCardDefinition> CreateEventDefinitions()
        {
            EnsureValid();
            var result = new List<EventCardDefinition>(eventDefinitions.Length);
            for (var i = 0; i < eventDefinitions.Length; i++)
            {
                result.Add(eventDefinitions[i].Create());
            }

            return result.AsReadOnly();
        }

        public IReadOnlyList<CharacterCardDefinition> CreateCharacterDefinitions()
        {
            EnsureValid();
            var result = new List<CharacterCardDefinition>(characterDefinitions.Length);
            for (var i = 0; i < characterDefinitions.Length; i++)
            {
                result.Add(characterDefinitions[i].Create());
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

            if (eventDefinitions == null ||
                eventDefinitions.Length != ExpectedEventDefinitionCount)
            {
                reason = "事件卡定义数量必须为 " + ExpectedEventDefinitionCount + "。";
                return false;
            }

            if (characterDefinitions == null ||
                characterDefinitions.Length != ExpectedCharacterDefinitionCount)
            {
                reason = "角色卡定义数量必须为 " + ExpectedCharacterDefinitionCount + "。";
                return false;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < eventDefinitions.Length; i++)
            {
                var definition = eventDefinitions[i];
                if (definition == null)
                {
                    reason = "事件卡目录包含空定义。";
                    return false;
                }

                if (!definition.TryValidate(out reason))
                {
                    return false;
                }

                if (!ids.Add(definition.cardId) || definition.cardId != CanonicalEventIds[i] ||
                    definition.color != ExpectedEventColor(i))
                {
                    reason = "事件卡稳定 ID、顺序或颜色错误：位置 " + i + "。";
                    return false;
                }
            }

            ids.Clear();
            for (var i = 0; i < characterDefinitions.Length; i++)
            {
                var definition = characterDefinitions[i];
                if (definition == null)
                {
                    reason = "角色卡目录包含空定义。";
                    return false;
                }

                if (!definition.TryValidate(out reason))
                {
                    return false;
                }

                if (!ids.Add(definition.templateId) ||
                    definition.templateId != CanonicalCharacterIds[i])
                {
                    reason = "角色卡稳定模板 ID 或顺序错误：位置 " + i + "。";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            string sourceHash,
            IReadOnlyList<EventCardDefinition> sourceEvents,
            IReadOnlyList<CharacterCardDefinition> sourceCharacters)
        {
            if (sourceEvents == null)
            {
                throw new ArgumentNullException(nameof(sourceEvents));
            }

            if (sourceCharacters == null)
            {
                throw new ArgumentNullException(nameof(sourceCharacters));
            }

            sourceSha256 = (sourceHash ?? string.Empty).ToUpperInvariant();
            eventDefinitions = new SerializedEventDefinition[sourceEvents.Count];
            for (var i = 0; i < sourceEvents.Count; i++)
            {
                eventDefinitions[i] = SerializedEventDefinition.From(sourceEvents[i]);
            }

            characterDefinitions = new SerializedCharacterDefinition[sourceCharacters.Count];
            for (var i = 0; i < sourceCharacters.Count; i++)
            {
                characterDefinitions[i] = SerializedCharacterDefinition.From(sourceCharacters[i]);
            }
        }

        private void OnValidate()
        {
            if (!TryValidateConfiguration(out var reason))
            {
                Debug.LogError("[EventCharacterCardCatalog] " + reason, this);
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
                Debug.LogError("[EventCharacterCardCatalog] " + reason, this);
            }
        }

        private void EnsureValid()
        {
            if (!TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException("EventCharacterCardCatalog 无效：" + reason);
            }
        }

        private static EventColor ExpectedEventColor(int index)
        {
            if (index < 6)
            {
                return EventColor.Green;
            }

            return index < 12 ? EventColor.Red : EventColor.Yellow;
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
