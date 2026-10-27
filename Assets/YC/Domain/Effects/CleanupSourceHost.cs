using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public partial class EffectTreeExecutor
    {
        // 公共TryCompleteNodeOnWorking在EffectCompleted分发后、第二次HasUnresolvedBlocker前调用。
        // PendingOutcome/CompletedEventId沿原Kernel保存；响应结清后续原完成提交，不重发事件或正文。
        private void AttachCleanupCompletionDependenciesOnWorking(GameState work, EffectNodeRuntimeState node, List<RuleJournalEntry> entries)
        {
            if(node.EffectTypeId==CleanupObligation.InvokeType)
            {
                if(!work.EffectRuntime.CleanupObligations.Any(r=>r.InvokeEffectId==node.EffectId && r.Status=="executing")) throw new KernelException(EffectFaultCodes.InvalidRuntimeState,"cleanup_obligation_missing");
                TryAttachDeferredResponseDependenciesOnWorking(work,node.EffectId,CleanupSourceDependencies.Instance.GetPendingResponses(work,node.EffectId),entries);return;
            }
            if (node.EffectTypeId != CleanupSourceInvocationExecutor.TypeId) return;
            if (!CleanupSourceService.Invocations(work).Any(i => i.TargetEffectId == node.EffectId && i.Status == "executing"))
                throw new KernelException(EffectFaultCodes.InvalidRuntimeState, "cleanup_invocation_missing");
            TryAttachDeferredResponseDependenciesOnWorking(work, node.EffectId,
                CleanupSourceDependencies.Instance.GetPendingResponses(work, node.EffectId), entries);
        }

        public bool TryInvokeCleanupSource(int playerId, string commandId, CleanupSourceRequestRef request,
            out bool alreadyInvoked, out string invocationId, out string diagnostic)
        {
            alreadyInvoked = false; invocationId = ""; diagnostic = "cleanup_source_request_invalid";
            if (request == null) return false;
            var sourceOwner = Runtime.EffectNodes.SingleOrDefault(n => n.SettlementWindow?.Context?.Id == request.WindowId);
            if (sourceOwner == null) return false;
            string digest = CommandDigest(playerId, request);
            if (!CleanupSourceService.TryPrepare(state, sourceOwner.SettlementWindow, playerId, commandId, digest,
                request, registry, out var plan, out alreadyInvoked, out diagnostic)) return false;
            if (alreadyInvoked)
            { invocationId = CleanupSourceService.Invocations(state).Single(i => i.CommandId == commandId).InvocationId; return true; }
            try
            {
                var work = GameStateCloneService.DeepClone(state);
                var owner = work.EffectRuntime.EffectNodes.Single(n => n.EffectId == sourceOwner.EffectId);
                var window = owner.SettlementWindow; var entry = window.Entries.Single(e => e.OptionId == plan.OptionId);
                var entries = new List<RuleJournalEntry>();
                int eventStart = work.EffectRuntime.RuleEvents.Count, receiptStart = work.EffectRuntime.DispatchReceipts.Count;
                var spec = EffectSpec.Create(CleanupSourceInvocationExecutor.TypeId, Object(
                    "invocationId", NormalizedValue.CreateString(plan.InvocationId),
                    "sourceProofDigest", NormalizedValue.CreateString(CleanupSourceService.SourceProofDigest(plan.SourceRef)),
                    "triggerEventId", NormalizedValue.CreateString(window.Context.TriggerId)), playerId);
                spec.SourceId = plan.SourceRef.ItemId; spec.StableKey = plan.InvocationId;
                var id = AppendChildEffects(work, owner, new List<EffectSpec> { spec }, "cleanup-invocation", plan.InvocationId, entries).Single();
                var target = work.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
                var invocation = CleanupSourceService.CommitAccepted(window, plan, id);
                target.FlowStage = "body";
                // 先有真实Invocation身份，编译器及载体授权才能证明当前正文的来源。
                var body = registry.ConstructCleanupInvocationBody(work, target, entry, plan.SourceRef);
                if (body == null || body.Any(e => e == null)) throw new InvalidOperationException("cleanup_source_body_invalid");
                invocation.BodyEffectIds = AppendChildEffects(work, target, body, "cleanup-body", plan.InvocationId, entries);
                foreach (var main in work.EffectRuntime.MainNodes.Where(n => n.ExecutionEffectId == owner.EffectId))
                { main.OrderedChildEffectIds.Clear(); main.OrderedChildEffectIds.AddRange(owner.ChildEffectIds); }
                ActivateNextChild(work.EffectRuntime, owner);
                CommitPreparedWork(work, commandId, entries, eventStart, receiptStart, new[] { owner.EffectId, id });
                invocationId = plan.InvocationId; diagnostic = ""; return true;
            }
            catch (KernelException error) { diagnostic = error.Message; return false; }
            catch (InvalidOperationException error) { diagnostic = error.Message; return false; }
            catch (ArgumentException error) { diagnostic = error.Message; return false; }
        }

        internal static string CommandDigest(int player, CleanupSourceRequestRef request) => AbilityActionExecutor.Digest(Object(
            "intent", NormalizedValue.CreateString("cleanup_source"), "player", NormalizedValue.CreateInteger(player),
            "window", NormalizedValue.CreateString(request.WindowId), "option", NormalizedValue.CreateString(request.OptionId),
            "owner", NormalizedValue.CreateString(request.OwnerEffectId), "candidates", NormalizedValue.CreateInteger(request.CandidateSetVersion),
            "request", NormalizedValue.CreateString(request.InteractionId), "generation", NormalizedValue.CreateInteger(request.RequestGeneration),
            "facts", NormalizedValue.CreateString(request.FactsDigest)));
    }
}
