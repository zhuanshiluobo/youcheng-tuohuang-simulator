using System;
using YC.Domain.Commands;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    public static class CommandPhasePolicy
    {
        public static bool IsCommandAllowed(GamePhase phase, GameCommandKind kind)
        {
            switch (kind)
            {
                case GameCommandKind.ChooseStartPlayer:
                    return phase == GamePhase.Setup;
                case GameCommandKind.ChooseInitialLocation:
                case GameCommandKind.ResolveEntranceEvent:
                    return phase == GamePhase.Entrance;
                case GameCommandKind.CoverCharacterCard:
                    return phase == GamePhase.CharacterCover;
                case GameCommandKind.DeployInfluence:
                case GameCommandKind.DispatchInfluence:
                case GameCommandKind.ExploreLocation:
                case GameCommandKind.MoveCity:
                case GameCommandKind.BuildFacility:
                case GameCommandKind.UseCharacterCard:
                case GameCommandKind.DeclareCityStyle:
                case GameCommandKind.UseSpecialAction:
                    return phase == GamePhase.ActionRound1 || phase == GamePhase.ActionRound2;
                case GameCommandKind.CollectResource:
                    return phase == GamePhase.ResourceCollection;
                case GameCommandKind.ResolvePendingChoice:
                    return true;
                case GameCommandKind.EndAction:
                    return phase == GamePhase.ActionRound1
                        || phase == GamePhase.ActionRound2
                        || phase == GamePhase.Cleanup;
                default:
                    return false;
            }
        }

        public static ValidationResult ValidatePhase(GameState state, GameCommandKind kind)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (IsCommandAllowed(state.Phase, kind))
            {
                return ValidationResult.Success;
            }

            return ValidationResult.Failure(
                CommandErrorCode.WrongPhase,
                "Command " + kind + " is not allowed during phase " + state.Phase + ".");
        }
    }
}
