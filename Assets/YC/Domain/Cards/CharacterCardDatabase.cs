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

        private static readonly string[] InitialTemplateIds =
        {
            Liskarm, Elysium, Texas, Cannot, TinMan
        };

        public static List<string> GetInitialCardIds(PlayerState player)
        {
            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            var ids = new List<string>(InitialTemplateIds.Length);
            for (var i = 0; i < InitialTemplateIds.Length; i++)
            {
                ids.Add(CreateOwnedCardId(player.Color, player.PlayerId, InitialTemplateIds[i]));
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
            var templateId = GetTemplateId(cardId);
            switch (templateId)
            {
                case Liskarm:
                    return Create(cardId, templateId, "雷蛇", CharacterCardEffectKind.LiskarmSecurityProtocol, CharacterCardEffectKind.LiskarmControlPosition);
                case Elysium:
                    return Create(cardId, templateId, "极境", CharacterCardEffectKind.ElysiumLogistics, CharacterCardEffectKind.ElysiumNavigation);
                case Texas:
                    return Create(cardId, templateId, "德克萨斯", CharacterCardEffectKind.TexasSpecialDelivery, CharacterCardEffectKind.TexasRemoveAndDoubleMove);
                case Cannot:
                    return Create(cardId, templateId, "坎诺特", CharacterCardEffectKind.CannotTradeChannel, CharacterCardEffectKind.CannotRequisition);
                case TinMan:
                    return Create(cardId, templateId, "锡人", CharacterCardEffectKind.TinManEstablishPrestige, CharacterCardEffectKind.TinManDeepPlanning);
                default:
                    return null;
            }
        }

        private static CharacterCardDefinition Create(
            string cardId,
            string templateId,
            string name,
            CharacterCardEffectKind strategy,
            CharacterCardEffectKind tactic)
        {
            return new CharacterCardDefinition
            {
                CardId = cardId,
                TemplateId = templateId,
                Name = name,
                StrategyEffect = strategy,
                TacticEffect = tactic
            };
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
