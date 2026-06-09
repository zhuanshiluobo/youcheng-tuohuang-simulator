using System;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    public static class PhaseFlow
    {
        public static GamePhase NextPhase(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            switch (state.Phase)
            {
                case GamePhase.Setup:
                    return GamePhase.Entrance;
                case GamePhase.Entrance:
                    return GamePhase.RoundStart;
                case GamePhase.RoundStart:
                    return GamePhase.CharacterCover;
                case GamePhase.CharacterCover:
                    return GamePhase.ActionRound1;
                case GamePhase.ActionRound1:
                    return GamePhase.ActionRound2;
                case GamePhase.ActionRound2:
                    return GamePhase.ResourceCollection;
                case GamePhase.ResourceCollection:
                    return GamePhase.Cleanup;
                case GamePhase.Cleanup:
                    return state.Round >= state.MaxRounds ? GamePhase.FinalScoring : GamePhase.RoundStart;
                case GamePhase.FinalScoring:
                    return GamePhase.GameOver;
                case GamePhase.GameOver:
                    return GamePhase.GameOver;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state.Phase), state.Phase, null);
            }
        }

        public static void Advance(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var nextPhase = NextPhase(state);
            if (state.Phase == GamePhase.Cleanup && nextPhase == GamePhase.RoundStart)
            {
                state.Round += 1;
                ResetRoundActionFlags(state);
            }

            state.Phase = nextPhase;
        }

        private static void ResetRoundActionFlags(GameState state)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                state.Players[i].HasMovedCityThisRound = false;
            }
        }
    }
}
