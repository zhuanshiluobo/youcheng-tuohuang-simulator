using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Infrastructure.Lua
{
    public static class FacilityLayerLuaCatalog
    {
        public const string Version = "1.1.1";
        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (registry.FacilityLayerContractsInstalled) return;
            registry.RegisterAbilityQueryContract(FacilityBuildQueryService.BuiltKind, (context, source, items) =>
            {
                var all = Value(context.Facts, "allCityItemIds")?.Items.Select(v => v.StringValue).ToList();
                return source.AuthorizedPlayerId != context.SubjectPlayerId || all == null || items?.Kind != NormalizedValueKind.Array ||
                    items.Items.Any(v => v.Kind != NormalizedValueKind.String || !all.Contains(v.StringValue)) ||
                    items.Items.Select(v => v.StringValue).Distinct(StringComparer.Ordinal).Count() != items.Items.Count ? "built_facilities_result_invalid" : "";
            });
            registry.RegisterAbilityQueryContract(FacilityBuildQueryService.SlotKind, (context, source, items) =>
            {
                if (source.AuthorizedPlayerId != context.SubjectPlayerId || items?.Kind != NormalizedValueKind.Array) return "facility_slot_result_invalid";
                var slots = new HashSet<int>();
                foreach (var item in items.Items)
                    if (item.Kind != NormalizedValueKind.Object || item.Properties.Count != 5 ||
                        item.Properties.Any(p => !new[] { "slotIndex", "expectedTopItemId", "orderDigest", "removalPrecondition", "requiredResponseAbilityId" }.Contains(p.Name)) ||
                        Value(item, "slotIndex")?.Kind != NormalizedValueKind.Integer || !slots.Add(Number(item, "slotIndex", -1)) ||
                        Number(item, "slotIndex", -1) < 0 || Number(item, "slotIndex", -1) >= BuildFacilityService.CityBoardSlotCount ||
                        Text(item, "expectedTopItemId").Length == 0 || Text(item, "orderDigest").Length == 0 || Text(item, "requiredResponseAbilityId").Length == 0 ||
                        Value(item, "removalPrecondition")?.Kind != NormalizedValueKind.Object) return "facility_slot_result_invalid";
                return "";
            });
            registry.RegisterAbilityQueryContract(FacilityBuildQueryService.SourceKind, (context, source, items) =>
            {
                var pool = Value(context.Facts, "candidatePoolIds")?.Items.Select(v => v.StringValue).ToList();
                return source.AuthorizedPlayerId != context.SubjectPlayerId || pool == null || items?.Kind != NormalizedValueKind.Array ||
                    items.Items.Any(v => v.Kind != NormalizedValueKind.String || !pool.Contains(v.StringValue)) ||
                    items.Items.Select(v => v.StringValue).Distinct(StringComparer.Ordinal).Count() != items.Items.Count ? "facility_source_result_invalid" : "";
            });
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("facility.overlay.source",FacilityBuildJournal.EventType,"","facility.overlay.source",c=>
            {
                var build=c.State.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==c.Event.SourceEffectId);
                var slot=build==null ? null : FacilityBuildJournal.SelectedSlot(build);
                if(slot?.IsOverlay!=true) return new List<EffectSpec>();
                var authorization=slot.OverlayAuthorization;
                var declaration=registry.AbilityDeclarations.SingleOrDefault(d=>d.AbilityId==slot.RequiredResponseAbilityId && d.DefinitionId==authorization.DefinitionId);
                if(declaration==null) throw new InvalidOperationException("facility_required_response_declaration_missing");
                // 旧合同由同一slot查询能力的正式直接响应装配；Journal仍核唯一必做叶、身份及足额。
                // 此目录只为新的setup响应创建SourceUse，不抢占原直接响应或替缺失响应放行。
                if(declaration.Kind=="ruleQuery" && declaration.AbilityId==authorization.AbilityId &&
                    declaration.QueryKinds.Contains(FacilityBuildQueryService.SlotKind)) return new List<EffectSpec>();
                if(declaration.Kind=="event" && declaration.ObservedEvents.Contains(FacilityBuildJournal.EventType)) return new List<EffectSpec>();
                if(declaration.Kind!="setup") throw new InvalidOperationException("facility_required_response_kind_invalid");
                var source=registry.QuerySources(c.State,build.PlayerId).SingleOrDefault(s=>s.AbilityId==declaration.AbilityId &&
                    s.ItemId==authorization.ItemId && s.DefinitionId==authorization.DefinitionId && s.ActiveFaceId==authorization.ActiveFaceId &&
                    s.AuthorizationRef==authorization.AuthorizationRef && s.EffectiveZoneId==authorization.EffectiveZoneId);
                if(source==null) throw new InvalidOperationException("facility_required_response_source_invalid");
                return new List<EffectSpec>{AbilitySourceEffectExecutor.Create(source,c.Event,declaration.HandlerRef,false,c.State.Round)};
            },EffectHandlerRole.Observer));
            registry.FacilityBuildCandidates.UseQueries(registry.FacilityBuildQueries);
            registry.CompleteFacilityLayerRegistration();
        }
        // 保留真实事件身份和原结果，只为设施落位/真实Build完成补充白名单事实。
        public static NormalizedValue EnrichEventPayload(GameState state, RuleEvent ruleEvent, NormalizedValue original)
        {
            var build = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == ruleEvent.SourceEffectId);
            if (build?.EffectTypeId != FacilityEntryEffectTypeIds.Build ||
                ruleEvent.EventType != FacilityBuildJournal.EventType && ruleEvent.EventType != "EffectCompleted") return original;
            var facts = FacilityBuildJournal.Facts(build);
            if (facts.Kind != NormalizedValueKind.Object) throw new InvalidOperationException("facility_build_facts_missing");
            var payload = original?.Kind == NormalizedValueKind.Object ? original.Clone() : Object("result", original?.Clone() ?? NormalizedValue.CreateNull());
            var existing = Value(payload, "buildFacts");
            if (existing != null)
            {
                if (existing.ToDeterministicString() != facts.ToDeterministicString()) throw new InvalidOperationException("facility_build_facts_payload_conflict");
                return payload;
            }
            payload.Properties.Add(new NormalizedValueEntry { Name = "buildFacts", Value = facts });
            payload.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            return payload;
        }
        // 纯查询处理器的State并非Lua可读对象；实际快照仅含白名单事实。
        public static LuaSnapshotValue CreateProjection(GameState state)
        {
            var players = new List<LuaSnapshotEntry>();
            foreach (var player in state.Players.OrderBy(p => p.PlayerId))
            {
                var used = FacilityLayerQuery.DeclaredItemIds(state, player.PlayerId);
                var cells = FacilityLayerQuery.ResolveCity(state, player.PlayerId);
                var cellValues = cells.Select(c => O(
                    E("slotIndex", N(c.SlotIndex)), E("cellZoneId", S(c.CellZoneId)),
                    E("topItemId", S(c.TopItemId)), E("coveredZoneId", S(c.CoveredZoneId)),
                    E("coveredItemIds", Strings(c.CoveredItemIds)), E("allItemIds", Strings(c.AllItemIds)),
                    E("orderDigest", S(c.OrderDigest)), E("orderRevision", N(c.OrderRevision))));
                var towers = FacilityLayerQuery.ResolveTowerAdjacency(state, player.PlayerId).Select(t => O(
                    E("towerItemId", S(t.TowerItemId)), E("slotIndex", N(t.SlotIndex)),
                    E("adjacentTopItemIds", Strings(t.AdjacentTopItemIds))));
                players.Add(E("p" + player.PlayerId.ToString(CultureInfo.InvariantCulture), O(
                    E("cells", A(cellValues)), E("allItemIds", Strings(cells.SelectMany(c => c.AllItemIds))),
                    E("factsDigest", S(FacilityLayerEvidence.Digest(state, player.PlayerId))),
                    E("topItemIds", Strings(cells.Where(c => c.TopItemId.Length > 0).Select(c => c.TopItemId))),
                    E("declaredItemIds", Strings(used.OrderBy(id => id, StringComparer.Ordinal))),
                    E("currentDeclaredTopSlots", A(FacilityLayerQuery.CurrentDeclaredTopSlots(state, player.PlayerId).Select(N))),
                    E("towers", A(towers)))));
            }
            var supply = FacilityLayerQuery.Supply(state).Select(s => O(
                E("slotIndex", N(s.SlotIndex)), E("slotZoneId", S(s.SlotZoneId)),
                E("topItemId", S(s.TopItemId)), E("itemIds", Strings(s.ItemIds)), E("orderDigest", S(s.OrderDigest)), E("orderRevision", N(s.OrderRevision))));
            return O(E("protocolVersion", N(1)), E("stateRevision", N(state.EffectRuntime?.StateRevision ?? 0)),
                E("players", O(players.ToArray())), E("supply", A(supply)));
        }
        public static LuaSnapshotValue GetCityLayers(LuaSnapshotValue projection, string player) => Player(projection, player).Get("cells");
        public static LuaSnapshotValue GetFactsDigest(LuaSnapshotValue projection, string player) => Player(projection, player).Get("factsDigest");
        public static LuaSnapshotValue GetTowerAdjacency(LuaSnapshotValue projection, string player) => Player(projection, player).Get("towers");
        public static LuaSnapshotValue GetSupplyLayers(LuaSnapshotValue projection) => projection.Get("supply");
        private static LuaSnapshotValue Player(LuaSnapshotValue projection, string player)
        {
            if (player == null || !player.StartsWith("p", StringComparison.Ordinal) || projection?.Get("protocolVersion").IntegerValue != 1)
                throw new InvalidOperationException("facility_lua_query_invalid");
            var result = projection.Get("players").Get(player);
            if (result == null || result.Kind != LuaSnapshotValueKind.Object) throw new InvalidOperationException("facility_lua_player_invalid");
            return result;
        }
        private static LuaSnapshotEntry E(string n, LuaSnapshotValue v) => new LuaSnapshotEntry(n, v);
        private static LuaSnapshotValue O(params LuaSnapshotEntry[] e) => LuaSnapshotValue.Object(e);
        private static LuaSnapshotValue A(IEnumerable<LuaSnapshotValue> v) => LuaSnapshotValue.Array(v.ToList());
        private static LuaSnapshotValue S(string v) => LuaSnapshotValue.String(v);
        private static LuaSnapshotValue N(int v) => LuaSnapshotValue.Integer(v);
        private static LuaSnapshotValue Strings(IEnumerable<string> ids) => A(ids.Select(S));
    }
}
