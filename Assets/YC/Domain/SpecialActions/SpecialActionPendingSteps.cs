namespace YC.Domain.SpecialActions
{
    public static class SpecialActionPendingSteps
    {
        public const string AwaitMilitaryTargets = "await_military_targets";
        public const string AwaitMobilizationTarget = "await_mobilization_target";
        public const string AwaitFreeMoveTarget = "await_free_move_target";
        public const string AwaitMoveEvent = "await_move_event";
        public const string AwaitRouteInfluence = "await_route_influence";

        public static bool IsCompatible(SpecialActionEffectKind effectKind, string step)
        {
            switch (effectKind)
            {
                case SpecialActionEffectKind.DeployInfluence:
                    return step == AwaitMilitaryTargets;
                case SpecialActionEffectKind.ReplaceInfluence:
                    return step == AwaitMobilizationTarget;
                case SpecialActionEffectKind.CompositePowerMove:
                    return step == AwaitFreeMoveTarget ||
                           step == AwaitMoveEvent ||
                           step == AwaitRouteInfluence;
                case SpecialActionEffectKind.ConsecutiveFreeMoves:
                    return step == AwaitFreeMoveTarget || step == AwaitMoveEvent;
                case SpecialActionEffectKind.GrantExtraMainActions:
                    return false;
                default:
                    return false;
            }
        }
    }
}
