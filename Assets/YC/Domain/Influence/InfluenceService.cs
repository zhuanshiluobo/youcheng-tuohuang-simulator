using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Effects;

namespace YC.Domain.Influence
{
    public sealed class InfluenceService
    {
        private readonly GameState boundState;
        private readonly IMapQueryService mapQuery;
        private readonly IInfluenceRoadCoverageQuery roadCoverageQuery;
        private readonly InfluencePlacementRule placementRule;
        private readonly InfluenceMoveRule moveRule;
        private readonly InfluenceRemovalService removalService;
        private readonly InfluenceQueryService queryService;
        // 后续锁定规则在组合根接入；内核只认位置禁令，不认卡名。
        private Func<GameState, string, bool> additionalPlacementAllowed = (state, slot) => true;
        public Func<GameState, string, bool> PlacementAllowed
        {
            get => (state, slot) => InfluenceLockRules.PlacementAllowed(state, slot) && additionalPlacementAllowed(state, slot);
            set => additionalPlacementAllowed = value ?? ((state, slot) => true);
        }

        public InfluenceOperationResult ValidateItemPlacement(GameState state, ItemSubjectRef owner, string slotId)
        {
            return ValidateItemPlacement(state, owner, slotId, null);
        }

        private InfluenceOperationResult ValidateItemPlacement(GameState state, ItemSubjectRef owner, string slotId,
            InfluencePlacementQuerySnapshot snapshot)
        {
            if (!PlacementAllowed(state, slotId)) return Failure(InfluenceFailureCode.InvalidSlot, "placement_prohibited", -1, slotId, false);
            if (owner.Kind == ItemSubjectKind.Company)
            {
                bool legal = new CompanyInfluenceRules(mapQuery, PlacementAllowed).GetPlacementSlotIds(state).Contains(slotId);
                return legal ? InfluenceOperationResult.Success(-1, slotId, false) : Failure(InfluenceFailureCode.InvalidSlot, "company_placement_rejected", -1, slotId, false);
            }
            if (owner.Kind != ItemSubjectKind.Player) return Failure(InfluenceFailureCode.InvalidPlayer, "unknown_subject", -1, slotId, false);
            var validation = placementRule.Validate(state, owner.PlayerId, slotId, false, snapshot);
            if (!validation.Succeeded) return validation;
            InfluenceSlotReference slot;
            string reason;
            if (!InfluenceSlotReference.TryParse(mapQuery, slotId, out slot, out reason)) return Failure(InfluenceFailureCode.InvalidSlot, reason, owner.PlayerId, slotId, false);
            if (slot.Kind == InfluenceSlotKind.Location &&
                !(snapshot == null ? state.Map.OpenLocationIds.Contains(slot.LocationId) : snapshot.IsLocationOpen(slot.LocationId)))
                return Failure(InfluenceFailureCode.InvalidSlot, "location_closed", owner.PlayerId, slotId, false);
            return validation;
        }

        public static string DefaultSupplyType(ItemSubjectRef owner) => owner.Kind == ItemSubjectKind.Company ? "infected.company_marker" : "core.player_marker";
        public IReadOnlyList<string> GetLegalPlacementSlotIds(GameState state, ItemSubjectRef owner, string itemTypeId)
        {
            var types = ItemTypeRegistry.CreateBuiltIn();
            if (!types.HasOperationProfile(itemTypeId, "core.influence_cube") || owner.Kind == ItemSubjectKind.Company && itemTypeId != "infected.company_marker" ||
                owner.Kind == ItemSubjectKind.Player && types.IsAssignableTo(itemTypeId, "infected.company_marker")) return Array.Empty<string>();
            var supply = state.ItemZones.Zones.SingleOrDefault(z => owner.Kind == ItemSubjectKind.Player ? z.TemplateId == "core.zone.player_supply" && z.OwnerPlayerId == owner.PlayerId : z.TemplateId == "core.zone.reward_pool" && z.LocalZoneId == "company_markers");
            if (supply == null || !state.ItemZones.Items.Any(i => i.ZoneId == supply.ZoneId && i.TypeId == itemTypeId && InfluenceSubjectQuery.GetSubjectKey(i.OwnerSubject) == InfluenceSubjectQuery.GetSubjectKey(owner))) return Array.Empty<string>();
            var snapshot = owner.Kind == ItemSubjectKind.Player ? placementRule.CreateQuerySnapshot(state, owner.PlayerId) : null;
            return state.ItemZones.Zones.Where(z => z.TemplateId == "core.zone.influence_slots").SelectMany(z => z.LegalSlotIds).Where(slot => ValidateItemPlacement(state, owner, slot, snapshot).Succeeded).OrderBy(slot => slot, StringComparer.Ordinal).ToList();
        }
        public InfluenceOperationResult Place(EffectExecutionContext context, ItemSubjectRef owner, string slotId, string itemTypeId = "")
        {
            var state = context.State;
            if (itemTypeId.Length == 0) itemTypeId = DefaultSupplyType(owner);
            var validation = ValidateItemPlacement(state, owner, slotId);
            if (!validation.Succeeded) return validation;
            var types = ItemTypeRegistry.CreateBuiltIn();
            if (!types.HasOperationProfile(itemTypeId, "core.influence_cube") || owner.Kind == ItemSubjectKind.Company && itemTypeId != "infected.company_marker" || owner.Kind == ItemSubjectKind.Player && types.IsAssignableTo(itemTypeId, "infected.company_marker"))
                return Failure(InfluenceFailureCode.InsufficientSupply, "typed_supply_unavailable", owner.PlayerId, slotId, false);
            var source = state.ItemZones.Zones.Find(zone => owner.Kind == ItemSubjectKind.Player
                ? zone.TemplateId == "core.zone.player_supply" && zone.OwnerPlayerId == owner.PlayerId
                : zone.TemplateId == "core.zone.reward_pool" && zone.LocalZoneId == "company_markers");
            var item = source == null ? null : state.ItemZones.Items.Where(value => value.TypeId == itemTypeId && value.ZoneId == source.ZoneId &&
                InfluenceSubjectQuery.GetSubjectKey(value.OwnerSubject) == InfluenceSubjectQuery.GetSubjectKey(owner)).OrderBy(value => value.ItemId, StringComparer.Ordinal).FirstOrDefault();
            if (item == null) return Failure(InfluenceFailureCode.InsufficientSupply, "insufficient_supply", owner.PlayerId, slotId, false);
            var before = GameStateCloneService.DeepClone(item);
            var destination = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.influence_slots" && zone.LegalSlotIds.Contains(slotId));
            new ItemTransferService().RelocateItems(context, Transfer(context, item, destination.ZoneId, slotId, "core.influence_cube"));
            return InfluenceOperationResult.Success(owner.PlayerId, slotId, true, item.ItemId).WithFact("place", before,
                new ItemPositionRef { ZoneId = destination.ZoneId, SlotId = slotId }, InfluenceEventTypeIds.InfluencePlaced);
        }

