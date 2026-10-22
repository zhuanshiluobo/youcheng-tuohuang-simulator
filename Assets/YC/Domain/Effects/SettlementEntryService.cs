using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Presentation;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public enum SettlementWholeEffectPolicy { Unspecified, Optional, Required }

    [Serializable]
    public sealed class SettlementWindowContext
    {
        public string Id = "", Kind = "", OwnerEffectId = "", TargetUseId = "", TriggerId = "", PaymentId = "";
        public int PlayerId = -1, StateRevision;
        public ResourceSet PaymentCost;
    }

    [Serializable]
    public sealed class SettlementOfferDefinition
    {
        public int ApiVersion = 1, PlayerId = -1, MaxUses = 1;
        public string LocalId = "", SourceItemId = "", WindowId = "", OwnerEffectId = "", AvailabilityHandlerId = "", Visibility = "owner";
        public SettlementPresentationDefinition Presentation;
        public List<EffectSpecRuntimeState> Effects = new List<EffectSpecRuntimeState>();
    }

    [Serializable]
    public sealed class SettlementAvailabilityResult
    {
        public bool Enabled;
        public string DisabledReasonKey = "";
    }

    [Serializable]
    public sealed class SettlementEntryRuntimeState
    {
        public string OptionId = "", LocalId = "", EntryKind = "", SourceItemId = "", SourceDefinitionId = "", AbilityId = "", SourceEffectId = "", TriggerId = "", TargetEffectId = "";
        public long RegistrationSequence;
        public int MaxUses = 1, AcceptedUseCount;
        public string Visibility = "owner", AvailabilityHandlerId = "", SettlementState = "available", OutcomeCode = "";
        public SettlementWholeEffectPolicy WholeEffectPolicy;
        public SettlementPresentationDefinition Presentation;
        public List<EffectSpecRuntimeState> Effects = new List<EffectSpecRuntimeState>();
    }

    [Serializable]
    public sealed class SettlementWindowRuntimeState
    {
        public int ApiVersion = 1;
        public SettlementWindowContext Context;
        public bool IsClosed;
        public long NextRegistrationSequence;
        public string ActiveTargetEffectId = "", SuspendedInteractionId = "", ResumeFlowStage = "";
        public List<SettlementEntryRuntimeState> Entries = new List<SettlementEntryRuntimeState>();
    }

    public sealed class SettlementInvocationPlan
    {
        public string EntryKind = "", WindowId = "", OwnerEffectId = "", OptionId = "", TargetEffectId = "", InteractionId = "";
        public IReadOnlyList<EffectSpec> Effects = Array.Empty<EffectSpec>();
    }

    public enum SettlementEndActionKind { StopCurrentNode, CloseCleanupWindow }
    public sealed class SettlementEndActionPlan
    {
        public SettlementEndActionKind Kind;
        public string WindowId = "", OwnerEffectId = "", NodeEffectId = "", InteractionId = "";
    }

    /// <summary>元数据及段调度规则；调用方在既有内核工作副本中保存窗口和提交，不在这里另建事务。</summary>
    public static class SettlementEntryService
    {
        public const string OptionalInsertion = "optional_insertion", CleanupOrder = "cleanup_order";
        public const string CharacterUse = "character_use_before_mode", ResourcePayment = "resource_payment_before_commit", PlayerCleanup = "player_cleanup";
        public const string SegmentTypeId = "effect.settlement.segment";

        public static SettlementWindowRuntimeState CreateWindow(GameState state, EffectNodeRuntimeState owner,
            string kind, int playerId, string targetUseId, string triggerId)
        {
            if (state == null || owner == null || owner.PlayerId != playerId || state.FindPlayer(playerId) == null ||
                !new[] { CharacterUse, ResourcePayment, PlayerCleanup }.Contains(kind))
                throw new ArgumentException("settlement_window_context_invalid");
            if (kind == ResourcePayment && (owner.Payment == null || owner.Payment.OwnerEffectId != owner.EffectId || owner.Payment.PlayerId != playerId))
                throw new ArgumentException("settlement_payment_context_missing");
            targetUseId = targetUseId ?? ""; triggerId = triggerId ?? "";
            return new SettlementWindowRuntimeState { Context = new SettlementWindowContext {
                Id = StableIdFactory.Create("settlement_window", state.GameId, owner.EffectId, kind,
                    playerId.ToString(CultureInfo.InvariantCulture), targetUseId, triggerId),
                Kind = kind, OwnerEffectId = owner.EffectId, PlayerId = playerId, TargetUseId = targetUseId,
                TriggerId = triggerId, StateRevision = state.EffectRuntime.StateRevision,
                PaymentId = kind == ResourcePayment ? owner.Payment.PaymentId : "",
                PaymentCost = kind == ResourcePayment ? GameStateCloneService.DeepClone(owner.Payment.PaymentCost) : null } };
        }

        public static SettlementWindowContext CreateQueryContext(GameState state, SettlementWindowRuntimeState window)
        {
            if (state?.EffectRuntime == null || window?.Context == null) throw new ArgumentException("settlement_window_context_invalid");
            var result = GameStateCloneService.DeepClone(window.Context);
            result.StateRevision = state.EffectRuntime.StateRevision;
            return result;
        }

        public static SettlementEntryRuntimeState RegisterOffer(SettlementWindowRuntimeState window, SettlementOfferDefinition offer)
        {
            RequireOpen(window);
            if (window.Context.Kind == PlayerCleanup || offer == null || offer.ApiVersion != 1 || offer.MaxUses != 1 ||
                offer.PlayerId != window.Context.PlayerId || offer.WindowId != window.Context.Id ||
                offer.OwnerEffectId != window.Context.OwnerEffectId || string.IsNullOrEmpty(offer.LocalId) ||
                string.IsNullOrEmpty(offer.SourceItemId) || string.IsNullOrEmpty(offer.AvailabilityHandlerId) ||
                offer.Visibility != "owner" || offer.Effects == null || offer.Effects.Count == 0 || offer.Effects.Any(e => e == null))
                throw new ArgumentException("settlement_offer_invalid");
            ValidatePresentation(offer.Presentation);
            string id = StableIdFactory.Create("settlement_option", window.Context.Id, offer.SourceItemId, offer.LocalId);
            var existing = window.Entries.Find(e => e.OptionId == id);
            if (existing != null)
            {
                // 刷新只更新未受理的定义，不重置身份、加入顺序或使用收据。
                if (existing.AcceptedUseCount == 0)
                {
                    existing.Presentation = GameplayPresentationNormalizer.Clone(offer.Presentation);
                    existing.AvailabilityHandlerId = offer.AvailabilityHandlerId;
                    existing.Effects = offer.Effects.Select(e => e.Clone()).ToList();
                }
                return existing;
            }
            var entry = new SettlementEntryRuntimeState { OptionId = id, LocalId = offer.LocalId,
                EntryKind = OptionalInsertion, SourceItemId = offer.SourceItemId, AvailabilityHandlerId = offer.AvailabilityHandlerId,
                RegistrationSequence = window.NextRegistrationSequence++, Presentation = GameplayPresentationNormalizer.Clone(offer.Presentation),
                Effects = offer.Effects.Select(e => e.Clone()).ToList() };
            window.Entries.Add(entry);
            return entry;
        }

        public static SettlementEntryRuntimeState RegisterCleanup(GameState state, SettlementWindowRuntimeState window,
            string targetEffectId, string localId, string abilityId, string sourceItemId, string sourceEffectId,
            SettlementWholeEffectPolicy policy, SettlementPresentationDefinition presentation, string availabilityHandlerId = "")
        {
            RequireOpen(window);
            var target = state?.EffectRuntime?.EffectNodes.Find(n => n.EffectId == targetEffectId);
            var owner = state?.EffectRuntime?.EffectNodes.Find(n => n.EffectId == window.Context.OwnerEffectId);
            if (window.Context.Kind != PlayerCleanup || target == null || target.PlayerId != window.Context.PlayerId ||
                target.ParentEffectId != window.Context.OwnerEffectId || owner == null || !owner.ChildEffectIds.Contains(targetEffectId) ||
                string.IsNullOrEmpty(localId) || string.IsNullOrEmpty(abilityId) ||
                !Enum.IsDefined(typeof(SettlementWholeEffectPolicy), policy) || policy == SettlementWholeEffectPolicy.Optional && string.IsNullOrEmpty(availabilityHandlerId))
                throw new ArgumentException("settlement_cleanup_target_invalid");
            ValidatePresentation(presentation);
            string id = StableIdFactory.Create("settlement_order", window.Context.Id, targetEffectId);
            var previous = window.Entries.Find(e => e.OptionId == id);
            if (previous != null)
            {
                if (previous.LocalId != localId || previous.AbilityId != abilityId || previous.SourceItemId != (sourceItemId ?? "") ||
                    previous.SourceEffectId != (sourceEffectId ?? "") || previous.WholeEffectPolicy != policy || previous.AvailabilityHandlerId != (availabilityHandlerId ?? ""))
                    throw new ArgumentException("settlement_cleanup_registration_conflict");
                return previous;
            }
            if (target.Status != EffectNodeStatus.Created) throw new ArgumentException("settlement_cleanup_already_started");
            var entry = new SettlementEntryRuntimeState { OptionId = id, EntryKind = CleanupOrder, TargetEffectId = targetEffectId,
                LocalId = localId, AbilityId = abilityId, SourceItemId = sourceItemId ?? "", SourceEffectId = sourceEffectId ?? "",
                TriggerId = window.Context.TriggerId, WholeEffectPolicy = policy, RegistrationSequence = window.NextRegistrationSequence++,
                AvailabilityHandlerId = availabilityHandlerId ?? "",
                Presentation = GameplayPresentationNormalizer.Clone(presentation) };
            window.Entries.Add(entry);
            return entry;
        }

        public static IReadOnlyList<CharacterSettlementOption> GetOptions(GameState state, SettlementWindowRuntimeState window,
            int viewerPlayerId, Func<SettlementEntryRuntimeState, SettlementAvailabilityResult> availability = null)
        {
            var result = new List<CharacterSettlementOption>();
            if (state?.EffectRuntime == null || window?.Context == null || window.IsClosed || window.Context.PlayerId != viewerPlayerId) return result;
            var request = state.EffectRuntime.InteractionRequests.LastOrDefault(r => r.OwnerEffectId == window.Context.OwnerEffectId && r.Status == "open" && r.AnsweringPlayerId == viewerPlayerId);
            foreach (var entry in window.Entries.OrderBy(e => e.RegistrationSequence))
            {
                string reason = BoundaryReason(state, window, entry, viewerPlayerId);
                if (reason.Length == 0 && availability != null)
                {
                    var value = availability(entry);
                    if (value == null || !value.Enabled) reason = value?.DisabledReasonKey ?? "settlement_availability_invalid";
                    if (value != null && !value.Enabled && reason.Length == 0) reason = "settlement_unavailable";
                }
                else if (reason.Length == 0 && (entry.EntryKind == OptionalInsertion || entry.WholeEffectPolicy == SettlementWholeEffectPolicy.Optional))
                    reason = "settlement_availability_missing";
                result.Add(new CharacterSettlementOption { OptionId = entry.OptionId, WindowId = window.Context.Id,
                    OwnerEffectId = window.Context.OwnerEffectId, InteractionId = request?.GetStableInteractionId() ?? "",
                    SourceItemId = entry.SourceItemId, SourceDefinitionId = entry.SourceDefinitionId,
                    LocalId = entry.LocalId, EntryKind = entry.EntryKind,
                    TargetEffectId = entry.TargetEffectId, CandidateId = entry.EntryKind == CleanupOrder ? entry.TargetEffectId : entry.SourceItemId,
                    TargetUseId = window.Context.TargetUseId, AnsweringPlayerId = viewerPlayerId,
                    StateRevision = state.EffectRuntime.StateRevision, CandidateSetVersion = request?.CandidateSetVersion ?? 0,
                    RegistrationSequence = entry.RegistrationSequence, CanDeclineWholeEffect = entry.WholeEffectPolicy == SettlementWholeEffectPolicy.Optional,
                    SettlementState = entry.SettlementState, Presentation = GameplayPresentationNormalizer.Clone(entry.Presentation),
                    IsEnabled = reason.Length == 0, VisibleToViewer = true, DisabledReasonCode = reason });
            }
            return result;
        }

        public static bool TryPrepareInvocation(GameState state, SettlementWindowRuntimeState window, int playerId,
            string optionId, string useId, int expectedRevision, Func<SettlementEntryRuntimeState, SettlementAvailabilityResult> availability,
            out bool alreadyInvoked, out SettlementInvocationPlan plan, out string reason)
        {
            alreadyInvoked = false; plan = null; reason = "settlement_option_missing";
            var entry = window?.Entries?.Find(e => e.OptionId == optionId);
            if (state?.EffectRuntime == null || window?.Context == null || entry == null) return false;
            if (playerId != window.Context.PlayerId || (useId ?? "") != window.Context.TargetUseId) { reason = "settlement_permission_denied"; return false; }
            if (entry.AcceptedUseCount > 0) { alreadyInvoked = true; reason = ""; return true; }
            if (expectedRevision != state.EffectRuntime.StateRevision) { reason = "state_revision_mismatch"; return false; }
            var option = GetOptions(state, window, playerId, availability).FirstOrDefault(o => o.OptionId == optionId);
            if (option?.IsEnabled != true) { reason = option?.DisabledReasonCode ?? "settlement_window_closed"; return false; }
            plan = new SettlementInvocationPlan { EntryKind = entry.EntryKind, WindowId = window.Context.Id,
                OwnerEffectId = window.Context.OwnerEffectId, OptionId = optionId, TargetEffectId = entry.TargetEffectId,
                InteractionId = option.InteractionId, Effects = entry.EntryKind == OptionalInsertion
                    ? entry.Effects.Select(EffectRegistry.FromRuntimeSpec).ToList() : (IReadOnlyList<EffectSpec>)Array.Empty<EffectSpec>() };
            reason = ""; return true;
        }

        public static void RecordInvocation(SettlementWindowRuntimeState window, SettlementInvocationPlan plan, string attachedTargetEffectId = "")
        {
            RequireOpen(window);
            if (plan == null || plan.WindowId != window.Context.Id || plan.OwnerEffectId != window.Context.OwnerEffectId)
                throw new ArgumentException("settlement_invocation_plan_invalid");
            var entry = window.Entries.Single(e => e.OptionId == plan.OptionId);
            if (entry.AcceptedUseCount > 0) return;
            string target = entry.EntryKind == CleanupOrder ? entry.TargetEffectId : attachedTargetEffectId;
            if (string.IsNullOrEmpty(target) || !string.IsNullOrEmpty(window.ActiveTargetEffectId)) throw new ArgumentException("settlement_execution_target_invalid");
            entry.TargetEffectId = target; entry.AcceptedUseCount++; entry.SettlementState = "executing";
            window.ActiveTargetEffectId = target;
        }

        public static bool IsChildEligible(EffectRuntimeState runtime, SettlementWindowRuntimeState window, string childEffectId)
        {
            if (runtime == null || window?.Context == null) return false;
            if (!runtime.EffectNodes.Any(n => n.EffectId == childEffectId && n.ParentEffectId == window.Context.OwnerEffectId)) return false;
            var entry = window.Entries.Find(e => e.TargetEffectId == childEffectId);
            if (entry != null) return !window.IsClosed && entry.SettlementState == "executing" && window.ActiveTargetEffectId == childEffectId;
            if (!string.IsNullOrEmpty(window.ActiveTargetEffectId)) return false;
            // 系统步骤不混入玩家排序集合，仍须等全部真实段结算。
            return window.Entries.Where(e => e.EntryKind == CleanupOrder).All(e => e.SettlementState == "resolved" || e.SettlementState == "declined");
        }

        public static void ObserveTerminal(SettlementWindowRuntimeState window, EffectNodeRuntimeState target)
        {
            if (window == null || target == null || target.Status != EffectNodeStatus.Completed && target.Status != EffectNodeStatus.Failed) return;
            var entry = window.Entries.Find(e => e.TargetEffectId == target.EffectId && e.SettlementState == "executing");
            if (entry == null) return;
            entry.SettlementState = "resolved";
            entry.OutcomeCode = target.Status == EffectNodeStatus.Completed ? "completed" : target.FailureReason;
            if (window.ActiveTargetEffectId == target.EffectId) window.ActiveTargetEffectId = "";
        }

        // 收尾排序是合法的玩家输入边界；没有模态 Interaction 也不代表调度死锁。
        // 公共内核 Advance 的 no_progress 判定复用此函数，不能伪造一个确认页来维持等待。
        public static bool HasPendingPlayerSelection(EffectRuntimeState runtime)
        {
            if (runtime == null || string.IsNullOrEmpty(runtime.ActiveSettlementWindowId)) return false;
            var owner = runtime.EffectNodes.Find(n => n.SettlementWindow?.Context?.Id == runtime.ActiveSettlementWindowId);
            var window = owner?.SettlementWindow;
            return window?.Context?.Kind == PlayerCleanup && !window.IsClosed &&
                owner.EffectId == window.Context.OwnerEffectId && owner.PlayerId == window.Context.PlayerId &&
                owner.Status != EffectNodeStatus.Completed && owner.Status != EffectNodeStatus.Failed && owner.Status != EffectNodeStatus.Faulted &&
                string.IsNullOrEmpty(window.ActiveTargetEffectId) && window.Entries.Any(e => e.EntryKind == CleanupOrder &&
                    e.SettlementState == "available" && e.WholeEffectPolicy != SettlementWholeEffectPolicy.Unspecified &&
                    runtime.EffectNodes.Any(n => n.EffectId == e.TargetEffectId && n.ParentEffectId == owner.EffectId &&
                        n.PlayerId == owner.PlayerId && n.Status == EffectNodeStatus.Created && owner.ChildEffectIds.Contains(n.EffectId)));
        }

        public static bool CanCloseCleanup(SettlementWindowRuntimeState window, out string reason)
        {
            reason = "settlement_window_invalid";
            if (window?.Context == null || window.Context.Kind != PlayerCleanup || window.IsClosed) return false;
            if (!string.IsNullOrEmpty(window.ActiveTargetEffectId)) { reason = "settlement_execution_pending"; return false; }
            if (window.Entries.Any(e => e.SettlementState == "available" && e.WholeEffectPolicy == SettlementWholeEffectPolicy.Unspecified))
            { reason = "settlement_whole_effect_policy_unresolved"; return false; }
            if (window.Entries.Any(e => e.SettlementState == "available" && e.WholeEffectPolicy == SettlementWholeEffectPolicy.Required))
            { reason = "settlement_required_pending"; return false; }
            reason = ""; return true;
        }

        // 当前节点身份和停止许可由Host既有节点/原语协议提供，绝不从WholeEffectPolicy或按钮状态推导。
        public static bool TryPrepareEndAction(GameState state, SettlementWindowRuntimeState window, int playerId,
            string currentNodeId, string interactionId, int expectedRevision, Func<EffectNodeRuntimeState, bool> canStopCurrentNode,
            out SettlementEndActionPlan plan, out string reason)
        {
            plan = null; reason = "settlement_end_context_invalid";
            if (state?.EffectRuntime == null || window?.Context == null || window.IsClosed || playerId != window.Context.PlayerId) return false;
            if (expectedRevision != state.EffectRuntime.StateRevision) { reason = "state_revision_mismatch"; return false; }
            var runtime = state.EffectRuntime;
            if (!string.IsNullOrEmpty(currentNodeId))
            {
                var node = runtime.EffectNodes.Find(n => n.EffectId == currentNodeId && n.PlayerId == playerId);
                if (node == null || node.EffectId == window.Context.OwnerEffectId || !IsWithin(runtime, node.EffectId, window.Context.OwnerEffectId) ||
                    node.Status == EffectNodeStatus.Completed || node.Status == EffectNodeStatus.Failed || node.Status == EffectNodeStatus.Faulted ||
                    string.IsNullOrEmpty(window.ActiveTargetEffectId) || !IsWithin(runtime, node.EffectId, window.ActiveTargetEffectId)) return false;
                if (canStopCurrentNode == null || !canStopCurrentNode(node)) { reason = "settlement_current_node_stop_forbidden"; return false; }
                if (!string.IsNullOrEmpty(interactionId) && !runtime.InteractionRequests.Any(r => r.GetStableInteractionId() == interactionId &&
                    r.OwnerEffectId == node.EffectId && r.Status == "open" && r.AnsweringPlayerId == playerId)) return false;
                plan = new SettlementEndActionPlan { Kind = SettlementEndActionKind.StopCurrentNode,
                    WindowId = window.Context.Id, OwnerEffectId = window.Context.OwnerEffectId, NodeEffectId = node.EffectId, InteractionId = interactionId ?? "" };
                reason = ""; return true;
            }
            // 不能通过漏传当前节点骗过整窗结束；活动段或它的真实阻塞交互必须先按准确节点处理。
            if (!string.IsNullOrEmpty(window.ActiveTargetEffectId) || runtime.InteractionRequests.Any(r => r.Status == "open" &&
                r.OwnerEffectId != window.Context.OwnerEffectId && IsWithin(runtime, r.OwnerEffectId, window.Context.OwnerEffectId)) ||
                runtime.EffectNodes.Any(n => n.EffectId != window.Context.OwnerEffectId &&
                    (n.Status == EffectNodeStatus.Ready || n.Status == EffectNodeStatus.Running || n.Status == EffectNodeStatus.Blocked) &&
                    IsWithin(runtime, n.EffectId, window.Context.OwnerEffectId)))
            { reason = "settlement_current_node_required"; return false; }
            if (!CanCloseCleanup(window, out reason)) return false;
            plan = new SettlementEndActionPlan { Kind = SettlementEndActionKind.CloseCleanupWindow,
                WindowId = window.Context.Id, OwnerEffectId = window.Context.OwnerEffectId };
            return true;
        }

        public static bool IsWithin(EffectRuntimeState runtime, string nodeId, string ancestorId)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (string id = nodeId; !string.IsNullOrEmpty(id) && visited.Add(id);)
            {
                if (id == ancestorId) return true;
                var node = runtime.EffectNodes.Find(n => n.EffectId == id);
                if (node == null) return false;
                id = node.ParentEffectId;
            }
            return false;
        }

        // 仅给Host工作副本使用；调用方须同步终止真实可选段并经同一次内核提交保存，不能仅删按钮。
        public static bool TryCloseCleanup(SettlementWindowRuntimeState window, out IReadOnlyList<string> declinedTargetIds, out string reason)
        {
            declinedTargetIds = Array.Empty<string>();
            if (!CanCloseCleanup(window, out reason)) return false;
            var declined = window.Entries.Where(e => e.SettlementState == "available" && e.WholeEffectPolicy == SettlementWholeEffectPolicy.Optional).ToList();
            foreach (var entry in declined) { entry.SettlementState = "declined"; entry.OutcomeCode = "whole_effect_declined"; }
            declinedTargetIds = declined.Select(e => e.TargetEffectId).ToList(); window.IsClosed = true; return true;
        }

        private static string BoundaryReason(GameState state, SettlementWindowRuntimeState window, SettlementEntryRuntimeState entry, int playerId)
        {
            if (window.IsClosed) return "settlement_window_closed";
            if (window.Context.PlayerId != playerId) return "settlement_permission_denied";
            if (entry.AcceptedUseCount >= entry.MaxUses || entry.SettlementState != "available") return "settlement_already_invoked";
            if (!string.IsNullOrEmpty(window.ActiveTargetEffectId)) return "settlement_execution_pending";
            var owner = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == window.Context.OwnerEffectId && n.PlayerId == playerId);
            if (owner == null || owner.Status == EffectNodeStatus.Completed || owner.Status == EffectNodeStatus.Failed || owner.Status == EffectNodeStatus.Faulted) return "settlement_window_closed";
            if (window.Context.Kind == ResourcePayment && (owner.Payment == null || !owner.Payment.BeforePaymentReady || !owner.Payment.AllowsSubstitution ||
                owner.Payment.IsCommitted || owner.Payment.IsCommitRequested || owner.Payment.Preparation?.IsCancelled == true)) return "payment_confirmation_host_missing";
            if (entry.EntryKind == CleanupOrder)
            {
                if (entry.WholeEffectPolicy == SettlementWholeEffectPolicy.Unspecified) return "settlement_whole_effect_policy_unresolved";
                var target = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == entry.TargetEffectId);
                return target == null || target.ParentEffectId != owner.EffectId || target.PlayerId != playerId || target.Status != EffectNodeStatus.Created
                    ? "settlement_cleanup_target_invalid" : "";
            }
            return state.ItemZones?.Items.Any(i => i.ItemId == entry.SourceItemId && i.ControllerSubject.PlayerId == playerId) == true ? "" : "settlement_source_unavailable";
        }

        private static void RequireOpen(SettlementWindowRuntimeState window)
        {
            if (window?.Context == null || window.ApiVersion != 1 || window.IsClosed || window.Entries == null) throw new ArgumentException("settlement_window_invalid");
        }

        private static void ValidatePresentation(SettlementPresentationDefinition presentation)
        {
            if (presentation == null || presentation.Label == null || presentation.Description == null || presentation.AriaLabel == null)
                throw new ArgumentException("settlement_presentation_missing");
            GameplayPresentationNormalizer.Validate(presentation);
        }

        // 恢复校验只核对窗口记录与真实节点，不修复缺失记录，也不按来源当前可见性撤销既成收尾。
        public static bool TryValidateWindow(GameState state, SettlementWindowRuntimeState window, out string reason)
        {
            reason = "settlement_window_state_invalid";
            if (state?.EffectRuntime == null || window?.Context == null || window.ApiVersion != 1 || window.Entries == null ||
                window.ActiveTargetEffectId == null || window.Context.TargetUseId == null || window.Context.TriggerId == null || string.IsNullOrEmpty(window.Context.OwnerEffectId) ||
                state.FindPlayer(window.Context.PlayerId) == null || window.NextRegistrationSequence < 0 ||
                !new[] { CharacterUse, ResourcePayment, PlayerCleanup }.Contains(window.Context.Kind)) return false;
            var c = window.Context;
            if (c.Id != StableIdFactory.Create("settlement_window", state.GameId, c.OwnerEffectId, c.Kind,
                c.PlayerId.ToString(CultureInfo.InvariantCulture), c.TargetUseId, c.TriggerId)) return false;
            var owner = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == c.OwnerEffectId && n.PlayerId == c.PlayerId);
            if (owner == null || window.Entries.Any(e => e == null) ||
                window.Entries.Select(e => e.OptionId).Distinct(StringComparer.Ordinal).Count() != window.Entries.Count ||
                window.Entries.Select(e => e.RegistrationSequence).Distinct().Count() != window.Entries.Count) return false;
            foreach (var entry in window.Entries)
            {
                if (entry.RegistrationSequence < 0 || entry.RegistrationSequence >= window.NextRegistrationSequence ||
                    entry.MaxUses != 1 || entry.AcceptedUseCount < 0 || entry.AcceptedUseCount > 1 || entry.Visibility != "owner" ||
                    string.IsNullOrEmpty(entry.LocalId) || entry.Effects == null || entry.Effects.Any(e => e == null) ||
                    !new[] { "available", "executing", "resolved", "declined" }.Contains(entry.SettlementState) ||
                    !Enum.IsDefined(typeof(SettlementWholeEffectPolicy), entry.WholeEffectPolicy)) return false;
                try { ValidatePresentation(entry.Presentation); }
                catch (ArgumentException) { return false; }
                bool cleanup = entry.EntryKind == CleanupOrder;
                if (!cleanup && entry.EntryKind != OptionalInsertion || cleanup != (c.Kind == PlayerCleanup)) return false;
                if (entry.OptionId != (cleanup ? StableIdFactory.Create("settlement_order", c.Id, entry.TargetEffectId) :
                    StableIdFactory.Create("settlement_option", c.Id, entry.SourceItemId, entry.LocalId))) return false;
                if (cleanup && (entry.Effects.Count != 0 || string.IsNullOrEmpty(entry.AbilityId) ||
                        entry.WholeEffectPolicy == SettlementWholeEffectPolicy.Optional && string.IsNullOrEmpty(entry.AvailabilityHandlerId)) ||
                    !cleanup && (entry.Effects.Count == 0 || string.IsNullOrEmpty(entry.SourceItemId) || string.IsNullOrEmpty(entry.AvailabilityHandlerId))) return false;
                bool selected = entry.SettlementState == "executing" || entry.SettlementState == "resolved";
                if (entry.AcceptedUseCount != (selected ? 1 : 0) || entry.SettlementState == "declined" &&
                    (!cleanup || entry.WholeEffectPolicy != SettlementWholeEffectPolicy.Optional || !window.IsClosed)) return false;
                var target = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == entry.TargetEffectId);
                if (!cleanup && !selected) { if (!string.IsNullOrEmpty(entry.TargetEffectId)) return false; continue; }
                if (target == null || target.PlayerId != c.PlayerId || target.ParentEffectId != owner.EffectId || !owner.ChildEffectIds.Contains(target.EffectId)) return false;
                bool terminal = target.Status == EffectNodeStatus.Completed || target.Status == EffectNodeStatus.Failed;
                if (entry.SettlementState == "available" && target.Status != EffectNodeStatus.Created ||
                    entry.SettlementState == "executing" && (terminal || target.Status == EffectNodeStatus.Faulted) ||
                    entry.SettlementState == "resolved" && !terminal || entry.SettlementState == "declined" && !terminal) return false;
            }
            var active = window.Entries.Where(e => e.SettlementState == "executing").ToList();
            if (active.Count > 1 || (active.Count == 0 ? window.ActiveTargetEffectId.Length != 0 : window.ActiveTargetEffectId != active[0].TargetEffectId)) return false;
            if (window.IsClosed && (active.Count != 0 || window.Entries.Any(e => e.EntryKind == CleanupOrder && e.SettlementState == "available"))) return false;
            reason = ""; return true;
        }

        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (!registry.TryGet(SegmentTypeId, out _)) registry.Register(new EffectRegistration(SegmentTypeId, ExecuteSegment, EffectExecutorKind.IntrinsicFlow, "1.0.0"));
        }

        // 由公共挂链器在同一工作副本中创建真实 child 后、激活任何 child 前调用。
        // 这里登记已有根节点，绝不在点击排序按钮时复制或重新创建正文。
        public static SettlementEntryRuntimeState RegisterAttachedCleanup(GameState state,
            EffectNodeRuntimeState owner, EffectNodeRuntimeState target, string availabilityHandlerId)
        {
            var declaration = target?.Settlement;
            if (state?.EffectRuntime == null || owner?.SettlementWindow?.Context?.Kind != PlayerCleanup ||
                declaration == null || declaration.EntryKind != CleanupOrder ||
                declaration.WholeEffectPolicy == SettlementWholeEffectPolicy.Unspecified ||
                string.IsNullOrEmpty(declaration.SourceItemId) || string.IsNullOrEmpty(declaration.AbilityId))
                throw new ArgumentException("settlement_attached_cleanup_invalid");
            var entry = RegisterCleanup(state, owner.SettlementWindow, target.EffectId, declaration.LocalId,
                declaration.AbilityId, declaration.SourceItemId, declaration.SourceEffectId,
                declaration.WholeEffectPolicy, declaration.Presentation, availabilityHandlerId);
            entry.SourceDefinitionId = state.ItemZones?.Items.Find(i => i.ItemId == declaration.SourceItemId)?.DefinitionId ?? "";
            return entry;
        }

        public static EffectSpec CreateCleanupSegment(int playerId, string localId, IEnumerable<EffectSpec> body)
        {
            if (string.IsNullOrEmpty(localId) || body == null) throw new ArgumentException("settlement_segment_invalid");
            var spec = EffectSpec.Create(SegmentTypeId, ItemTransferEffectExecutor.Object("localId", NormalizedValue.CreateString(localId)), playerId);
            spec.StableKey = localId; spec.DefinitionVersion = "1.0.0";
            spec.NestedEffectGroups.Add(body.ToList()); return spec;
        }

        private static EffectStepResult ExecuteSegment(EffectExecutionContext context)
        {
            if (context.Node.FlowStage == "")
            {
                var step = EffectStepResult.Continue("settling");
                foreach (var child in context.Node.NestedEffects) step.AddChild(EffectRegistry.FromRuntimeSpec(child));
                return step;
            }
            if (context.Node.FlowStage != "settling") return EffectStepResult.Failed("settlement_segment_stage_invalid");
            if (context.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed)) return EffectStepResult.Failed(context.ChildNodes.First(n => n.Status == EffectNodeStatus.Failed).FailureReason);
            return context.ChildNodes.All(n => n.Status == EffectNodeStatus.Completed) ? EffectStepResult.Completed() : EffectStepResult.NoProgress("等待收尾段内部效果。");
        }
    }
}
