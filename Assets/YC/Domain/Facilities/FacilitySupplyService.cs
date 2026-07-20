using System;
using System.Collections.Generic;

namespace YC.Domain.Facilities
{
    /// <summary>
    /// 统一管理设施供应区与设施牌堆之间的牌张移动。
    /// </summary>
    public static class FacilitySupplyService
    {
        public const int SupplySize = 6;

        public static void Refill(List<string> supply, List<string> deck)
        {
            if (supply == null) throw new ArgumentNullException(nameof(supply));
            if (deck == null) throw new ArgumentNullException(nameof(deck));

            while (supply.Count < SupplySize && deck.Count > 0)
            {
                supply.Add(deck[0]);
                deck.RemoveAt(0);
            }
        }

        public static bool ReplaceBuiltCard(
            List<string> supply,
            List<string> deck,
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
                supply[supplyIndex] = deck[0];
                deck.RemoveAt(0);
            }
            else
            {
                supply.RemoveAt(supplyIndex);
            }

            Refill(supply, deck);
            return true;
        }

        public static bool ReturnSupplyCardToDeck(
            List<string> supply,
            List<string> deck,
            string facilityId)
        {
            if (supply == null) throw new ArgumentNullException(nameof(supply));
            if (deck == null) throw new ArgumentNullException(nameof(deck));

            if (string.IsNullOrEmpty(facilityId) || !supply.Remove(facilityId))
            {
                return false;
            }

            deck.Add(facilityId);
            Refill(supply, deck);
            return true;
        }
    }
}
