using System;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static class InfluenceEffectFailureMapper
    {
        public static ValidationResult ToValidation(EffectNodeRuntimeState node)
        {
            if (node == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "影响力 Effect 未生成执行节点。");
            }

            var code = ToCommandErrorCode(node.FailureReason);
            var reason = string.IsNullOrEmpty(node.FailureReason)
                ? "影响力 Effect 执行失败。"
                : node.FailureReason;
            return ValidationResult.Failure(code, reason);
        }

        public static InfluenceFailureCode ToInfluenceFailureCode(string failureCode)
        {
            switch ((failureCode ?? string.Empty).ToLowerInvariant())
            {
                case "invalidplayer":
                    return InfluenceFailureCode.InvalidPlayer;
                case "influencenotfound":
                    return InfluenceFailureCode.InfluenceNotFound;
                case "influenceownermismatch":
                    return InfluenceFailureCode.InfluenceOwnerMismatch;
                case "insufficientsupply":
                    return InfluenceFailureCode.InsufficientSupply;
                case "resourcetokenrequired":
                    return InfluenceFailureCode.ResourceTokenRequired;
                case "occupiedslot":
                    return InfluenceFailureCode.OccupiedSlot;
                case "opponentcitypresent":
                    return InfluenceFailureCode.OpponentCityPresent;
                case "routecoveredbyroad":
                    return InfluenceFailureCode.RouteCoveredByRoad;
                default:
                    return InfluenceFailureCode.InvalidState;
            }
        }

        private static CommandErrorCode ToCommandErrorCode(string failureCode)
        {
            switch ((failureCode ?? string.Empty).ToLowerInvariant())
            {
                case "invalidplayer":
                    return CommandErrorCode.InvalidPlayer;
                case "influencenotfound":
                case "influenceownermismatch":
                    return CommandErrorCode.InvalidSource;
                case "insufficientsupply":
                    return CommandErrorCode.InsufficientInfluence;
                case "resourcetokenrequired":
                    return CommandErrorCode.ClosedLocation;
                case "occupiedslot":
                case "opponentcitypresent":
                case "routecoveredbyroad":
                    return CommandErrorCode.OccupiedSlot;
                default:
                    return CommandErrorCode.InvalidTarget;
            }
        }
    }
}
