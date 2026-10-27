using System;
using System.Collections.Generic;

namespace YC.Domain.Effects
{
    [Serializable] public sealed class UsageTrackTransition
    {
        public string FromSlotId = "", ToSlotId = "";
    }
    [Serializable] public sealed class UsageTrackDeclaration
    {
        public int ProtocolVersion = 1;
        public string DefinitionId = "", Version = "1.0.0", TrackId = "", CardZoneLocalId = "", InitialSlotId = "";
        public string ResetBoundary = "all_cleanup_completed", InitializationPolicy = "owner_supply_or_retrieve", UsageGroupScope = "source_player_round";
        public List<string> Slots = new List<string>();
        public List<UsageTrackTransition> ActivationTransitions = new List<UsageTrackTransition>(), ResetTransitions = new List<UsageTrackTransition>();
    }
    [Serializable] public sealed class UsageTrackBinding
    {
        public int ProtocolVersion = 1, PlayerId = -1, InitializedRoundNumber;
        public string BindingId = "", SourceItemId = "", TrackId = "", DefinitionVersion = "", AuthorizationRef = "", ZoneId = "", MarkerItemId = "", InitializationReceiptId = "", InitializationEffectId = "";
    }
    [Serializable] public sealed class UsageGroupReceipt
    {
        public int RoundNumber, PlayerId = -1;
        public string GroupKey = "", InvocationId = "", OwnerEffectId = "", BindingId = "";
    }
    [Serializable] public sealed class UsageTrackResetProgress
    {
        public int ProtocolVersion = 1, RoundNumber;
        public string RoundId = "", OwnerEffectId = "", Phase = "waiting";
        public List<int> ParticipantIds = new List<int>();
        public List<string> RequiredObligationIds = new List<string>(), TargetBindingIds = new List<string>(), ProcessedBindingIds = new List<string>();
    }
    [Serializable] public sealed class UsageTrackView
    {
        public UsageTrackBinding Binding;
        public string SlotId = "", DisabledReason = "";
    }
}
