using System;
using System.Collections.Generic;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    public static class EventCardDatabase
    {
        public const int ExpectedDefinitionCount = 22;

        private static readonly string[] CanonicalGreenCardIds =
        {
            "event_green_01", "event_green_02", "event_green_03",
            "event_green_04", "event_green_05", "event_green_06"
        };

        private static readonly string[] CanonicalRedCardIds =
        {
            "event_red_01", "event_red_02", "event_red_03",
            "event_red_04", "event_red_05", "event_red_06"
        };

        private static readonly string[] CanonicalYellowCardIds =
        {
            "event_yellow_01", "event_yellow_02", "event_yellow_03",
            "event_yellow_04", "event_yellow_05", "event_yellow_06",
            "event_yellow_07", "event_yellow_08", "event_yellow_09",
            "event_yellow_10"
        };

        // 保留公开可变 List 的源码与反射兼容；运行时目录使用独立规范数组和快照。
        public static readonly List<string> GreenCardIds =
            new List<string>(CanonicalGreenCardIds);
        public static readonly List<string> RedCardIds =
            new List<string>(CanonicalRedCardIds);
        public static readonly List<string> YellowCardIds =
            new List<string>(CanonicalYellowCardIds);

        private static readonly object Gate = new object();
        private static IReadOnlyDictionary<string, EventCardDefinition> cardsById;

        public static bool IsInitialized
        {
            get
            {
                lock (Gate)
                {
                    return cardsById != null;
                }
            }
        }

        public static void Initialize(IEnumerable<EventCardDefinition> sourceDefinitions)
        {
            if (sourceDefinitions == null)
            {
                throw new ArgumentNullException(nameof(sourceDefinitions));
            }

            var ordered = new List<EventCardDefinition>(ExpectedDefinitionCount);
            var next = new Dictionary<string, EventCardDefinition>(StringComparer.Ordinal);
            foreach (var source in sourceDefinitions)
            {
                ValidateDefinition(source);
                if (next.ContainsKey(source.CardId))
                {
                    throw new InvalidOperationException("事件卡目录包含重复 ID：" + source.CardId);
                }

                var clone = CloneDefinition(source);
                ordered.Add(clone);
                next.Add(clone.CardId, clone);
            }

            ValidateDefinitionSet(ordered);
            lock (Gate)
            {
                if (cardsById != null)
                {
                    if (DefinitionSetsEqual(cardsById, next))
                    {
                        return;
                    }

                    throw new InvalidOperationException(
                        "EventCardDatabase 已使用不同的完整事件卡目录初始化，禁止覆盖。");
                }

                cardsById = next;
            }
        }

        public static EventCardDefinition Get(string cardId)
        {
            IReadOnlyDictionary<string, EventCardDefinition> snapshot;
            lock (Gate)
            {
                snapshot = cardsById;
            }

            if (snapshot == null)
            {
                throw new InvalidOperationException(
                    "EventCardDatabase 未初始化：必须先由 EventCharacterCatalogBootstrap 注入完整目录。");
            }

            return !string.IsNullOrEmpty(cardId) && snapshot.TryGetValue(cardId, out var definition)
                ? CloneDefinition(definition)
                : null;
        }

        private static void ValidateDefinitionSet(IReadOnlyList<EventCardDefinition> definitions)
        {
            if (definitions.Count != ExpectedDefinitionCount)
            {
                throw new InvalidOperationException(
                    "事件卡目录必须精确包含 22 项，实际 " + definitions.Count + " 项。");
            }

            var offset = 0;
            ValidatePool(definitions, ref offset, CanonicalGreenCardIds, EventColor.Green);
            ValidatePool(definitions, ref offset, CanonicalRedCardIds, EventColor.Red);
            ValidatePool(definitions, ref offset, CanonicalYellowCardIds, EventColor.Yellow);
        }

        private static void ValidatePool(
            IReadOnlyList<EventCardDefinition> definitions,
            ref int offset,
            IReadOnlyList<string> expectedIds,
            EventColor expectedColor)
        {
            for (var i = 0; i < expectedIds.Count; i++, offset++)
            {
                var definition = definitions[offset];
                if (definition.CardId != expectedIds[i] || definition.Color != expectedColor)
                {
                    throw new InvalidOperationException(
                        "事件卡稳定顺序或颜色错误：位置 " + offset + " 应为 " + expectedIds[i] + "。");
                }
            }
        }

        private static void ValidateDefinition(EventCardDefinition definition)
        {
            if (definition == null)
            {
                throw new InvalidOperationException("事件卡目录包含空定义。");
            }

            if (string.IsNullOrEmpty(definition.CardId) || string.IsNullOrEmpty(definition.Name) ||
                string.IsNullOrEmpty(definition.Description))
            {
                throw new InvalidOperationException("事件卡缺少必需文本字段：" + definition.CardId);
            }

            if (!Enum.IsDefined(typeof(EventColor), definition.Color) ||
                !Enum.IsDefined(typeof(ResourceType), definition.ResourceType) ||
                !Enum.IsDefined(typeof(ResourceType), definition.RepresentativeResourceType) ||
                definition.ResourceAmount <= 0 || definition.RepresentativeResourceAmount <= 0)
            {
                throw new InvalidOperationException(definition.CardId + " 的颜色或代表资源字段无效。");
            }

            if (definition.ChoiceDescriptions == null || definition.ChoiceRewards == null ||
                definition.ChoicePendingEffects == null ||
                definition.ChoiceDescriptions.Count < 2 || definition.ChoiceDescriptions.Count > 3 ||
                definition.ChoiceDescriptions.Count != definition.ChoiceRewards.Count ||
                definition.ChoiceDescriptions.Count != definition.ChoicePendingEffects.Count)
            {
                throw new InvalidOperationException(definition.CardId + " 的选项结构不完整或数量不一致。");
            }

            for (var i = 0; i < definition.ChoiceDescriptions.Count; i++)
            {
                if (string.IsNullOrEmpty(definition.ChoiceDescriptions[i]))
                {
                    throw new InvalidOperationException(definition.CardId + " 包含空选项文本。");
                }

                ValidateResourceSet(definition.ChoiceRewards[i], definition.CardId + " 的选项奖励");
                var effects = definition.ChoicePendingEffects[i];
                if (effects == null)
                {
                    throw new InvalidOperationException(definition.CardId + " 包含空 pending effect 列表。");
                }

                for (var effectIndex = 0; effectIndex < effects.Count; effectIndex++)
                {
                    ValidateEffect(effects[effectIndex], definition.CardId);
                }
            }
        }

        private static void ValidateResourceSet(ResourceSet value, string label)
        {
            if (value == null || value.Originium < 0 || value.OriginiumShard < 0 || value.Iron < 0 ||
                value.PureOriginium < 0 || value.GoldVoucher < 0)
            {
                throw new InvalidOperationException(label + "为空或包含负数资源。");
            }
        }

        private static void ValidateEffect(EventEffect effect, string cardId)
        {
            if (effect == null || !Enum.IsDefined(typeof(EventEffectKind), effect.Kind) ||
                !Enum.IsDefined(typeof(EventEffectTargetScope), effect.TargetScope) ||
                !Enum.IsDefined(typeof(ResourceType), effect.ResourceType) ||
                !Enum.IsDefined(typeof(ResourceType), effect.CostResourceType) ||
                effect.Kind == EventEffectKind.None || effect.Amount <= 0 || effect.CostAmount < 0)
            {
                throw new InvalidOperationException(cardId + " 包含无效 pending effect。");
            }

            if (effect.Kind == EventEffectKind.GainScore &&
                effect.TargetScope != EventEffectTargetScope.Self)
            {
                throw new InvalidOperationException(cardId + " 的分数 effect 目标无效。");
            }

            if (effect.Kind == EventEffectKind.GrantResource &&
                effect.TargetScope != EventEffectTargetScope.Self &&
                effect.TargetScope != EventEffectTargetScope.Opponents)
            {
                throw new InvalidOperationException(cardId + " 的资源 effect 目标无效。");
            }

            if (effect.Kind == EventEffectKind.PlaceInfluence &&
                effect.TargetScope != EventEffectTargetScope.None &&
                effect.TargetScope != EventEffectTargetScope.CurrentLocationOrAdjacentRoute &&
                effect.TargetScope != EventEffectTargetScope.AdjacentRoute)
            {
                throw new InvalidOperationException(cardId + " 的影响力 effect 目标无效。");
            }
        }

        private static EventCardDefinition CloneDefinition(EventCardDefinition source)
        {
            var clone = new EventCardDefinition
            {
                CardId = source.CardId,
                Name = source.Name,
                Description = source.Description,
                Color = source.Color,
                ResourceType = source.ResourceType,
                ResourceAmount = source.ResourceAmount,
                RepresentativeResourceType = source.RepresentativeResourceType,
                RepresentativeResourceAmount = source.RepresentativeResourceAmount,
                ChoiceDescriptions = new List<string>(source.ChoiceDescriptions),
                ChoiceRewards = new List<ResourceSet>(source.ChoiceRewards.Count),
                ChoicePendingEffects = new List<List<EventEffect>>(source.ChoicePendingEffects.Count)
            };

            for (var i = 0; i < source.ChoiceRewards.Count; i++)
            {
                clone.ChoiceRewards.Add(source.ChoiceRewards[i].Clone());
                var effects = source.ChoicePendingEffects[i];
                var effectCopies = new List<EventEffect>(effects.Count);
                for (var effectIndex = 0; effectIndex < effects.Count; effectIndex++)
                {
                    var effect = effects[effectIndex];
                    effectCopies.Add(new EventEffect
                    {
                        Kind = effect.Kind,
                        ResourceType = effect.ResourceType,
                        Amount = effect.Amount,
                        TargetScope = effect.TargetScope,
                        CostResourceType = effect.CostResourceType,
                        CostAmount = effect.CostAmount
                    });
                }

                clone.ChoicePendingEffects.Add(effectCopies);
            }

            return clone;
        }

        private static bool DefinitionSetsEqual(
            IReadOnlyDictionary<string, EventCardDefinition> left,
            IReadOnlyDictionary<string, EventCardDefinition> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach (var pair in left)
            {
                if (!right.TryGetValue(pair.Key, out var candidate) ||
                    !DefinitionsEqual(pair.Value, candidate))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool DefinitionsEqual(EventCardDefinition left, EventCardDefinition right)
        {
            if (left.CardId != right.CardId || left.Name != right.Name ||
                left.Description != right.Description || left.Color != right.Color ||
                left.ResourceType != right.ResourceType || left.ResourceAmount != right.ResourceAmount ||
                left.RepresentativeResourceType != right.RepresentativeResourceType ||
                left.RepresentativeResourceAmount != right.RepresentativeResourceAmount ||
                left.ChoiceDescriptions.Count != right.ChoiceDescriptions.Count)
            {
                return false;
            }

            for (var i = 0; i < left.ChoiceDescriptions.Count; i++)
            {
                if (left.ChoiceDescriptions[i] != right.ChoiceDescriptions[i] ||
                    !ResourceSetsEqual(left.ChoiceRewards[i], right.ChoiceRewards[i]) ||
                    left.ChoicePendingEffects[i].Count != right.ChoicePendingEffects[i].Count)
                {
                    return false;
                }

                for (var effectIndex = 0; effectIndex < left.ChoicePendingEffects[i].Count; effectIndex++)
                {
                    if (!EffectsEqual(
                            left.ChoicePendingEffects[i][effectIndex],
                            right.ChoicePendingEffects[i][effectIndex]))
                    {
                        return false;
                    }
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

        private static bool EffectsEqual(EventEffect left, EventEffect right)
        {
            return left.Kind == right.Kind && left.ResourceType == right.ResourceType &&
                   left.Amount == right.Amount && left.TargetScope == right.TargetScope &&
                   left.CostResourceType == right.CostResourceType &&
                   left.CostAmount == right.CostAmount;
        }

        internal static void ResetForTests()
        {
            lock (Gate)
            {
                cardsById = null;
            }
        }
    }
}
