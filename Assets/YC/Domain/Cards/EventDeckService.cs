using System;
using System.Collections.Generic;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    public sealed class EventDeckService
    {
        private readonly Random random;

        public EventDeckService() : this(new Random()) { }

        public EventDeckService(Random random)
        {
            this.random = random ?? throw new ArgumentNullException(nameof(random));
        }

        public void InitializeDecks(
            DeckRuntimeState decks,
            IReadOnlyList<string> greenCardIds,
            IReadOnlyList<string> yellowCardIds,
            IReadOnlyList<string> redCardIds)
        {
            if (decks == null) throw new ArgumentNullException(nameof(decks));

            decks.EventDeckGreen.Clear();
            decks.EventDeckGreen.AddRange(greenCardIds);
            Shuffle(decks.EventDeckGreen);

            decks.EventDeckYellow.Clear();
            decks.EventDeckYellow.AddRange(yellowCardIds);
            Shuffle(decks.EventDeckYellow);

            decks.EventDeckRed.Clear();
            decks.EventDeckRed.AddRange(redCardIds);
            Shuffle(decks.EventDeckRed);
        }

        public string Draw(DeckRuntimeState decks, EventColor color)
        {
            if (decks == null) throw new ArgumentNullException(nameof(decks));

            var targetDeck = GetDeck(decks, color);
            if (targetDeck.Count == 0)
            {
                throw new InvalidOperationException(string.Format("No cards remaining in {0} event deck.", color));
            }

            var lastIndex = targetDeck.Count - 1;
            var cardId = targetDeck[lastIndex];
            targetDeck.RemoveAt(lastIndex);
            return cardId;
        }

        public int RemainingCount(DeckRuntimeState decks, EventColor color)
        {
            if (decks == null) return 0;
            return GetDeck(decks, color).Count;
        }

        private void Shuffle(List<string> list)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                var temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }
        }

        private static List<string> GetDeck(DeckRuntimeState decks, EventColor color)
        {
            switch (color)
            {
                case EventColor.Green:
                    return decks.EventDeckGreen;
                case EventColor.Yellow:
                    return decks.EventDeckYellow;
                case EventColor.Red:
                    return decks.EventDeckRed;
                default:
                    throw new ArgumentOutOfRangeException(nameof(color), color, null);
            }
        }
    }
}
