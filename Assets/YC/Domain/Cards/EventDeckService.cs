using System;
using System.Collections.Generic;
using YC.Domain.CardFlows;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    public sealed class EventDeckService
    {
        public const int DefaultSeed = 20260615;

        private readonly Random random;
        private readonly GameState explicitSeedState;
        private readonly ICardPoolService cardPoolService;

        public EventDeckService() : this(DefaultSeed) { }

        public EventDeckService(int seed)
        {
            explicitSeedState = GameplayRandomRuntime.CreateExplicitSeedState(seed);
            cardPoolService = new CardPoolService();
        }

        public EventDeckService(Random random)
            : this(random, new CardPoolService())
        {
        }

        public EventDeckService(Random random, ICardPoolService cardPoolService)
        {
            this.random = random ?? throw new ArgumentNullException(nameof(random));
            this.cardPoolService = cardPoolService ?? throw new ArgumentNullException(nameof(cardPoolService));
        }

        public void InitializeDecks(GameState state, IReadOnlyList<string> greenCardIds,
            IReadOnlyList<string> yellowCardIds, IReadOnlyList<string> redCardIds, string mapId = StaticMapDefinitions.FourPlayerMapId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!GameplayRandomRuntime.TryValidate(state.RandomRuntime, state.GameId, state.EventDeckSeed, out var reason))
                throw new InvalidOperationException(reason);
            if (mapId != StaticMapDefinitions.ThreePlayerMapId && mapId != StaticMapDefinitions.FourPlayerMapId)
                throw new ArgumentException("未知的正式地图 ID：" + mapId, nameof(mapId));
            var pools = cardPoolService as CardPoolService ?? throw new InvalidOperationException("stateful_card_pool_required");
            pools.InitializePool(state, EventCardPoolIds.EventGreen, FilterCardIds(greenCardIds, mapId));
            pools.InitializePool(state, EventCardPoolIds.EventYellow, FilterCardIds(yellowCardIds, mapId));
            pools.InitializePool(state, EventCardPoolIds.EventRed, FilterCardIds(redCardIds, mapId));
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
            string mapId = StaticMapDefinitions.FourPlayerMapId)
        {
            if (decks == null) throw new ArgumentNullException(nameof(decks));
            if (mapId != StaticMapDefinitions.ThreePlayerMapId && mapId != StaticMapDefinitions.FourPlayerMapId)
                throw new ArgumentException("未知的正式地图 ID：" + mapId, nameof(mapId));

            if (explicitSeedState != null)
            {
                explicitSeedState.Decks = decks;
                InitializeDecks(explicitSeedState, greenCardIds, yellowCardIds, redCardIds, mapId);
                return;
            }
            // 显式 Random 注入保留；正式入口使用 GameState 重载。
            cardPoolService.InitializePool(decks, EventCardPoolIds.EventGreen, FilterCardIds(greenCardIds, mapId), random.Next());
            cardPoolService.InitializePool(decks, EventCardPoolIds.EventYellow, FilterCardIds(yellowCardIds, mapId), random.Next());
            cardPoolService.InitializePool(decks, EventCardPoolIds.EventRed, FilterCardIds(redCardIds, mapId), random.Next());
        }

        [Obsolete("请使用 MapId 重载；人数不能决定所选地图的事件牌池。")]
        public void InitializeDecks(DeckRuntimeState decks, IReadOnlyList<string> greenCardIds,
            IReadOnlyList<string> yellowCardIds, IReadOnlyList<string> redCardIds, int playerCount)
            => InitializeDecks(decks, greenCardIds, yellowCardIds, redCardIds,
                playerCount == 3 ? StaticMapDefinitions.ThreePlayerMapId : StaticMapDefinitions.FourPlayerMapId);

        private static IReadOnlyList<string> FilterCardIds(IReadOnlyList<string> cardIds, string mapId)
        {
            if (mapId != StaticMapDefinitions.ThreePlayerMapId || cardIds == null)
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
