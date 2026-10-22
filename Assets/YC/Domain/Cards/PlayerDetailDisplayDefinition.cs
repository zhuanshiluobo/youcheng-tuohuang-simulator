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
        public PlayerDetailDisplayCategory DisplayCategory;
        public PlayerDetailActivation Activation;
        public List<string> StorageLocalZoneIds = new List<string>();
        public bool IncludeCoverSlots;
    }
}
