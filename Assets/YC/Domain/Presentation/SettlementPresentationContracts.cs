using System;
using YC.Domain.Effects;

namespace YC.Domain.Presentation
{
    [Serializable]
    public sealed class SettlementEndActionView
    {
        public string WindowId = string.Empty, OwnerEffectId = string.Empty, CurrentNodeEffectId = string.Empty, InteractionId = string.Empty;
        public int StateRevision;
        public bool CanStopCurrentNode, CanCloseCleanupWindow;
        public YC.Domain.Rules.CommandErrorCode StopCurrentNodeErrorCode, CloseCleanupWindowErrorCode;
        public string StopCurrentNodeReasonCode = string.Empty, CloseCleanupWindowReasonCode = string.Empty;
    }
    [Serializable]
    public sealed class SettlementEffectDefinition
    {
        public string EntryKind = string.Empty, LocalId = string.Empty;
        public SettlementWholeEffectPolicy WholeEffectPolicy = SettlementWholeEffectPolicy.Unspecified;
        public string SourceItemId = string.Empty, SourceEffectId = string.Empty, AbilityId = string.Empty;
        public SettlementPresentationDefinition Presentation;
    }
}
