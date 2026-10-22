using System;
using YC.Domain.Cards;

namespace YC.Application.Sessions
{
    [Serializable]
    public sealed class SettlementOptionView
    {
        public string WindowId = string.Empty, LocalId = string.Empty, EntryKind = string.Empty, TargetEffectId = string.Empty, SettlementState = string.Empty;
        public long RegistrationSequence;
        public bool CanDeclineWholeEffect;
        public YC.Domain.Presentation.SettlementPresentationDefinition Presentation;
        public string SourceDefinitionId = string.Empty;
        public bool CanInspectSource;
        public YC.Domain.Presentation.GameplayTextRef DisabledReason;
        public string OptionId = string.Empty, InteractionId = string.Empty, OwnerEffectId = string.Empty, SourceItemId = string.Empty, TargetUseId = string.Empty, CandidateId = string.Empty;
        public int AnsweringPlayerId = -1, StateRevision, CandidateSetVersion, RequiredGoldVoucherAmount;
        public string TitleKey = string.Empty, DescriptionKey = string.Empty, DisabledReasonCode = string.Empty;
        public bool IsEnabled, VisibleToViewer;
        public static SettlementOptionView FromOption(CharacterSettlementOption option) => new SettlementOptionView {
            SourceDefinitionId = option.SourceDefinitionId, CanInspectSource = option.CanInspectSource, DisabledReason = YC.Domain.Presentation.GameplayPresentationNormalizer.Clone(option.DisabledReason),
            WindowId = option.WindowId, LocalId = option.LocalId, EntryKind = option.EntryKind, TargetEffectId = option.TargetEffectId, SettlementState = option.SettlementState,
            RegistrationSequence = option.RegistrationSequence, CanDeclineWholeEffect = option.CanDeclineWholeEffect,
            Presentation = YC.Domain.Presentation.GameplayPresentationNormalizer.Clone(option.Presentation),
            OptionId = option.OptionId, InteractionId = option.InteractionId, OwnerEffectId = option.OwnerEffectId, SourceItemId = option.SourceItemId, TargetUseId = option.TargetUseId, CandidateId = option.CandidateId,
            AnsweringPlayerId = option.AnsweringPlayerId, StateRevision = option.StateRevision, CandidateSetVersion = option.CandidateSetVersion, RequiredGoldVoucherAmount = option.RequiredGoldVoucherAmount,
            TitleKey = option.TitleKey, DescriptionKey = option.DescriptionKey, DisabledReasonCode = option.DisabledReasonCode, IsEnabled = option.IsEnabled, VisibleToViewer = option.VisibleToViewer };
    }
}
