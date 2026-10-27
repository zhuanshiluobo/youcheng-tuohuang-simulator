using System;
using YC.Domain.Rules;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Scoring
{
    /// <summary>只消费受来源约束的结构化修正；没有任何卡牌或公司专属公式。</summary>
    public sealed class ScoringModifierQueryService
    {
        private readonly IMapQueryService map;
        private readonly EffectRegistry registry;
        private static readonly ConditionalWeakTable<EffectRegistry, object> registrations = new ConditionalWeakTable<EffectRegistry, object>();
        public ScoringModifierQueryService(IMapQueryService mapQuery, EffectRegistry registry)
            : this(mapQuery, registry, true) { }
        public ScoringModifierQueryService(IMapQueryService mapQuery, EffectRegistry registry, bool registerContract)
        {
            map = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            if (registerContract) Register(registry);
        }
        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            lock (registrations)
            {
                if (registrations.TryGetValue(registry, out _)) return;
                registry.RegisterAbilityQueryContract(ScoringModifierProtocol.QueryKind, ValidateItems);
                registrations.Add(registry, new object());
            }
        }
        public static string ValidateDeclarations(IEnumerable<AbilitySourceDeclaration> declarations)
        {
            foreach (var d in declarations.Where(d => d.QueryKinds.Contains(ScoringModifierProtocol.QueryKind)))
            {
                if (d.Kind != "ruleQuery" || d.Actions.Count != 0 || d.ObservedEvents.Count != 0) return "scoring_declaration_entry_invalid";
                var allowed = new[] { ScoringModifierProtocol.Influence, ScoringModifierProtocol.Tie, ScoringModifierProtocol.Value, ScoringModifierProtocol.Resources }.Select(ScoringModifierProtocol.Permission).ToArray();
                var permissions = d.AllowedOperations.Where(p => p.StartsWith("scoring.", StringComparison.Ordinal)).ToArray();
                if (permissions.Length == 0 || permissions.Distinct().Count() != permissions.Length || permissions.Any(p => !allowed.Contains(p))) return "scoring_declaration_scope_required";
            }
            return "";
        }
        public IReadOnlyList<ScoringModifierRecord> QueryPass(GameState state, ScoringQueryRequest request)
            => QueryPassInternal(state, request, null);
        private IReadOnlyList<ScoringModifierRecord> QueryPassInternal(GameState state, ScoringQueryRequest request, List<ScoringSourceRecord> queriedSources)
        {
            EnsureCatalog(state);
            if (request != null && !new[] { ScoringModifierProtocol.Influence, ScoringModifierProtocol.Tie, ScoringModifierProtocol.Value, ScoringModifierProtocol.Resources }.Contains(request.Pass)) throw new InvalidOperationException("scoring_pass_unknown");
            if (request == null || string.IsNullOrEmpty(request.SessionId) || string.IsNullOrEmpty(request.BasisDigest) || request.Facts == null || !request.Facts.IsValid()) throw new InvalidOperationException("scoring_query_facts_invalid");
            if (Text(request.Facts, "pass") != request.Pass || Text(request.Facts, "regionId") != request.RegionId || Number(request.Facts, "playerId", -1) != request.PlayerId) throw new InvalidOperationException("scoring_query_index_mismatch");
            if (request.Pass == ScoringModifierProtocol.Resources ? state.FindPlayer(request.PlayerId) == null : !map.Map.Regions.Any(r => r.RegionId == request.RegionId)) throw new InvalidOperationException("scoring_query_target_invalid");
            string error = ValidateDeclarations(registry.AbilityDeclarations); if (error.Length > 0) throw new InvalidOperationException(error);
            var query = new AbilityQueryContext { QueryKind = ScoringModifierProtocol.QueryKind, Purpose = request.Pass, QueryId = StableIdFactory.Create("scoring-query", request.SessionId, request.Pass, request.RegionId, request.PlayerId.ToString(CultureInfo.InvariantCulture), request.BasisDigest),
                FactsDigest = request.BasisDigest, MapId = state.MapId, ContentVersion = registry.ContentPackHash, StateRevision = state.EffectRuntime.StateRevision, SubjectPlayerId = request.PlayerId, Facts = request.Facts.Clone() };
            var result = new List<ScoringModifierRecord>();
            foreach (var source in registry.QuerySources(state, query).OrderBy(s => s.SourceKey, StringComparer.Ordinal))
            {
                var declaration = registry.AbilityDeclarations.Single(d => d.AbilityId == source.AbilityId);
                if (!declaration.AllowedOperations.Contains(ScoringModifierProtocol.Permission(request.Pass))) continue;
                if (request.PlayerId >= 0 && source.AuthorizedPlayerId != request.PlayerId) continue;
                query.SourceRef = source;
                // 即使错误的受信处理器试图改状态，也只能触及这个临时副本，并会被完整比较拒绝。
                var work = GameStateCloneService.DeepClone(state);
                string before = JsonConvert.SerializeObject(work);
                var returned = registry.QueryRules(work, query);
                if (before != JsonConvert.SerializeObject(work)) throw new InvalidOperationException("scoring_query_mutated_state");
                foreach (var row in returned)
                {
                    if (queriedSources != null && queriedSources.All(s => s.Source.SourceKey != row.SourceRef.SourceKey)) queriedSources.Add(new ScoringSourceRecord { Source = row.SourceRef, Version = row.VersionRef });
                    foreach (var item in row.Items.Items)
                    {
                        var record = new ScoringModifierRecord { QueryId = row.QueryId, BasisDigest = row.BasisDigest, Kind = Text(item, "kind"), RegionId = Text(item, "regionId"), PlayerId = Number(item, "playerId", -1),
                            Delta = Number(item, "delta"), RuleId = Text(item, "ruleId"), ReasonRef = Text(item, "reasonRef"), ProfileId = Text(item, "profileId"), Source = row.SourceRef, Version = row.VersionRef };
                        var entries = Value(item, "entries");
                        if (entries != null) foreach (var entry in entries.Items) record.Entries.Add(new ResourceScoreEntry { ResourceType = Text(entry, "resourceType"), Units = Number(entry, "units"), Points = Number(entry, "points") });
                        result.Add(record);
                    }
                }
            }
            return result;
        }
        private static string ValidateItems(AbilityQueryContext query, AbilitySourceRef source, NormalizedValue items)
        {
            if (items?.Kind != NormalizedValueKind.Array) return "scoring_items_array_required";
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.Items)
            {
                string kind = Text(item, "kind");
                string[] fields;
                switch (kind)
                {
                    case ScoringModifierProtocol.Influence: fields = new[] { "kind", "regionId", "playerId", "delta" }; break;
                    case ScoringModifierProtocol.Tie: fields = new[] { "kind", "regionId", "playerId", "ruleId" }; break;
                    case ScoringModifierProtocol.Value: fields = new[] { "kind", "regionId", "delta", "reasonRef" }; break;
                    case ScoringModifierProtocol.Resources: fields = new[] { "kind", "playerId", "profileId", "entries" }; break;
                    default: return "scoring_kind_unknown";
                }
                if (kind != query.Purpose || !Shape(item, fields)) return "scoring_item_shape_or_pass_invalid";
                if (kind != ScoringModifierProtocol.Resources && (Value(item, "regionId")?.Kind != NormalizedValueKind.String || Text(item, "regionId") != Text(query.Facts, "regionId"))) return "scoring_region_outside_query";
                if (kind != ScoringModifierProtocol.Value && (!Integer(item, "playerId") || Number(item, "playerId", -1) != source.AuthorizedPlayerId || query.SubjectPlayerId >= 0 && Number(item, "playerId") != query.SubjectPlayerId)) return "scoring_player_unauthorized";
                if ((kind == ScoringModifierProtocol.Influence || kind == ScoringModifierProtocol.Value) && !Integer(item, "delta")) return "scoring_delta_integer_required";
                if (kind == ScoringModifierProtocol.Tie && (Value(item, "ruleId")?.Kind != NormalizedValueKind.String || !Identifier(Text(item, "ruleId")))) return "scoring_rule_id_invalid";
                if (kind == ScoringModifierProtocol.Value)
                {
                    string reason = Text(item, "reasonRef");
                    if (Value(item, "reasonRef")?.Kind != NormalizedValueKind.String || reason != "region" && !(reason.StartsWith("claim:", StringComparison.Ordinal) && Value(query.Facts, "controlClaims")?.Items.Any(c => Text(c, "sourceKey") == source.SourceKey && Text(c, "ruleId") == reason.Substring(6)) == true)) return "scoring_value_reason_invalid";
                }
                if (kind == ScoringModifierProtocol.Resources)
                {
                    if (Value(item, "profileId")?.Kind != NormalizedValueKind.String || !Identifier(Text(item, "profileId"))) return "scoring_profile_id_invalid";
                    var entries = Value(item, "entries");
                    // Lua的{}没有数组/对象标记；仅此已声明数组字段允许空表表示空方案。
                    // 保存时仍投影成ResourceScoreEntry列表，不放宽任何非空对象或其他字段。
                    bool emptyTable = entries?.Kind == NormalizedValueKind.Object && entries.Properties.Count == 0 && entries.Items.Count == 0;
                    if (entries == null || entries.Kind != NormalizedValueKind.Array && !emptyTable || entries.Items.Count > Enum.GetNames(typeof(ResourceType)).Length) return "scoring_profile_entries_invalid";
                    var resources = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var entry in entries.Items)
                        if (!Shape(entry, new[] { "resourceType", "units", "points" }) || Value(entry, "resourceType")?.Kind != NormalizedValueKind.String || !Enum.GetNames(typeof(ResourceType)).Contains(Text(entry, "resourceType")) || !resources.Add(Text(entry, "resourceType")) || !Integer(entry, "units") || Number(entry, "units") <= 0 || !Integer(entry, "points") || Number(entry, "points") <= 0) return "scoring_profile_entry_invalid";
                }
                string key = kind + ":" + Text(item, "regionId") + ":" + Number(item, "playerId", -1);
                if (!keys.Add(key)) return "scoring_duplicate_source_target";
            }
            return "";
        }
        private static bool Shape(NormalizedValue value, string[] fields) => value?.Kind == NormalizedValueKind.Object && value.Properties.Count == fields.Length && value.Properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == fields.Length && value.Properties.All(p => fields.Contains(p.Name));
        private static bool Integer(NormalizedValue value, string key) { var n = Value(value, key); return n?.Kind == NormalizedValueKind.Integer && n.IntegerValue >= NormalizedValue.MinInteger && n.IntegerValue <= NormalizedValue.MaxInteger; }
        private static bool Identifier(string value) => !string.IsNullOrEmpty(value) && value.Length <= 128 && value.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == ':' || c == '-');
        private void EnsureCatalog(GameState state)
        {
            if (state == null || state.MapId != map.Map.MapId || state.ContentPackHash != registry.ContentPackHash) throw new InvalidOperationException("scoring_map_or_content_mismatch");
            if (!registry.HasAbilityCatalog && state.ContentDefinitions?.AbilitySources.Any(d => d.QueryKinds.Contains(ScoringModifierProtocol.QueryKind) || d.ObservedEvents.Contains("FinalScoringStarted")) == true) throw new InvalidOperationException("scoring_ability_catalog_missing");
        }
        public static string Hash(string value)
        {
            using (var algorithm = SHA256.Create()) return string.Concat(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        public ScoringControlSnapshot BuildControlSnapshot(GameState state, string sessionId)
        {
            EnsureCatalog(state);
            string basis = ComputeDependencyDigest(state);
            var snapshot = new ScoringControlSnapshot { SessionId = sessionId, MapId = state.MapId, ContentHash = registry.ContentPackHash, BasisDigest = basis,
                SnapshotId = StableIdFactory.Create("final-control", sessionId, basis), RulesDigest = SourceDigest(state), InfluenceFacts = ReadInfluences(state) };
            foreach (var region in map.Map.Regions.OrderBy(r => r.RegionId, StringComparer.Ordinal))
            {
                var result = new RegionControlResult { RegionId = region.RegionId, BaseScoreValue = region.ScoreValue, ScoreValue = region.ScoreValue };
                foreach (var player in state.Players.OrderBy(p => p.PlayerId))
                {
                    result.PhysicalCounts[player.PlayerId] = snapshot.InfluenceFacts.Count(i => i.RegionId == region.RegionId && i.SubjectKind == "player" && i.PlayerId == player.PlayerId);
                    result.CityCounts[player.PlayerId] = region.LocationIds.Contains(player.CityLocationId) ? 2 : 0;
                    result.InfluenceCounts[player.PlayerId] = checked(result.PhysicalCounts[player.PlayerId] + result.CityCounts[player.PlayerId]);
                }
                var facts = RegionFacts(ScoringModifierProtocol.Influence, result);
                result.InfluenceModifiers.AddRange(QueryPassInternal(state, Request(sessionId, basis, facts), snapshot.Sources));
                foreach (var delta in result.InfluenceModifiers) result.InfluenceCounts[delta.PlayerId] = checked(result.InfluenceCounts[delta.PlayerId] + delta.Delta);
                int highest = result.InfluenceCounts.Count == 0 ? 0 : result.InfluenceCounts.Values.Max();
                if (highest > 0) result.PositiveHighestPlayerIds.AddRange(result.InfluenceCounts.Where(p => p.Value == highest).Select(p => p.Key).OrderBy(id => id));
                result.IsTie = result.PositiveHighestPlayerIds.Count > 1;
                if (result.PositiveHighestPlayerIds.Count == 1)
                { result.ControllerPlayerId = result.PositiveHighestPlayerIds[0]; result.ControlReason = "unique_positive_highest"; }
                else if (result.IsTie)
                {
                    var claims = QueryPassInternal(state, Request(sessionId, basis, RegionFacts(ScoringModifierProtocol.Tie, result)), snapshot.Sources);
                    if (claims.Any(c => !result.PositiveHighestPlayerIds.Contains(c.PlayerId))) throw new InvalidOperationException("scoring_claim_not_positive_highest");
                    if (claims.Select(c => c.PlayerId).Distinct().Count() > 1) throw new InvalidOperationException("scoring_tie_claim_conflict");
                    if (claims.Count > 0)
                    {
                        result.ControllerPlayerId = claims[0].PlayerId; result.ControlReason = "tie_claim";
                        foreach (var claim in claims) result.ControlClaims.Add(new RegionControlClaim { PlayerId = claim.PlayerId, SourceKey = claim.Source.SourceKey, RuleId = claim.RuleId });
                        result.TieModifiers.AddRange(claims);
                    }
                }
                result.ValueModifiers.AddRange(QueryPassInternal(state, Request(sessionId, basis, RegionFacts(ScoringModifierProtocol.Value, result)), snapshot.Sources));
                foreach (var delta in result.ValueModifiers) result.ScoreValue = checked(result.ScoreValue + delta.Delta);
                snapshot.Regions.Add(FinalRegionControlSnapshot.FromResult(result));
            }
            snapshot.Sources = snapshot.Sources.OrderBy(s => s.Source.SourceKey, StringComparer.Ordinal).ToList();
            if (basis != ComputeDependencyDigest(state)) throw new InvalidOperationException("scoring_basis_changed_during_query");
            return snapshot;
        }
        public PlayerResourceScoreProfile QueryResourceProfile(GameState state, string sessionId, int playerId)
        {
            var player = state.FindPlayer(playerId) ?? throw new InvalidOperationException("scoring_resource_player_invalid");
            var values = Enum.GetNames(typeof(ResourceType)).Select(name => new NormalizedValueEntry { Name = name, Value = NormalizedValue.CreateInteger(player.Resources.Get((ResourceType)Enum.Parse(typeof(ResourceType), name))) }).ToList();
            var facts = Object("pass", S(ScoringModifierProtocol.Resources), "regionId", S(""), "playerId", N(playerId), "mapId", S(state.MapId), "resources", NormalizedValue.CreateObject(values));
            var profiles = QueryPass(state, Request(sessionId, ComputeDependencyDigest(state), facts));
            if (profiles.Count > 1) throw new InvalidOperationException("scoring_resource_profile_conflict");
            if (profiles.Count == 0) return null;
            var profile = profiles[0];
            return new PlayerResourceScoreProfile { PlayerId = playerId, ProfileId = profile.ProfileId, Source = profile.Source, Version = profile.Version, Entries = profile.Entries };
        }
        public string ComputeDependencyDigest(GameState state)
        {
            EnsureCatalog(state);
            return Hash(JsonConvert.SerializeObject(new { state.MapId, Content = registry.ContentPackHash,
                Regions = map.Map.Regions.OrderBy(r => r.RegionId, StringComparer.Ordinal).Select(r => new { r.RegionId, r.ScoreValue, Locations = r.LocationIds.OrderBy(x => x, StringComparer.Ordinal), Routes = r.RouteIds.OrderBy(x => x, StringComparer.Ordinal) }),
                Influences = ReadInfluences(state), Cities = state.Players.OrderBy(p => p.PlayerId).Select(p => new { p.PlayerId, p.CityLocationId }), Rules = SourceDigest(state) }));
        }
        public string SourceDigest(GameState state)
        {
            var sources = registry.QuerySources(state, -1, "ruleQuery", queryKind: ScoringModifierProtocol.QueryKind)
                .Concat(registry.QuerySources(state, -1, "event", "FinalScoringStarted")).OrderBy(s => s.SourceKey, StringComparer.Ordinal).ToArray();
            return Hash(JsonConvert.SerializeObject(new { registry.ContentPackHash, Sources = sources }));
        }
        public List<ScoringInfluenceFact> ReadInfluences(GameState state)
        {
            var result = new List<ScoringInfluenceFact>();
            if (ItemZoneLegacyProjection.IsActive(state))
            {
                var query = new ItemZoneQuery();
                foreach (var item in state.ItemZones.Items.Where(i => query.IsInfluence(state, i.ItemId)).OrderBy(i => i.ItemId, StringComparer.Ordinal))
                {
                    string region = InfluenceSubjectQuery.GetRegionId(state, item.ItemId);
                    if (string.IsNullOrEmpty(region)) continue;
                    var subject = item.OwnerSubject;
                    result.Add(new ScoringInfluenceFact { ItemId = item.ItemId, TypeId = item.TypeId, RegionId = region, Position = item.ZoneId + "/" + item.SlotId,
                        SubjectKind = subject.Kind == ItemSubjectKind.Player ? "player" : subject.Kind == ItemSubjectKind.Company ? "company" : "other", PlayerId = subject.Kind == ItemSubjectKind.Player ? subject.PlayerId : -1, CompanyId = subject.CompanyId ?? "" });
                }
            }
            else
            {
                // 仅支持无实体区域的既有基础隔离/旧版路径；不在此恢复旧公司专属公式。
                foreach (var item in state.Map.Influences.OrderBy(i => i.SlotId, StringComparer.Ordinal))
                {
                    var region = map.Map.Regions.SingleOrDefault(r => !string.IsNullOrEmpty(item.LocationId) && r.LocationIds.Contains(item.LocationId) || !string.IsNullOrEmpty(item.RouteId) && r.RouteIds.Contains(item.RouteId));
                    if (region == null) continue;
                    bool company = item.OwnerSubject?.SubjectType == "company";
                    result.Add(new ScoringInfluenceFact { ItemId = string.IsNullOrEmpty(item.InfluenceId) ? "legacy:" + item.SlotId : item.InfluenceId, TypeId = "legacy.influence", RegionId = region.RegionId, Position = item.SlotId,
                        SubjectKind = company ? "company" : "player", PlayerId = company ? -1 : item.PlayerId, CompanyId = company ? item.OwnerSubject.DefinitionId : "" });
                }
            }
            return result;
        }
        private static ScoringQueryRequest Request(string session, string basis, NormalizedValue facts) => new ScoringQueryRequest {
            SessionId = session, Pass = Text(facts, "pass"), RegionId = Text(facts, "regionId"), PlayerId = Number(facts, "playerId", -1), Facts = facts,
            BasisDigest = Hash(basis + "|" + facts.ToDeterministicString()) };
        private static NormalizedValue RegionFacts(string pass, RegionControlResult region) => Object("pass", S(pass), "regionId", S(region.RegionId), "playerId", N(-1),
            "baseValue", N(region.BaseScoreValue), "physicalCounts", Counts(region.PhysicalCounts), "cityCounts", Counts(region.CityCounts), "effectiveCounts", Counts(region.InfluenceCounts),
            "positiveHighest", NormalizedValue.CreateArray(region.PositiveHighestPlayerIds.Select(N).ToList()), "controllerPlayerId", N(region.ControllerPlayerId ?? -1), "controlReason", S(region.ControlReason),
            "controlClaims", NormalizedValue.CreateArray(region.ControlClaims.Select(c => Object("sourceKey", S(c.SourceKey), "ruleId", S(c.RuleId), "playerId", N(c.PlayerId))).ToList()));
        private static NormalizedValue Counts(Dictionary<int, int> counts) => NormalizedValue.CreateArray(counts.OrderBy(p => p.Key).Select(p => Object("playerId", N(p.Key), "count", N(p.Value))).ToList());
        private static NormalizedValue S(string value) => NormalizedValue.CreateString(value ?? "");
        private static NormalizedValue N(int value) => NormalizedValue.CreateInteger(value);
    }
}
