using System;
using System.Collections.Generic;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    public static class CharacterCardDatabase
    {
        public const string Liskarm = "liskarm";
        public const string Elysium = "elysium";
        public const string Texas = "texas";
        public const string Cannot = "cannot";
        public const string TinMan = "tin-man";

        public const int ExpectedTemplateCount = 5;

        private static readonly string[] CanonicalInitialTemplateIds =
        {
            Liskarm, Elysium, Texas, Cannot, TinMan
        };

        private static readonly object Gate = new object();
        private static IReadOnlyDictionary<string, CharacterCardDefinition> templatesById;
        private static IReadOnlyList<string> initialTemplateIds;

        public static bool IsInitialized
        {
            get
            {
                lock (Gate)
                {
                    return templatesById != null;
                }
            }
        }

        public static void Initialize(IEnumerable<CharacterCardDefinition> sourceDefinitions)
        {
            if (sourceDefinitions == null)
            {
                throw new ArgumentNullException(nameof(sourceDefinitions));
            }

            var orderedIds = new List<string>(ExpectedTemplateCount);
            var next = new Dictionary<string, CharacterCardDefinition>(StringComparer.Ordinal);
            foreach (var source in sourceDefinitions)
            {
                ValidateTemplate(source);
                if (next.ContainsKey(source.TemplateId))
                {
                    throw new InvalidOperationException(
                        "角色卡目录包含重复模板 ID：" + source.TemplateId);
                }

                orderedIds.Add(source.TemplateId);
                next.Add(source.TemplateId, CloneTemplate(source));
            }

            ValidateTemplateSet(orderedIds);
            lock (Gate)
            {
                if (templatesById != null)
                {
                    if (TemplateSetsEqual(templatesById, initialTemplateIds, next, orderedIds))
                    {
                        return;
                    }

                    throw new InvalidOperationException(
                        "CharacterCardDatabase 已使用不同的完整角色卡目录初始化，禁止覆盖。");
                }

                templatesById = next;
                initialTemplateIds = orderedIds.AsReadOnly();
            }
        }

        public static List<string> GetInitialCardIds(PlayerState player)
        {
            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            IReadOnlyList<string> templateIds;
            lock (Gate)
            {
                templateIds = initialTemplateIds;
            }

            if (templateIds == null)
            {
                throw new InvalidOperationException(
                    "CharacterCardDatabase 未初始化：必须先由 EventCharacterCatalogBootstrap 注入完整目录。");
            }

            var ids = new List<string>(templateIds.Count);
            for (var i = 0; i < templateIds.Count; i++)
            {
                ids.Add(CreateOwnedCardId(player.Color, player.PlayerId, templateIds[i]));
            }

            return ids;
        }

        public static void InitializePlayerHand(PlayerState player)
        {
            if (player == null || player.HandCardIds.Count > 0)
            {
                return;
            }

            player.HandCardIds.AddRange(GetInitialCardIds(player));
        }

        public static CharacterCardDefinition Get(string cardId)
        {
            IReadOnlyDictionary<string, CharacterCardDefinition> snapshot;
            lock (Gate)
            {
                snapshot = templatesById;
            }

            if (snapshot == null)
            {
                throw new InvalidOperationException(
                    "CharacterCardDatabase 未初始化：必须先由 EventCharacterCatalogBootstrap 注入完整目录。");
            }

            var templateId = GetTemplateId(cardId);
            if (string.IsNullOrEmpty(templateId) ||
                !snapshot.TryGetValue(templateId, out var template))
            {
                return null;
            }

            var definition = CloneTemplate(template);
            definition.CardId = cardId;
            return definition;
        }

        private static void ValidateTemplate(CharacterCardDefinition definition)
        {
            if (definition == null)
            {
                throw new InvalidOperationException("角色卡目录包含空定义。");
            }

            if (string.IsNullOrEmpty(definition.TemplateId) ||
                definition.CardId != definition.TemplateId ||
                string.IsNullOrEmpty(definition.Name) ||
                !Enum.IsDefined(typeof(CharacterCardEffectKind), definition.StrategyEffect) ||
                !Enum.IsDefined(typeof(CharacterCardEffectKind), definition.TacticEffect) ||
                definition.StrategyEffect == CharacterCardEffectKind.Unsupported ||
                definition.TacticEffect == CharacterCardEffectKind.Unsupported ||
                definition.StrategyEffect == definition.TacticEffect)
            {
                throw new InvalidOperationException(
                    "角色卡模板字段无效：" + (definition.TemplateId ?? string.Empty));
            }
        }

        private static void ValidateTemplateSet(IReadOnlyList<string> orderedIds)
        {
            if (orderedIds.Count != ExpectedTemplateCount)
            {
                throw new InvalidOperationException(
                    "角色卡目录必须精确包含 5 个模板，实际 " + orderedIds.Count + " 个。");
            }

            for (var i = 0; i < CanonicalInitialTemplateIds.Length; i++)
            {
                if (orderedIds[i] != CanonicalInitialTemplateIds[i])
                {
                    throw new InvalidOperationException(
                        "角色卡模板顺序错误：位置 " + i + " 应为 " +
                        CanonicalInitialTemplateIds[i] + "。");
                }
            }
        }

        private static CharacterCardDefinition CloneTemplate(
            CharacterCardDefinition source)
        {
            return new CharacterCardDefinition
            {
                CardId = source.CardId,
                TemplateId = source.TemplateId,
                Name = source.Name,
                StrategyEffect = source.StrategyEffect,
                TacticEffect = source.TacticEffect
            };
        }

        private static bool TemplateSetsEqual(
            IReadOnlyDictionary<string, CharacterCardDefinition> left,
            IReadOnlyList<string> leftOrder,
            IReadOnlyDictionary<string, CharacterCardDefinition> right,
            IReadOnlyList<string> rightOrder)
        {
            if (left.Count != right.Count || leftOrder.Count != rightOrder.Count)
            {
                return false;
            }

            for (var i = 0; i < leftOrder.Count; i++)
            {
                if (leftOrder[i] != rightOrder[i])
                {
                    return false;
                }
            }

            foreach (var pair in left)
            {
                if (!right.TryGetValue(pair.Key, out var candidate) ||
                    !TemplatesEqual(pair.Value, candidate))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TemplatesEqual(
            CharacterCardDefinition left,
            CharacterCardDefinition right)
        {
            return left.CardId == right.CardId &&
                   left.TemplateId == right.TemplateId &&
                   left.Name == right.Name &&
                   left.StrategyEffect == right.StrategyEffect &&
                   left.TacticEffect == right.TacticEffect;
        }

        internal static void ResetForTests()
        {
            lock (Gate)
            {
                templatesById = null;
                initialTemplateIds = null;
            }
        }

        private static string CreateOwnedCardId(PlayerColor color, int playerId, string templateId)
        {
            return "character." + color.ToString().ToLowerInvariant() + ".p" + playerId + "." + templateId;
        }

        private static string GetTemplateId(string cardId)
        {
            if (string.IsNullOrEmpty(cardId))
            {
                return string.Empty;
            }

            var separator = cardId.LastIndexOf('.');
            return separator < 0 || separator >= cardId.Length - 1
                ? string.Empty
                : cardId.Substring(separator + 1);
        }
    }
}
