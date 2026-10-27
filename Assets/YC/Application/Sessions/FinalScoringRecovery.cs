using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Application.Sessions
{
    /// <summary>新会话只校验保存事实；不推进、不重放终局事件、不迁移用户原档。</summary>
    public static class FinalScoringRecovery
    {
        public static bool ValidateRaw(JObject stateJson, out string reason)
        {
            try { return ValidateRawCore(stateJson, out reason); }
            catch (Exception error) when (error is ArgumentException || error is OverflowException || error is InvalidOperationException || error is JsonException)
            { reason = "final_scoring_raw_invalid:" + error.Message; return false; }
        }
        private static bool ValidateRawCore(JObject stateJson, out string reason)
        {
            reason = "final_scoring_raw_invalid";
            if (stateJson == null) return false;
            var final = stateJson["FinalScoring"] as JObject;
            var savedSession = final?["Session"];
            if (savedSession == null || savedSession.Type == JTokenType.Null)
            {
                var nodes = stateJson.SelectToken("EffectRuntime.EffectNodes") as JArray;
                if (nodes?.OfType<JObject>().Any(n => FinalScoringSessionService.IsFinalStage((string)n["FlowStage"]) && (string)n["FlowStage"] != FinalScoringSessionService.Prepare) == true) return false;
                reason = ""; return true;
            }
            RequireRaw(savedSession, typeof(FinalScoringSessionState), "FinalScoring.Session");
            if ((int)savedSession["ProtocolVersion"] != 1) return false;
            RequireRaw(final["IsResolved"], typeof(bool), "FinalScoring.IsResolved");
            if ((bool)final["IsResolved"])
            {
                // 仅新Session的完成结果要求原始字段齐全；旧无Session档不补新协议字段。
                RequireRaw(final["PlayerScores"], typeof(List<FinalPlayerScoreState>), "FinalScoring.PlayerScores");
                RequireRaw(final["RegionScores"], typeof(List<FinalRegionScoreState>), "FinalScoring.RegionScores");
                RequireRaw(final["WinnerPlayerIds"], typeof(List<int>), "FinalScoring.WinnerPlayerIds");
            }
            reason = ""; return true;
        }
        private static void RequireRaw(JToken token, Type type, string path, int depth = 0, bool nullable = false)
        {
            // 字段必须实际保存；null只在协议列出的引用和Nullable<int>上合法。
            if (token == null || depth > 64) throw new InvalidOperationException("field_missing_or_too_deep:" + path);
            Type underlying = Nullable.GetUnderlyingType(type);
            if (token.Type == JTokenType.Null)
            {
                if (nullable || underlying != null) return;
                throw new InvalidOperationException("field_null:" + path);
            }
            if (underlying != null) { RequireRaw(token, underlying, path, depth + 1); return; }
            bool primitive = type == typeof(string) || type == typeof(bool) || type == typeof(int) || type == typeof(long) || type.IsEnum;
            if (primitive)
            {
                bool valid = type == typeof(string) ? token.Type == JTokenType.String :
                    type == typeof(bool) ? token.Type == JTokenType.Boolean :
                    type == typeof(long) ? token.Type == JTokenType.Integer && long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _) :
                    token.Type == JTokenType.Integer && int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && (!type.IsEnum || Enum.IsDefined(type, number));
                if (!valid) throw new InvalidOperationException("field_type_or_range:" + path);
                return;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                if (token is not JArray array) throw new InvalidOperationException("field_array_required:" + path);
                for (int i = 0; i < array.Count; i++) RequireRaw(array[i], type.GetGenericArguments()[0], path + "[" + i + "]", depth + 1);
                return;
            }
            if (type == typeof(Dictionary<int, int>))
            {
                if (token is not JObject dictionary) throw new InvalidOperationException("field_dictionary_required:" + path);
                foreach (var entry in dictionary.Properties())
                {
                    if (!int.TryParse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int key) || entry.Name != key.ToString(CultureInfo.InvariantCulture)) throw new InvalidOperationException("field_dictionary_key:" + path);
                    RequireRaw(entry.Value, typeof(int), path + "." + entry.Name, depth + 1);
                }
                return;
            }
            if (!IsRawDto(type) || token is not JObject value) throw new InvalidOperationException("field_object_required:" + path);
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            if (value.Properties().Count() != fields.Length) throw new InvalidOperationException("field_shape:" + path);
            foreach (var field in fields)
            {
                if (!value.TryGetValue(field.Name, StringComparison.Ordinal, out var child)) throw new InvalidOperationException("field_missing:" + path + "." + field.Name);
                bool allowNull = type == typeof(FinalScoringPlayerLedger) && (field.Name == nameof(FinalScoringPlayerLedger.Resources) || field.Name == nameof(FinalScoringPlayerLedger.Profile)) ||
                    type == typeof(FinalScoringEffectReceipt) && field.Name == nameof(FinalScoringEffectReceipt.Evidence);
                RequireRaw(child, field.FieldType, path + "." + field.Name, depth + 1, allowNull);
            }
        }
        private static bool IsRawDto(Type type) => type == typeof(FinalScoringSessionState) || type == typeof(FinalScoringPlayerLedger) ||
            type == typeof(FinalScoringEffectReceipt) || type == typeof(FinalScoringFactReceipt) || type == typeof(ScoringControlSnapshot) ||
            type == typeof(FinalRegionControlSnapshot) || type == typeof(RegionControlClaim) || type == typeof(ScoringModifierRecord) ||
            type == typeof(ScoringSourceRecord) || type == typeof(ScoringInfluenceFact) || type == typeof(PlayerResourceScoreProfile) ||
            type == typeof(ResourceScoreEntry) || type == typeof(ResourceSet) || type == typeof(AbilitySourceRef) || type == typeof(AbilityVersionRef) ||
            type == typeof(NormalizedValue) || type == typeof(NormalizedValueEntry) || type == typeof(FinalPlayerScoreState) ||
            type == typeof(FinalRegionScoreState) || type == typeof(PlayerInfluenceCountState);
        public static bool TryValidate(GameState state, EffectRegistry registry, out string reason)
            => TryValidate(state, registry, null, out reason);
        public static bool TryValidate(GameState state, EffectRegistry registry, Func<GameState, NormalizedValue, string> validateLayerEvidence, out string reason)
        {
            reason = "";
            try
            {
                if (state == null || state.EffectRuntime == null) throw new InvalidOperationException("final_scoring_state_missing");
                var session = state.FinalScoring?.Session;
                if (session == null)
                {
                    // 旧已完成结果原样保留。旧已开始且在途的版本只能由原构建恢复，绝不补空重播。
                    if (state.FinalScoring?.IsResolved == true) return true;
                    if (state.EffectRuntime.EffectNodes.Any(n => FinalScoringSessionService.IsFinalStage(n.FlowStage) && n.FlowStage != FinalScoringSessionService.Prepare)) throw new InvalidOperationException("final_scoring_session_missing");
                    if (state.EffectRuntime.RuleEvents.Any(e => e.EventType == "FinalScoringStarted")) throw new InvalidOperationException("legacy_final_scoring_requires_original_execution_version");
                    return true;
                }
                if (session.ProtocolVersion != 1 || session.ExecutionVersion != ScoringModifierProtocol.Version || string.IsNullOrEmpty(session.SessionId) || session.ContentHash != state.ContentPackHash || registry != null && session.ContentHash != registry.ContentPackHash || session.Control == null || session.Players == null || session.Effects == null || session.Facts == null || session.ProcessedSourceReceiptIds == null) throw new InvalidOperationException("final_scoring_version_or_fields_invalid");
                var owner = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == session.OwnerEffectId);
                var main = state.EffectRuntime.MainNodes.SingleOrDefault(n => n.NodeId == session.OwnerMainNodeId);
                if (owner == null || main == null || main.ExecutionEffectId != owner.EffectId || main.NodeTypeId != RoundMainlineNodeTypeIds.RoundEnded || main.RoundNumber < state.MaxRounds || !FinalScoringSessionService.IsFinalStage(owner.FlowStage) || session.SessionId != StableIdFactory.Create("final-session", state.GameId, owner.EffectId, ScoringModifierProtocol.Version)) throw new InvalidOperationException("final_scoring_owner_invalid");
                if (!session.Players.Select(p => p.PlayerId).OrderBy(p => p).SequenceEqual(state.Players.Select(p => p.PlayerId).OrderBy(p => p)) || session.ProcessedSourceReceiptIds.Distinct(StringComparer.Ordinal).Count() != session.ProcessedSourceReceiptIds.Count || session.Effects.Select(e => e.EffectId).Distinct(StringComparer.Ordinal).Count() != session.Effects.Count) throw new InvalidOperationException("final_scoring_duplicate_or_missing_ledger");
                ValidateStage(state, session, owner, main);
                ValidateControl(state, session, registry);
                if (session.Facts.Select(f => f.EffectId).Distinct(StringComparer.Ordinal).Count() != session.Facts.Count) throw new InvalidOperationException("final_scoring_fact_receipt_duplicate");
                foreach (var fact in session.Facts)
                {
                    var node = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == fact.EffectId);
                    var source = state.EffectRuntime.AbilitySourceUses.SingleOrDefault(s => s.OwnerEffectId == fact.SourceRootEffectId && s.TriggerEventId == session.StartedEventId);
                    if (node?.Status != EffectNodeStatus.Completed || node.ContinuationScopeRootEffectId != node.EffectId || source == null || fact.Source?.SourceKey != source.SourceKey || fact.Version?.HandlerHash != source.VersionRef?.HandlerHash || fact.Version?.ContentPackHash != session.ContentHash || fact.ReceiptId != StableIdFactory.Create("final-fact-receipt", session.SessionId, node.EffectId) || fact.Evidence == null || !FinalScoringSessionService.Within(state, node.EffectId, source.OwnerEffectId) || fact.Evidence.ToDeterministicString() != Value(node.ContinuationContext, "scoringEvidence")?.ToDeterministicString()) throw new InvalidOperationException("final_scoring_fact_receipt_invalid");
                    ValidateEvidence(session, fact.Evidence, state, validateLayerEvidence);
                }
                foreach (var receipt in session.Effects)
                {
                    var node = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == receipt.EffectId);
                    var source = state.EffectRuntime.AbilitySourceUses.SingleOrDefault(s => s.OwnerEffectId == receipt.SourceRootEffectId && s.TriggerEventId == session.StartedEventId);
                    if (node?.Status != EffectNodeStatus.Completed || source == null || receipt.ReceiptId != StableIdFactory.Create("final-effect-receipt", session.SessionId, node.EffectId) || receipt.Source == null || receipt.Version == null || source.SourceKey != receipt.Source.SourceKey || receipt.Version.ContentPackHash != session.ContentHash || receipt.Version.HandlerHash != source.VersionRef?.HandlerHash || !FinalScoringSessionService.Within(state, node.EffectId, source.OwnerEffectId) || Number(node.NormalizedResult, "playerId", -1) != receipt.PlayerId) throw new InvalidOperationException("final_scoring_effect_receipt_invalid");
                    bool score = FinalScoringSessionService.IsScoreEffect(node), resource = node.EffectTypeId == ResourceEffectTypeIds.Gain;
                    if (!score && !resource || !score && Text(node.NormalizedResult, "operation") != node.EffectTypeId || Value(node.NormalizedResult, "amount")?.Kind != NormalizedValueKind.Integer || receipt.ScoreDelta != (score ? FinalScoringSessionService.ReadScoreDelta(node) : 0) || receipt.ResourceDelta != (resource ? Number(node.NormalizedResult, "amount") : 0) || receipt.ResourceType != (resource ? Text(node.NormalizedResult, "resourceType") : "")) throw new InvalidOperationException("final_scoring_effect_amount_mismatch");
                    var evidence = Value(node.ContinuationContext, "scoringEvidence");
                    if (receipt.Evidence?.ToDeterministicString() != evidence?.ToDeterministicString()) throw new InvalidOperationException("final_scoring_evidence_mismatch");
                    if (evidence != null) ValidateEvidence(session, evidence, state, validateLayerEvidence);
                }
                foreach (string id in session.ProcessedSourceReceiptIds)
                    if (!state.EffectRuntime.AbilitySourceUses.Any(r => r.ReceiptId == id && r.TriggerEventId == session.StartedEventId && r.Outcome == "completed")) throw new InvalidOperationException("final_scoring_processed_source_missing");
                if (owner.FlowStage != FinalScoringSessionService.Special && !session.ProcessedSourceReceiptIds.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(state.EffectRuntime.AbilitySourceUses.Where(r => r.TriggerEventId == session.StartedEventId).Select(r => r.ReceiptId).OrderBy(x => x, StringComparer.Ordinal))) throw new InvalidOperationException("final_scoring_unprocessed_zero_source");
                foreach (var ledger in session.Players)
                {
                    int archived = session.Effects.Where(e => e.PlayerId == ledger.PlayerId).Aggregate(0, (sum, e) => checked(sum + e.ScoreDelta));
                    if (ledger.SpecialScore != archived) throw new InvalidOperationException("final_scoring_special_ledger_mismatch");
                    int actualSpecial = ActualSpecial(state, session, ledger.PlayerId);
                    if (owner.FlowStage != FinalScoringSessionService.Special && actualSpecial != archived) throw new InvalidOperationException("final_scoring_unarchived_effect");
                    int regionScore = session.RegionCommitReceipt.Length == 0 ? 0 : session.Control.Regions.Where(r => r.ControllerPlayerId == ledger.PlayerId).Aggregate(0, (sum, r) => checked(sum + r.ScoreValue));
                    if (ledger.RegionScore != regionScore || ledger.ResourceScore != (session.ResourceCommitReceipt.Length == 0 ? 0 : FinalScoringService.CalculateResourceScore(ledger.Resources, ledger.Profile))) throw new InvalidOperationException("final_scoring_phase_amount_mismatch");
                    if (state.FindPlayer(ledger.PlayerId).Score != checked(ledger.StartScore + actualSpecial + ledger.RegionScore + ledger.ResourceScore)) throw new InvalidOperationException("final_scoring_live_score_mismatch");
                    if (ledger.Resources != null && Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>().Any(t => ledger.Resources.Get(t) < 0 || ledger.Resources.Get(t) != state.FindPlayer(ledger.PlayerId).Resources.Get(t))) throw new InvalidOperationException("final_scoring_resource_snapshot_mismatch");
                    if (ledger.Profile != null && (ledger.Profile.PlayerId != ledger.PlayerId || ledger.Profile.Source?.AuthorizedPlayerId != ledger.PlayerId || ledger.Profile.Version?.ContentPackHash != session.ContentHash)) throw new InvalidOperationException("final_scoring_resource_profile_source_invalid");
                    if (ledger.Profile != null) ValidateSource(state, registry, ledger.Profile.Source, ledger.Profile.Version, ScoringModifierProtocol.Resources);
                }
                if (state.FinalScoring.IsResolved)
                {
                    ValidateCompletedResult(state, session);
                    var candidates = state.FinalScoring.PlayerScores.Where(p => p.TotalScore == state.FinalScoring.PlayerScores.Max(s => s.TotalScore)).ToList();
                    candidates = candidates.Where(p => p.GoldVoucherTiebreaker == candidates.Max(s => s.GoldVoucherTiebreaker)).ToList();
                    candidates = candidates.Where(p => p.PureOriginiumTiebreaker == candidates.Max(s => s.PureOriginiumTiebreaker)).ToList();
                    if (!candidates.Select(p => p.PlayerId).OrderBy(p => p).SequenceEqual(state.FinalScoring.WinnerPlayerIds.OrderBy(p => p))) throw new InvalidOperationException("final_scoring_winners_mismatch");
                }
                return true;
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is OverflowException || error is NullReferenceException)
            { reason = "final_scoring_invalid:" + error.Message; return false; }
        }
        private static void ValidateCompletedResult(GameState state, FinalScoringSessionState session)
        {
            var final = state.FinalScoring;
            if (final.PlayerScores == null || final.RegionScores == null || final.WinnerPlayerIds == null || final.PlayerScores.Any(p => p == null) || final.RegionScores.Any(r => r == null)) throw new InvalidOperationException("final_scoring_final_result_missing");
            if (final.PlayerScores.Select(p => p.PlayerId).Distinct().Count() != final.PlayerScores.Count ||
                !final.PlayerScores.Select(p => p.PlayerId).OrderBy(p => p).SequenceEqual(session.Players.Select(p => p.PlayerId).OrderBy(p => p))) throw new InvalidOperationException("final_scoring_final_player_set_mismatch");
            if (final.RegionScores.Select(r => r.RegionId).Distinct(StringComparer.Ordinal).Count() != final.RegionScores.Count ||
                !final.RegionScores.Select(r => r.RegionId).OrderBy(r => r, StringComparer.Ordinal).SequenceEqual(session.Control.Regions.Select(r => r.RegionId).OrderBy(r => r, StringComparer.Ordinal))) throw new InvalidOperationException("final_scoring_final_region_set_mismatch");
            foreach (var region in session.Control.Regions)
            {
                var result = final.RegionScores.Single(r => r.RegionId == region.RegionId);
                if (result.ScoreValue != region.ScoreValue || result.ControllerPlayerId != (region.ControllerPlayerId ?? -1) || result.InfluenceCounts == null || result.InfluenceCounts.Any(c => c == null) ||
                    result.InfluenceCounts.Select(c => c.PlayerId).Distinct().Count() != result.InfluenceCounts.Count ||
                    !result.InfluenceCounts.Select(c => c.PlayerId).OrderBy(p => p).SequenceEqual(region.InfluenceCounts.Keys.OrderBy(p => p)) ||
                    result.InfluenceCounts.Any(c => c.Count != region.InfluenceCounts[c.PlayerId])) throw new InvalidOperationException("final_scoring_final_region_result_mismatch:" + region.RegionId);
            }
            foreach (var ledger in session.Players)
            {
                var score = final.PlayerScores.Single(p => p.PlayerId == ledger.PlayerId);
                if (score.TotalScore != checked(ledger.StartScore + ledger.SpecialScore + ledger.RegionScore + ledger.ResourceScore) || score.TotalScore != state.FindPlayer(ledger.PlayerId).Score || score.SpecialScore != ledger.SpecialScore || score.RegionScore != ledger.RegionScore || score.ResourceScore != ledger.ResourceScore || score.BaseScore != checked(ledger.StartScore - ledger.FacilityScore - ledger.CityStyleScore) || score.FacilityScore != ledger.FacilityScore || score.CityStyleScore != ledger.CityStyleScore || score.GoldVoucherTiebreaker != ledger.Resources.GoldVoucher || score.PureOriginiumTiebreaker != ledger.Resources.PureOriginium) throw new InvalidOperationException("final_scoring_final_result_mismatch");
                if (score.ControlledRegionIds == null || !score.ControlledRegionIds.OrderBy(r => r, StringComparer.Ordinal).SequenceEqual(session.Control.Regions.Where(r => r.ControllerPlayerId == ledger.PlayerId).Select(r => r.RegionId).OrderBy(r => r, StringComparer.Ordinal))) throw new InvalidOperationException("final_scoring_final_controlled_regions_mismatch:" + ledger.PlayerId);
                if (score.RemainingResources == null || Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>().Any(t => score.RemainingResources.Get(t) != ledger.Resources.Get(t))) throw new InvalidOperationException("final_scoring_final_remaining_resources_mismatch:" + ledger.PlayerId);
            }
        }
        private static void ValidateStage(GameState state, FinalScoringSessionState s, EffectNodeRuntimeState owner, MainlineNodeRuntimeState main)
        {
            bool started = s.StartedEventId.Length > 0;
            if (!started && (s.Effects.Count != 0 || s.Facts.Count != 0 || s.ProcessedSourceReceiptIds.Count != 0)) throw new InvalidOperationException("final_scoring_income_before_start_event");
            if (started != main.PublishedEventKeys.Contains("FinalScoringStarted") || started && !state.EffectRuntime.RuleEvents.Any(e => e.EventId == s.StartedEventId && e.EventType == "FinalScoringStarted" && e.SourceEffectId == s.OwnerEffectId)) throw new InvalidOperationException("final_scoring_start_receipt_invalid");
            bool region = owner.FlowStage == FinalScoringSessionService.ResourceSnapshot || owner.FlowStage == FinalScoringSessionService.ResourceCommit || owner.FlowStage == FinalScoringSessionService.Complete || owner.FlowStage == FinalScoringSessionService.Finished;
            bool resources = owner.FlowStage == FinalScoringSessionService.Complete || owner.FlowStage == FinalScoringSessionService.Finished;
            bool snapshots = owner.FlowStage == FinalScoringSessionService.ResourceCommit || resources;
            bool finished = owner.FlowStage == FinalScoringSessionService.Finished;
            if (owner.FlowStage == FinalScoringSessionService.Prepare || owner.FlowStage != FinalScoringSessionService.Special && !started || s.RegionCommitReceipt != (region ? FinalScoringSessionService.Receipt(s, "region") : "") || s.ResourceCommitReceipt != (resources ? FinalScoringSessionService.Receipt(s, "resources") : "") || s.CompletionReceipt != (finished ? FinalScoringSessionService.Receipt(s, "complete") : "") || state.FinalScoring.IsResolved != finished || s.Players.Any(p => (p.Resources != null) != snapshots)) throw new InvalidOperationException("final_scoring_stage_receipts_inconsistent");
        }
        private static int ActualSpecial(GameState state, FinalScoringSessionState s, int player)
        {
            if (string.IsNullOrEmpty(s.StartedEventId)) return 0;
            var roots = state.EffectRuntime.AbilitySourceUses.Where(r => r.TriggerEventId == s.StartedEventId).Select(r => r.OwnerEffectId).ToArray();
            return state.EffectRuntime.EffectNodes.Where(n => n.Status == EffectNodeStatus.Completed && FinalScoringSessionService.IsScoreEffect(n) && Number(n.NormalizedResult, "playerId", -1) == player && roots.Any(root => FinalScoringSessionService.Within(state, n.EffectId, root))).Aggregate(0, (sum, n) => checked(sum + FinalScoringSessionService.ReadScoreDelta(n)));
        }
        private static void ValidateControl(GameState state, FinalScoringSessionState s, EffectRegistry registry)
        {
            var c = s.Control;
            if (c.ProtocolVersion != 1 || c.SessionId != s.SessionId || c.ContentHash != s.ContentHash || c.MapId != state.MapId || string.IsNullOrEmpty(c.BasisDigest) || c.SnapshotId != StableIdFactory.Create("final-control", s.SessionId, c.BasisDigest) || c.Regions == null || c.Sources == null || c.InfluenceFacts == null || c.Regions.Select(r => r.RegionId).Distinct().Count() != c.Regions.Count) throw new InvalidOperationException("final_scoring_control_snapshot_invalid");
            var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId));
            if (!map.Map.Regions.Select(r => r.RegionId).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(c.Regions.Select(r => r.RegionId).OrderBy(x => x, StringComparer.Ordinal))) throw new InvalidOperationException("final_scoring_region_set_mismatch");
            if (registry != null)
            {
                var queries = new ScoringModifierQueryService(map, registry, false);
                if (queries.ComputeDependencyDigest(state) != c.BasisDigest || queries.SourceDigest(state) != c.RulesDigest || JsonConvert.SerializeObject(queries.ReadInfluences(state)) != JsonConvert.SerializeObject(c.InfluenceFacts)) throw new InvalidOperationException("final_scoring_control_dependency_changed");
            }
            foreach (var source in c.Sources)
                ValidateSource(state, registry, source.Source, source.Version, "");
            foreach (var region in c.Regions)
            {
                var players = s.Players.Select(p => p.PlayerId).OrderBy(p => p).ToArray();
                foreach (var facts in new[] { region.InfluenceCountFacts, region.PhysicalCountFacts, region.CityCountFacts })
                    if (facts == null || facts.Any(f => f == null) || !facts.Select(f => f.PlayerId).SequenceEqual(players))
                        throw new InvalidOperationException("final_scoring_control_count_facts_noncanonical");
                if (region.InfluenceCounts == null || region.PhysicalCounts == null || region.CityCounts == null || region.InfluenceModifiers == null || region.TieModifiers == null || region.ValueModifiers == null || region.PositiveHighestPlayerIds == null || region.ControlClaims == null || !region.InfluenceCounts.Keys.OrderBy(p => p).SequenceEqual(s.Players.Select(p => p.PlayerId).OrderBy(p => p))) throw new InvalidOperationException("final_scoring_control_counts_missing");
                foreach (var player in s.Players)
                    if (!region.PhysicalCounts.ContainsKey(player.PlayerId) || !region.CityCounts.ContainsKey(player.PlayerId) || region.PhysicalCounts[player.PlayerId] != c.InfluenceFacts.Count(f => f.RegionId == region.RegionId && f.SubjectKind == "player" && f.PlayerId == player.PlayerId) || region.CityCounts[player.PlayerId] != (map.Map.Regions.Single(r => r.RegionId == region.RegionId).LocationIds.Contains(state.FindPlayer(player.PlayerId).CityLocationId) ? 2 : 0) || region.InfluenceCounts[player.PlayerId] != checked(region.PhysicalCounts[player.PlayerId] + region.CityCounts[player.PlayerId] + region.InfluenceModifiers.Where(d => d.PlayerId == player.PlayerId).Aggregate(0, (sum, d) => checked(sum + d.Delta)))) throw new InvalidOperationException("final_scoring_control_sum_invalid");
                if (region.BaseScoreValue != map.Map.Regions.Single(r => r.RegionId == region.RegionId).ScoreValue) throw new InvalidOperationException("final_scoring_region_base_value_invalid");
                int maximum = region.InfluenceCounts.Count == 0 ? 0 : region.InfluenceCounts.Values.Max();
                var highest = region.InfluenceCounts.Where(p => maximum > 0 && p.Value == maximum).Select(p => p.Key).OrderBy(p => p).ToList();
                if (!highest.SequenceEqual(region.PositiveHighestPlayerIds.OrderBy(p => p)) || region.IsTie != (highest.Count > 1) || region.ScoreValue != checked(region.BaseScoreValue + region.ValueModifiers.Aggregate(0, (sum, d) => checked(sum + d.Delta)))) throw new InvalidOperationException("final_scoring_control_derived_invalid");
                int? controller = highest.Count == 1 ? highest[0] : (int?)null;
                if (region.ControlClaims.Count > 0)
                {
                    if (highest.Count < 2 || region.ControlClaims.Select(p => p.PlayerId).Distinct().Count() != 1 || !highest.Contains(region.ControlClaims[0].PlayerId)) throw new InvalidOperationException("final_scoring_control_claim_invalid");
                    controller = region.ControlClaims[0].PlayerId;
                }
                string reason = region.ControlClaims.Count > 0 ? "tie_claim" : highest.Count == 1 ? "unique_positive_highest" : "uncontrolled";
                if (region.ControllerPlayerId != controller || region.ControlReason != reason) throw new InvalidOperationException("final_scoring_controller_invalid");
                if (region.InfluenceModifiers.Any(d => d.Kind != ScoringModifierProtocol.Influence) || region.TieModifiers.Any(d => d.Kind != ScoringModifierProtocol.Tie) || region.ValueModifiers.Any(d => d.Kind != ScoringModifierProtocol.Value) || region.ControlClaims.Count != region.TieModifiers.Count || region.ControlClaims.Any(claim => !region.TieModifiers.Any(d => d.Source?.SourceKey == claim.SourceKey && d.RuleId == claim.RuleId && d.PlayerId == claim.PlayerId))) throw new InvalidOperationException("final_scoring_modifier_category_invalid");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in region.InfluenceModifiers.Concat(region.TieModifiers).Concat(region.ValueModifiers))
                {
                    if (d.Source == null || d.Version == null || d.Version.ContentPackHash != s.ContentHash || d.RegionId != region.RegionId || d.Kind != ScoringModifierProtocol.Value && d.PlayerId != d.Source.AuthorizedPlayerId || !c.Sources.Any(source => source.Source.SourceKey == d.Source.SourceKey && source.Version.HandlerHash == d.Version.HandlerHash) || !keys.Add(d.Source.SourceKey + ":" + d.Kind + ":" + d.PlayerId)) throw new InvalidOperationException("final_scoring_modifier_receipt_invalid");
                    ValidateSource(state, registry, d.Source, d.Version, d.Kind);
                    if (d.Delta < NormalizedValue.MinInteger || d.Delta > NormalizedValue.MaxInteger || d.Kind == ScoringModifierProtocol.Value && d.ReasonRef != "region" && !region.ControlClaims.Any(claim => d.ReasonRef == "claim:" + claim.RuleId && d.Source.SourceKey == claim.SourceKey)) throw new InvalidOperationException("final_scoring_modifier_value_invalid");
                }
            }
        }
        private static void ValidateSource(GameState state, EffectRegistry registry, AbilitySourceRef source, AbilityVersionRef version, string kind)
        {
            var declaration = state.ContentDefinitions?.AbilitySources.SingleOrDefault(d => d.AbilityId == source?.AbilityId);
            if (source == null || version == null || declaration == null || declaration.Kind != "ruleQuery" || !declaration.QueryKinds.Contains(ScoringModifierProtocol.QueryKind) || version.ContentPackHash != state.ContentPackHash || version.DefinitionVersion != source.DeclarationVersion || version.DefinitionVersion != declaration.DeclarationVersion || string.IsNullOrEmpty(version.HandlerHash) || !AbilitySourceService.IsCurrent(state, state.ContentDefinitions.AbilitySources, source) || kind.Length > 0 && !declaration.AllowedOperations.Contains(ScoringModifierProtocol.Permission(kind)) || registry != null && registry.HandlerHash(declaration.HandlerRef) != version.HandlerHash) throw new InvalidOperationException("final_scoring_modifier_source_version_invalid");
        }
        private static void ValidateEvidence(FinalScoringSessionState session, NormalizedValue evidence, GameState state, Func<GameState, NormalizedValue, string> validateLayerEvidence)
        {
            if (!EffectContinuationContext.IsValid(evidence) || Text(evidence, "sessionId") != session.SessionId || Text(evidence, "snapshotId") != session.Control.SnapshotId) throw new InvalidOperationException("final_scoring_evidence_context_invalid");
            if (Text(evidence, "regionId").Length > 0 && session.Control.Regions.All(r => r.RegionId != Text(evidence, "regionId"))) throw new InvalidOperationException("final_scoring_evidence_region_invalid");
            if (Value(evidence, "slotId") != null || Value(evidence, "facilityItemIds") != null || Value(evidence, "facilityFactsDigest") != null)
            {
                FinalScoringSessionService.ValidateLayerFacts(state, evidence, validateLayerEvidence);
            }
        }
    }
}
