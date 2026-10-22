using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public interface ISettlementProjectionProvider
    {
        IReadOnlyList<CharacterSettlementOption> GetSettlementOptions(GameState state, GameStateViewer viewer);
    }
}
