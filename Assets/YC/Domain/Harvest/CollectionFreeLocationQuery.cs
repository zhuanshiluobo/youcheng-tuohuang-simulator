using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Travel;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Harvest
{
    public sealed class CollectionFreeLocationQuery
    {
        public const string QueryKind = "collection_free_locations.v1";
        public const string Version = "1.0.0";
        private readonly EffectRegistry registry;
        private readonly IMapQueryService map;
        public CollectionFreeLocationQuery(EffectRegistry registry, IMapQueryService map)
        { this.registry = registry; this.map = map ?? throw new ArgumentNullException(nameof(map)); }

        public CollectionFreeLocationResult Query(GameState state, CollectionQueryContext query)
        {
            ValidateContext(state, query, map);
            var sources = Sources(state, query.PlayerId).ToArray();
            string basis = FactsDigest(state, query, map, sources);
            var queried = new List<CollectionQuerySource>();
            var legal = query.LegalLocationIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var free = new HashSet<string>(StringComparer.Ordinal);
            var evidence = new List<FreeLocationEvidence>();
            if (legal.Contains(query.CityStartLocationId))
            {
                free.Add(query.CityStartLocationId);
                evidence.Add(new FreeLocationEvidence(query.CityStartLocationId, "city", basis));
            }
            if (sources.Length > 0 && registry == null)
                throw new InvalidOperationException("collection_query_registry_required");
            if (registry != null)
            {
                registry.EnsureCollectionQueryContract();
                foreach (var source in sources)
                {
                    var context = new AbilityQueryContext {
                        QueryId = StableIdFactory.Create("collection-free", query.SelectionSessionId, source.SourceKey, basis),
                        QueryKind = QueryKind, FactsDigest = basis, MapId = state.MapId, Purpose = query.Purpose,
                        ContentVersion = query.ContentVersion, SubjectPlayerId = query.PlayerId, SourceRef = source,
                        Facts = Object("purpose", NormalizedValue.CreateString(query.Purpose),
                            "sourceEffectId", NormalizedValue.CreateString(query.SourceEffectId),
                            "selectionSessionId", NormalizedValue.CreateString(query.SelectionSessionId),
                            "startLocationId", NormalizedValue.CreateString(query.CityStartLocationId),
                            "legalLocationIds", Strings(legal)) };
                    var returned = registry.QueryRules(state, context);
                    if (returned.Count != 1) throw new InvalidOperationException("collection_query_source_not_bound");
                    foreach (var result in returned)
                    {
                        string invalid = ValidateReturn(context, result.SourceRef, result.Items);
                        if (invalid.Length > 0) throw new InvalidOperationException(invalid);
                        var declaration = registry.AbilityDeclarations.Single(d => d.AbilityId == result.SourceRef.AbilityId && d.DefinitionId == result.SourceRef.DefinitionId);
                        registry.RegisterCollectionQueryHandlerVersion(declaration.HandlerRef, result.VersionRef.HandlerHash);
                        queried.Add(new CollectionQuerySource(result.SourceRef, result.VersionRef));
                        if (result.SourceRef.AuthorizedPlayerId != query.PlayerId || result.SourceRef.SourceKey != source.SourceKey)
                            throw new InvalidOperationException("collection_query_source_mismatch");
                        foreach (var item in result.Items.Items)
                        {
                            string id = Text(item, "locationId");
                            if (!map.Map.Locations.Any(l => l.LocationId == id))
                                throw new InvalidOperationException("collection_query_unknown_location");
                            // 返回真实地图点不授予采集权；仅与原C相交。
                            if (!legal.Contains(id)) continue;
                            free.Add(id);
                            var proof = new FreeLocationEvidence(id, "ability", basis, result.SourceRef, result.VersionRef);
                            if (evidence.All(e => e.Identity != proof.Identity)) evidence.Add(proof);
                        }
                    }
                }
            }
            return Create(legal, free.OrderBy(x => x, StringComparer.Ordinal).ToArray(), evidence, basis, queried);
        }

        internal static IEnumerable<AbilitySourceRef> Sources(GameState state, int playerId)
            => AbilitySourceService.QuerySources(state, state.ContentDefinitions?.AbilitySources ?? new List<AbilitySourceDeclaration>(),
                playerId, "ruleQuery", queryKind: QueryKind);

        internal static string ValidateReturn(AbilityQueryContext query, AbilitySourceRef source, NormalizedValue items)
        {
            if (query.Purpose != "normal" && query.Purpose != "extra" || source.AuthorizedPlayerId != query.SubjectPlayerId)
                return "collection_query_purpose_or_authority_invalid";
            foreach (var item in items.Items)
                if (item.Kind != NormalizedValueKind.Object || item.Properties.Count != 2 ||
                    item.Properties.Any(p => p.Name != "kind" && p.Name != "locationId") ||
                    Text(item, "kind") != "free_location" || Text(item, "locationId").Length == 0)
                    return "collection_query_item_invalid";
            return "";
        }

        public static CollectionFreeLocationResult Create(IReadOnlyList<string> legal, IReadOnlyList<string> free,
            IReadOnlyList<FreeLocationEvidence> evidence, string basis, IReadOnlyList<CollectionQuerySource> querySources = null)
        {
            if (legal == null || free == null || evidence == null || basis == null || basis.Length != 64 ||
                legal.Any(string.IsNullOrEmpty) || free.Any(string.IsNullOrEmpty) ||
                legal.Distinct(StringComparer.Ordinal).Count() != legal.Count || free.Distinct(StringComparer.Ordinal).Count() != free.Count ||
                free.Any(id => !legal.Contains(id)) || evidence.Any(e => e == null || !free.Contains(e.LocationId) || e.BasisDigest != basis ||
                    e.Kind != "city" && e.Kind != "ability" || e.Kind == "ability" && (e.SourceRef == null || e.VersionRef == null) ||
                    e.Kind == "city" && (e.SourceRef != null || e.VersionRef != null)) ||
                evidence.Select(e => e.Identity).Distinct(StringComparer.Ordinal).Count() != evidence.Count ||
                free.Any(id => evidence.All(e => e.LocationId != id)))
                throw new ArgumentException("collection_free_evidence_invalid");
            if (querySources != null && querySources.Any(s => s == null)) throw new ArgumentException("collection_query_sources_invalid");
            var frozen = (querySources ?? evidence.Where(e => e.Kind == "ability")
                .GroupBy(e => e.SourceRef.SourceKey, StringComparer.Ordinal).Select(g => new CollectionQuerySource(g.First().SourceRef, g.First().VersionRef)).ToArray())
                .OrderBy(s => s.SourceRef.SourceKey, StringComparer.Ordinal).ToArray();
            if (frozen.Any(s => s == null || s.SourceRef == null || s.VersionRef == null) ||
                frozen.Select(s => s.SourceRef.SourceKey).Distinct(StringComparer.Ordinal).Count() != frozen.Length ||
                evidence.Where(e => e.Kind == "ability").Any(e => !frozen.Any(s =>
                    JsonConvert.SerializeObject(s.SourceRef) == JsonConvert.SerializeObject(e.SourceRef) &&
                    JsonConvert.SerializeObject(s.VersionRef) == JsonConvert.SerializeObject(e.VersionRef))))
                throw new ArgumentException("collection_query_sources_invalid");
            var sortedLegal = legal.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var sortedFree = free.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var proofs = evidence.OrderBy(x => x.Identity, StringComparer.Ordinal).ToArray();
            var fields = new List<string> { Version, basis, "legal" }; fields.AddRange(sortedLegal);
            fields.Add("free"); fields.AddRange(sortedFree);
            fields.Add("sources");
            foreach (var s in frozen) { fields.Add(JsonConvert.SerializeObject(s.SourceRef)); fields.Add(JsonConvert.SerializeObject(s.VersionRef)); }
            foreach (var e in proofs)
            {
                fields.Add(e.Identity);
                fields.Add(JsonConvert.SerializeObject(e.SourceRef));
                fields.Add(JsonConvert.SerializeObject(e.VersionRef));
            }
            return new CollectionFreeLocationResult(sortedLegal, sortedFree, proofs, basis, RouteResourceSelectionService.Hash(fields), frozen);
        }

        public static bool MatchesCurrentFacts(GameState state, CollectionQueryContext query, IMapQueryService map,
            CollectionFreeLocationResult saved)
            => saved != null && saved.LegalLocationIds.SequenceEqual(query.LegalLocationIds.OrderBy(x => x, StringComparer.Ordinal)) &&
                saved.BasisDigest == FactsDigest(state, query, map, saved.QuerySources.Select(s => s.SourceRef).ToArray());

        public static string FactsDigest(GameState state, CollectionQueryContext query, IMapQueryService map, IReadOnlyList<AbilitySourceRef> frozenSources = null)
        {
            ValidateContext(state, query, map);
            var fields = new List<string> { QueryKind, Version, state.GameId, state.ContentPackHash, state.MapId,
                query.PlayerId.ToString(CultureInfo.InvariantCulture), query.Purpose, query.SourceEffectId,
                query.SelectionSessionId, query.CityStartLocationId, state.Round.ToString(CultureInfo.InvariantCulture) };
            fields.Add(JsonConvert.SerializeObject(map.Map));
            fields.Add(state.Phase.ToString()); fields.Add(state.ActionRound.ToString(CultureInfo.InvariantCulture));
            fields.AddRange(query.LegalLocationIds.OrderBy(x => x, StringComparer.Ordinal));
            foreach (var id in state.Map.OpenLocationIds.OrderBy(x => x, StringComparer.Ordinal)) fields.Add("open:" + id);
            foreach (var player in state.Players.OrderBy(x => x.PlayerId))
            { fields.Add("city"); fields.Add(player.PlayerId.ToString(CultureInfo.InvariantCulture)); fields.Add(player.CityLocationId); }
            foreach (var t in state.Map.ResourceTokens.ToArray().OrderBy(x => x.LocationId, StringComparer.Ordinal))
            { fields.Add("token"); fields.Add(t.LocationId); fields.Add(t.ResourceType.ToString()); fields.Add(t.Amount.ToString(CultureInfo.InvariantCulture)); }
            foreach (var i in state.Map.Influences.ToArray().OrderBy(x => x.InfluenceId, StringComparer.Ordinal))
            {
                fields.Add("influence"); fields.Add(i.InfluenceId); fields.Add(i.SlotId); fields.Add(i.LocationId); fields.Add(i.RouteId);
                fields.Add(JsonConvert.SerializeObject(i.OwnerSubject));
                var item = state.ItemZones?.Items.Find(x => x.ItemId == i.InfluenceId);
                fields.Add(item?.TypeId ?? ""); fields.Add(item?.ZoneId ?? "");
                fields.Add(JsonConvert.SerializeObject(item?.OwnerSubject));
                fields.Add(JsonConvert.SerializeObject(item?.ControllerSubject)); fields.Add(item?.OriginalSupplyZoneId ?? "");
            }
            foreach (var source in (frozenSources ?? Sources(state, query.PlayerId).ToArray()).OrderBy(x => x.SourceKey, StringComparer.Ordinal))
                fields.Add(JsonConvert.SerializeObject(source));
            // 来源授权由Host封存；客户端无Runtime也能以公开真实物品事实核对，不伪造选择收据。
            var definitions = new HashSet<string>((state.ContentDefinitions?.AbilitySources ?? new List<AbilitySourceDeclaration>())
                .Where(d => d.Kind == "ruleQuery" && d.QueryKinds.Contains(QueryKind)).Select(d => d.DefinitionId), StringComparer.Ordinal);
            foreach (var item in state.ItemZones.Items.Where(i => definitions.Contains(i.DefinitionId))
                .OrderBy(i => i.ItemId, StringComparer.Ordinal))
            {
                fields.Add("source-item"); fields.Add(item.ItemId); fields.Add(item.TypeId); fields.Add(item.ZoneId);
                fields.Add(JsonConvert.SerializeObject(item.OwnerSubject)); fields.Add(JsonConvert.SerializeObject(item.ControllerSubject));
                fields.Add(Text(item.LocalState,"activeFaceId")); fields.Add(Text(item.LocalState,"activationAuthorizationRef"));
                fields.Add(JsonConvert.SerializeObject(Value(item.LocalState,"grantingSourceRefs")));
            }
            foreach (var road in state.Map.RoadRouteIds.ToArray().OrderBy(x => x, StringComparer.Ordinal)) fields.Add("road:" + road);
            return RouteResourceSelectionService.Hash(fields);
        }

        private static void ValidateContext(GameState state, CollectionQueryContext query, IMapQueryService map)
        {
            if (state == null || query == null || state.FindPlayer(query.PlayerId) == null ||
                YC.Domain.Items.ItemZoneLegacyProjection.IsActive(state) && state.ContentDefinitions == null ||
                query.Purpose != "normal" && query.Purpose != "extra" || map.Map.MapId != state.MapId ||
                query.ContentVersion != state.ContentPackHash || state.FindPlayer(query.PlayerId).CityLocationId != query.CityStartLocationId ||
                query.LegalLocationIds.Any(id => !map.Map.Locations.Any(l => l.LocationId == id)) ||
                query.LegalLocationIds.Distinct(StringComparer.Ordinal).Count() != query.LegalLocationIds.Count)
                throw new InvalidOperationException("collection_query_context_invalid");
        }
    }
}
