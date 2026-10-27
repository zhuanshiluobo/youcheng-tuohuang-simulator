using System;
using System.Collections.Generic;
using YC.Domain.State;
using YC.Domain.Presentation;

namespace YC.Domain.Effects
{
    [Serializable] public sealed class AbilityVersionRef
    {
        public string ContractVersion = "QY-COMMON/1.0.1", ContentPackHash = "", DefinitionVersion = "", HandlerHash = "", ExecutorVersion = "1.0.0";
    }
    [Serializable] public sealed class AbilitySourceRef
    {
        public string SourceKey = "", ItemId = "", DefinitionId = "", AbilityId = "", ActiveFaceId = "", DeclarationVersion = "";
        public int AuthorizedPlayerId = -1;
        public string AuthorizationRef = "", EffectiveZoneId = "", RuleOwnerItemId = "";
        public List<string> GrantingSourceRefs = new List<string>();
    }
    [Serializable] public sealed class AbilitySourceDeclaration
    {
        public int ProtocolVersion = 1;
        public string DefinitionId = "", AbilityId = "", Kind = "", HandlerRef = "", DeclarationVersion = "", ActiveFaceId = "";
        public string AuthorizationPolicy = "controller", PresentationRef = "", RepeatPolicy = "once";
        public string CompletionPolicy = "succeeded";
        public string CleanupInjectionPolicy = "always";
        public List<string> CompletedEffectTypes = new List<string>();
        public List<string> EffectiveZones = new List<string>(), ObservedEvents = new List<string>(), QueryKinds = new List<string>(), AllowedOperations = new List<string>();
        public List<AbilityActionDeclaration> Actions = new List<AbilityActionDeclaration>();
    }
    [Serializable] public sealed class AbilityActionDeclaration
    {
        public string ActionId = "", EntryKind = "", BodyHandlerRef = "", EntryCostRef = "", EntryAfterEffectsRef = "";
        public string UsageTrackId = "", LifecyclePolicy = "unbounded", LockPolicy = "none", DirectCallPolicy = "deny", PresentationRef = "";
        public int SortOrder;
        public AbilityActionPresentationDefinition Presentation;
    }
    [Serializable] public sealed class AbilityActionRequestRef
    {
        public string InteractionId = "", OwnerEffectId = "", WindowId = "", FactsDigest = "";
        public int AnsweringPlayerId = -1, CandidateSetVersion = 1, RequestGeneration = 1;
        public List<string> AllowedAnswers = new List<string>();
    }
    [Serializable] public sealed class AbilityActionOption
    {
        public string OptionId = "", ActionId = "", EntryKind = "", AuthorizationRef = "", TrackBindingId = "", PresentationRef = "";
        public string DisabledReason = "", DisplayName = "", Description = "";
        public string EntryCostDescription = "", EntryAfterEffectsDescription = "";
        public GameplayTextRef DisplayNameRef, DescriptionRef, EntryCostDescriptionRef, EntryAfterEffectsDescriptionRef;
        public int SortOrder;
        public bool Enabled;
        public AbilitySourceRef SourceRef;
        public AbilityVersionRef VersionRef;
        public AbilityActionRequestRef RequestRef;
    }
    [Serializable] public sealed class AbilityActionInvocation
    {
        public int ProtocolVersion = 1;
        public string InvocationId = "", OwnerEffectId = "", CommandId = "", CommandDigest = "", OptionId = "", ActionId = "", OccupancyKey = "";
        public string ParentEffectId = "", CallPolicyRef = "", Phase = "accepted", Outcome = "", FailureReason = "", FirstFaultPhase = "", TrackBindingId = "", UsageProviderVersion = "";
        public bool PaymentCommitted, ActivationCommitted, MainActionCommitted;
        public string EntryConditionVersion = "", EntryConditionHandlerHash = "", EntryConditionHandlerVersion = "", EntryConditionProgramDigest = "";
        public AbilitySourceRef SourceRef;
        public AbilityVersionRef VersionRef;
        public AbilityActionRequestRef RequestRef;
        public List<string> PaymentEffectIds = new List<string>(), BodyEffectIds = new List<string>(), ReceiptIds = new List<string>();
    }
    [Serializable] public sealed class AbilitySourceUseReceipt
    {
        public int ProtocolVersion = 1;
        public string ReceiptId = "", SourceKey = "", ItemId = "", AbilityId = "", ScopeKey = "", OwnerEffectId = "", Outcome = "";
        public int PlayerId = -1;
        public string TriggerEventId = "";
        public AbilitySourceRef SourceRef;
        public AbilityVersionRef VersionRef;
    }
    [Serializable] public sealed class AbilityQueryContext
    {
        public string QueryId = "", QueryKind = "", FactsDigest = "", MapId = "", Purpose = "", ContentVersion = "";
        public int StateRevision, SubjectPlayerId = -1;
        public AbilitySourceRef SourceRef;
        public NormalizedValue Facts = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
    }
    [Serializable] public sealed class AbilityRuleQueryResult
    {
        public string QueryId = "", QueryKind = "", BasisDigest = "";
        public AbilitySourceRef SourceRef;
        public AbilityVersionRef VersionRef;
        public NormalizedValue Items;
    }
    [Serializable] public sealed class AbilityActionCallPolicy
    {
        public string PolicyRef = "", CallerAbilityId = "", TargetActionId = "", CallerHandlerRef = "", CallerHandlerHash = "";
        public bool OmitEntryCost, OmitEntryAfterEffects, OmitMainAction;
    }
    public sealed class AbilityActionCallContext
    {
        public GameState State;
        public EffectNodeRuntimeState OwnerNode;
        public AbilitySourceRef SourceRef;
        public AbilityActionDeclaration Action;
        public NormalizedValue Arguments;
    }
    public interface IAbilityActionUsageProvider
    {
        string DefinitionVersion { get; }
        string Validate(GameState state, AbilitySourceRef source, AbilityActionDeclaration action, out string bindingId);
        void Activate(EffectExecutionContext context, AbilityActionInvocation invocation, AbilityActionDeclaration action);
    }
    public delegate string AbilityActionArgumentNormalizer(NormalizedValue input, out NormalizedValue normalized);
    public static class AbilityActionReasons
    {
        public static string Display(string code)
        {
            switch (code)
            {
                case "": return "";
                case "wrong_phase": return "当前阶段不能发动。";
                case "not_current_player": return "尚未轮到本人行动。";
                case "pending_choice_required": case "action_window_busy": return "请先完成当前选择。";
                case "insufficient_resources": case "insufficient_resource": return "入口资源不足。";
                case "insufficient_card_markers": return "牌上可支付标记不足。";
                case "action_group_occupied": return "该行动正在执行或本次额度已用。";
                case "usage_track_unavailable": return "使用轨道尚未接通。";
                case "usage_track_not_initialized": return "使用轨道尚未初始化。";
                case "usage_marker_departed": return "使用轨道上的标记已离开。";
                case "usage_track_version_mismatch": case "usage_track_definition_mismatch": return "使用轨道版本或声明不一致。";
                case "usage_owner_changed": return "该使用轨道不属于当前使用玩家。";
                case "usage_track_host_managed": return "使用标记只能由正式行动流程移动。";
                case "style_action_used_this_round": return "本回合已发动该行动。";
                case "style_track_exhausted": return "使用次数已耗尽。";
                case "character_action_round_conflict": case "character_action_round_locked": return "本行动轮角色与该行动互斥。";
                case "character_instance_blocked": return "本行动轮不能使用刚盖放的该角色。";
                case "character_use_in_progress": return "请先完成本次角色使用。";
                case "character_window_closed": return "该角色当前没有使用窗口。";
                case "entry_cost_preview_unsupported": return "入口费用预览尚未接通。";
                case "action_fault_paused": return "当前结算因故障暂停。";
                case "entry_payment_declined": return "入口费用未支付，本次行动结束。";
                default: return code;
            }
        }
    }
}
