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
            string slotId,
            string influenceId,
            string fromSlotId)
        {
            Succeeded = succeeded;
            StateChanged = stateChanged;
            FailureCode = failureCode;
            Reason = reason;
            PlayerId = playerId;
            SlotId = slotId ?? string.Empty;
            InfluenceId = influenceId ?? string.Empty;
            FromSlotId = fromSlotId ?? string.Empty;
        }

        public bool Succeeded { get; private set; }
        public bool StateChanged { get; private set; }
        public InfluenceFailureCode FailureCode { get; private set; }
        public string Reason { get; private set; }
        public int PlayerId { get; private set; }
        public string SlotId { get; private set; }
        public string InfluenceId { get; private set; }
        public string FromSlotId { get; private set; }
        public string StableFailureCode
        {
            get { return FailureCode.ToString().ToLowerInvariant(); }
        }
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
            return Success(playerId, slotId, stateChanged, string.Empty, string.Empty);
        }

        public static InfluenceOperationResult Success(
            int playerId,
            string slotId,
            bool stateChanged,
            string influenceId,
            string fromSlotId = "")
        {
            return new InfluenceOperationResult(
                true,
                stateChanged,
                InfluenceFailureCode.None,
                string.Empty,
                playerId,
                slotId,
                influenceId,
                fromSlotId);
        }

        public static InfluenceOperationResult Failure(
            InfluenceFailureCode failureCode,
            string reason,
            int playerId,
            string slotId,
            bool stateChanged)
        {
            return Failure(failureCode, reason, playerId, slotId, stateChanged, string.Empty, string.Empty);
        }

        public static InfluenceOperationResult Failure(
            InfluenceFailureCode failureCode,
            string reason,
            int playerId,
            string slotId,
            bool stateChanged,
            string influenceId,
            string fromSlotId = "")
        {
            return new InfluenceOperationResult(
                false,
                stateChanged,
                failureCode,
                reason,
                playerId,
                slotId,
                influenceId,
                fromSlotId);
        }

        private static CommandErrorCode ToCommandErrorCode(InfluenceFailureCode failureCode)
        {
            switch (failureCode)
            {
                case InfluenceFailureCode.InvalidPlayer:
                    return CommandErrorCode.InvalidPlayer;
                case InfluenceFailureCode.OccupiedSlot:
                case InfluenceFailureCode.OpponentCityPresent:
                case InfluenceFailureCode.RouteCoveredByRoad:
                    return CommandErrorCode.OccupiedSlot;
                case InfluenceFailureCode.InsufficientSupply:
                    return CommandErrorCode.InsufficientInfluence;
                case InfluenceFailureCode.ResourceTokenRequired:
                    return CommandErrorCode.ClosedLocation;
                case InfluenceFailureCode.InfluenceNotFound:
                case InfluenceFailureCode.InfluenceOwnerMismatch:
                    return CommandErrorCode.InvalidSource;
                case InfluenceFailureCode.InvalidSlot:
                case InfluenceFailureCode.InvalidState:
                default:
                    return CommandErrorCode.InvalidTarget;
            }
        }
    }
}
