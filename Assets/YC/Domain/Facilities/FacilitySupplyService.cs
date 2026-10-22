using System;
using System.Collections.Generic;
using YC.Domain.CardFlows;
using YC.Domain.State;
using YC.Domain.Items;
using YC.Domain.Effects;
using System.Linq;

namespace YC.Domain.Facilities
{
    /// <summary>
    /// 统一管理设施供应区与设施牌堆之间的牌张移动。
    /// </summary>
    public static class FacilitySupplyService
    {
        public const int SupplySize = 6;
        private static readonly ICardPoolService CardPool = new CardPoolService();

        // 实体路径只补指定槽。空堆成功无变化，已占用槽明确拒绝覆盖。
        public static ItemTransferResult RefillSlot(EffectExecutionContext context, string slotZoneId)
        {
            var query = new ItemZoneQuery();
            var slot = query.FindZone(context.State, slotZoneId);
            if (slot?.TemplateId != "core.zone.facility_supply_slot" || query.GetItemIds(context.State, slotZoneId).Count > 0)
                throw new InvalidOperationException("destination_rejected");
            var deck = ItemTransferEffectExecutor.FindZone(context.State, "facility_deck");
            var request = new ItemTransferRequest { ExecutingPlayerId = context.Node.PlayerId, ExpectedSource = new ItemPositionRef { ZoneId = deck.ZoneId }, Destination = new ZoneRef { ZoneId = slotZoneId }, InsertionKind = "unordered", OperationProfileId = "core.facility_card_transfer", ItemIds = query.GetTopItemIds(context.State, deck.ZoneId, 1).ToList() };
            return new ItemTransferService().RelocateItems(context, request);
        }

        public static void Initialize(
            DeckRuntimeState decks,
            IReadOnlyList<string> facilityCardIds,
            int seed)
        {
            if (decks == null) throw new ArgumentNullException(nameof(decks));

            decks.FacilitySupply.Clear();
            CardPool.InitializePool(
                decks,
                CardPoolService.FacilityPoolId,
                facilityCardIds,
                seed);
            Refill(decks.FacilitySupply, decks.FacilityDeck);
        }

        public static void Refill(IList<string> supply, IList<string> deck)
        {
            if (supply == null) throw new ArgumentNullException(nameof(supply));
            if (deck == null) throw new ArgumentNullException(nameof(deck));

            while (supply.Count < SupplySize && deck.Count > 0)
            {
                supply.Add(Draw(deck));
            }
        }

        public static bool ReplaceBuiltCard(
            IList<string> supply,
            IList<string> deck,
            string facilityId)
        {
            if (supply == null) throw new ArgumentNullException(nameof(supply));
            if (deck == null) throw new ArgumentNullException(nameof(deck));

            var supplyIndex = supply.IndexOf(facilityId);
            if (supplyIndex < 0)
            {
                return false;
            }

            if (deck.Count > 0)
            {
                supply[supplyIndex] = Draw(deck);
            }
            else
            {
                supply.RemoveAt(supplyIndex);
            }

            Refill(supply, deck);
            return true;
        }

        public static bool ReturnSupplyCardToDeck(
            IList<string> supply,
            IList<string> deck,
            string facilityId)
        {
            if (supply == null) throw new ArgumentNullException(nameof(supply));
            if (deck == null) throw new ArgumentNullException(nameof(deck));

            var supplyIndex = string.IsNullOrEmpty(facilityId)
                ? -1
                : supply.IndexOf(facilityId);
            if (supplyIndex < 0)
            {
                return false;
            }

            // CardPoolService 从列表末尾抽牌，因此退回牌堆的牌放到列表开头（牌堆底）。
            deck.Insert(0, facilityId);
            // 与正常建设补牌保持一致：新牌直接写回被选中的原槽位，其他供应牌不位移。
            supply[supplyIndex] = Draw(deck);
            Refill(supply, deck);
            return true;
        }

        private static string Draw(IList<string> deck)
        {
            if (deck.Count == 0) throw new InvalidOperationException("设施牌堆为空。");
            int index = deck.Count - 1;
            string cardId = deck[index];
            deck.RemoveAt(index);
            return cardId;
        }
    }
}
