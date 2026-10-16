using System;
using System.Collections.Generic;

namespace YC.Domain.Cards
{
    [Serializable]
    public sealed class CharacterUseRuntimeState
    {
        public string UseId = string.Empty, OwnerEffectId = string.Empty, CardInstanceId = string.Empty;
        public int ExecutingPlayerId = -1, ActionRoundSequence;
        public List<string> ModeOrder = new List<string>(), CompletedModes = new List<string>();
        public List<string> SkippedModes = new List<string>();
        public string StopCommandId = string.Empty, StoppedActivationEffectId = string.Empty;
        public bool DoubleUseGranted, SourceConsumed, Finished;
        // 缺省保持旧存档语义；新快速行动由 Host 显式选择版本 3。
        public int SelectionProtocolVersion = 1;
        public bool OfferSecondEffect, CanOfferSecondEffect, SelectionCancelled;
        public string DoubleUseSourceItemId = string.Empty, FinishDestinationZoneId = string.Empty;
        public List<CharacterModeContract> Modes = new List<CharacterModeContract>();
    }

    /// <summary>内容声明的模式合同及本次使用事实；不保存第二份牌位置。</summary>
    [Serializable]
    public sealed class CharacterModeContract
    {
        public string Mode = string.Empty, AbilityId = string.Empty, SkillType = "normal";
        public string SettlementActionId = string.Empty, TitleKey = string.Empty, DescriptionKey = string.Empty;
        public bool Activated, SubscriptionActive;
        public string LastCleanupKey = string.Empty;
    }

    [Serializable]
    public sealed class CoverSlotState
    {
        public string SlotId = string.Empty, CardInstanceId = string.Empty, SourceEffectId = string.Empty;
        public int PlayerId = -1, BlockedActionRoundSequence;
        public string AcceptedTypeId = "core.character_card";
        // 可复用的额外槽与可使用窗口由入口声明，不由物理类型目录猜测。
        public bool Reusable;
        public List<int> AllowedActionRounds = new List<int> { 1, 2 };
    }

    [Serializable]
    public sealed class FacilityCharacterBindingState
    {
        public string BindingId = string.Empty, CharacterItemId = string.Empty, FacilityItemId = string.Empty, SourceEffectId = string.Empty;
        public int ControllerPlayerId = -1;
        public bool Active;
    }

    [Serializable]
    public sealed class CharacterActionRoundPermission
    {
        public int PlayerId = -1, ExpiresActionRoundSequence;
        public string SourceEffectId = string.Empty;
    }

    [Serializable]
    public sealed class CharacterAvailability
    {
        public string ItemId = string.Empty, DefinitionId = string.Empty, ZoneId = string.Empty, SlotId = string.Empty;
        public int ControllerPlayerId = -1, BlockedActionRoundSequence;
        public bool CanUse;
        public bool CanStopPendingMode;
        public string PendingUseId = string.Empty, PendingModeInteractionId = string.Empty;
        public string DisabledReasonCode = string.Empty;
    }
}
