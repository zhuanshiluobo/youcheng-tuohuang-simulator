using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Influence
{
    /// <summary>离场实例在所属 Effect 的暂存区等待一次去向提交；收留与归还不会再次触发移除。</summary>
    public sealed class InfluenceDestinationResolver
    {
        private readonly ItemTransferService transfers;
        public InfluenceDestinationResolver(ItemTransferService transfers = null) { this.transfers = transfers ?? new ItemTransferService(); }
        public string GetStagingZoneId(EffectExecutionContext context)
            => GetStagingZoneId(context.State, context.Node.EffectId);
        private static string GetStagingZoneId(GameState state, string effectId)
        {
            var existing = state.ItemZones.Zones.Find(zone => zone.TemplateId == "core.zone.effect_staging" && zone.OwnerEffectId == effectId && zone.LocalZoneId == "removed_influence");
            if (existing != null) return existing.ZoneId;
            var declaration = new CardZoneDefinition { LocalZoneId = "removed_influence", ZoneTemplateId = "core.zone.effect_staging",
                AcceptedItemKinds = new System.Collections.Generic.List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
                AcceptedTypeIds = new System.Collections.Generic.List<string> { "core.cube" }, CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered,
                VisibilityPolicyId = ItemZoneVisibility.Public, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
            if (!new ItemZoneStateFactory().TryCreateEffectStaging(state, effectId, declaration, out var id, out var reason)) throw new InvalidOperationException(reason);
            return id;
        }
        public ItemTransferResult Resolve(EffectExecutionContext context, string itemId, string destinationZoneId)
        {
            var item = new ItemZoneQuery().FindItem(context.State, itemId);
            if (item == null) throw new InvalidOperationException("unknown_item");
            var source = context.State.ItemZones.Zones.Find(zone => zone.ZoneId == item.ZoneId);
            if (source?.TemplateId != "core.zone.effect_staging") return new ItemTransferResult();
            var owner = context.State.EffectRuntime.EffectNodes.Find(node => node.EffectId == source.OwnerEffectId);
            bool deferred = EffectTreeExecutor.TryFindDeferredResponse(context.State, context.Node.EffectId, out var response);
            if (deferred)
            {
                if (response.ReservedItemId != itemId || response.ReservationZoneId != source.ZoneId || source.OwnerEffectId != response.ResponseRootEffectId ||
                    response.AnsweringPlayerId != context.Node.PlayerId) throw new InvalidOperationException("deferred_destination_permission_denied");
            }
            else if (Text(owner?.ContinuationData, "pendingItemId") != itemId) throw new InvalidOperationException("pending_destination_required");
            var result = transfers.RelocateItems(context, new ItemTransferRequest { ItemIds = new System.Collections.Generic.List<string> { itemId },
                ExpectedSource = new ItemPositionRef { ZoneId = source.ZoneId }, Destination = new ZoneRef { ZoneId = destinationZoneId },
                InsertionKind = "unordered", OperationProfileId = "core.cube_storage", CarrierPolicyId = "preserve", ExecutingPlayerId = context.Node.PlayerId, RequiredCount = 1 });
            if (!deferred)
            {
                Set(owner.ContinuationData, "destinationResolved", NormalizedValue.CreateBoolean(true));
                Set(owner.ContinuationData, "destinationZoneId", NormalizedValue.CreateString(destinationZoneId));
            }
            return result;
        }
        public ItemTransferResult ReturnToSupply(EffectExecutionContext context, string itemId)
        {
            var item = new ItemZoneQuery().FindItem(context.State, itemId);
            if (item == null) throw new InvalidOperationException("unknown_item");
            string supplyId = ResolveOriginalSupply(context.State, item, item.OriginalSupplyZoneId);
            var supply = context.State.ItemZones.Zones.Single(zone => zone.ZoneId == supplyId);
            return Resolve(context, itemId, supply.ZoneId);
        }

        public static string DestinationHandlerId(string abilityId) => "influence.removed_destination." + abilityId;

        public DeferredEventResponseDescriptor DescribeDeferredResponse(EffectEventHandlerContext eventContext, EffectSpec body)
        {
            if (eventContext?.State == null || body == null ||
                !new[] { InfluenceEventTypeIds.InfluenceRemoved, InfluenceEventTypeIds.InfluenceReplaced }.Contains(eventContext.Event?.EventType) ||
                string.IsNullOrEmpty(body.SourceId) || !body.SourceId.StartsWith("character-card:", StringComparison.Ordinal)) return null;
            string cardId = body.SourceId.Substring("character-card:".Length);
            var state = eventContext.State;
            var departure = state.EffectRuntime.EffectNodes.Find(node => node.EffectId == eventContext.Event.SourceEffectId);
            string itemId = Text(departure?.ContinuationData, "pendingItemId");
            if (!TryFindStorageChoice(body, body.PlayerId > 0 ? body.PlayerId : eventContext.OwnerNode.PlayerId,
                cardId, itemId, out int answeringPlayer, out _, out _, out _)) return null;
            var card = state.ItemZones.Items.Find(item => item.ItemId == cardId);
            var cube = state.ItemZones.Items.Find(item => item.ItemId == itemId);
            string abilityId = eventContext.Registration.AbilityId;
            string key = StableIdFactory.Create("deferred_response", state.GameId, departure?.EffectId ?? string.Empty, itemId, cardId, abilityId);
            var existingResponse = state.EffectRuntime.DeferredResponses.SingleOrDefault(value => value.DeferredResponseId == key);
            bool existingHandoff = existingResponse != null && existingResponse.SourceDepartureEffectId == departure?.EffectId &&
                existingResponse.SourceCardInstanceId == cardId && existingResponse.AbilityId == abilityId && existingResponse.AnsweringPlayerId == answeringPlayer &&
                existingResponse.ReservedItemId == itemId && Text(departure?.ContinuationData, "destinationHandoffResponseId") == key;
            if (departure == null || cube?.PhysicalKind != ItemPhysicalKind.Cube || card?.ControllerSubject.Kind != ItemSubjectKind.Player ||
                card.ControllerSubject.PlayerId != answeringPlayer || state.FindPlayer(answeringPlayer) == null ||
                !CharacterLifecycleService.IsSubscriptionActive(state, cardId, abilityId) ||
                !new[] { "remove", "replace" }.Contains(Text(departure.ContinuationData, "pendingDepartureKind")) || Flag(departure.ContinuationData, "destinationResolved") ||
                !existingHandoff && !state.ItemZones.Zones.Any(zone => zone.ZoneId == cube.ZoneId && zone.TemplateId == "core.zone.effect_staging" && zone.OwnerEffectId == departure.EffectId) ||
                cube.OwnerSubject.Kind == ItemSubjectKind.Player && cube.OwnerSubject.PlayerId != answeringPlayer)
                throw new InvalidOperationException("deferred_destination_material_invalid");
            return new DeferredEventResponseDescriptor {
                StableResponseKey = key,
                SourceEventId = eventContext.Event.EventId, SourceDepartureEffectId = departure.EffectId,
                SourceCardInstanceId = card.ItemId, AbilityId = abilityId, AnsweringPlayerId = answeringPlayer, ReservedItemId = cube.ItemId,
                OriginalSupplyZoneId = ResolveOriginalSupply(state, cube, existingHandoff ? existingResponse.OriginalSupplyZoneId : cube.OriginalSupplyZoneId), DestinationHandlerId = DestinationHandlerId(abilityId)
            };
        }

        public ItemTransferResult AdoptPendingDestination(GameState work, EffectNodeRuntimeState responseRoot, DeferredResponseState response)
        {
            var departure = work.EffectRuntime.EffectNodes.Find(node => node.EffectId == response.SourceDepartureEffectId);
            var item = work.ItemZones.Items.Find(value => value.ItemId == response.ReservedItemId);
            if (Text(departure?.ContinuationData, "destinationHandoffResponseId") == response.DeferredResponseId &&
                work.EffectRuntime.DeferredResponses.Any(value => ReferenceEquals(value, response)) &&
                responseRoot?.EffectId == response.ResponseRootEffectId && TryValidateDeferredDestination(work, response, out _))
                return new ItemTransferResult();
            if (response.Status != DeferredResponseStatus.Pending || responseRoot?.EffectId != response.ResponseRootEffectId ||
                responseRoot.PlayerId != response.AnsweringPlayerId || departure == null || item == null ||
                Text(departure.ContinuationData, "pendingItemId") != item.ItemId || Flag(departure.ContinuationData, "destinationResolved") ||
                !new[] { "remove", "replace" }.Contains(Text(departure.ContinuationData, "pendingDepartureKind")) ||
                !CharacterLifecycleService.IsSubscriptionActive(work, response.SourceCardInstanceId, response.AbilityId) ||
                !work.ItemZones.Items.Any(card => card.ItemId == response.SourceCardInstanceId && card.ControllerSubject.PlayerId == response.AnsweringPlayerId) ||
                !work.EffectRuntime.RuleEvents.Any(ruleEvent => ruleEvent.EventId == response.SourceEventId && ruleEvent.SourceEffectId == departure.EffectId))
                throw new InvalidOperationException("deferred_destination_adoption_rejected");
            response.OriginalSupplyZoneId = ResolveOriginalSupply(work, item, response.OriginalSupplyZoneId);
            string sourceZone = item.ZoneId;
            response.ReservationZoneId = GetStagingZoneId(work, responseRoot.EffectId);
            var moved = transfers.AdoptDeferredReservationOnWorking(work, response, sourceZone, response.ReservationZoneId);
            if (moved.MovedCount != 1 || moved.MovedItemIds.Count != 1 || moved.MovedItemIds[0] != item.ItemId ||
                work.ItemZones.Items.Any(value => value.ZoneId == sourceZone)) throw new InvalidOperationException("deferred_reservation_handoff_incomplete");
            Set(departure.ContinuationData, "destinationHandoffResponseId", NormalizedValue.CreateString(response.DeferredResponseId));
            return moved;
        }

        public DeferredDestinationResult CompleteDeferredDestination(EffectExecutionContext responseContext, DeferredResponseState response)
        {
            if (!TryValidateDeferredDestination(responseContext.State, response, out string diagnostic))
                return DeferredDestinationResult.Unresolved(diagnostic);
            if (response.Status == DeferredResponseStatus.Stored) return DeferredDestinationResult.Stored(response.ResolvedDestinationZoneId);
            if (response.Status == DeferredResponseStatus.Returned) return DeferredDestinationResult.Returned(response.ResolvedDestinationZoneId);
            var descendants = Descendants(responseContext.State, response.ResponseRootEffectId);
            if (descendants.Any(node => node.Status != EffectNodeStatus.Completed)) return DeferredDestinationResult.Unresolved("deferred_children_not_complete");
            if (!TryReadAnsweredDestination(responseContext.State, response, out bool store, out string target, out diagnostic))
                return DeferredDestinationResult.Unresolved(diagnostic);
            var item = responseContext.State.ItemZones.Items.Single(value => value.ItemId == response.ReservedItemId);
            if (store)
                return item.ZoneId == target ? DeferredDestinationResult.Stored(target) : DeferredDestinationResult.Unresolved("deferred_store_not_applied");
            if (item.ZoneId != response.ReservationZoneId) return DeferredDestinationResult.Unresolved("deferred_decline_item_not_reserved");
            string supply = ResolveOriginalSupply(responseContext.State, item, response.OriginalSupplyZoneId);
            Resolve(responseContext, item.ItemId, supply);
            return item.ZoneId == supply ? DeferredDestinationResult.Returned(supply) : DeferredDestinationResult.Unresolved("deferred_return_not_applied");
        }

        public bool TryValidateDeferredDestination(GameState state, DeferredResponseState response, out string diagnostic)
        {
            diagnostic = "deferred_destination_state_invalid";
            if (state?.EffectRuntime == null || response == null || !Enum.IsDefined(typeof(DeferredResponseStatus), response.Status) || response.DestinationHandlerId != DestinationHandlerId(response.AbilityId) ||
                response.DeferredResponseId != StableIdFactory.Create("deferred_response", state.GameId, response.SourceDepartureEffectId,
                    response.ReservedItemId, response.SourceCardInstanceId, response.AbilityId)) return false;
            var departure = state.EffectRuntime.EffectNodes.Find(node => node.EffectId == response.SourceDepartureEffectId);
            var root = state.EffectRuntime.EffectNodes.Find(node => node.EffectId == response.ResponseRootEffectId);
            var item = state.ItemZones.Items.Find(value => value.ItemId == response.ReservedItemId);
            var reservation = state.ItemZones.Zones.Find(zone => zone.ZoneId == response.ReservationZoneId);
            if (departure == null || root == null || root.PlayerId != response.AnsweringPlayerId || root.ParentEffectId.Length > 0 ||
                root.SourceId != "character-card:" + response.SourceCardInstanceId || item?.PhysicalKind != ItemPhysicalKind.Cube ||
                Text(departure.ContinuationData, "pendingItemId") != item.ItemId ||
                Text(departure.ContinuationData, "destinationHandoffResponseId") != response.DeferredResponseId ||
                Flag(departure.ContinuationData, "destinationResolved") || !new[] { "remove", "replace" }.Contains(Text(departure.ContinuationData, "pendingDepartureKind")) ||
                reservation?.TemplateId != "core.zone.effect_staging" || reservation.OwnerEffectId != root.EffectId || reservation.LocalZoneId != "removed_influence" ||
                !state.EffectRuntime.RuleEvents.Any(ruleEvent => ruleEvent.EventId == response.SourceEventId && ruleEvent.SourceEffectId == departure.EffectId &&
                    new[] { InfluenceEventTypeIds.InfluenceRemoved, InfluenceEventTypeIds.InfluenceReplaced }.Contains(ruleEvent.EventType))) return false;
            try { if (ResolveOriginalSupply(state, item, response.OriginalSupplyZoneId) != response.OriginalSupplyZoneId) return false; }
            catch (InvalidOperationException) { diagnostic = "deferred_original_supply_invalid"; return false; }
            if (response.Status == DeferredResponseStatus.Pending && item.ZoneId == response.ReservationZoneId)
            {
                if (root.Status == EffectNodeStatus.Completed || root.Status == EffectNodeStatus.Failed || root.Status == EffectNodeStatus.Faulted ||
                    !string.IsNullOrEmpty(response.ResolvedDestinationZoneId) || !IsCurrentStorageCarrier(state, response)) return false;
                diagnostic = string.Empty; return true;
            }
            if (!TryReadAnsweredDestination(state, response, out bool store, out string target, out diagnostic)) return false;
            if (response.Status != DeferredResponseStatus.Pending)
            {
                string recorded = response.Status == DeferredResponseStatus.Stored ? target : response.OriginalSupplyZoneId;
                if (store != (response.Status == DeferredResponseStatus.Stored) || response.ResolvedDestinationZoneId != recorded ||
                    !HasDestinationTransferFact(state, response, recorded)) return false;
                diagnostic = string.Empty; return true;
            }
            if (!store || item.ZoneId != target || !IsCurrentStorageCarrier(state, response) || !HasDestinationTransferFact(state, response, target)) return false;
            diagnostic = string.Empty; return true;
        }

        private static bool IsCurrentStorageCarrier(GameState state, DeferredResponseState response)
        {
            var card = state.ItemZones.Items.Find(item => item.ItemId == response.SourceCardInstanceId);
            return card?.ControllerSubject.Kind == ItemSubjectKind.Player && card.ControllerSubject.PlayerId == response.AnsweringPlayerId &&
                state.ItemZones.Zones.Any(zone => zone.ZoneId == card.ZoneId && new[] { "core.zone.character_persistent", "core.zone.character_covered" }.Contains(zone.TemplateId));
        }

        private static bool HasDestinationTransferFact(GameState state, DeferredResponseState response, string zoneId) =>
            new[] { state.EffectRuntime.EffectNodes.Single(node => node.EffectId == response.ResponseRootEffectId) }
                .Concat(Descendants(state, response.ResponseRootEffectId))
                .Any(node => node.PositionChanges.Any(change => change.ItemId == response.ReservedItemId &&
                    change.From?.ZoneId == response.ReservationZoneId && change.To?.ZoneId == zoneId));

        private static string ResolveOriginalSupply(GameState state, ItemInstanceState cube, string supplied)
        {
            string original = cube.OriginalSupplyZoneId ?? string.Empty;
            if (!string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(supplied) && original != supplied)
                throw new InvalidOperationException("influence_original_supply_conflict");
            string id = string.IsNullOrEmpty(supplied) ? original : supplied;
            var types = ItemTypeRegistry.CreateBuiltIn();
            if (string.IsNullOrEmpty(id))
            {
                if (cube.OwnerSubject.Kind == ItemSubjectKind.Player && types.IsAssignableTo(cube.TypeId, "core.player_marker"))
                    id = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.player_marker_supply" && zone.OwnerPlayerId == cube.OwnerSubject.PlayerId).ZoneId;
                else if (cube.OwnerSubject.Kind == ItemSubjectKind.Company && types.IsAssignableTo(cube.TypeId, "infected.company_marker"))
                    id = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.reward_pool" && zone.LocalZoneId == "company_markers").ZoneId;
                else throw new InvalidOperationException("influence_supply_unknown");
            }
            var supply = state.ItemZones.Zones.SingleOrDefault(zone => zone.ZoneId == id);
            if (supply == null || !new[] { "core.zone.player_marker_supply", "core.zone.reward_pool", "core.zone.box_reserve" }.Contains(supply.TemplateId) ||
                supply.TemplateId == "core.zone.player_marker_supply" && (cube.OwnerSubject.Kind != ItemSubjectKind.Player || supply.OwnerPlayerId != cube.OwnerSubject.PlayerId) ||
                !new ItemZoneQuery().Accepts(supply, cube))
                throw new InvalidOperationException("influence_original_supply_invalid");
            return id;
        }

        private static bool TryFindStorageChoice(EffectSpec spec, int inheritedPlayer, string cardId, string itemId,
            out int answeringPlayer, out string localZone, out string storeOption, out string declineOption)
        {
            answeringPlayer = -1; localZone = storeOption = declineOption = string.Empty;
            int player = spec.PlayerId > 0 ? spec.PlayerId : inheritedPlayer;
            if (spec.EffectTypeId == LuaDomainEffectTypeIds.Choice)
            {
                var options = Value(spec.NormalizedArguments, "options");
                if (options?.Kind != NormalizedValueKind.Array || options.Items.Count != 2 || spec.NestedEffectGroups.Count != 2 ||
                    Number(spec.NormalizedArguments, "minSelections", 1) != 1 || Number(spec.NormalizedArguments, "maxSelections", 1) != 1) return false;
                for (int i = 0; i < 2; i++)
                {
                    var group = spec.NestedEffectGroups[i];
                    string option = options.Items[i].StringValue;
                    if (string.IsNullOrEmpty(option)) return false;
                    if (group.Count == 0) declineOption = option;
                    else if (group.Count == 1 && group[0].EffectTypeId == ContentPrimitiveEffectExecutor.StoreRemoved &&
                        Text(group[0].NormalizedArguments, "cardInstanceId") == cardId && Text(group[0].NormalizedArguments, "influenceItemId") == itemId)
                    { storeOption = option; localZone = Text(group[0].NormalizedArguments, "localZoneId"); }
                    else return false;
                }
                var explicitPlayer = Value(spec.NormalizedArguments, "player") ?? Value(spec.NormalizedArguments, "executingPlayer");
                string key = explicitPlayer?.Kind == NormalizedValueKind.Integer ? explicitPlayer.IntegerValue.ToString(CultureInfo.InvariantCulture) :
                    explicitPlayer?.Kind == NormalizedValueKind.StableReference ? explicitPlayer.ReferenceId : explicitPlayer?.StringValue;
                if (!string.IsNullOrEmpty(key) && key.StartsWith("p", StringComparison.OrdinalIgnoreCase)) key = key.Substring(1);
                if (!string.IsNullOrEmpty(key) && int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int resolved)) player = resolved;
                answeringPlayer = player;
                return player > 0 && localZone.Length > 0 && storeOption.Length > 0 && declineOption.Length > 0;
            }
            var matches = spec.Children.Concat(spec.NestedEffectGroups.SelectMany(group => group)).Where(child =>
                TryFindStorageChoice(child, player, cardId, itemId, out _, out _, out _, out _)).ToList();
            return matches.Count == 1 && TryFindStorageChoice(matches[0], player, cardId, itemId, out answeringPlayer, out localZone, out storeOption, out declineOption);
        }

        private static List<EffectNodeRuntimeState> Descendants(GameState state, string rootId)
        {
            var nodes = new List<EffectNodeRuntimeState>(); var pending = new Queue<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            var root = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == rootId);
            foreach (string id in root.ChildEffectIds) pending.Enqueue(id);
            while (pending.Count > 0)
            {
                string id = pending.Dequeue();
                if (!seen.Add(id)) throw new InvalidOperationException("deferred_child_cycle");
                var node = state.EffectRuntime.EffectNodes.Single(value => value.EffectId == id); nodes.Add(node);
                foreach (string child in node.ChildEffectIds) pending.Enqueue(child);
            }
            return nodes;
        }

        private static bool TryReadAnsweredDestination(GameState state, DeferredResponseState response, out bool store, out string zoneId, out string diagnostic)
        {
            store = false; zoneId = string.Empty; diagnostic = "deferred_destination_answer_missing";
            var choices = Descendants(state, response.ResponseRootEffectId).Where(node => node.EffectTypeId == LuaDomainEffectTypeIds.Choice).ToList();
            if (choices.Count != 1) return false;
            var choice = choices[0];
            var spec = new EffectSpec(choice.EffectTypeId, choice.NormalizedArguments) { PlayerId = choice.PlayerId };
            int offset = 0;
            if (choice.NestedGroupSizes == null || choice.NestedEffects == null || choice.NestedGroupSizes.Any(size => size < 0) ||
                choice.NestedGroupSizes.Sum(size => (long)size) != choice.NestedEffects.Count) return false;
            foreach (int size in choice.NestedGroupSizes)
            {
                var group = new List<EffectSpec>();
                for (int i = 0; i < size; i++) group.Add(EffectRegistry.FromRuntimeSpec(choice.NestedEffects[offset + i]));
                offset += size; spec.NestedEffectGroups.Add(group);
            }
            if (!TryFindStorageChoice(spec, choice.PlayerId, response.SourceCardInstanceId, response.ReservedItemId,
                out int answeringPlayer, out string local, out string storeOption, out string declineOption) || answeringPlayer != response.AnsweringPlayerId) return false;
            var request = state.EffectRuntime.InteractionRequests.LastOrDefault(value => value.OwnerEffectId == choice.EffectId && value.Status == "answered" && value.AnsweringPlayerId == response.AnsweringPlayerId);
            var answer = request?.NormalizedAnswer;
            if (answer?.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
            string selected = answer?.Kind == NormalizedValueKind.String ? answer.StringValue : answer?.Kind == NormalizedValueKind.StableReference ? answer.ReferenceId : string.Empty;
            if (selected != storeOption && selected != declineOption) return false;
            store = selected == storeOption;
            var destination = state.ItemZones.Zones.SingleOrDefault(zone => zone.ParentKind == ZoneParentKind.Item && zone.ParentId == response.SourceCardInstanceId && zone.LocalZoneId == local &&
                zone.TemplateId == "core.zone.card_cube_storage" && zone.Configuration?.EntryPolicyId == "core.removed_owned_influence");
            if (destination == null) { diagnostic = "deferred_destination_card_zone_invalid"; return false; }
            zoneId = destination.ZoneId; diagnostic = string.Empty; return true;
        }
        public static void Set(NormalizedValue data, string key, NormalizedValue value)
        {
            data.Properties.RemoveAll(entry => entry.Name == key);
            data.Properties.Add(new NormalizedValueEntry { Name = key, Value = value });
            data.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        }
    }
}
