using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Facilities
{
    public sealed class FacilityBuildQueryService
    {
        public const string Version = "1.0.0", BuiltKind = "built_facilities.v1",
            SlotKind = "facility.build.slot.v1", SourceKind = "facility.build.sources.v1";
        private readonly EffectRegistry registry;
        public FacilityBuildQueryService(EffectRegistry registry = null) { this.registry = registry; }

        private AbilityQueryContext Context(GameState state, int player, string kind, string request, NormalizedValue facts) =>
            new AbilityQueryContext { QueryKind = kind, SubjectPlayerId = player, MapId = state.MapId,
                ContentVersion = state.ContentPackHash, Facts = facts, FactsDigest = Hash(facts.ToDeterministicString()),
                QueryId = StableIdFactory.Create("facility-query", state.GameId, kind, player.ToString(CultureInfo.InvariantCulture),
                    request ?? "", (state.EffectRuntime?.StateRevision ?? 0).ToString(CultureInfo.InvariantCulture), Hash(facts.ToDeterministicString())) };
        private IReadOnlyList<AbilityRuleQueryResult> Rules(GameState state, AbilityQueryContext context)
        {
            if (registry == null)
            {
                if (FacilityLayerQuery.ResolveCity(state, context.SubjectPlayerId).Any(c => c.CoveredItemIds.Count > 0))
                    throw new InvalidOperationException("facility_query_registry_required");
                return Array.Empty<AbilityRuleQueryResult>(); // 未扩展的旧单层入口，不代替活动内容provider。
            }
            // 先查真实有效来源；存在来源却未接合同必须由QueryRules明确报错。
            var sources = registry.QuerySources(state, context).Where(s => s.AuthorizedPlayerId == context.SubjectPlayerId).ToList();
            if (sources.Count == 0)
            {
                if (context.QueryKind == BuiltKind && FacilityLayerQuery.ResolveCity(state, context.SubjectPlayerId).Any(c => c.CoveredItemIds.Count > 0))
                    throw new InvalidOperationException("facility_built_facts_source_missing");
                return Array.Empty<AbilityRuleQueryResult>();
            }
            var results = new List<AbilityRuleQueryResult>();
            foreach (var source in sources)
            {
                var scoped = GameStateCloneService.DeepClone(context); scoped.SourceRef = source;
                results.AddRange(registry.QueryRules(state, scoped));
            }
            return results.AsReadOnly();
        }
        public FacilityBuiltFacts ResolveBuiltFacts(GameState state, int player, string request = "")
        {
            var tops = FacilityLayerQuery.TopCityItems(state, player).ToList();
            var facts = Object("player", NormalizedValue.CreateInteger(player), "baseItemIds", Strings(tops),
                "allCityItemIds", Strings(FacilityLayerQuery.AllCityItems(state, player)),
                "stateRevision", NormalizedValue.CreateInteger(state.EffectRuntime?.StateRevision ?? 0));
            var context = Context(state, player, BuiltKind, request, facts);
            var result = new FacilityBuiltFacts { PlayerId = player, StateRevision = state.EffectRuntime?.StateRevision ?? 0,
                QueryId = context.QueryId, FactsDigest = context.FactsDigest, ContentHash = state.ContentPackHash, ItemIds = tops };
            foreach (var rule in Rules(state, context))
            {
                string error = ValidateBuiltResult(state, player, rule.Items);
                if (error.Length != 0) throw new InvalidOperationException(error);
                result.SourceResults.Add(GameStateCloneService.DeepClone(rule));
                foreach (var item in rule.Items.Items.Select(v => v.StringValue))
                    if (!result.ItemIds.Contains(item)) result.ItemIds.Add(item);
            }
            return result;
        }
        public static string ValidateBuiltResult(GameState state, int player, NormalizedValue items)
        {
            if (items?.Kind != NormalizedValueKind.Array || items.Items.Any(v => v.Kind != NormalizedValueKind.String) ||
                items.Items.Select(v => v.StringValue).Distinct(StringComparer.Ordinal).Count() != items.Items.Count)
                return "built_facilities_result_invalid";
            return items.Items.Any(v => !FacilityLayerQuery.IsInCity(state, v.StringValue, player)) ? "built_facilities_outside_city" : "";
        }
        public IReadOnlyList<FacilityBuildSlotCandidate> ResolveSlots(GameState state, int player, string newItemId, string request = "")
        {
            var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == newItemId);
            if (!FacilityLayerQuery.IsFacility(item)) throw new InvalidOperationException("facility_build_instance_required");
            var cells = FacilityLayerQuery.ResolveCity(state, player);
            var facts = Object("player", NormalizedValue.CreateInteger(player), "newItemId", NormalizedValue.CreateString(item.ItemId),
                "definitionId", NormalizedValue.CreateString(item.DefinitionId),
                "newScore", NormalizedValue.CreateInteger(FacilityCardDatabase.Get(state, item.DefinitionId).Score),
                "cells", NormalizedValue.CreateArray(cells.Select(c => Object("slotIndex", NormalizedValue.CreateInteger(c.SlotIndex),
                    "cellZoneId", NormalizedValue.CreateString(c.CellZoneId), "topItemId", NormalizedValue.CreateString(c.TopItemId),
                    "topScore", NormalizedValue.CreateInteger(c.TopItemId.Length == 0 ? 0 : FacilityCardDatabase.Get(state, state.ItemZones.Items.Single(i => i.ItemId == c.TopItemId).DefinitionId).Score),
                    "orderDigest", NormalizedValue.CreateString(c.OrderDigest))).ToList()));
            var result = cells.Where(c => c.TopItemId.Length == 0).Select(c => Candidate(c, newItemId, state)).ToList();
            foreach (var rule in Rules(state, Context(state, player, SlotKind, request, facts)))
            foreach (var value in rule.Items.Items)
            {
                int slot = Number(value, "slotIndex", -1);
                var cell = cells.SingleOrDefault(c => c.SlotIndex == slot);
                if (cell == null || cell.TopItemId.Length == 0 || Text(value, "expectedTopItemId") != cell.TopItemId ||
                    Text(value, "orderDigest") != cell.OrderDigest || cell.CoveredZoneId.Length == 0)
                    throw new InvalidOperationException("facility_overlay_candidate_invalid");
                var precondition = Value(value, "removalPrecondition");
                if (!CanFulfillRemoval(state, player, precondition)) continue;
                var candidate = Candidate(cell, newItemId, state);
                candidate.IsOverlay = true; candidate.OverlayAuthorization = GameStateCloneService.DeepClone(rule.SourceRef);
                candidate.RemovalPrecondition = precondition.Clone();
                candidate.RequiredResponseAbilityId = Text(value, "requiredResponseAbilityId");
                if (candidate.RequiredResponseAbilityId.Length == 0) throw new InvalidOperationException("facility_required_response_declaration_missing");
                var previous = result.SingleOrDefault(c => c.SlotIndex == slot);
                if (previous != null)
                {
                    if (previous.OverlayAuthorization?.SourceKey != candidate.OverlayAuthorization.SourceKey || previous.RequiredResponseAbilityId != candidate.RequiredResponseAbilityId ||
                        previous.RemovalPrecondition.ToDeterministicString() != candidate.RemovalPrecondition.ToDeterministicString())
                        throw new InvalidOperationException("facility_overlay_contract_conflict");
                    continue;
                }
                result.Add(candidate);
            }
            return result.OrderBy(c => c.SlotIndex).ToList().AsReadOnly();
        }
        private static FacilityBuildSlotCandidate Candidate(FacilityCellLayerSnapshot cell, string newItemId, GameState state) =>
            new FacilityBuildSlotCandidate { PlayerId = cell.PlayerId, SlotIndex = cell.SlotIndex, CellZoneId = cell.CellZoneId,
                NewItemId = newItemId, ExpectedTopItemId = cell.TopItemId, OrderDigest = cell.OrderDigest,
                StateRevision = state.EffectRuntime?.StateRevision ?? 0 };
        public FacilityBuildSlotCandidate RevalidateSlot(GameState state, FacilityBuildSlotCandidate observed, string request)
        {
            if (observed == null) throw new InvalidOperationException("facility_slot_observation_required");
            var current = ResolveSlots(state, observed.PlayerId, observed.NewItemId, request).SingleOrDefault(c => c.SlotIndex == observed.SlotIndex);
            if (current == null || current.ExpectedTopItemId != observed.ExpectedTopItemId || current.OrderDigest != observed.OrderDigest ||
                current.IsOverlay != observed.IsOverlay || current.OverlayAuthorization?.SourceKey != observed.OverlayAuthorization?.SourceKey ||
                current.RequiredResponseAbilityId != observed.RequiredResponseAbilityId ||
                current.RemovalPrecondition.ToDeterministicString() != observed.RemovalPrecondition.ToDeterministicString())
                throw new InvalidOperationException("facility_slot_candidate_changed");
            return current;
        }
        public static bool CanFulfillRemoval(GameState state, int player, NormalizedValue condition)
        {
            if (condition?.Kind != NormalizedValueKind.Object || condition.Properties.Any(p =>
                    !new[] { "operation", "ownerFilter", "requiredCount" }.Contains(p.Name)) ||
                Text(condition, "operation") != "remove" || Number(condition, "requiredCount", 0) <= 0 ||
                Value(condition, "requiredCount")?.Kind != NormalizedValueKind.Integer || Text(condition, "ownerFilter").Length == 0)
                throw new InvalidOperationException("facility_removal_precondition_invalid");
            return new InfluenceSubjectQuery().GetCandidateItemIds(state, player, Text(condition, "ownerFilter")).Count >= Number(condition, "requiredCount");
        }
        public IReadOnlyList<FacilityBuildSourceAuthorization> ResolveAuthorizedSources(GameState state, int player, string sourceKind, string request, string sourceKey = "")
        {
            var legal = state.ItemZones.Items.Where(i => FacilityLayerQuery.IsFacility(i) && LegalExtraSource(state, i, player)).Select(i => i.ItemId).ToList();
            var context = Context(state, player, SourceKind, request, Object("player", NormalizedValue.CreateInteger(player),
                "requestedSource", NormalizedValue.CreateString(sourceKind), "candidatePoolIds", Strings(legal)));
            var result = new List<FacilityBuildSourceAuthorization>();
            foreach (var rule in Rules(state, context))
            {
                if (sourceKey.Length > 0 && rule.SourceRef.SourceKey != sourceKey) continue;
                if (rule.Items.Kind != NormalizedValueKind.Array || rule.Items.Items.Any(v => v.Kind != NormalizedValueKind.String || !legal.Contains(v.StringValue)))
                    throw new InvalidOperationException("facility_source_authorization_invalid");
                foreach (var id in rule.Items.Items.Select(v => v.StringValue).Distinct(StringComparer.Ordinal))
                {
                    var item = state.ItemZones.Items.Single(i => i.ItemId == id);
                    var zone = state.ItemZones.Zones.Single(z => z.ZoneId == item.ZoneId);
                    if (zone.ParentKind == ZoneParentKind.Item && zone.ParentId != rule.SourceRef.ItemId)
                        throw new InvalidOperationException("facility_source_carrier_mismatch");
                    result.Add(new FacilityBuildSourceAuthorization { PlayerId = player, SourceKind = sourceKind,
                        ItemId = id, SourceZoneId = zone.ZoneId, SourceKey = rule.SourceRef.SourceKey,
                        SourceRef = GameStateCloneService.DeepClone(rule.SourceRef), ContentHash = state.ContentPackHash,
                        DefinitionVersion = rule.VersionRef.DefinitionVersion, HandlerHash = rule.VersionRef.HandlerHash });
                }
            }
            return result.AsReadOnly();
        }
        private static bool LegalExtraSource(GameState state, ItemInstanceState item, int player)
        {
            var zone = state.ItemZones.Zones.Single(z => z.ZoneId == item.ZoneId);
            if (zone.TemplateId == "core.zone.player_supply") return item.OwnerSubject.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId == player && zone.OwnerPlayerId == player;
            if (zone.TemplateId != "core.zone.card_facility_storage" || zone.ParentKind != ZoneParentKind.Item) return false;
            var carrier = state.ItemZones.Items.Single(i => i.ItemId == zone.ParentId);
            return carrier.ControllerSubject.Kind == ItemSubjectKind.Player && carrier.ControllerSubject.PlayerId == player;
        }
        public FacilityBuildSourceAuthorization AuthorizeSource(GameState state, int player, string itemId, string sourceKind,
            string request, string sourceKey = "", bool allowLegacyStored = true)
        {
            if (sourceKind == "authorized" && sourceKey.Length == 0) throw new InvalidOperationException("facility_source_authorization_required");
            var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == itemId);
            if (!FacilityLayerQuery.IsFacility(item)) throw new InvalidOperationException("invalid_build_source");
            var zone = state.ItemZones.Zones.Single(z => z.ZoneId == item.ZoneId);
            bool standard = sourceKind == "supply" && FacilityLayerQuery.IsSupplyTop(state, itemId) ||
                sourceKind == "deck" && zone.LocalZoneId == "facility_deck" ||
                sourceKind == "reserve" && zone.TemplateId == "core.zone.box_reserve";
            bool stored = sourceKind == "supply" && allowLegacyStored && FacilityBuildCandidateQuery.IsActiveStoredFacility(state, zone, player);
            if (standard || stored) return new FacilityBuildSourceAuthorization { PlayerId = player, ItemId = itemId, SourceZoneId = item.ZoneId, SourceKind = sourceKind, ContentHash = state.ContentPackHash };
            var authorized = ResolveAuthorizedSources(state, player, sourceKind, request, sourceKey).SingleOrDefault(a => a.ItemId == itemId);
            if (authorized == null) throw new InvalidOperationException("invalid_build_source");
            return authorized;
        }
        public bool IsBuildable(GameState state, int player, string itemId, string sourceKind, string paymentMode, string request = "", string sourceKey = "")
        {
            AuthorizeSource(state, player, itemId, sourceKind, request, sourceKey);
            var item = state.ItemZones.Items.Single(i => i.ItemId == itemId); var definition = FacilityCardDatabase.Get(state, item.DefinitionId);
            var built = ResolveBuiltFacts(state, player, request);
            if (FacilityCardDatabase.PlayerHasBuiltUniqueFacilityForInstances(state, state.FindPlayer(player), definition, built.ItemIds) ||
                ResolveSlots(state, player, itemId, request).Count == 0) return false;
            if (paymentMode == "free" || sourceKind == "reserve") return true;
            var cost = new FacilityBuildCostService().GetEffectiveResourceCostForInstances(state, state.FindPlayer(player), definition, built.ItemIds);
            return BuildFacilityService.ResolvePaymentMode(state, state.FindPlayer(player), definition, cost, paymentMode).Length > 0;
        }
        public IReadOnlyList<FacilityBlueprintPair> ResolveBlueprintPairs(GameState state, int player, IEnumerable<string> authorizedBlueprintIds)
        {
            var blueprints = authorizedBlueprintIds.Distinct(StringComparer.Ordinal).Select(id => state.ItemZones.Items.Single(i => i.ItemId == id)).ToList();
            if (blueprints.Any(i => !FacilityLayerQuery.IsFacility(i) || !LegalExtraSource(state, i, player) ||
                state.ItemZones.Zones.Single(z => z.ZoneId == i.ZoneId).TemplateId != "core.zone.card_facility_storage"))
                throw new InvalidOperationException("facility_blueprint_source_invalid");
            var result = new List<FacilityBlueprintPair>();
            foreach (var slot in FacilityLayerQuery.Supply(state).Where(s => s.TopItemId.Length > 0))
            {
                var supply = state.ItemZones.Items.Single(i => i.ItemId == slot.TopItemId);
                string color = FacilityCardDatabase.Get(state, supply.DefinitionId).Color;
                if (color == "rainbow") continue;
                foreach (var blueprint in blueprints.Where(i => FacilityCardDatabase.Get(state, i.DefinitionId).Color == color).OrderBy(i => i.ItemId, StringComparer.Ordinal))
                    result.Add(new FacilityBlueprintPair { SupplyItemId = supply.ItemId, SupplyZoneId = supply.ZoneId, BlueprintItemId = blueprint.ItemId,
                        BlueprintZoneId = blueprint.ZoneId, Color = color, OrderDigest = slot.OrderDigest });
            }
            return result.AsReadOnly();
        }
        public void RevalidatePair(GameState state, int player, FacilityBlueprintPair pair, IEnumerable<string> currentlyAuthorizedIds)
        {
            if (!ResolveBlueprintPairs(state, player, currentlyAuthorizedIds).Any(p => p.SupplyItemId == pair.SupplyItemId && p.BlueprintItemId == pair.BlueprintItemId &&
                p.SupplyZoneId == pair.SupplyZoneId && p.BlueprintZoneId == pair.BlueprintZoneId && p.OrderDigest == pair.OrderDigest && p.Color == pair.Color))
                throw new InvalidOperationException("facility_blueprint_pair_changed");
        }
        internal static string Hash(string text)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
    }
}

