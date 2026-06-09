namespace YC.Domain.Influence
{
    using YC.Domain.Commands;
    using YC.Domain.Rules;

    public enum InfluenceFailureCode
    {
        None,
        InvalidState,
        InvalidPlayer,
        InvalidSlot,
        OccupiedSlot,
        InsufficientSupply,
        ResourceTokenRequired,
        OpponentCityPresent,
        RouteCoveredByRoad,
        InfluenceNotFound,
        InfluenceOwnerMismatch
    }

    public sealed class InfluenceOperationResult
    {
        private InfluenceOperationResult(
            bool succeeded,
            bool stateChanged,
            InfluenceFailureCode failureCode,
            string reason,
            int playerId,
            string slotId)
        {
            Succeeded = succeeded;
            StateChanged = stateChanged;
            FailureCode = failureCode;
            Reason = reason;
            PlayerId = playerId;
            SlotId = slotId ?? string.Empty;
        }

        public bool Succeeded { get; private set; }
        public bool StateChanged { get; private set; }
        public InfluenceFailureCode FailureCode { get; private set; }
        public string Reason { get; private set; }
        public int PlayerId { get; private set; }
        public string SlotId { get; private set; }
        public ValidationResult Validation
        {
            get
            {
                return Succeeded
                    ? ValidationResult.Success
                    : ValidationResult.Failure(ToCommandErrorCode(FailureCode), Reason);
            }
        }

        public static InfluenceOperationResult Success(int playerId, string slotId, bool stateChanged)
        {
            return new InfluenceOperationResult(
                true,
                stateChanged,
                InfluenceFailureCode.None,
                string.Empty,
                playerId,
                slotId);
        }

        public static InfluenceOperationResult Failure(
            InfluenceFailureCode failureCode,
            string reason,
            int playerId,
            string slotId,
            bool stateChanged)
        {
            return new InfluenceOperationResult(false, stateChanged, failureCode, reason, playerId, slotId);
        }

        private static CommandErrorCode ToCommandErrorCode(InfluenceFailureCode failureCode)
        {
            switch (failureCode)
            {
                case InfluenceFailureCode.InvalidPlayer:
                    return CommandErrorCode.InvalidPlayer;
                case InfluenceFailureCode.OccupiedSlot:
                case InfluenceFailureCode.OpponentCityPresent:
                    return CommandErrorCode.OccupiedSlot;
                case InfluenceFailureCode.InsufficientSupply:
                    return CommandErrorCode.InsufficientInfluence;
                case InfluenceFailureCode.ResourceTokenRequired:
                    return CommandErrorCode.ClosedLocation;
                case InfluenceFailureCode.InfluenceNotFound:
                case InfluenceFailureCode.InfluenceOwnerMismatch:
                    return CommandErrorCode.InvalidSource;
                case InfluenceFailureCode.InvalidSlot:
                case InfluenceFailureCode.RouteCoveredByRoad:
                case InfluenceFailureCode.InvalidState:
                default:
                    return CommandErrorCode.InvalidTarget;
            }
        }
    }
}
