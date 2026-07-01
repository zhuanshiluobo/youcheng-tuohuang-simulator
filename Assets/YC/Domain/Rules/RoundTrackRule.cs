using YC.Domain.State;

namespace YC.Domain.Rules
{
    public static class RoundTrackRule
    {
        public const int FirstRoundIndex = 1;
        public const int FinalIndex = 9;

        public static int GetRoundIndex(GameState state)
        {
            if (IsFinalState(state))
            {
                return FinalIndex;
            }

            var round = state == null ? FirstRoundIndex : state.Round;
            if (round < FirstRoundIndex)
            {
                return FirstRoundIndex;
            }

            return round > FinalIndex - 1 ? FinalIndex - 1 : round;
        }

        public static bool IsFinalState(GameState state)
        {
            return state != null &&
                   (state.Phase == GamePhase.FinalScoring || state.Phase == GamePhase.GameOver);
        }
    }
}