        public InfluenceOperationResult Move(EffectExecutionContext context, string itemId, string slotId)
        {
            var item = new ItemZoneQuery().FindItem(context.State, itemId);
            if (item == null || !new ItemZoneQuery().IsInfluence(context.State, itemId)) return Failure(InfluenceFailureCode.InfluenceNotFound, "influence_not_found", -1, slotId, false);
            var validation = ValidateItemPlacement(context.State, item.OwnerSubject, slotId);
            if (!validation.Succeeded) return validation;
            var before = GameStateCloneService.DeepClone(item);
            var destination = context.State.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.influence_slots" && zone.LegalSlotIds.Contains(slotId));
            new ItemTransferService().RelocateItems(context, Transfer(context, item, destination.ZoneId, slotId, "core.influence_cube"));
            return InfluenceOperationResult.Success(before.OwnerSubject.PlayerId, slotId, true, itemId, before.SlotId).WithFact("move", before,
                new ItemPositionRef { ZoneId = destination.ZoneId, SlotId = slotId }, InfluenceEventTypeIds.InfluenceMoved);
        }

        public IReadOnlyList<string> GetLegalMovementSlotIds(GameState state, string itemId)
        {
            var item = new ItemZoneQuery().FindItem(state, itemId);
            if (item == null || !new ItemZoneQuery().IsInfluence(state, itemId)) return Array.Empty<string>();
            var slots = state.ItemZones.Zones.Where(zone => zone.TemplateId == "core.zone.influence_slots").SelectMany(zone => zone.LegalSlotIds);
            return slots.Where(slot => ValidateItemPlacement(state, item.OwnerSubject, slot).Succeeded).OrderBy(slot => slot, StringComparer.Ordinal).ToList().AsReadOnly();
        }

        public InfluenceOperationResult Depart(EffectExecutionContext context, string itemId, string operationKind)
        {
            if (operationKind != "remove" && operationKind != "replace") throw new InvalidOperationException("invalid_departure_kind");
            var item = new ItemZoneQuery().FindItem(context.State, itemId);
            if (item == null || !new ItemZoneQuery().IsInfluence(context.State, itemId)) return Failure(InfluenceFailureCode.InfluenceNotFound, "influence_not_found", -1, itemId, false);
            var before = GameStateCloneService.DeepClone(item);
            var destination = new InfluenceDestinationResolver().GetStagingZoneId(context);
            new ItemTransferService().RelocateItems(context, Transfer(context, item, destination, "", "core.cube_storage"));
            InfluenceDestinationResolver.Set(context.Node.ContinuationData, "pendingItemId", NormalizedValue.CreateString(itemId));
            InfluenceDestinationResolver.Set(context.Node.ContinuationData, "pendingDepartureKind", NormalizedValue.CreateString(operationKind));
            InfluenceDestinationResolver.Set(context.Node.ContinuationData, "destinationResolved", NormalizedValue.CreateBoolean(false));
            return InfluenceOperationResult.Success(before.OwnerSubject.PlayerId, before.SlotId, true, itemId, before.SlotId).WithFact(operationKind, before,
                new ItemPositionRef { ZoneId = destination }, operationKind == "remove" ? InfluenceEventTypeIds.InfluenceRemoved : "");
        }

