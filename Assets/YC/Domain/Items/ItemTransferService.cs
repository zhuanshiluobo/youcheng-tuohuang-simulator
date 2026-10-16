using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Domain.Items
{
    /// <summary>只在 Host 工作副本内提交。先整组校验，后发布唯一位置和有序成员。</summary>
    public sealed class ItemTransferService
    {
        private readonly ItemTypeRegistry types;
        private readonly ZoneTemplateRegistry templates;
        private readonly ItemZoneQuery query;
        public ItemTransferService(ItemTypeRegistry types = null, ZoneTemplateRegistry templates = null)
        {
            this.types = types ?? ItemTypeRegistry.CreateBuiltIn();
            this.templates = templates ?? ZoneTemplateRegistry.CreateBuiltIn(this.types);
            query = new ItemZoneQuery(this.types, this.templates);
        }
        public ItemTransferValidationResult Validate(GameState state, ItemTransferRequest request)
        {
            if (!query.TryValidate(state, out var reason)) throw new InvalidOperationException("item_state_invalid: " + reason);
            if (request == null || request.ItemIds == null || request.ExpectedSource == null || request.Destination == null || request.RequiredCount < 0)
                return Invalid("invalid_transfer_request");
            if (state.FindPlayer(request.ExecutingPlayerId) == null) return Invalid("invalid_player");
            if (!new[] { "core.cube_storage", "core.player_marker_storage", "core.influence_cube", "core.facility_card_transfer", "core.character_card_transfer", "core.reward_card_transfer", "core.mobile_city_move", "core.adjacent_location_token_move" }.Contains(request.OperationProfileId)) return Invalid("operation_profile_rejected");
            if (request.CarrierPolicyId != "preserve") return Invalid("carrier_policy_rejected");
            var source = query.FindZone(state, request.ExpectedSource.ZoneId);
            var destination = query.FindZone(state, request.Destination.ZoneId);
            if (source == null) return Invalid("source_zone_changed");
            if (destination == null) return Invalid("destination_rejected");
            if (request.ItemIds.Distinct(StringComparer.Ordinal).Count() != request.ItemIds.Count) return Invalid("duplicate_item_id");
            if (request.ItemIds.Count < request.RequiredCount) return Invalid("insufficient_items");
            var targetTemplate = templates.Find(destination.TemplateId);
            bool deck = targetTemplate.OrderingKind == ZoneOrderingKind.Deck;
            bool slotted = targetTemplate.OrderingKind == ZoneOrderingKind.FixedSlots || targetTemplate.OrderingKind == ZoneOrderingKind.Track;
            if (deck ? request.InsertionKind != "top" && request.InsertionKind != "bottom" : slotted ? request.InsertionKind != "slot" : request.InsertionKind != "unordered")
                return Invalid("insertion_rejected");
            if (slotted && (request.ItemIds.Count > 1 || !destination.LegalSlotIds.Contains(request.DestinationSlotId))) return Invalid("destination_rejected");
            if (!slotted && !string.IsNullOrEmpty(request.DestinationSlotId)) return Invalid("insertion_rejected");
            bool recipientDestination = destination.TemplateId == "core.zone.player_marker_supply" || destination.TemplateId == "core.zone.character_hand" && request.OperationProfileId == "core.character_card_transfer";
            bool departedOwner = IsPendingDepartureCapture(state, source, destination, request) || YC.Domain.Effects.ContentTransactionEffectExecutor.AllowsMarkerTransfer(state, request);
            if (!departedOwner && !CanAccess(state, source, request.ExecutingPlayerId, true) || !recipientDestination && !CanAccess(state, destination, request.ExecutingPlayerId, false))
                return Invalid("transfer_permission_denied");
            int destinationPlayer = AccessPlayer(state, destination);
            if (destinationPlayer > 0 && destinationPlayer != request.ExecutingPlayerId && !recipientDestination) return Invalid("transfer_permission_denied");
            if (destination.TemplateId == "core.zone.influence_slots" && request.OperationProfileId != "core.influence_cube") return Invalid("operation_profile_rejected");
            if (destination.TemplateId == "core.zone.score_track") return Invalid("operation_profile_rejected");
            foreach (string id in request.ItemIds)
            {
                var item = query.FindItem(state, id);
                if (item == null) return Invalid("unknown_item");
                if (item.ZoneId != source.ZoneId || (!string.IsNullOrEmpty(request.ExpectedSource.SlotId) && item.SlotId != request.ExpectedSource.SlotId)) return Invalid("source_zone_changed");
                if (!types.HasOperationProfile(item.TypeId, request.OperationProfileId)) return Invalid("operation_profile_rejected");
                if (request.OperationProfileId == "core.deck_shuffle" || request.OperationProfileId == "core.character_card_use") return Invalid("operation_profile_rejected");
                if (!query.Accepts(destination, item)) return Invalid("destination_rejected");
                if (destination.TemplateId == "core.zone.player_marker_supply" && (item.OwnerSubject.Kind != ItemSubjectKind.Player || item.OwnerSubject.PlayerId != destination.OwnerPlayerId)) return Invalid("destination_rejected");
                if (destination.Configuration?.EntryPolicyId == "core.owned_player_marker" && (!query.IsPlayerMarker(state, id) || item.OwnerSubject.PlayerId != request.ExecutingPlayerId)) return Invalid("transfer_permission_denied");
                if (destination.Configuration?.EntryPolicyId == "core.removed_owned_influence")
                {
                    var departure = state.EffectRuntime.EffectNodes.Find(node => node.EffectId == source.OwnerEffectId);
                    bool initial = source.TemplateId == "core.zone.player_marker_supply" && query.IsPlayerMarker(state, id) && item.OwnerSubject.PlayerId == request.ExecutingPlayerId;
                    bool pending = source.TemplateId == "core.zone.effect_staging" && item.PhysicalKind == ItemPhysicalKind.Cube &&
                        YC.Domain.Effects.ItemTransferEffectExecutor.Text(departure?.ContinuationData, "pendingItemId") == id &&
                        new[] { "remove", "replace" }.Contains(YC.Domain.Effects.ItemTransferEffectExecutor.Text(departure?.ContinuationData, "pendingDepartureKind"));
                    pending |= IsPendingDepartureCapture(state, source, destination, request);
                    if (!initial && !pending) return Invalid("transfer_permission_denied");
                }
                foreach (var attached in state.ItemZones.Zones.Where(zone => zone.ParentKind == ZoneParentKind.Item && zone.ParentId == id))
                    if (attached.Configuration?.CarrierExitPolicyId == "core.return_contents_before_exit" && state.ItemZones.Items.Any(content => content.ZoneId == attached.ZoneId) &&
                        !(request.OperationProfileId == "core.character_card_transfer" && (destination.TemplateId == "core.zone.character_covered" || destination.TemplateId == "core.zone.character_persistent") && destination.OwnerPlayerId == item.ControllerSubject.PlayerId)) return Invalid("carrier_contents_not_returned");
            }
            var outside = state.ItemZones.Items.Where(item => item.ZoneId == destination.ZoneId && !request.ItemIds.Contains(item.ItemId)).ToList();
            if (destination.TemplateId == "core.zone.player_awards" && outside.Count(i => types.IsAssignableTo(i.TypeId, "core.contract_card")) + request.ItemIds.Count(id => types.IsAssignableTo(query.FindItem(state, id).TypeId, "core.contract_card")) > 1) return Invalid("contract_limit_reached");
            int capacity = destination.Configuration?.CapacityPolicy == "bounded" ? destination.Configuration.Capacity.Value : targetTemplate.Capacity;
            if (capacity >= 0 && outside.Count + request.ItemIds.Count > capacity || slotted && outside.Any(item => item.SlotId == request.DestinationSlotId)) return Invalid("destination_rejected");
            // 工作预览捕获载体环等结构约束；不触碰原成员或 OrderRevision。
            var preview = GameStateCloneService.DeepClone(state);
            var moved = Apply(preview, request);
            if (moved.MovedCount < request.RequiredCount) return Invalid("insufficient_items");
            if (!query.TryValidate(preview, out reason)) return Invalid("destination_rejected");
            return new ItemTransferValidationResult { IsValid = true };
        }
        public ItemTransferResult AdoptDeferredReservationOnWorking(GameState work, DeferredResponseState response,
            string sourceStagingZoneId, string destinationStagingZoneId)
        {
            var runtime = work?.EffectRuntime;
            var root = runtime?.EffectNodes.Find(n => n.EffectId == response?.ResponseRootEffectId);
            var departure = runtime?.EffectNodes.Find(n => n.EffectId == response?.SourceDepartureEffectId);
            var source = work?.ItemZones.Zones.Find(z => z.ZoneId == sourceStagingZoneId);
            var destination = work?.ItemZones.Zones.Find(z => z.ZoneId == destinationStagingZoneId);
            var item = work?.ItemZones.Items.Find(i => i.ItemId == response?.ReservedItemId);
            if (response == null || response.Status != DeferredResponseStatus.Pending ||
                !runtime.DeferredResponses.Any(r => ReferenceEquals(r, response)) || root == null || departure == null ||
                !string.IsNullOrEmpty(root.ParentEffectId) || root.PlayerId != response.AnsweringPlayerId ||
                response.AnsweringPlayerId == response.MainWindowPlayerIdAtCreation || response.MainWindowPlayerIdAtCreation < 1 ||
                source?.TemplateId != "core.zone.effect_staging" || source.OwnerEffectId != departure.EffectId ||
                destination?.TemplateId != "core.zone.effect_staging" || destination.OwnerEffectId != root.EffectId ||
                response.ReservationZoneId != destination.ZoneId || item == null || item.ZoneId != source.ZoneId || item.PhysicalKind != ItemPhysicalKind.Cube ||
                root.Status == EffectNodeStatus.Completed || root.Status == EffectNodeStatus.Failed || root.Status == EffectNodeStatus.Faulted ||
                YC.Domain.Effects.ItemTransferEffectExecutor.Text(departure.ContinuationData, "pendingItemId") != item.ItemId ||
                !new[] { "remove", "replace" }.Contains(YC.Domain.Effects.ItemTransferEffectExecutor.Text(departure.ContinuationData, "pendingDepartureKind")) ||
                YC.Domain.Effects.ItemTransferEffectExecutor.Flag(departure.ContinuationData, "destinationResolved") ||
                !runtime.RuleEvents.Any(e => e.EventId == response.SourceEventId && e.SourceEffectId == departure.EffectId &&
                    new[] { "InfluenceRemoved", "InfluenceReplaced" }.Contains(e.EventType) && e.ResponseKind == RuleEventResponseKind.Effects))
                throw new InvalidOperationException("deferred_reservation_handoff_rejected");
            var request = new ItemTransferRequest { ItemIds = new List<string> { item.ItemId },
                ExpectedSource = new ItemPositionRef { ZoneId = source.ZoneId }, Destination = new ZoneRef { ZoneId = destination.ZoneId },
                ExecutingPlayerId = departure.PlayerId, InsertionKind = "unordered", OperationProfileId = "core.cube_storage", RequiredCount = 1 };
            // 这是已核验的跨玩家两暂存交接；普通Validate故意不许可跨owner暂存。
            // 在窄入口内预演结构约束，不扩大任何普通转移权限。
            if (!query.Accepts(destination, item) || work.ItemZones.Items.Any(i => i.ZoneId == destination.ZoneId))
                throw new InvalidOperationException("deferred_reservation_destination_rejected");
            var preview = GameStateCloneService.DeepClone(work);
            Apply(preview, request);
            if (!query.TryValidate(preview, out var reason)) throw new InvalidOperationException(reason);
            var result = Apply(work, request);
            root.PositionChanges.AddRange(GameStateCloneService.DeepClone(result.PositionChanges));
            return result;
        }

        public ItemTransferResult RelocateItems(EffectExecutionContext context, ItemTransferRequest request)
        {
            if (context == null || request == null || context.Node.PlayerId != request.ExecutingPlayerId) throw new InvalidOperationException("transfer_permission_denied");
            if (YC.Domain.Effects.ContentTransactionEffectExecutor.AllowsMarkerTransfer(context.State, request) &&
                (!new[] { YC.Domain.Effects.ContentTransactionEffectExecutor.Prefix + "store_marker", YC.Domain.Effects.ContentTransactionEffectExecutor.Prefix + "return_marker" }.Contains(context.Node.EffectTypeId) ||
                 YC.Domain.Effects.ItemTransferEffectExecutor.Text(context.Node.ContinuationData, "selectedMarkerItemId") != request.ItemIds[0]))
                throw new InvalidOperationException("transfer_permission_denied");
            foreach (string zoneId in new[] { request.ExpectedSource?.ZoneId, request.Destination?.ZoneId })
            {
                var zone = query.FindZone(context.State, zoneId);
                if (zone?.TemplateId != "core.zone.effect_staging") continue;
                var node = context.Node;
                while (node != null && node.EffectId != zone.OwnerEffectId) node = context.State.EffectRuntime.EffectNodes.Find(value => value.EffectId == node.ParentEffectId);
                if (node == null) throw new InvalidOperationException("transfer_permission_denied");
            }
            var validation = Validate(context.State, request);
            if (!validation.IsValid) throw new InvalidOperationException(validation.ReasonCode);
            var result = Apply(context.State, request);
            context.Node.PositionChanges.AddRange(GameStateCloneService.DeepClone(result.PositionChanges));
            return result;
        }
        internal ItemTransferResult RelocateBoundCityToken(EffectExecutionContext context, string itemId, string destinationZoneId)
        {
            var state=context.State;
            var binding=state.InfluenceLocks.SingleOrDefault(l=>l.Active && l.SourceItemId==itemId);
            var token=state.ItemZones.Items.SingleOrDefault(i=>i.ItemId==itemId && i.TypeId=="infected.influence_lock_token");
            var target=state.ItemZones.Zones.SingleOrDefault(z=>z.ZoneId==destinationZoneId);
            if(binding==null || token==null || target==null)throw new InvalidOperationException("bound_token_transfer_rejected");
            bool expiry=context.Node.EffectTypeId==YC.Domain.Influence.InfluenceLockRules.ExpireTypeId &&
                YC.Domain.Movement.TokenMovementProfileRegistry.IsExpiredBeforeActionRound(state,binding.ExpiresBeforeActionRoundSequence) && target.TemplateId=="core.zone.box_retired";
            bool follow=context.Node.EffectTypeId==CityMoveEffectTypeIds.CommitPosition && target.ZoneId==YC.Domain.Influence.InfluenceLockRules.TokenZone(state,YC.Domain.Movement.TokenMovementProfileRegistry.GetLocationId(state,binding.BoundCityItemId));
            if(!expiry && !follow)throw new InvalidOperationException("bound_token_transfer_rejected");
            var result=Apply(state,new ItemTransferRequest{ExecutingPlayerId=context.Node.PlayerId,ItemIds=new List<string>{itemId},ExpectedSource=new ItemPositionRef{ZoneId=token.ZoneId},Destination=new ZoneRef{ZoneId=destinationZoneId},InsertionKind="unordered"});
            context.Node.PositionChanges.AddRange(GameStateCloneService.DeepClone(result.PositionChanges));
            if(!query.TryValidate(state,out string reason))throw new InvalidOperationException(reason);
            return result;
        }
        private static ItemTransferResult Apply(GameState state, ItemTransferRequest request)
        {
            var result = new ItemTransferResult();
            var destination = state.ItemZones.Zones.Single(zone => zone.ZoneId == request.Destination.ZoneId);
            foreach (string id in request.ItemIds)
            {
                var item = state.ItemZones.Items.Single(value => value.ItemId == id);
                if (item.ZoneId == destination.ZoneId && item.SlotId == request.DestinationSlotId && request.InsertionKind == "unordered") continue;
                result.PositionChanges.Add(new ItemPositionChange { ItemId = id, From = new ItemPositionRef { ZoneId = item.ZoneId, SlotId = item.SlotId }, To = new ItemPositionRef { ZoneId = destination.ZoneId, SlotId = request.DestinationSlotId } });
                item.ZoneId = destination.ZoneId; item.SlotId = request.DestinationSlotId;
                if (destination.TemplateId.StartsWith("core.zone.character_", StringComparison.Ordinal) || destination.TemplateId == "core.zone.city_facility_cell" || destination.TemplateId == "core.zone.player_awards") item.ControllerSubject = ItemSubjectRef.Player(destination.OwnerPlayerId);
                if (SourceWasPersistent(result.PositionChanges, id, state)) YC.Domain.Cards.CharacterLifecycleService.DetachExitedSubscriptions(state, id);
                foreach (var slot in state.CharacterCoverSlots.Where(slot => slot.CardInstanceId == id && (item.ZoneId != result.PositionChanges.Last().From.ZoneId || item.SlotId != slot.SlotId))) slot.CardInstanceId = "";
                foreach (var binding in state.FacilityCharacterBindings.Where(binding => binding.Active && binding.FacilityItemId == id))
                    if (destination.TemplateId != "core.zone.city_facility_cell" || destination.OwnerPlayerId != binding.ControllerPlayerId) binding.Active = false;
                result.MovedItemIds.Add(id);
            }
            foreach (var order in state.ItemZones.Orders)
            {
                var removed = order.ItemIds.RemoveAll(result.MovedItemIds.Contains);
                if (order.ZoneId == destination.ZoneId && result.MovedItemIds.Count > 0)
                {
                    if (request.InsertionKind == "top") order.ItemIds.InsertRange(0, result.MovedItemIds);
                    else order.ItemIds.AddRange(result.MovedItemIds);
                    removed++;
                }
                if (removed > 0) order.OrderRevision++;
            }
            result.MovedCount = result.MovedItemIds.Count; result.Changed = result.MovedCount > 0;
            return result;
        }
        private static bool SourceWasPersistent(List<ItemPositionChange> changes, string itemId, GameState state) => changes.Any(change => change.ItemId == itemId && state.ItemZones.Zones.Any(zone => zone.ZoneId == change.From.ZoneId && zone.TemplateId == "core.zone.character_persistent"));
        private static bool CanAccess(GameState state, ZoneInstanceState zone, int playerId, bool source)
        {
            var visited = new HashSet<string>();
            while (zone != null && visited.Add(zone.ZoneId))
            {
                if (source && zone.VisibilityPolicyId == ItemZoneVisibility.SecretRetired) return false;
                int accessPlayer = AccessPlayer(state, zone);
                if (accessPlayer > 0 && accessPlayer != playerId && (source || zone.VisibilityPolicyId == ItemZoneVisibility.Owner)) return false;
                if (zone.ParentKind == ZoneParentKind.Item)
                {
                    var carrier = state.ItemZones.Items.Single(item => item.ItemId == zone.ParentId);
                    if (carrier.ControllerSubject.Kind == ItemSubjectKind.Player && carrier.ControllerSubject.PlayerId != playerId) return false;
                    zone = state.ItemZones.Zones.Single(value => value.ZoneId == carrier.ZoneId);
                }
                else if (zone.ParentKind == ZoneParentKind.Zone) zone = state.ItemZones.Zones.Single(value => value.ZoneId == zone.ParentId);
                else return true;
            }
            return false;
        }
        private static int AccessPlayer(GameState state, ZoneInstanceState zone)
        {
            if (zone.ParentKind == ZoneParentKind.Item && (zone.TemplateId == "core.zone.card_cube_storage" || zone.TemplateId == "core.zone.card_facility_storage"))
                return state.ItemZones.Items.Single(item => item.ItemId == zone.ParentId).ControllerSubject.PlayerId;
            return zone.OwnerPlayerId;
        }
        private static bool IsPendingDepartureCapture(GameState state, ZoneInstanceState source, ZoneInstanceState destination, ItemTransferRequest request)
        {
            if (source.TemplateId != "core.zone.effect_staging" || destination.Configuration?.EntryPolicyId != "core.removed_owned_influence" || destination.ParentKind != ZoneParentKind.Item || request.OperationProfileId != "core.cube_storage" || request.ItemIds.Count != 1) return false;
            var owner = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == source.OwnerEffectId);
            var card = state.ItemZones.Items.Find(i => i.ItemId == destination.ParentId);
            var cube = state.ItemZones.Items.Find(i => i.ItemId == request.ItemIds[0]);
            var response = state.EffectRuntime.DeferredResponses?.Find(r => r.Status == DeferredResponseStatus.Pending &&
                r.ResponseRootEffectId == source.OwnerEffectId && r.ReservationZoneId == source.ZoneId && r.ReservedItemId == cube?.ItemId &&
                r.SourceCardInstanceId == card?.ItemId && r.AnsweringPlayerId == request.ExecutingPlayerId);
            if (response != null)
            {
                var departure = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == response.SourceDepartureEffectId);
                return card?.ControllerSubject.Kind == ItemSubjectKind.Player && card.ControllerSubject.PlayerId == request.ExecutingPlayerId &&
                    state.ItemZones.Zones.Any(z => z.ZoneId == card.ZoneId &&
                        (z.TemplateId == "core.zone.character_persistent" || z.TemplateId == "core.zone.character_covered")) &&
                    cube?.PhysicalKind == ItemPhysicalKind.Cube &&
                    (cube.OwnerSubject.Kind != ItemSubjectKind.Player || cube.OwnerSubject.PlayerId == request.ExecutingPlayerId) &&
                    YC.Domain.Effects.ItemTransferEffectExecutor.Text(departure?.ContinuationData, "pendingItemId") == cube.ItemId &&
                    YC.Domain.Effects.ItemTransferEffectExecutor.Text(departure?.ContinuationData, "destinationHandoffResponseId") == response.DeferredResponseId &&
                    !YC.Domain.Effects.ItemTransferEffectExecutor.Flag(departure.ContinuationData, "destinationResolved") &&
                    new[] { "remove", "replace" }.Contains(YC.Domain.Effects.ItemTransferEffectExecutor.Text(departure.ContinuationData, "pendingDepartureKind"));
            }
            return card?.ControllerSubject.PlayerId == request.ExecutingPlayerId && state.ItemZones.Zones.Any(z => z.ZoneId == card.ZoneId && z.TemplateId == "core.zone.character_persistent") &&
                cube?.PhysicalKind == ItemPhysicalKind.Cube && (cube.OwnerSubject.Kind != ItemSubjectKind.Player || cube.OwnerSubject.PlayerId == request.ExecutingPlayerId) &&
                YC.Domain.Effects.ItemTransferEffectExecutor.Text(owner?.ContinuationData, "pendingItemId") == cube.ItemId &&
                !YC.Domain.Effects.ItemTransferEffectExecutor.Flag(owner.ContinuationData, "destinationResolved") &&
                new[] { "remove", "replace" }.Contains(YC.Domain.Effects.ItemTransferEffectExecutor.Text(owner.ContinuationData, "pendingDepartureKind"));
        }
        private static ItemTransferValidationResult Invalid(string code) => new ItemTransferValidationResult { ReasonCode = code };
    }
}
