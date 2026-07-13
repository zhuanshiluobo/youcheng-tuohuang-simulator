using System;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    public static class FederalCouncilEffectService
    {
        public static void RecordLatestBuilder(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.FindPlayer(playerId) == null)
            {
                throw new ArgumentException("联邦理事处的建造者必须存在于游戏中。", nameof(playerId));
            }

            state.LastFederalCouncilBuilderThisRoundPlayerId = playerId;
        }

        public static int ConsumeLatestBuilder(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var playerId = state.LastFederalCouncilBuilderThisRoundPlayerId;
            state.LastFederalCouncilBuilderThisRoundPlayerId = -1;
            return state.FindPlayer(playerId) != null ? playerId : -1;
        }
    }
}