        internal static ItemTransferRequest Transfer(EffectExecutionContext context, ItemInstanceState item, string zoneId, string slotId, string profile) =>
            new ItemTransferRequest { ItemIds = new List<string> { item.ItemId }, ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId, SlotId = item.SlotId },
                Destination = new ZoneRef { ZoneId = zoneId }, InsertionKind = string.IsNullOrEmpty(slotId) ? "unordered" : "slot", DestinationSlotId = slotId,
                OperationProfileId = profile, CarrierPolicyId = "preserve", ExecutingPlayerId = context.Node.PlayerId, RequiredCount = 1 };

        public IReadOnlyList<string> GetRoadCandidateRouteIds(GameState state) => mapQuery.Map.Routes
            .Where(route => !state.Map.RoadRouteIds.Contains(route.RouteId)).Select(route => route.RouteId).OrderBy(id => id, StringComparer.Ordinal).ToList().AsReadOnly();

        public string PlaceRoad(EffectExecutionContext context, string routeId)
        {
            mapQuery.GetRoute(routeId);
            if (!GetRoadCandidateRouteIds(context.State).Contains(routeId)) throw new InvalidOperationException("road_already_present");
            var parent = context.State.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.map_route" && zone.LocalZoneId == routeId);
            var road = context.State.ItemZones.Zones.Single(zone => zone.ParentId == parent.ZoneId && zone.TemplateId == "core.zone.route_road");
            var covered = context.State.ItemZones.Zones.Single(zone => zone.ParentId == parent.ZoneId && zone.TemplateId == "core.zone.covered_cubes");
            var cubes = context.State.ItemZones.Items.Where(item => item.PhysicalKind == ItemPhysicalKind.Cube &&
                context.State.ItemZones.Zones.Exists(zone => zone.ZoneId == item.ZoneId && zone.ParentId == parent.ZoneId && zone.TemplateId == "core.zone.influence_slots")).Select(GameStateCloneService.DeepClone).ToList();
            foreach (var cube in cubes) new ItemTransferService().RelocateItems(context, Transfer(context, cube, covered.ZoneId, "", "core.cube_storage"));
            var mappings = context.State.ContentDefinitions?.Components?.Where(c => c.TypeId == "core.road_token").ToList() ?? new List<YC.Domain.Rules.ComponentContentDefinition>();
            if (mappings.Count > 1 || mappings.Any(c => c.QuantityPolicy != "infinite" || string.IsNullOrEmpty(c.DefinitionId))) throw new InvalidOperationException("road_component_mapping_invalid");
            string definitionId = mappings.Count == 0 ? "core.component.road" : mappings[0].DefinitionId;
            if (!new ItemZoneStateFactory().TryCreateGrantedItem(context.State, context.Node.EffectId, "road", definitionId, "core.road_token", road.ZoneId, new ItemSubjectRef(), out var itemId, out var reason)) throw new InvalidOperationException(reason);
            return itemId;
        }

        public InfluenceService(IMapQueryService mapQuery)
            : this(null, mapQuery, new RuntimeStateRoadCoverageQuery())
        {
        }

        public InfluenceService(GameState state, IMapQueryService mapQuery)
            : this(state, mapQuery, new RuntimeStateRoadCoverageQuery())
        {
        }

        public InfluenceService(
            GameState state,
            IMapQueryService mapQuery,
            IInfluenceRoadCoverageQuery roadCoverageQuery)
        {
            _ = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.mapQuery = mapQuery;
            boundState = state;
            this.roadCoverageQuery = roadCoverageQuery ?? new NoInfluenceRoadCoverageQuery();
            placementRule = new InfluencePlacementRule(mapQuery, roadCoverageQuery);
            moveRule = new InfluenceMoveRule(placementRule);
            removalService = new InfluenceRemovalService();
            queryService = new InfluenceQueryService(mapQuery);
        }

        public InfluenceService(
            GameState state,
            InfluencePlacementRule placementRule,
            InfluenceMoveRule moveRule,
            InfluenceRemovalService removalService,
            InfluenceQueryService queryService)
        {
            _ = placementRule ?? throw new ArgumentNullException(nameof(placementRule));
            _ = moveRule ?? throw new ArgumentNullException(nameof(moveRule));
            _ = removalService ?? throw new ArgumentNullException(nameof(removalService));
            _ = queryService ?? throw new ArgumentNullException(nameof(queryService));
            this.placementRule = placementRule;
            this.moveRule = moveRule;
            this.removalService = removalService;
            this.queryService = queryService;
            boundState = state;
            mapQuery = null;
            roadCoverageQuery = null;
        }

        public InfluenceService(
            InfluencePlacementRule placementRule,
            InfluenceMoveRule moveRule,
            InfluenceRemovalService removalService,
            InfluenceQueryService queryService)
            : this(null, placementRule, moveRule, removalService, queryService)
        {
        }

        public ValidationResult CanPlace(GameState state, int playerId, string slotId)
        {
            return ToValidationResult(CanPlaceCore(state, playerId, slotId));
        }

        /// <summary>
        /// 返回当前状态下可以用于一次“放置影响力”结算的基础候选集合。
        /// 候选在调用时按当前状态重新计算，调用方不能把它缓存成跨 Effect 的组合选择。
        /// </summary>
        public IReadOnlyList<string> GetLegalPlacementSlotIds(GameState state, int playerId)
        {
            return EnumerateLegalPlacementSlots(state, playerId).ToList().AsReadOnly();
        }

