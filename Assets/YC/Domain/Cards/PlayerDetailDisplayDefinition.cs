using System;
using System.Collections.Generic;

namespace YC.Domain.Cards
{
    public enum PlayerDetailDisplayCategory { PlayerEffect, StorageArea, QuickAction, Immediate, Public }
    public enum PlayerDetailActivation { Subscription, Delayed, Held }

    /// <summary>内容包提供的玩家文案及展示分类；不是可写规则状态。</summary>
    [Serializable]
    public sealed class PlayerDetailDisplayDefinition
    {
        public string DefinitionId = string.Empty, AbilityId = string.Empty;
        public string DisplayName = string.Empty, EffectText = string.Empty;
        // 只读展示条件；不授予来源、动作或规则。空RuleRef保留旧内容行为。
        public string RequiredRuleRef = string.Empty, RequiredRuleBinding = "owner";
        public YC.Domain.Presentation.GameplayTextRef EffectTextRef;
        public PlayerDetailDisplayCategory DisplayCategory;
        public PlayerDetailActivation Activation;
        public List<string> StorageLocalZoneIds = new List<string>();
        public bool IncludeCoverSlots;
    }
}
