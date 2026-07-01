using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.CardFlows
{
    public interface ICardPoolService
    {
        void InitializePool(DeckRuntimeState decks, string poolId, IReadOnlyList<string> cardIds, int seed);
        string Peek(DeckRuntimeState decks, string poolId);
        string Draw(DeckRuntimeState decks, string poolId);
        int RemainingCount(DeckRuntimeState decks, string poolId);
    }
}