        public bool HasAnyLegalPlacementSlot(GameState state, int playerId)
        {
            return EnumerateLegalPlacementSlots(state, playerId).Any();
        }

        private IEnumerable<string> EnumerateLegalPlacementSlots(GameState state, int playerId)
        {
            ValidateState(state);
            if (mapQuery == null || mapQuery.Map == null) yield break;
            var snapshot = placementRule.CreateQuerySnapshot(state, playerId);
            var accepted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var slotId in EnumeratePlacementSlots())
            {
                if (string.IsNullOrEmpty(slotId) || accepted.Contains(slotId)) continue;
                if (!CanPlaceCore(state, playerId, slotId, snapshot).Succeeded) continue;
                accepted.Add(slotId);
                yield return slotId;
            }
        }

        private IEnumerable<string> EnumeratePlacementSlots()
        {
            if (mapQuery.Map.Locations != null)
                foreach (var location in mapQuery.Map.Locations)
                {
                    if (location == null || string.IsNullOrEmpty(location.LocationId)) continue;
                    for (var index = 0; index < location.InfluenceSlotCount; index++)
                        yield return GetLocationSlotId(location.LocationId, index);
                }
            if (mapQuery.Map.Routes != null)
                foreach (var route in mapQuery.Map.Routes)
                {
                    if (route == null || string.IsNullOrEmpty(route.RouteId)) continue;
                    for (var index = 0; index < route.InfluenceSlotCount; index++)
                        yield return GetRouteSlotId(route.RouteId, index);
                }
        }

        public InfluenceOperationResult Place(GameState state, int playerId, string slotId)
        {
            return PlaceCore(state, playerId, slotId, string.Empty, null, null);
        }

        public InfluenceOperationResult Place(
            GameState state,
            int playerId,
            string slotId,
            string influenceId,
            RuleSubjectReference ownerSubject,
            InfluenceSourceReference source)
        {
            return PlaceCore(state, playerId, slotId, influenceId, ownerSubject, source);
        }

        public ValidationResult CanMove(GameState state, int playerId, string sourceInfluenceId, string targetSlotId)
        {
            return ToValidationResult(CanMoveCore(state, playerId, sourceInfluenceId, targetSlotId));
        }

        public InfluenceOperationResult Move(GameState state, int playerId, string sourceInfluenceId, string targetSlotId)
        {
            return MoveCore(state, playerId, sourceInfluenceId, targetSlotId);
        }

        public InfluenceOperationResult MoveAtomically(
            GameState state,
            int playerId,
            IReadOnlyList<InfluenceMoveRequest> moves)
        {
            ValidateState(state);
            if (moves == null || moves.Count == 0)
            {
                return Failure(
                    InfluenceFailureCode.InvalidState,
                    "至少需要提供一次影响力移动。",
                    playerId,
                    string.Empty,
                    false);
            }

            var projectedState = CreateMoveProjection(state);
            var movedInfluences = new HashSet<InfluencePlacement>();
            var canonicalTargets = new List<string>(moves.Count);
            for (var i = 0; i < moves.Count; i++)
            {
                var move = moves[i];
                if (move == null)
                {
                    return Failure(
                        InfluenceFailureCode.InvalidState,
                        "影响力移动参数不能为空。",
                        playerId,
                        string.Empty,
                        false);
                }

                var projectedInfluence = FindInfluence(projectedState, move.SourceSlotId);
                if (projectedInfluence != null && movedInfluences.Contains(projectedInfluence))
                {
                    return Failure(
                        InfluenceFailureCode.InfluenceOwnerMismatch,
                        "一次行动中不能重复移动同一个影响力。",
                        playerId,
                        move.SourceSlotId,
                        false);
                }

                var validation = moveRule.Validate(
                    projectedState,
                    playerId,
                    move.SourceSlotId,
                    move.TargetSlotId);
                if (!validation.Succeeded)
                {
                    return validation;
                }

                movedInfluences.Add(projectedInfluence);
                canonicalTargets.Add(validation.SlotId);
                ApplySlotFromId(projectedInfluence, validation.SlotId);
            }

            // 所有批量校验成功后才补齐旧快照身份并提交移动。
            InfluenceIdentity.Ensure(state);
            for (var i = 0; i < moves.Count; i++)
            {
                var influence = FindInfluence(state, moves[i].SourceSlotId);
                ApplySlotFromId(influence, canonicalTargets[i]);
            }

            return InfluenceOperationResult.Success(
                playerId,
                canonicalTargets[canonicalTargets.Count - 1],
                true);
        }

        public ValidationResult CanRemoveThenMoveAtomically(
            GameState state,
            int playerId,
            string removeSlotId,
            InfluenceMoveRequest move)
        {
            return CanRemoveThenMoveAtomically(
                state,
                playerId,
                removeSlotId,
                new[] { move });
        }

        public ValidationResult CanRemoveThenMoveAtomically(
            GameState state,
            int playerId,
            string removeSlotId,
            IReadOnlyList<InfluenceMoveRequest> moves)
        {
            List<string> canonicalTargetSlotIds;
            return ToValidationResult(ValidateRemoveThenMove(
                state,
                playerId,
                removeSlotId,
                moves,
                out canonicalTargetSlotIds));
        }

        public InfluenceOperationResult RemoveThenMoveAtomically(
            GameState state,
            int playerId,
            string removeSlotId,
            InfluenceMoveRequest move)
        {
            return RemoveThenMoveAtomically(
                state,
                playerId,
                removeSlotId,
                new[] { move });
        }

        public InfluenceOperationResult RemoveThenMoveAtomically(
            GameState state,
            int playerId,
            string removeSlotId,
            IReadOnlyList<InfluenceMoveRequest> moves)
        {
            List<string> canonicalTargetSlotIds;
            var validation = ValidateRemoveThenMove(
                state,
                playerId,
                removeSlotId,
                moves,
                out canonicalTargetSlotIds);
            if (!validation.Succeeded)
            {
                return validation;
            }

            var removal = removalService.Remove(state, removeSlotId);
            if (!removal.Succeeded)
            {
                return removal;
            }

            for (var i = 0; i < moves.Count; i++)
            {
                var source = FindInfluence(state, moves[i].SourceSlotId);
                ApplySlotFromId(source, canonicalTargetSlotIds[i]);
            }

            return InfluenceOperationResult.Success(
                playerId,
                canonicalTargetSlotIds[canonicalTargetSlotIds.Count - 1],
                true);
        }

        public InfluenceOperationResult PlaceAtomically(
            GameState state,
            int playerId,
            IReadOnlyList<string> slotIds)
        {
            ValidateState(state);
            if (slotIds == null || slotIds.Count == 0)
            {
                return Failure(InfluenceFailureCode.InvalidState, "At least one influence placement is required.", playerId, string.Empty, false);
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return Failure(InfluenceFailureCode.InvalidPlayer, "Player must exist.", playerId, string.Empty, false);
            }

            if (player.InfluenceSupply < slotIds.Count)
            {
                return Failure(InfluenceFailureCode.InsufficientSupply, "Not enough influence markers in supply.", playerId, string.Empty, false);
            }

            var uniqueSlots = new HashSet<string>();
            for (var i = 0; i < slotIds.Count; i++)
            {
                if (string.IsNullOrEmpty(slotIds[i]) || !uniqueSlots.Add(slotIds[i]))
                {
                    return Failure(InfluenceFailureCode.InvalidSlot, "Influence placement slots must be non-empty and distinct.", playerId, slotIds[i], false);
                }

                var validation = placementRule.Validate(state, playerId, slotIds[i], true);
                if (!validation.Succeeded)
                {
                    return validation;
                }
            }

            for (var i = 0; i < slotIds.Count; i++)
            {
                var placement = PlaceCore(state, playerId, slotIds[i]);
                if (!placement.Succeeded)
                {
                    return placement;
                }
            }

            return InfluenceOperationResult.Success(playerId, slotIds[slotIds.Count - 1], true);
        }

        public ValidationResult CanMoveAtomically(
            GameState state,
            int playerId,
            IReadOnlyList<InfluenceMoveRequest> moves)
        {
            ValidateState(state);
            if (moves == null || moves.Count == 0)
            {
                return ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    "At least one influence move is required.");
            }

            var projectedState = CreateMoveProjection(state);
            var movedInfluences = new HashSet<InfluencePlacement>();
            for (var i = 0; i < moves.Count; i++)
            {
                var move = moves[i];
                if (move == null)
                {
                    return ValidationResult.Failure(
                        CommandErrorCode.InvalidTarget,
                        "Influence move cannot be null.");
                }

                var projectedInfluence = FindInfluence(projectedState, move.SourceSlotId);
                if (projectedInfluence != null && movedInfluences.Contains(projectedInfluence))
                {
                    return ValidationResult.Failure(
                        CommandErrorCode.InvalidSource,
                        "The same influence cannot move twice in one action.");
                }

                var validation = moveRule.Validate(
                    projectedState,
                    playerId,
                    move.SourceSlotId,
                    move.TargetSlotId);
                if (!validation.Succeeded)
                {
                    return ToValidationResult(validation);
                }

                movedInfluences.Add(projectedInfluence);
                ApplySlotFromId(projectedInfluence, validation.SlotId);
            }

            return ValidationResult.Success;
        }

        public InfluenceOperationResult Remove(GameState state, string influenceId)
        {
            return RemoveCore(state, influenceId);
        }

        public InfluenceOperationResult Replace(GameState state, string targetInfluenceId, int newOwnerPlayerId)
        {
            return ReplaceCore(state, targetInfluenceId, newOwnerPlayerId);
        }

        public int CountInRegion(GameState state, int playerId, string regionId, bool includeCity)
        {
            return CountInRegionCore(state, playerId, regionId, includeCity);
        }

        public InfluenceOperationResult CanPlace(int playerId, string slotId)
        {
            return CanPlaceCore(GetBoundState(), playerId, slotId);
        }

        public InfluenceOperationResult Place(int playerId, string slotId)
        {
            return PlaceCore(GetBoundState(), playerId, slotId);
        }

        public InfluenceOperationResult CanMove(string influenceId, string targetSlotId)
        {
            var state = GetBoundState();
            var placement = FindInfluence(state, influenceId);
            if (placement == null)
            {
                return Failure(InfluenceFailureCode.InfluenceNotFound, "Source influence must exist before it can move.", -1, influenceId, false);
            }

            return CanMoveCore(state, placement.PlayerId, influenceId, targetSlotId);
        }

        public InfluenceOperationResult CanMove(int playerId, string sourceInfluenceId, string targetSlotId)
        {
            return CanMoveCore(GetBoundState(), playerId, sourceInfluenceId, targetSlotId);
        }

        public InfluenceOperationResult Move(string influenceId, string targetSlotId)
        {
            var state = GetBoundState();
            var placement = FindInfluence(state, influenceId);
            if (placement == null)
            {
                return Failure(InfluenceFailureCode.InfluenceNotFound, "Source influence must exist before it can move.", -1, influenceId, false);
            }

            return MoveCore(state, placement.PlayerId, influenceId, targetSlotId);
        }

        public InfluenceOperationResult Move(int playerId, string sourceInfluenceId, string targetSlotId)
        {
            return MoveCore(GetBoundState(), playerId, sourceInfluenceId, targetSlotId);
        }

        public InfluenceOperationResult Remove(string influenceId)
        {
            return RemoveCore(GetBoundState(), influenceId);
        }

        public InfluenceOperationResult Replace(string targetInfluenceId, int newOwnerPlayerId)
        {
            return ReplaceCore(GetBoundState(), targetInfluenceId, newOwnerPlayerId);
        }

        public int CountInRegion(int playerId, string regionId, bool includeCity)
        {
            return CountInRegionCore(GetBoundState(), playerId, regionId, includeCity);
        }

        public bool HasInfluenceAt(GameState state, string slotId)
        {
            ValidateState(state);
            return queryService.HasInfluenceAt(state, slotId);
        }

        public bool HasInfluenceAt(string slotId)
        {
            return HasInfluenceAt(GetBoundState(), slotId);
        }

        public static string GetLocationSlotId(string locationId, int index)
        {
            return InfluenceSlotReference.ForLocation(locationId, index).SlotId;
        }

        public static string GetRouteSlotId(string routeId, int index)
        {
            return InfluenceSlotReference.ForRoute(routeId, index).SlotId;
        }

        public InfluencePlacement FindInfluence(GameState state, string influenceId)
        {
            ValidateState(state);
            if (string.IsNullOrEmpty(influenceId))
            {
                return null;
            }

            return queryService.FindInfluence(state, influenceId);
        }

        public InfluencePlacement FindInfluence(string influenceId)
        {
            return FindInfluence(GetBoundState(), influenceId);
        }

        // 调用方在本次同步刷新内提供局部列表；保留状态校验及原查询语义。
        public InfluencePlacement FindInfluence(GameState state, string influenceId, IReadOnlyList<InfluencePlacement> placements)
        {
            ValidateState(state);
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (string.IsNullOrEmpty(influenceId)) return null;
            return queryService.FindInfluence(state, influenceId, placements);
        }

        private InfluenceOperationResult CanPlaceCore(GameState state, int playerId, string slotId,
            InfluencePlacementQuerySnapshot snapshot = null)
        {
            ValidateState(state);
            if (ItemZoneLegacyProjection.IsActive(state))
            {
                var validation = ValidateItemPlacement(state, ItemSubjectRef.Player(playerId), slotId, snapshot);
                if (!validation.Succeeded) return validation;
            }
            return placementRule.Validate(state, playerId, slotId, true, snapshot);
        }

        private InfluenceOperationResult PlaceCore(GameState state, int playerId, string slotId)
        {
            return PlaceCore(state, playerId, slotId, string.Empty, null, null);
        }

        private InfluenceOperationResult PlaceCore(
            GameState state,
            int playerId,
            string slotId,
            string influenceId,
            RuleSubjectReference ownerSubject,
            InfluenceSourceReference source)
        {
            ValidateState(state);
            var validation = placementRule.Validate(state, playerId, slotId, true);
            if (!validation.Succeeded)
            {
                return validation;
            }

            var player = state.FindPlayer(playerId);
            var canonicalSlotId = validation.SlotId;
            var resolvedInfluenceId = string.IsNullOrEmpty(influenceId)
                ? string.Empty
                : influenceId;
            if (!string.IsNullOrEmpty(resolvedInfluenceId) && FindInfluence(state, resolvedInfluenceId) != null)
            {
                return Failure(
                    InfluenceFailureCode.InvalidState,
                    "影响力实例 ID 已存在。",
                    playerId,
                    canonicalSlotId,
                    false);
            }

            // 所有可失败校验完成后才修改旧快照身份和供给序号。
            InfluenceIdentity.Ensure(state);
            if (string.IsNullOrEmpty(resolvedInfluenceId))
            {
                resolvedInfluenceId = InfluenceIdentity.CreateNewId(
                    state,
                    playerId,
                    canonicalSlotId,
                    source == null ? "direct_place" : source.SourceId);
            }

            player.InfluenceSupply -= 1;
            state.Map.Influences.Add(CreatePlacementFromSlotId(
                playerId,
                canonicalSlotId,
                resolvedInfluenceId,
                ownerSubject,
                source));
            return InfluenceOperationResult.Success(playerId, canonicalSlotId, true, resolvedInfluenceId);
        }

        private InfluenceOperationResult ValidateRemoveThenMove(
            GameState state,
            int playerId,
            string removeSlotId,
            IReadOnlyList<InfluenceMoveRequest> moves,
            out List<string> canonicalTargetSlotIds)
        {
            ValidateState(state);
            canonicalTargetSlotIds = new List<string>();
            if (string.IsNullOrEmpty(removeSlotId) || moves == null || moves.Count == 0)
            {
                return Failure(
                    InfluenceFailureCode.InvalidState,
                    "移除和调度参数都不能为空。",
                    playerId,
                    removeSlotId,
                    false);
            }

            var projectedState = CreateMoveProjection(state);
            var removalTarget = FindInfluence(projectedState, removeSlotId);
            if (removalTarget == null)
            {
                return Failure(
                    InfluenceFailureCode.InfluenceNotFound,
                    "要移除的影响力不存在。",
                    playerId,
                    removeSlotId,
                    false);
            }

            projectedState.Map.Influences.Remove(removalTarget);
            var movedInfluences = new HashSet<InfluencePlacement>();
            for (var i = 0; i < moves.Count; i++)
            {
                var move = moves[i];
                if (move == null)
                {
                    return Failure(
                        InfluenceFailureCode.InvalidState,
                        "影响力调度参数不能为空。",
                        playerId,
                        string.Empty,
                        false);
                }

                var projectedInfluence = FindInfluence(projectedState, move.SourceSlotId);
                if (projectedInfluence != null && movedInfluences.Contains(projectedInfluence))
                {
                    return Failure(
                        InfluenceFailureCode.InfluenceOwnerMismatch,
                        "一次效果中不能重复移动同一个影响力。",
                        playerId,
                        move.SourceSlotId,
                        false);
                }

                var moveValidation = moveRule.Validate(
                    projectedState,
                    playerId,
                    move.SourceSlotId,
                    move.TargetSlotId);
                if (!moveValidation.Succeeded)
                {
                    return moveValidation;
                }

                movedInfluences.Add(projectedInfluence);
                canonicalTargetSlotIds.Add(moveValidation.SlotId);
                ApplySlotFromId(projectedInfluence, moveValidation.SlotId);
            }

            return InfluenceOperationResult.Success(
                playerId,
                canonicalTargetSlotIds[canonicalTargetSlotIds.Count - 1],
                false);
        }

        private InfluenceOperationResult CanMoveCore(GameState state, int playerId, string sourceInfluenceId, string targetSlotId)
        {
            ValidateState(state);
            return moveRule.Validate(state, playerId, sourceInfluenceId, targetSlotId);
        }

        private InfluenceOperationResult MoveCore(GameState state, int playerId, string sourceInfluenceId, string targetSlotId)
        {
            YC.Domain.Items.ItemZoneLegacyProjection.RejectLegacyWrite(state);
            ValidateState(state);
            var validation = moveRule.Validate(state, playerId, sourceInfluenceId, targetSlotId);
            if (!validation.Succeeded)
            {
                return validation;
            }

            InfluenceIdentity.Ensure(state);
            var placement = FindInfluence(state, sourceInfluenceId);
            var canonicalTargetSlotId = validation.SlotId;
            var fromSlotId = placement.SlotId;
            ApplySlotFromId(placement, canonicalTargetSlotId);
            return InfluenceOperationResult.Success(
                playerId,
                canonicalTargetSlotId,
                true,
                placement.InfluenceId,
                fromSlotId);
        }

        private InfluenceOperationResult RemoveCore(GameState state, string influenceId)
        {
            ValidateState(state);
            return removalService.Remove(state, influenceId);
        }

        private InfluenceOperationResult ReplaceCore(GameState state, string targetInfluenceId, int newOwnerPlayerId)
        {
            ValidateState(state);
            var target = FindInfluence(state, targetInfluenceId);
            if (target == null)
            {
                return Failure(InfluenceFailureCode.InfluenceNotFound, "Target influence must exist before it can be replaced.", newOwnerPlayerId, targetInfluenceId, false);
            }

            var newOwner = state.FindPlayer(newOwnerPlayerId);
            if (newOwner == null)
            {
                return Failure(InfluenceFailureCode.InvalidPlayer, "Replacement owner must be an existing player.", newOwnerPlayerId, targetInfluenceId, false);
            }

            var targetSlotId = target.SlotId;
            if (newOwner.InfluenceSupply <= 0)
            {
                return Failure(
                    InfluenceFailureCode.InsufficientSupply,
                    "Replacement owner must have an available marker in supply.",
                    newOwnerPlayerId,
                    targetSlotId,
                    false);
            }

            var targetIndex = state.Map.Influences.IndexOf(target);
            state.Map.Influences.RemoveAt(targetIndex);

            var placementValidation = placementRule.Validate(state, newOwnerPlayerId, targetSlotId, false);
            if (!placementValidation.Succeeded)
            {
                state.Map.Influences.Insert(targetIndex, target);
                return Failure(
                    placementValidation.FailureCode,
                    placementValidation.Reason,
                    newOwnerPlayerId,
                    targetSlotId,
                    false);
            }

            InfluenceIdentity.Ensure(state);

            var oldOwner = state.FindPlayer(target.PlayerId);
            if (oldOwner != null)
            {
                oldOwner.InfluenceSupply += 1;
            }

            newOwner.InfluenceSupply -= 1;
            var replacementId = InfluenceIdentity.CreateNewId(state, newOwnerPlayerId, targetSlotId, "legacy_replace");
            state.Map.Influences.Insert(
                targetIndex,
                CreatePlacementFromSlotId(
                    newOwnerPlayerId,
                    targetSlotId,
                    replacementId,
                    InfluenceIdentity.CreatePlayerOwner(newOwnerPlayerId),
                    InfluenceIdentity.CreatePlayerSupplySource(newOwnerPlayerId, "legacy_replace")));
            return InfluenceOperationResult.Success(newOwnerPlayerId, targetSlotId, true, replacementId);
        }

        private int CountInRegionCore(GameState state, int playerId, string regionId, bool includeCity)
        {
            ValidateState(state);
            return queryService.CountInRegion(state, playerId, regionId, includeCity);
        }

        private static InfluencePlacement CreatePlacementFromSlotId(
            int playerId,
            string slotId,
            string influenceId,
            RuleSubjectReference ownerSubject,
            InfluenceSourceReference source)
        {
            var placement = new InfluencePlacement
            {
                PlayerId = playerId,
                InfluenceId = influenceId ?? string.Empty,
                OwnerSubject = InfluenceIdentity.CloneSubject(ownerSubject ?? InfluenceIdentity.CreatePlayerOwner(playerId)),
                Source = InfluenceIdentity.CloneSource(source ?? InfluenceIdentity.CreatePlayerSupplySource(playerId, "direct_place"))
            };
            ApplySlotFromId(placement, slotId);
            return placement;
        }

        private static GameState CreateMoveProjection(GameState state)
        {
            return GameStateCloneService.DeepClone(state);
        }

        private static void ApplySlotFromId(InfluencePlacement placement, string slotId)
        {
            placement.SlotId = slotId;
            if (slotId.StartsWith(InfluenceSlotReference.LocationPrefix, StringComparison.Ordinal))
            {
                var rest = slotId.Substring(InfluenceSlotReference.LocationPrefix.Length);
                var lastColon = rest.LastIndexOf(':');
                placement.LocationId = lastColon >= 0 ? rest.Substring(0, lastColon) : rest;
                placement.RouteId = string.Empty;
            }
            else if (slotId.StartsWith(InfluenceSlotReference.RoutePrefix, StringComparison.Ordinal))
            {
                var rest = slotId.Substring(InfluenceSlotReference.RoutePrefix.Length);
                var lastColon = rest.LastIndexOf(':');
                placement.RouteId = lastColon >= 0 ? rest.Substring(0, lastColon) : rest;
                placement.LocationId = string.Empty;
            }
        }

        private GameState GetBoundState()
        {
            if (boundState == null)
            {
                throw new InvalidOperationException("This InfluenceService method requires a GameState-bound service. Use the overload that accepts GameState.");
            }

            return boundState;
        }

        private static void ValidateState(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.Map == null)
            {
                throw new ArgumentException("GameState must include map runtime state.", nameof(state));
            }
        }

        private static InfluenceOperationResult Failure(
            InfluenceFailureCode failureCode,
            string reason,
            int playerId,
            string slotId,
            bool stateChanged)
        {
            return InfluenceOperationResult.Failure(failureCode, reason, playerId, slotId, stateChanged);
        }

        private static ValidationResult ToValidationResult(InfluenceOperationResult result)
        {
            if (result.Succeeded)
            {
                return ValidationResult.Success;
            }

            return ValidationResult.Failure(ToCommandErrorCode(result.FailureCode), result.Reason);
        }

        private static CommandResult ToCommandResult(
            InfluenceOperationResult result,
            GameEventKind eventKind,
            string defaultMessage)
        {
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(ToValidationResult(result));
            }

            var message = string.IsNullOrEmpty(defaultMessage) ? "Influence operation completed." : defaultMessage;
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = eventKind,
                    PlayerId = result.PlayerId,
                    SubjectId = result.SlotId,
                    Message = message
                }
            }, message);
        }

        private static CommandErrorCode ToCommandErrorCode(InfluenceFailureCode failureCode)
        {
            switch (failureCode)
            {
                case InfluenceFailureCode.None:
                    return CommandErrorCode.None;
                case InfluenceFailureCode.InvalidPlayer:
                    return CommandErrorCode.InvalidPlayer;
                case InfluenceFailureCode.InvalidSlot:
                    return CommandErrorCode.InvalidTarget;
                case InfluenceFailureCode.OccupiedSlot:
                case InfluenceFailureCode.OpponentCityPresent:
                case InfluenceFailureCode.RouteCoveredByRoad:
                    return CommandErrorCode.OccupiedSlot;
                case InfluenceFailureCode.InsufficientSupply:
                    return CommandErrorCode.InsufficientInfluence;
                case InfluenceFailureCode.ResourceTokenRequired:
                    return CommandErrorCode.ClosedLocation;
                case InfluenceFailureCode.InfluenceNotFound:
                case InfluenceFailureCode.InfluenceOwnerMismatch:
                    return CommandErrorCode.InvalidSource;
                case InfluenceFailureCode.InvalidState:
                default:
                    return CommandErrorCode.InvalidTarget;
            }
        }
    }
}
