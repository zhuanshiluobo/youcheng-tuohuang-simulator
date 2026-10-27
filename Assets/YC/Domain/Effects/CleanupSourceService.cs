using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    /// <summary>稳定入口只登记一次；每次实际点击各有请求、正文和付款事实。</summary>
    public static class CleanupSourceService
    {
        public const string Version = "1.1.0", Capability = "cleanup_sources.v1", RequestPolicy = "cleanup_source_facts.v2";
        public static string OptionId(string window, string source, string local) => StableIdFactory.Create("cleanup-source", window, source, local);
        public static string InvocationId(string option, int sequence) => StableIdFactory.Create("cleanup-invocation", option, sequence.ToString(CultureInfo.InvariantCulture));
        public static string SourceProofDigest(AbilitySourceRef source) => AbilityActionExecutor.Digest(Object(
            "key", NormalizedValue.CreateString(source.SourceKey), "item", NormalizedValue.CreateString(source.ItemId),
            "definition", NormalizedValue.CreateString(source.DefinitionId), "ability", NormalizedValue.CreateString(source.AbilityId),
            "face", NormalizedValue.CreateString(source.ActiveFaceId), "version", NormalizedValue.CreateString(source.DeclarationVersion),
            "player", NormalizedValue.CreateInteger(source.AuthorizedPlayerId), "authorization", NormalizedValue.CreateString(source.AuthorizationRef),
            "zone", NormalizedValue.CreateString(source.EffectiveZoneId), "ownerItem", NormalizedValue.CreateString(source.RuleOwnerItemId),
            "grants", Strings(source.GrantingSourceRefs.OrderBy(s => s, StringComparer.Ordinal))));
        public static bool Terminal(EffectNodeRuntimeState n) => n != null && (n.Status == EffectNodeStatus.Completed || n.Status == EffectNodeStatus.Failed);
        public static IEnumerable<SettlementEntryRuntimeState> Sources(SettlementWindowRuntimeState w) => w?.Entries?.Where(e => e?.CleanupSource != null) ?? Enumerable.Empty<SettlementEntryRuntimeState>();
        public static IEnumerable<CleanupSourceInvocationState> Invocations(GameState state) => state.EffectRuntime.EffectNodes
            .Where(n => n.SettlementWindow != null).SelectMany(n => Sources(n.SettlementWindow)).SelectMany(e => e.CleanupSource.Invocations);

        public static SettlementEntryRuntimeState RegisterSource(GameState work, SettlementWindowRuntimeState window,
            EffectNodeRuntimeState target, AbilitySourceRef source, AbilitySourceDeclaration declaration, SettlementSourceContract contract)
        {
            if (source == null || declaration == null || contract == null || window?.Context?.Kind != SettlementEntryService.PlayerCleanup || window.IsClosed || target == null ||
                target.ParentEffectId != window.Context.OwnerEffectId || target.PlayerId != window.Context.PlayerId ||
                target.EffectTypeId != CleanupSourceInvocationExecutor.RegistrationTypeId || target.Settlement == null ||
                contract.AbilityId != source.AbilityId || contract.DefinitionId != source.DefinitionId || contract.LocalId != target.Settlement.LocalId ||
                contract.WholeEffectPolicy != SettlementWholeEffectPolicy.Optional || declaration.Kind != "cleanup" ||
                !new[] { "once", "unbounded" }.Contains(declaration.RepeatPolicy) || source.SourceKey != Text(target.NormalizedArguments, "sourceKey"))
                throw new ArgumentException("cleanup_source_registration_invalid");
            string id = OptionId(window.Context.Id, source.SourceKey, contract.LocalId);
            var old = window.Entries.SingleOrDefault(e => e.OptionId == id);
            if (old != null)
            {
                if (old.TargetEffectId != target.EffectId || old.CleanupSource?.RepeatPolicy != declaration.RepeatPolicy)
                    throw new ArgumentException("cleanup_source_registration_conflict");
                return old;
            }
            var entry = new SettlementEntryRuntimeState { OptionId = id, EntryKind = SettlementEntryService.CleanupOrder,
                LocalId = contract.LocalId, SourceItemId = source.ItemId, SourceDefinitionId = source.DefinitionId,
                AbilityId = source.AbilityId, SourceEffectId = target.Settlement.SourceEffectId, TargetEffectId = target.EffectId,
                TriggerId = window.Context.TriggerId, WholeEffectPolicy = SettlementWholeEffectPolicy.Optional,
                AvailabilityHandlerId = contract.AvailabilityHandlerId, RegistrationSequence = window.NextRegistrationSequence++,
                Presentation = GameStateCloneService.DeepClone(target.Settlement.Presentation),
                CleanupSource = new CleanupSourceEntryState { RepeatPolicy = declaration.RepeatPolicy, BodyHandlerRef = declaration.HandlerRef,
                    SourceRef = GameStateCloneService.DeepClone(source), VersionRef = new AbilityVersionRef {
                        ContentPackHash = Text(target.NormalizedArguments, "contentHash"), DefinitionVersion = declaration.DeclarationVersion,
                        HandlerHash = Text(target.NormalizedArguments, "handlerHash"), ExecutorVersion = Version },
                    RegistrationEventId = window.Context.TriggerId, RegistrationTargetId = target.EffectId,
                    RegistrationReceiptId = StableIdFactory.Create("cleanup-source-registration", window.Context.Id, target.EffectId, source.SourceKey) } };
            window.Entries.Add(entry);
            return entry;
        }

        public static string BoundaryReason(GameState state, SettlementWindowRuntimeState window, SettlementEntryRuntimeState entry)
        {
            var source = entry.CleanupSource;
            if (window.IsClosed || source.Sealed) return "settlement_window_closed";
            var owner = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == window.Context.OwnerEffectId);
            if (state.EffectRuntime.Status == EffectRuntimeStatus.PausedFault) return "cleanup_source_fault_paused";
            if (owner == null || Terminal(owner) || owner.Status == EffectNodeStatus.Faulted) return "settlement_window_closed";
            if (source.ActiveInvocationId.Length > 0 || window.ActiveTargetEffectId.Length > 0) return "settlement_execution_pending";
            if (source.RepeatPolicy == "once" && source.Invocations.Count > 0) return "settlement_already_invoked";
            var anchor = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == source.RegistrationTargetId);
            // 单个Created登记锚点保持原窗的合法等待；没有预建兑换正文或无限义务。
            return anchor?.Status != EffectNodeStatus.Created ? "cleanup_source_registration_invalid" : "";
        }

        public static bool TryPrepare(GameState state, SettlementWindowRuntimeState window, int player,
            string commandId, string digest, CleanupSourceRequestRef request, EffectRegistry registry,
            out CleanupSourceAcceptance plan, out bool replay, out string reason)
        {
            plan = null; replay = false; reason = "cleanup_source_request_invalid";
            if (request == null || string.IsNullOrWhiteSpace(commandId) || request.AnsweringPlayerId != player ||
                window?.Context?.PlayerId != player || request.WindowId != window.Context.Id || request.OwnerEffectId != window.Context.OwnerEffectId) return false;
            var entry = window.Entries.SingleOrDefault(e => e.OptionId == request.OptionId && e.CleanupSource != null);
            if (entry == null) return false;
            var old = Invocations(state).SingleOrDefault(i => i.CommandId == commandId);
            if (old != null)
            {
                if (old.CommandDigest != digest || old.RequestRef.OptionId != request.OptionId || old.SourceRef.AuthorizedPlayerId != player)
                { reason = "cleanup_source_command_payload_conflict"; return false; }
                replay = true; reason = ""; return true;
            }
            if (window.Context.Id != state.EffectRuntime.ActiveSettlementWindowId || window.IsClosed || entry.CleanupSource.Sealed)
            { reason = "settlement_window_closed"; return false; }
            if (entry.CleanupSource.Invocations.Any(i => i.RequestRef.RequestGeneration == request.RequestGeneration))
            { reason = "cleanup_source_request_consumed"; return false; }
            var current = registry.GetCleanupSourceRequest(state, window, entry);
            if (current.RequestGeneration != request.RequestGeneration || current.CandidateSetVersion != request.CandidateSetVersion || current.InteractionId != request.InteractionId || current.FactsDigest != request.FactsDigest)
            { reason = "cleanup_source_request_stale"; return false; }
            reason = BoundaryReason(state, window, entry);
            if (reason.Length > 0) return false;
            var available = registry.QueryCleanupSource(state, window, entry);
            if (!available.Enabled) { reason = available.DisabledReasonKey; return false; }
            int sequence = entry.CleanupSource.NextSequence;
            if (sequence == int.MaxValue || entry.CleanupSource.RequestGeneration == int.MaxValue)
            { reason = "cleanup_source_sequence_exhausted"; return false; }
            plan = new CleanupSourceAcceptance { WindowId = window.Context.Id, OptionId = entry.OptionId, Sequence = sequence,
                InvocationId = InvocationId(entry.OptionId, sequence), CommandId = commandId, CommandDigest = digest,
                RequestRef = GameStateCloneService.DeepClone(request), SourceRef = registry.CurrentCleanupSource(state, entry),
                VersionRef = GameStateCloneService.DeepClone(entry.CleanupSource.VersionRef) };
            reason = ""; return true;
        }

        public static CleanupSourceInvocationState CommitAccepted(SettlementWindowRuntimeState window,
            CleanupSourceAcceptance plan, string targetId)
        {
            var entry = window.Entries.Single(e => e.OptionId == plan.OptionId); var source = entry.CleanupSource;
            if (window.IsClosed || source.Sealed || window.ActiveTargetEffectId.Length > 0 || source.ActiveInvocationId.Length > 0 ||
                source.NextSequence != plan.Sequence) throw new ArgumentException("cleanup_source_acceptance_conflict");
            var invocation = new CleanupSourceInvocationState { InvocationId = plan.InvocationId, Sequence = plan.Sequence,
                CommandId = plan.CommandId, CommandDigest = plan.CommandDigest, TargetEffectId = targetId,
                RequestRef = GameStateCloneService.DeepClone(plan.RequestRef), SourceRef = GameStateCloneService.DeepClone(plan.SourceRef),
                VersionRef = GameStateCloneService.DeepClone(plan.VersionRef) };
            source.Invocations.Add(invocation); source.NextSequence = checked(source.NextSequence + 1);
            source.ActiveInvocationId = invocation.InvocationId; entry.AcceptedUseCount = source.Invocations.Count;
            entry.SettlementState = "executing"; window.ActiveTargetEffectId = targetId;
            return invocation;
        }

        public static bool ObserveTerminal(SettlementWindowRuntimeState window, EffectNodeRuntimeState target)
        {
            var entry = Sources(window).SingleOrDefault(e => e.CleanupSource.Invocations.Any(i => i.TargetEffectId == target.EffectId));
            if (entry == null) return false;
            var source = entry.CleanupSource; var invocation = source.Invocations.Single(i => i.TargetEffectId == target.EffectId);
            if (!Terminal(target) || invocation.Status != "executing") return true;
            // 包装执行器已经核实原支付、必要响应与真实子树；Faulted永不经过此路径。
            invocation.Outcome = Text(target.NormalizedResult, "outcome");
            if (!new[] { "completed", "declined", "condition_failed" }.Contains(invocation.Outcome))
                throw new KernelException(EffectFaultCodes.InvalidRuntimeState, "cleanup_source_terminal_receipt_missing");
            invocation.Status = invocation.Outcome;
            invocation.CompletionReceiptId = StableIdFactory.Create("cleanup-invocation-complete", invocation.InvocationId, target.EffectId, invocation.Outcome);
            source.ActiveInvocationId = ""; source.RequestGeneration = checked(source.RequestGeneration + 1);
            entry.SettlementState = source.RepeatPolicy == "once" ? "resolved" : "available";
            entry.OutcomeCode = invocation.Outcome;
            if (window.ActiveTargetEffectId == target.EffectId) window.ActiveTargetEffectId = "";
            return true;
        }

        // 公共Kernel须先在完成事件分发后挂接必要响应，再进入终态；这里是终态不变量校验，不是等待入口。
        public static void ObserveTerminal(GameState work, SettlementWindowRuntimeState window, EffectNodeRuntimeState target)
        {
            var invocation = Sources(window).SelectMany(e => e.CleanupSource.Invocations).SingleOrDefault(i => i.TargetEffectId == target.EffectId);
            if (invocation == null || !Terminal(target)) return;
            if (CleanupSourceDependencies.Instance.HasPendingForInvocation(work, target.EffectId, out string reason))
                throw new KernelException(EffectFaultCodes.InvalidRuntimeState, reason);
            invocation.PaymentEffectIds = work.EffectRuntime.EffectNodes.Where(n => InfluenceDepartureProtocol.IsWithin(work, n.EffectId, target.EffectId) &&
                CleanupSourceInvocationExecutor.IsPaid(work, n)).Select(n => n.EffectId).OrderBy(id => id, StringComparer.Ordinal).ToList();
            ObserveTerminal(window, target);
        }

        public static bool IsInvocationTarget(SettlementWindowRuntimeState window, string id) => Sources(window)
            .Any(e => e.CleanupSource.Invocations.Any(i => i.TargetEffectId == id));

        // 公共Runtime门禁的窄接点：玩家收尾先于窗口后置系统步，保留窗口和唯一活动调用门禁。
        public static bool IsInvocationSchedulingEligible(EffectRuntimeState runtime, SettlementWindowRuntimeState window, string id)
        {
            if (!IsInvocationTarget(window, id) || !SettlementEntryService.IsChildEligible(runtime, window, id)) return false;
            var owner = runtime.EffectNodes.SingleOrDefault(n => n.EffectId == window.Context.OwnerEffectId);
            int index = owner?.ChildEffectIds.IndexOf(id) ?? -1;
            // 窗口的系统步骤在玩家收尾段结清后执行；正在执行的调用不能反过来等待它们。
            // IsChildEligible仍校验真实窗口、唯一活动调用、来源和执行状态。
            return index >= 0;
        }

        public static bool CanClose(GameState state, SettlementWindowRuntimeState window, out string reason)
        {
            reason = "settlement_execution_pending";
            if (Sources(window).Any(e => e.CleanupSource.ActiveInvocationId.Length > 0)) return false;
            if (Sources(window).Any(e => !state.EffectRuntime.EffectNodes.Any(n => n.EffectId == e.TargetEffectId && n.Status == EffectNodeStatus.Created)))
            { reason = "cleanup_source_registration_invalid"; return false; }
            return !CleanupSourceDependencies.Instance.HasPendingForWindow(state, window.Context.OwnerEffectId, out reason);
        }
    }

    public sealed class CleanupSourceDependencies : ICleanupInvocationDependencies
    {
        public static readonly CleanupSourceDependencies Instance = new CleanupSourceDependencies();
        public bool HasPendingForInvocation(GameState state, string target, out string reason) => Pending(state, target, out reason);
        public bool HasPendingForWindow(GameState state, string owner, out string reason) => Pending(state, owner, out reason);
        public static bool RelatesTo(GameState state, DeferredResponseState response, string root)
        {
            bool Within(string id) => !string.IsNullOrEmpty(id) && InfluenceDepartureProtocol.IsWithin(state, id, root);
            return Within(response.SourceOperationEffectId) || Within(response.SourceDepartureEffectId) ||
                (response.SourceEventIds ?? new List<string>()).Concat(new[] { response.SourceEventId })
                    .Any(id => state.EffectRuntime.RuleEvents.Any(e => e.EventId == id && Within(e.SourceEffectId)));
        }
        public IReadOnlyList<DeferredResponseDependency> GetPendingResponses(GameState state, string root)
        {
            return EffectTreeExecutor.GetDeferredResponseDependencies(state,
                new DeferredDependencyQuery { OwnerEffectId = root, DependencyKind = DeferredDependencyKind.FinalizeGame })
                .Where(d => state.EffectRuntime.DeferredResponses.Any(r => r.DeferredResponseId == d.DeferredResponseId && RelatesTo(state, r, root))).ToArray();
        }
        private static bool Pending(GameState state, string root, out string reason)
        {
            bool Within(string id) => !string.IsNullOrEmpty(id) && InfluenceDepartureProtocol.IsWithin(state, id, root);
            var runtime = state.EffectRuntime;
            if (Instance.GetPendingResponses(state, root).Count > 0)
            { reason = "cleanup_required_response_pending"; return true; }
            if (runtime.InfluenceDepartures.Any(d => Within(d.SourceEffectId) && d.Status == "open") ||
                runtime.InfluenceDepartureBatches.Any(b => Within(b.OwnerEffectId) && !b.Sealed))
            { reason = "cleanup_departure_pending"; return true; }
            reason = ""; return false;
        }
    }
}
