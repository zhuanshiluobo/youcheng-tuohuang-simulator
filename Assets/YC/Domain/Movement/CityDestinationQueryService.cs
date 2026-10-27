using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Travel;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Movement
{
    public sealed class CityDestinationQuery
    {
        public int MovingPlayerId { get; }
        public string SourceEffectId { get; }
        public string Purpose { get; }
        public string TargetPolicy { get; }
        public IReadOnlyList<string> TopologyLocationIds { get; }
        public CityDestinationQuery(int player, string sourceEffectId, string purpose,
            string targetPolicy, IReadOnlyList<string> topology)
        {
            MovingPlayerId = player; SourceEffectId = sourceEffectId ?? "";
            Purpose = purpose ?? ""; TargetPolicy = targetPolicy ?? "";
            TopologyLocationIds = Array.AsReadOnly((topology ?? throw new ArgumentNullException(nameof(topology))).ToArray());
        }
    }
    public sealed class CityDestinationExclusion
    {
        public string CandidateId { get; }
        private readonly AbilitySourceRef source;
        private readonly AbilityVersionRef version;
        public AbilitySourceRef SourceRef => GameStateCloneService.DeepClone(source);
        public AbilityVersionRef VersionRef => GameStateCloneService.DeepClone(version);
        internal CityDestinationExclusion(string candidateId, AbilitySourceRef source, AbilityVersionRef version)
        { CandidateId = candidateId; this.source = GameStateCloneService.DeepClone(source); this.version = GameStateCloneService.DeepClone(version); }
    }
    public sealed class CityDestinationQueryResult
    {
        public IReadOnlyList<string> AllowedLocationIds { get; }
        public IReadOnlyList<CityDestinationExclusion> HardExclusions { get; }
        public string FactsDigest { get; }
        public string ResultDigest { get; }
        internal CityDestinationQueryResult(IEnumerable<string> ids, IReadOnlyList<CityDestinationExclusion> exclusions, string facts)
        {
            AllowedLocationIds = Array.AsReadOnly(ids.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            HardExclusions = Array.AsReadOnly(exclusions.OrderBy(x => x.CandidateId, StringComparer.Ordinal).ThenBy(x => x.SourceRef.SourceKey, StringComparer.Ordinal).ToArray()); FactsDigest = facts;
            ResultDigest = RouteResourceSelectionService.Hash(new[] { facts }.Concat(AllowedLocationIds)
                .Concat(HardExclusions.Select(x => x.CandidateId + ":" + x.SourceRef.SourceKey + ":" + x.VersionRef.HandlerHash)));
        }
    }

    public sealed class CityDestinationQueryService
    {
        public const string CandidateKind = "city_destination";
        public const string QueryKind = "city_destination.v1";
        public const string Version = "1.0.0";
        private readonly IMapQueryService map;
        private readonly EffectRegistry registry;
        public CityDestinationQueryService(IMapQueryService map, EffectRegistry registry = null)
        { this.map = map ?? throw new ArgumentNullException(nameof(map)); this.registry = registry; }
        public static string CandidateId(string locationId) => CityMoveCandidateQueryService.CandidatePrefix + locationId;
        public CityDestinationQueryResult Query(GameState state, CityDestinationQuery query)
        {
            if (state == null || query == null || state.FindPlayer(query.MovingPlayerId) == null ||
                YC.Domain.Items.ItemZoneLegacyProjection.IsActive(state) && state.ContentDefinitions == null ||
                map.Map.MapId != state.MapId || !new[] { "ordinary", "free", "specified" }.Contains(query.Purpose) ||
                query.TopologyLocationIds.Distinct(StringComparer.Ordinal).Count() != query.TopologyLocationIds.Count ||
                query.TopologyLocationIds.Any(id => !map.Map.Locations.Any(l => l.LocationId == id)))
                throw new InvalidOperationException("city_destination_context_invalid");
            var sources = AbilitySourceService.QuerySources(state,
                state.ContentDefinitions?.AbilitySources ?? new List<AbilitySourceDeclaration>(), -1, "ruleQuery", queryKind: QueryKind);
            if (sources.Count > 0 && registry == null) throw new InvalidOperationException("city_destination_registry_required");
            var pool = query.TopologyLocationIds.Select(CandidateId).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var fields = new List<string> { QueryKind, Version, state.GameId, state.ContentPackHash, state.MapId,
                query.MovingPlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture), query.SourceEffectId, query.Purpose,
                query.TargetPolicy, state.FindPlayer(query.MovingPlayerId).CityLocationId };
            fields.Add(JsonConvert.SerializeObject(map.Map)); fields.Add(state.Round.ToString(System.Globalization.CultureInfo.InvariantCulture));
            fields.Add(state.Phase.ToString()); fields.AddRange(pool);
            foreach (var player in state.Players.OrderBy(p => p.PlayerId)) { fields.Add(player.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)); fields.Add(player.CityLocationId); }
            foreach (var id in state.Map.OpenLocationIds.OrderBy(x => x, StringComparer.Ordinal)) fields.Add("open:" + id);
            foreach (var token in state.Map.ResourceTokens.ToArray().OrderBy(t => t.LocationId, StringComparer.Ordinal)) fields.Add(JsonConvert.SerializeObject(token));
            foreach (var source in sources.OrderBy(x => x.SourceKey, StringComparer.Ordinal)) fields.Add(JsonConvert.SerializeObject(source));
            foreach (var i in state.Map.Influences.ToArray().OrderBy(x => x.InfluenceId, StringComparer.Ordinal))
            {
                fields.Add(JsonConvert.SerializeObject(i));
                var item = state.ItemZones?.Items.Find(x => x.ItemId == i.InfluenceId);
                fields.Add(item?.TypeId ?? ""); fields.Add(item?.ZoneId ?? "");
                fields.Add(JsonConvert.SerializeObject(item?.OwnerSubject));
                fields.Add(JsonConvert.SerializeObject(item?.ControllerSubject));
            }
            string digest = RouteResourceSelectionService.Hash(fields);
            var destinations = query.TopologyLocationIds.Select(id => Object("candidateId", NormalizedValue.CreateString(CandidateId(id)),
                "locationId", NormalizedValue.CreateString(id))).ToList();
            var draft = new CandidateSetDraft { CandidateSetId = StableIdFactory.Create("city-destinations", query.SourceEffectId, digest),
                Version = 1, CandidateKind = CandidateKind, SourceEffectId = query.SourceEffectId, ExecutingPlayerId = query.MovingPlayerId,
                CandidatePoolIds = pool, DefaultCandidateIds = new List<string>(pool),
                Context = Object("purpose", NormalizedValue.CreateString(query.Purpose), "targetPolicy", NormalizedValue.CreateString(query.TargetPolicy),
                    "startLocationId", NormalizedValue.CreateString(state.FindPlayer(query.MovingPlayerId).CityLocationId),
                    "factsDigest", NormalizedValue.CreateString(digest), "destinations", NormalizedValue.CreateArray(destinations)) };
            var soft = registry == null ? new CandidateSetResolver().Resolve(state, draft, new CandidatePolicyRegistry()) :
                new CandidateSetResolver().Resolve(state, draft, registry.CityDestinationCandidatePolicies);
            if (!soft.Succeeded) throw new InvalidOperationException("city_destination_candidate_invalid:" + soft.Diagnostic);
            var hard = registry == null ? (IReadOnlyList<CityDestinationExclusion>)Array.Empty<CityDestinationExclusion>() :
                registry.QueryCityDestinationExclusions(state, draft, digest);
            var excluded = new HashSet<string>(hard.Select(x => x.CandidateId), StringComparer.Ordinal);
            // 所有普通补丁完成后再套硬禁入；后来的Add不能复活被禁止点。
            return new CityDestinationQueryResult(soft.FinalCandidateIds.Where(id => !excluded.Contains(id))
                .Select(CityMoveCandidateQueryService.GetTargetLocationId).Where(id => MatchesPolicy(state, query.MovingPlayerId, id, query.TargetPolicy)), hard, digest);
        }

        public static bool MatchesPolicy(GameState state, int playerId, string locationId, string policy)
        {
            if (policy == "standard") return true;
            if (policy != "explored" && policy != "explored_own_influence") return false;
            if (!new ResourceTokenService().HasResourceToken(state.Map, locationId)) return false;
            return policy == "explored" || state.Map.Influences.ToArray().Any(i => i.PlayerId == playerId && i.LocationId == locationId);
        }

        public ValidationResult ValidateTarget(GameState state, CityDestinationQuery query, string locationId)
        {
            try
            {
                return Query(state, query).AllowedLocationIds.Contains(locationId) ? ValidationResult.Success :
                    ValidationResult.Failure(CommandErrorCode.InvalidTarget, "city_destination_excluded");
            }
            catch (InvalidOperationException error)
            { return ValidationResult.Failure(CommandErrorCode.InvalidTarget, error.Message); }
        }
    }
}
