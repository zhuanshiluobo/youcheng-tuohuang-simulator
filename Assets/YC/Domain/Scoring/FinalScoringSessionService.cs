using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Scoring
{
    /// <summary>由原末轮RoundEnded工作副本调用；不创建根节点，也不在终态观察者中直接改分。</summary>
    public sealed class FinalScoringSessionService
    {
        public const string Prepare = "final_prepare", Special = "final_special", RegionCommit = "final_region_commit",
            ResourceSnapshot = "final_resource_snapshot", ResourceCommit = "final_resource_commit", Complete = "final_complete", Finished = "final_finished";
        private readonly ScoringModifierQueryService queries;
        private readonly Func<GameState, NormalizedValue, string> validateLayerEvidence;
        public FinalScoringSessionService(ScoringModifierQueryService queries, Func<GameState, NormalizedValue, string> validateLayerEvidence = null)
        { this.queries = queries ?? throw new ArgumentNullException(nameof(queries)); this.validateLayerEvidence = validateLayerEvidence; }
        public static bool IsFinalStage(string stage) => stage == Prepare || stage == Special || stage == RegionCommit || stage == ResourceSnapshot || stage == ResourceCommit || stage == Complete || stage == Finished;
        public static bool IsActiveOwner(GameState state)
        {
            if (state?.FinalScoring?.Session != null && !state.FinalScoring.IsResolved) return true;
            var main = state?.EffectRuntime?.MainNodes.Find(n => n.NodeId == state.EffectRuntime.ActiveMainNodeId);
            return main?.NodeTypeId == RoundMainlineNodeTypeIds.RoundEnded && main.RoundNumber >= state.MaxRounds &&
                IsFinalStage(state.EffectRuntime.EffectNodes.Find(n => n.EffectId == main.ExecutionEffectId)?.FlowStage);
        }
        public EffectStepResult Advance(EffectExecutionContext context, MainlineNodeRuntimeState owner, Func<EffectEventRequest> createStartedEvent)
        {
            if (context == null || owner == null || owner.NodeTypeId != RoundMainlineNodeTypeIds.RoundEnded || owner.RoundNumber < context.State.MaxRounds || owner.ExecutionEffectId != context.Node.EffectId || !IsFinalStage(context.Node.FlowStage)) throw new InvalidOperationException("final_scoring_owner_or_stage_invalid");
            if (context.State.EffectRuntime.Status == EffectRuntimeStatus.PausedFault) throw new InvalidOperationException("final_scoring_runtime_faulted");
            var session = context.State.FinalScoring?.Session;
            if (context.Node.FlowStage == Prepare && session == null)
            {
                var wait = AwaitObligations(context); if (wait != null) return wait;
                if (!owner.CompletionRequested || !context.State.EffectRuntime.RuleEvents.Any(e => e.EventType == "RoundEnded" && e.SourceEffectId == context.Node.EffectId)) throw new InvalidOperationException("final_round_lifecycle_not_published");
                if (owner.PublishedEventKeys.Contains("FinalScoringStarted")) throw new InvalidOperationException("legacy_final_scoring_already_started");
                if (context.State.FinalScoring?.IsResolved == true) throw new InvalidOperationException("legacy_final_scoring_already_resolved");
                string id = StableIdFactory.Create("final-session", context.State.GameId, context.Node.EffectId, ScoringModifierProtocol.Version);
                session = new FinalScoringSessionState { SessionId = id, OwnerEffectId = context.Node.EffectId, OwnerMainNodeId = owner.NodeId, ContentHash = context.Registry.ContentPackHash };
                foreach (var player in context.State.Players.OrderBy(p => p.PlayerId)) session.Players.Add(new FinalScoringPlayerLedger { PlayerId = player.PlayerId, StartScore = player.Score,
                    FacilityScore = FinalScoringService.CalculateFacilityScore(player, context.State), CityStyleScore = FinalScoringService.CalculateCityStyleScore(player, context.State) });
                session.Control = queries.BuildControlSnapshot(context.State, id);
                if (context.State.FinalScoring == null) context.State.FinalScoring = new FinalScoringState();
                context.State.FinalScoring.Session = session;
                return EffectStepResult.Continue(Special);
            }
            if (session == null || session.ProtocolVersion != 1 || session.ExecutionVersion != ScoringModifierProtocol.Version || session.OwnerEffectId != context.Node.EffectId || session.OwnerMainNodeId != owner.NodeId || session.ContentHash != context.Registry.ContentPackHash) throw new InvalidOperationException("final_scoring_session_mismatch");
            if (session.Control == null || session.Control.BasisDigest != queries.ComputeDependencyDigest(context.State)) throw new InvalidOperationException("final_scoring_frozen_control_changed");
            if (context.Node.FlowStage == Finished)
            {
                if (!context.State.FinalScoring.IsResolved || session.CompletionReceipt != Receipt(session, "complete")) throw new InvalidOperationException("final_scoring_finished_without_receipt");
                return EffectStepResult.Completed().WithFlowStage(Finished);
            }
            var pending = AwaitObligations(context); if (pending != null) return pending;
            switch (context.Node.FlowStage)
            {
                case Special:
                    if (session.StartedEventId.Length == 0)
                    {
                        if (owner.PublishedEventKeys.Contains("FinalScoringStarted") || createStartedEvent == null) throw new InvalidOperationException("final_scoring_start_publication_invalid");
                        var e = createStartedEvent();
                        if (e == null || e.EventType != "FinalScoringStarted") throw new InvalidOperationException("final_scoring_start_event_invalid");
                        if (e.EventId.Length == 0) e.EventId = StableIdFactory.Create("final-started", session.SessionId);
                        e.SourceEffectId = context.Node.EffectId; e.OwnerNodeId = context.Node.EffectId;
                        session.StartedEventId = e.EventId; owner.PublishedEventKeys.Add("FinalScoringStarted");
                        return EffectStepResult.Continue(Special).AddEvent(e);
                    }
                    if (!context.State.EffectRuntime.RuleEvents.Any(e => e.EventId == session.StartedEventId && e.EventType == "FinalScoringStarted" && e.SourceEffectId == session.OwnerEffectId)) throw new InvalidOperationException("final_scoring_start_event_missing");
                    ArchiveSpecialEffects(context);
                    Reconcile(context.State, session);
                    return EffectStepResult.Continue(RegionCommit);
                case RegionCommit:
                    if (session.RegionCommitReceipt.Length == 0)
                    {
                        Reconcile(context.State, session);
                        var increments = session.Players.ToDictionary(p => p.PlayerId, _ => 0);
                        foreach (var region in session.Control.Regions)
                            if (region.ControllerPlayerId.HasValue) increments[region.ControllerPlayerId.Value] = checked(increments[region.ControllerPlayerId.Value] + region.ScoreValue);
                        foreach (var ledger in session.Players) { ledger.RegionScore = increments[ledger.PlayerId]; context.State.FindPlayer(ledger.PlayerId).Score = checked(context.State.FindPlayer(ledger.PlayerId).Score + ledger.RegionScore); }
                        session.RegionCommitReceipt = Receipt(session, "region");
                    }
                    return EffectStepResult.Continue(ResourceSnapshot);
                case ResourceSnapshot:
                    if (session.RegionCommitReceipt != Receipt(session, "region") || session.ResourceCommitReceipt.Length != 0) throw new InvalidOperationException("final_scoring_resource_stage_invalid");
                    Reconcile(context.State, session);
                    foreach (var ledger in session.Players)
                    {
                        if (ledger.Resources != null) throw new InvalidOperationException("final_scoring_resource_snapshot_duplicate");
                        ledger.Resources = context.State.FindPlayer(ledger.PlayerId).Resources.Clone();
                        ledger.Profile = queries.QueryResourceProfile(context.State, session.SessionId, ledger.PlayerId);
                    }
                    return EffectStepResult.Continue(ResourceCommit);
                case ResourceCommit:
                    if (session.RegionCommitReceipt != Receipt(session, "region")) throw new InvalidOperationException("final_scoring_region_receipt_missing");
                    if (session.ResourceCommitReceipt.Length == 0)
                    {
                        Reconcile(context.State, session);
                        var increments = new Dictionary<int, int>();
                        foreach (var ledger in session.Players)
                        {
                            var actual = context.State.FindPlayer(ledger.PlayerId).Resources;
                            if (ledger.Resources == null || Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>().Any(t => ledger.Resources.Get(t) < 0 || ledger.Resources.Get(t) != actual.Get(t))) throw new InvalidOperationException("final_scoring_resource_snapshot_changed");
                            increments[ledger.PlayerId] = FinalScoringService.CalculateResourceScore(ledger.Resources, ledger.Profile);
                        }
                        foreach (var ledger in session.Players) { ledger.ResourceScore = increments[ledger.PlayerId]; context.State.FindPlayer(ledger.PlayerId).Score = checked(context.State.FindPlayer(ledger.PlayerId).Score + ledger.ResourceScore); }
                        session.ResourceCommitReceipt = Receipt(session, "resources");
                    }
                    return EffectStepResult.Continue(Complete);
                case Complete:
                    if (session.RegionCommitReceipt != Receipt(session, "region") || session.ResourceCommitReceipt != Receipt(session, "resources")) throw new InvalidOperationException("final_scoring_completion_out_of_order");
                    Reconcile(context.State, session);
                    FinalScoringService.CompleteSession(context.State, session);
                    session.CompletionReceipt = Receipt(session, "complete");
                    return EffectStepResult.Completed().WithFlowStage(Finished);
                default: throw new InvalidOperationException("final_scoring_phase_invalid");
            }
        }
        private static EffectStepResult AwaitObligations(EffectExecutionContext context)
        {
            var children = context.ChildNodes.Where(n => !Terminal(n)).Select(n => n.EffectId).ToList();
            if (children.Count > 0)
            {
                if (!context.TryAttachCompletionDependenciesOnWorking(children, "final_scoring_required_children"))
                    throw new InvalidOperationException("final_scoring_child_dependency_invalid");
                return EffectStepResult.Continue(context.Node.FlowStage);
            }
            var dependencies = EffectTreeExecutor.GetDeferredResponseDependencies(context.State, new DeferredDependencyQuery { DependencyKind = DeferredDependencyKind.FinalizeGame, OwnerEffectId = context.Node.EffectId });
            if (dependencies.Count > 0)
            {
                if (!context.TryAttachDeferredResponseDependenciesOnWorking(dependencies)) throw new InvalidOperationException("final_scoring_dependency_cycle");
                return EffectStepResult.Continue(context.Node.FlowStage);
            }
            foreach (var batch in context.State.EffectRuntime.InfluenceDepartureBatches.Where(b => !b.Sealed))
            {
                var producer = context.State.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == batch.OwnerEffectId);
                if (producer == null || Terminal(producer) || Within(context.State, context.Node.EffectId, producer.EffectId)) throw new InvalidOperationException("final_scoring_unsealed_producer_invalid");
                if (!context.TryAttachCompletionDependenciesOnWorking(new[] { producer.EffectId }, "final_scoring_unsealed_producer"))
                    throw new InvalidOperationException("final_scoring_producer_dependency_invalid");
                return EffectStepResult.Continue(context.Node.FlowStage);
            }
            var paid = context.State.EffectRuntime.EffectNodes.Where(n => n.Payment?.IsCommitted == true && !Terminal(n)).Select(n => n.EffectId).ToList();
            if (paid.Count > 0)
            {
                if (!context.TryAttachCompletionDependenciesOnWorking(paid, "final_scoring_paid_effects"))
                    throw new InvalidOperationException("final_scoring_payment_dependency_invalid");
                return EffectStepResult.Continue(context.Node.FlowStage);
            }
            return null;
        }
        public void ArchiveSpecialEffects(EffectExecutionContext context)
        {
            var state = context.State; var session = state.FinalScoring?.Session ?? throw new InvalidOperationException("final_scoring_session_missing");
            foreach (var source in state.EffectRuntime.AbilitySourceUses.Where(r => r.TriggerEventId == session.StartedEventId).OrderBy(r => r.ReceiptId, StringComparer.Ordinal))
            {
                if (session.ProcessedSourceReceiptIds.Contains(source.ReceiptId)) continue;
                var root = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == source.OwnerEffectId);
                if (root?.Status != EffectNodeStatus.Completed || source.Outcome != "completed" || source.SourceRef == null || source.VersionRef == null || !Within(state, root.EffectId, session.OwnerEffectId)) throw new InvalidOperationException("final_scoring_source_incomplete");
                var validatedEvidenceScopes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var effect in state.EffectRuntime.EffectNodes.Where(n => n.Status == EffectNodeStatus.Completed && Within(state, n.EffectId, root.EffectId)).OrderBy(n => n.EffectId, StringComparer.Ordinal))
                {
                    var evidence = Value(effect.ContinuationContext, "scoringEvidence");
                    if (evidence != null)
                    {
                        if ((Value(evidence, "slotId") != null || Value(evidence, "facilityFactsDigest") != null || Value(evidence, "facilityItemIds") != null) &&
                            Number(evidence, "playerId", -1) != source.SourceRef.AuthorizedPlayerId)
                            throw new InvalidOperationException("final_scoring_layer_source_player_mismatch");
                        if (string.IsNullOrEmpty(effect.ContinuationScopeRootEffectId)) throw new InvalidOperationException("final_scoring_evidence_scope_missing");
                        if (validatedEvidenceScopes.Add(effect.ContinuationScopeRootEffectId)) ValidateEvidence(state, session, evidence);
                    }
                    if (evidence != null && effect.ContinuationScopeRootEffectId == effect.EffectId)
                    {
                        if (session.Facts.Any(r => r.EffectId == effect.EffectId)) throw new InvalidOperationException("final_scoring_fact_receipt_duplicate");
                        session.Facts.Add(new FinalScoringFactReceipt { ReceiptId = StableIdFactory.Create("final-fact-receipt", session.SessionId, effect.EffectId), EffectId = effect.EffectId, SourceRootEffectId = root.EffectId,
                            Source = GameStateCloneService.DeepClone(source.SourceRef), Version = GameStateCloneService.DeepClone(source.VersionRef), Evidence = evidence.Clone() });
                    }
                    bool score = IsScoreEffect(effect);
                    bool resource = effect.EffectTypeId == ResourceEffectTypeIds.Gain;
                    if (!score && !resource) continue;
                    if (session.Effects.Any(r => r.EffectId == effect.EffectId)) throw new InvalidOperationException("final_scoring_effect_receipt_duplicate");
                    var result = effect.NormalizedResult;
                    if (Value(result, "amount")?.Kind != NormalizedValueKind.Integer || Value(result, "playerId")?.Kind != NormalizedValueKind.Integer || !score && Text(result, "operation") != effect.EffectTypeId) throw new InvalidOperationException("final_scoring_effect_result_invalid");
                    int player = Number(result, "playerId", -1), amount = Number(result, "amount");
                    int scoreDelta = score ? ReadScoreDelta(effect) : 0;
                    var ledger = session.Players.SingleOrDefault(p => p.PlayerId == player) ?? throw new InvalidOperationException("final_scoring_beneficiary_invalid");
                    session.Effects.Add(new FinalScoringEffectReceipt { ReceiptId = StableIdFactory.Create("final-effect-receipt", session.SessionId, effect.EffectId), EffectId = effect.EffectId, SourceRootEffectId = root.EffectId,
                        PlayerId = player, ScoreDelta = scoreDelta, ResourceDelta = resource ? amount : 0, ResourceType = resource ? Text(result, "resourceType") : "",
                        Source = GameStateCloneService.DeepClone(source.SourceRef), Version = GameStateCloneService.DeepClone(source.VersionRef), Evidence = evidence?.Clone(), RegionId = Text(evidence, "regionId"), SlotId = Text(evidence, "slotId") });
                    if (score) ledger.SpecialScore = checked(ledger.SpecialScore + scoreDelta);
                }
                session.ProcessedSourceReceiptIds.Add(source.ReceiptId);
            }
        }
        private void ValidateEvidence(GameState state, FinalScoringSessionState session, NormalizedValue evidence)
        {
            if (!EffectContinuationContext.IsValid(evidence) || evidence.Kind != NormalizedValueKind.Object || Text(evidence, "sessionId") != session.SessionId || Text(evidence, "snapshotId") != session.Control.SnapshotId) throw new InvalidOperationException("final_scoring_evidence_context_invalid");
            string region = Text(evidence, "regionId");
            if (region.Length > 0 && session.Control.Regions.All(r => r.RegionId != region)) throw new InvalidOperationException("final_scoring_evidence_region_invalid");
            if (Value(evidence, "slotId") != null || Value(evidence, "facilityItemIds") != null || Value(evidence, "facilityFactsDigest") != null)
            {
                ValidateLayerFacts(state, evidence, validateLayerEvidence);
            }
        }
        public static string Receipt(FinalScoringSessionState session, string kind) => StableIdFactory.Create("final-stage-receipt", session.SessionId, kind);
        public static void ValidateLayerFacts(GameState state, NormalizedValue evidence, Func<GameState, NormalizedValue, string> validator)
        {
            if (validator == null) throw new InvalidOperationException("final_scoring_layer_evidence_validator_required");
            if (state == null || evidence == null) throw new ArgumentNullException("state_or_evidence");
            var work = GameStateCloneService.DeepClone(state); string before = JsonConvert.SerializeObject(work);
            string reason = validator(work, evidence.Clone());
            if (before != JsonConvert.SerializeObject(work)) throw new InvalidOperationException("final_scoring_layer_validator_mutated_state");
            if (!string.IsNullOrEmpty(reason)) throw new InvalidOperationException(reason);
        }
        public static bool IsScoreEffect(EffectNodeRuntimeState node) => node.EffectTypeId == ResourceEffectTypeIds.GainScore || node.EffectTypeId == LuaDomainEffectTypeIds.LoseScore;
        public static int ReadScoreDelta(EffectNodeRuntimeState node)
        {
            var value = node.NormalizedResult;
            if (node.EffectTypeId == ResourceEffectTypeIds.GainScore && Text(value, "operation") == node.EffectTypeId && Value(value, "amount")?.Kind == NormalizedValueKind.Integer) return Number(value, "amount");
            // LoseScore保存实际前后分数；有下限时不能把请求的amount冒充实际损失。
            if (node.EffectTypeId == LuaDomainEffectTypeIds.LoseScore && Value(value, "previousScore")?.Kind == NormalizedValueKind.Integer && Value(value, "score")?.Kind == NormalizedValueKind.Integer)
                return checked(Number(value, "score") - Number(value, "previousScore"));
            throw new InvalidOperationException("final_scoring_score_result_invalid");
        }
        public static bool Terminal(EffectNodeRuntimeState node) => node != null && (node.Status == EffectNodeStatus.Completed || node.Status == EffectNodeStatus.Failed);
        public static bool Within(GameState state, string nodeId, string ancestor)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(nodeId) && visited.Add(nodeId))
            { if (nodeId == ancestor) return true; nodeId = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == nodeId)?.ParentEffectId; }
            return false;
        }
        public static void Reconcile(GameState state, FinalScoringSessionState session)
        {
            foreach (var ledger in session.Players)
                if (state.FindPlayer(ledger.PlayerId)?.Score != checked(ledger.StartScore + ledger.SpecialScore + ledger.RegionScore + ledger.ResourceScore)) throw new InvalidOperationException("final_scoring_total_does_not_reconcile");
        }
    }
}
