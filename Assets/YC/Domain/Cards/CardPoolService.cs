using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.CardFlows
{
    public sealed class CardPoolService : ICardPoolService
    {
        public const string FacilityPoolId = "facility";

        public void InitializePool(DeckRuntimeState decks, string poolId, IReadOnlyList<string> cardIds, int seed)
        {
            if (decks == null)
            {
                throw new ArgumentNullException(nameof(decks));
            }

            var targetPool = GetOrCreatePool(decks, poolId);
            targetPool.Clear();
            if (cardIds != null)
            {
                for (var i = 0; i < cardIds.Count; i++)
                {
                    targetPool.Add(cardIds[i]);
                }
            }

            Shuffle(targetPool, new Random(seed));
        }

        public string Peek(DeckRuntimeState decks, string poolId)
        {
            if (decks == null)
            {
                throw new ArgumentNullException(nameof(decks));
            }

            var targetPool = GetOrCreatePool(decks, poolId);
            if (targetPool.Count <= 0)
            {
                throw new InvalidOperationException("No cards remaining in card pool " + poolId + ".");
            }

            return targetPool[targetPool.Count - 1];
        }

        public string Draw(DeckRuntimeState decks, string poolId)
        {
            if (decks == null)
            {
                throw new ArgumentNullException(nameof(decks));
            }

            var targetPool = GetOrCreatePool(decks, poolId);
            if (targetPool.Count <= 0)
            {
                throw new InvalidOperationException("No cards remaining in card pool " + poolId + ".");
            }

            var lastIndex = targetPool.Count - 1;
            var cardId = targetPool[lastIndex];
            targetPool.RemoveAt(lastIndex);
            return cardId;
        }

        public int RemainingCount(DeckRuntimeState decks, string poolId)
        {
            if (decks == null)
            {
                return 0;
            }

            return GetOrCreatePool(decks, poolId).Count;
        }

        private static void Shuffle(List<string> list, Random random)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                var temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }
        }

        private static List<string> GetOrCreatePool(DeckRuntimeState decks, string poolId)
        {
            switch (poolId)
            {
                case FacilityPoolId:
                    return decks.FacilityDeck;
                case EventCardPoolIds.EventGreen:
                    return decks.EventDeckGreen;
                case EventCardPoolIds.EventYellow:
                    return decks.EventDeckYellow;
                case EventCardPoolIds.EventRed:
                    return decks.EventDeckRed;
            }

            if (decks.CardPools == null)
            {
                decks.CardPools = new List<CardPoolState>();
            }

            for (var i = 0; i < decks.CardPools.Count; i++)
            {
                var pool = decks.CardPools[i];
                if (pool != null && pool.PoolId == poolId)
                {
                    return pool.RemainingCardIds;
                }
            }

            var created = new CardPoolState
            {
                PoolId = poolId ?? string.Empty
            };
            decks.CardPools.Add(created);
            return created.RemainingCardIds;
        }
    }
}
