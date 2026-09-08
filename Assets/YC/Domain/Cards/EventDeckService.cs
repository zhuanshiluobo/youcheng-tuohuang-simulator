using System;
using System.Collections.Generic;
using YC.Domain.CardFlows;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    public sealed class EventDeckService
    {
        public const int DefaultSeed = 20260615;

        private readonly Random random;
        private readonly ICardPoolService cardPoolService;

        public EventDeckService() : this(DefaultSeed) { }

        public EventDeckService(int seed) : this(new Random(seed), new CardPoolService()) { }

        public EventDeckService(Random random)
            : this(random, new CardPoolService())
        {
        }

        public EventDeckService(Random random, ICardPoolService cardPoolService)
        {
            this.random = random ?? throw new ArgumentNullException(nameof(random));
            this.cardPoolService = cardPoolService ?? throw new ArgumentNullException(nameof(cardPoolService));
        }

        public static int CreateSeed(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return DefaultSeed;
            }

            unchecked
            {
                var hash = 2166136261u;
                for (var i = 0; i < source.Length; i++)
                {
                    hash ^= source[i];
                    hash *= 16777619u;
                }

                return (int)(hash & 0x7fffffff);
            }
        }

        public void InitializeDecks(
            DeckRuntimeState decks,
            IReadOnlyList<string> greenCardIds,
            IReadOnlyList<string> yellowCardIds,
            IReadOnlyList<string> redCardIds,
            int playerCount = 4)
        {
            if (decks == null) throw new ArgumentNullException(nameof(decks));

            // 三人先过滤源列表再洗牌；不改共享目录，其他人数保持原牌池和随机序列。
            cardPoolService.InitializePool(decks, EventCardPoolIds.EventGreen, FilterCardIds(greenCardIds, playerCount), random.Next());
            cardPoolService.InitializePool(decks, EventCardPoolIds.EventYellow, FilterCardIds(yellowCardIds, playerCount), random.Next());
            cardPoolService.InitializePool(decks, EventCardPoolIds.EventRed, FilterCardIds(redCardIds, playerCount), random.Next());
        }

        private static IReadOnlyList<string> FilterCardIds(IReadOnlyList<string> cardIds, int playerCount)
        {
            if (playerCount != 3 || cardIds == null)
            {
                return cardIds;
            }

            var filtered = new List<string>(cardIds.Count);
            for (var i = 0; i < cardIds.Count; i++)
            {
                if (!EventCardDatabase.IsFourPlayerOnly(cardIds[i]))
                {
                    filtered.Add(cardIds[i]);
                }
            }

            return filtered;
        }

        public string Draw(DeckRuntimeState decks, EventColor color)
        {
            if (decks == null) throw new ArgumentNullException(nameof(decks));
            return cardPoolService.Draw(decks, EventCardPoolIds.FromColor(color));
        }

        public int RemainingCount(DeckRuntimeState decks, EventColor color)
        {
            return decks == null ? 0 : cardPoolService.RemainingCount(decks, EventCardPoolIds.FromColor(color));
        }

        public string Peek(DeckRuntimeState decks, EventColor color)
        {
            if (decks == null) throw new ArgumentNullException(nameof(decks));
            return cardPoolService.Peek(decks, EventCardPoolIds.FromColor(color));
        }
    }
}
