using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using YC.Domain.Cards;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class InfluenceOperationDomainTests
    {
        private GameMapDefinition map;
        private InfluenceService service;
        private EffectRegistry registry;
        [SetUp] public void SetupMapAndProductionRegistry()
        {
            map = new GameMapDefinition { MapId = "test.influence_map", MinPlayers = 2, MaxPlayers = 4,
                Regions = new List<MapRegionDefinition> { new MapRegionDefinition { RegionId = "harbor" }, new MapRegionDefinition { RegionId = "upland" } },
                Locations = new List<MapLocationDefinition> {
                    new MapLocationDefinition { LocationId = "quay", RegionId = "harbor", CanDockCity = true, InfluenceSlotCount = 2 },
                    new MapLocationDefinition { LocationId = "pier", RegionId = "harbor", CanDockCity = true, InfluenceSlotCount = 2 },
                    new MapLocationDefinition { LocationId = "mine", RegionId = "upland", CanDockCity = true, InfluenceSlotCount = 2 },
                    new MapLocationDefinition { LocationId = "closed", RegionId = "upland", CanDockCity = true, InfluenceSlotCount = 2 } } };
            for (int index = 0; index < 9; index++) map.Routes.Add(new MapRouteDefinition { RouteId = "channel_" + index, RegionId = "harbor", FromLocationId = "quay", ToLocationId = index == 0 ? "closed" : "mine", InfluenceSlotCount = 2, BaseCost = 2 });
            map.Routes.Add(new MapRouteDefinition { RouteId = "pier_channel", RegionId = "harbor", FromLocationId = "quay", ToLocationId = "pier", InfluenceSlotCount = 2, BaseCost = 2 });
            var query = new MapQueryService(map);
            service = new InfluenceService(query);
            registry = new EffectRegistry();
            InfluenceEffectExecutor.Register(registry, service);
            MapOperationEffectExecutor.Register(registry, query);
            ContentPrimitiveEffectExecutor.Register(registry, query, service);
            ItemTransferEffectExecutor.Register(registry);
            ResourceEffectExecutor.Register(registry);
        }
        private GameState State(int players = 2)
        {
            var source = new GameState { GameId = "game.influence", MapId = map.MapId, ContentPackHash = "content.influence", Round = 1, ActionRound = 1,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = players, ContentPackHash = "content.influence" } };
            for (int id = 1; id <= players; id++) source.Players.Add(new PlayerState { PlayerId = id, Resources = new ResourceSet { GoldVoucher = 10, OriginiumShard = 10 } });
            var recipe = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "player_markers", DefinitionId = "core.component.marker", TypeId = "core.player_marker", Quantity = 8, PerPlayer = true, InitialZoneRole = "player_markers" },
                new ItemRecipeEntry { EntryId = "company_markers", DefinitionId = "infected.component.company_marker", TypeId = "infected.company_marker", Quantity = 8, InitialZoneRole = "company_markers" },
                new ItemRecipeEntry { EntryId = "cube_carrier", DefinitionId = "test.cube_carrier", TypeId = "core.basic_character_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" },
                new ItemRecipeEntry { EntryId = "cities", DefinitionId = "core.component.city", TypeId = "core.mobile_city", Quantity = 1, PerPlayer = true, InitialZoneRole = "city_reserve" },
                new ItemRecipeEntry { EntryId = "path_token", DefinitionId = "infected.component.gavial_token", TypeId = "infected.gavial_token", Quantity = 1, InitialZoneRole = "reserve" } };
            foreach (var location in map.Locations) recipe.Add(new ItemRecipeEntry { EntryId = "resource_" + location.LocationId, DefinitionId = "core.component.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + location.LocationId });
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, recipe);
            state.Map.OpenLocationIds.AddRange(new[] { "quay", "pier", "mine" });
            Valid(state);
            return state;
        }
        private static void Valid(GameState state) => Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.True, reason);
        private EffectNodeRuntimeState Run(GameState state, EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state); return engine.GetNode(id);
        }
        private EffectNodeRuntimeState Place(GameState state, int player, string slot) => Run(state, InfluenceEffectSpecFactory.PlaceInfluence(player, slot));
        private string PlacedId(EffectNodeRuntimeState node) => Value(node.NormalizedResult, "influenceId").ReferenceId;
        private string CompanyPlace(GameState state, string location, int index = 0) => PlacedId(Run(state,
            InfluenceEffectSpecFactory.PlaceInfluence(1, InfluenceService.GetLocationSlotId(location, index), ownerSubject: new RuleSubjectReference { SubjectType = "company", InstanceId = "stonehoove_security", DefinitionId = "stonehoove_security", PlayerId = -1 })));
        private static ZoneInstanceState Child(GameState state, string parentLocal, string parentTemplate, string template) => state.ItemZones.Zones.Single(zone => zone.TemplateId == template && zone.ParentId == state.ItemZones.Zones.Single(parent => parent.LocalZoneId == parentLocal && parent.TemplateId == parentTemplate).ZoneId);
        private static void SetPosition(GameState state, string id, string zoneId)
        {
            foreach (var order in state.ItemZones.Orders) if (order.ItemIds.Remove(id)) order.OrderRevision++;
            var item = state.ItemZones.Items.Single(value => value.ItemId == id); item.ZoneId = zoneId; item.SlotId = "";
            var targetOrder = state.ItemZones.Orders.Find(order => order.ZoneId == zoneId); if (targetOrder != null) { targetOrder.ItemIds.Add(id); targetOrder.OrderRevision++; }
        }
        private EffectSpec Select(string operation, string filter, int count = 2, int required = 0) => EffectSpec.Create(ContentPrimitiveEffectExecutor.SelectInfluence,
            Object("player", NormalizedValue.CreateString("1"), "operation", NormalizedValue.CreateString(operation), "subjectFilter", NormalizedValue.CreateString(filter),
                "count", NormalizedValue.CreateInteger(count), "requiredCount", NormalizedValue.CreateInteger(required), "distinctPolicy", NormalizedValue.CreateString("subject")), 1);
        private void Answer(GameState state, string value)
        {
            var request = state.EffectRuntime.InteractionRequests.Last(item => item.Status == "open");
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, NormalizedValue.CreateString(value), out var reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state);
        }
        private GameState Restore(GameState state)
        {
            var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); Valid(restored.State); return restored.State;
        }
        private EffectSpec Road(string route) => EffectSpec.Create(LuaDomainEffectTypeIds.PlaceRoad, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "routeId", NormalizedValue.CreateString(route)), 1);
        private string StoreZone(GameState state)
        {
            string carrier = state.ItemZones.Items.Single(item => item.TypeId == "core.basic_character_card" && item.ControllerSubject.PlayerId == 1).ItemId;
            var declaration = new CardZoneDefinition { LocalZoneId = "stored_cubes", ZoneTemplateId = "core.zone.card_cube_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
                AcceptedTypeIds = new List<string> { "core.cube" }, CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered, VisibilityPolicyId = ItemZoneVisibility.Public, EntryPolicyId = "core.removed_owned_influence", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, carrier, declaration, out var zone, out var reason), Is.True, reason); return zone;
        }
        private void RegisterDestinationResponse(string eventType, string zone, bool wait = false)
        {
            registry.Register(new EffectRegistration("test.influence.destination", context =>
            {
                if (wait && context.Node.FlowStage == "") return EffectStepResult.Continue("awaiting_confirm").AddInteraction(new EffectInteractionSpec { InteractionTypeId = "test.confirm", AnsweringPlayerId = 1, Visibility = "owner", PromptKey = "test.confirm", MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id", CandidateIds = { "accept" } });
                var result = new InfluenceDestinationResolver().Resolve(context, Text(context.Node.NormalizedArguments, "itemId"), zone);
                return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(result.Changed)));
            }, EffectExecutorKind.IntrinsicFlow, "v1"));
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.capture", eventType, "", "test.capture", context => new List<EffectSpec> {
                EffectSpec.Create("test.influence.destination", Object("itemId", NormalizedValue.CreateString(Value(context.Event.Payload, "affectedItemIds").Items[0].StringValue)), 1) }));
        }

        [Test] public void PlacementUsesRealSupplyItemAndOneEvent()
        {
            var state = State(); var all = state.ItemZones.Items.Select(item => item.ItemId).ToArray(); var node = Place(state, 1, "location:quay:0");
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(state.ItemZones.Items.Select(item => item.ItemId), Is.EqualTo(all));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(7)); Assert.That(Value(node.NormalizedResult, "affectedItemIds").Items.Single().StringValue, Is.EqualTo(PlacedId(node)));
            Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == InfluenceEventTypeIds.InfluencePlaced), Is.EqualTo(1));
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void RoutePlacementAndMovementRemainLegalWhenAllCoveredLocationsAreClosed(int players)
        {
            var state = State(players); state.Map.OpenLocationIds.Clear();
            const string slot = "route:channel_0:0";
            Assert.That(service.GetLegalPlacementSlotIds(state, 1), Does.Contain(slot));
            int supply = state.FindPlayer(1).InfluenceSupply;
            var placed = Place(state, 1, slot);
            Assert.That(placed.Status, Is.EqualTo(EffectNodeStatus.Completed));
            string id = PlacedId(placed);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(supply - 1));
            Assert.That(service.GetLegalMovementSlotIds(state, id), Does.Contain("route:channel_1:0"));
            var moved = Run(state, InfluenceEffectSpecFactory.MoveInfluence(1, id, "route:channel_1:0"));
            Assert.That(moved.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.Map.Influences.Single().SlotId, Is.EqualTo("route:channel_1:0"));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluencePlaced), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceMoved), Is.EqualTo(1));
        }
        [Test] public void ResourcePointPlacementStillRequiresIndicatorAndOpenLocation()
        {
            var state = State();
            var token = state.ItemZones.Items.Single(i => i.TypeId == "core.resource_point_token" &&
                i.ZoneId == Child(state, "mine", "core.zone.map_location", "core.zone.resource_point_markers").ZoneId);
            SetPosition(state, token.ItemId, FindZone(state, "retired").ZoneId);
            Assert.That(service.GetLegalPlacementSlotIds(state, 1), Does.Not.Contain("location:mine:0"));
            Assert.That(Place(state, 1, "location:mine:0").Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(service.GetLegalPlacementSlotIds(state, 1), Does.Not.Contain("location:closed:0"));
            Assert.That(Place(state, 1, "location:closed:0").Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(state.Map.Influences, Is.Empty);
        }
        [Test] public void RouteEligibilityPreservesOccupancyRoadAndPlacementProhibition()
        {
            var state = State(); state.Map.OpenLocationIds.Clear();
            Place(state, 2, "route:channel_0:0");
            Run(state, Road("channel_1"));
            service.PlacementAllowed = (s, slot) => slot != "route:channel_2:0";
            foreach (var slot in new[] { "route:channel_0:0", "route:channel_1:0", "route:channel_2:0" })
            {
                Assert.That(service.GetLegalPlacementSlotIds(state, 1), Does.Not.Contain(slot));
                Assert.That(Place(state, 1, slot).Status, Is.EqualTo(EffectNodeStatus.Failed));
            }
            Assert.That(state.Map.Influences.Single().PlayerId, Is.EqualTo(2));
        }
        [Test] public void CompanyAndOpponentCityRestrictionsRemainLocalToResourcePoints()
        {
            var state = State();
            var city = state.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == 2);
            SetPosition(state, city.ItemId, Child(state, "mine", "core.zone.map_location", "core.zone.city_dock").ZoneId);
            Assert.That(service.CanPlace(state, 1, "location:mine:0").IsValid, Is.False);
            Assert.That(Place(state, 1, "location:mine:0").Status, Is.EqualTo(EffectNodeStatus.Failed));
            var company = ItemSubjectRef.Company("stonehoove_security");
            Assert.That(service.ValidateItemPlacement(state, company, "location:mine:0").Succeeded, Is.False);
            Assert.That(service.ValidateItemPlacement(state, company, "route:channel_0:0").Succeeded, Is.False);
            Assert.That(service.ValidateItemPlacement(state, company, "location:pier:0").Succeeded, Is.True);
        }
        [Test] public void TwoPlayerInitialInfluenceOffersRemoteAndClosedRegionRoutesAndCommitsOnce()
        {
            map = StaticMapDefinitions.CreateThreePlayerMap();
            var query = new MapQueryService(map); service = new InfluenceService(query); registry = new EffectRegistry();
            InfluenceEffectExecutor.Register(registry, service); ExpansionSetupFlow.Register(registry);
            var state = State(); state.StartPlayerId = 1; state.Map.OpenLocationIds.Clear();
            state.Map.OpenLocationIds.AddRange(new[] { "A-01", "B-02" });
            var city = state.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == 2);
            SetPosition(state, city.ItemId, Child(state, "A-01", "core.zone.map_location", "core.zone.city_dock").ZoneId);
            var node = Run(state, ExpansionSetupFlow.InitialInfluenceSpec(2));
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(request.CandidateIds, Is.EquivalentTo(service.GetLegalPlacementSlotIds(state, 2)));
            Assert.That(request.CandidateIds, Does.Contain("location:B-02:0"));
            var remoteRoute = map.Routes.First(r => !r.CoveredLocationIds.Contains("A-01") && r.FromLocationId != "A-01" && r.ToLocationId != "A-01" &&
                map.Locations.Any(l => l.IsRedZone && (r.CoveredLocationIds.Contains(l.LocationId) || r.FromLocationId == l.LocationId || r.ToLocationId == l.LocationId)));
            string slot = InfluenceService.GetRouteSlotId(remoteRoute.RouteId, 0);
            Assert.That(request.CandidateIds, Does.Contain(slot));
            int supply = state.FindPlayer(2).InfluenceSupply;
            Answer(state, slot); var engine = new EffectTreeExecutor(state, registry); engine.RunUntilQuiescent();
            Assert.That(engine.GetNode(node.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.Map.Influences.Single().SlotId, Is.EqualTo(slot));
            Assert.That(state.FindPlayer(2).InfluenceSupply, Is.EqualTo(supply - 1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluencePlaced), Is.EqualTo(1));
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void RegisteredMapAllowsClosedRedZoneRoutesButRejectsClosedRedZoneResourcePoints(int players)
        {
            map = players == 4 ? StaticMapDefinitions.CreateFourPlayerMap() : StaticMapDefinitions.CreateThreePlayerMap();
            var query = new MapQueryService(map); service = new InfluenceService(query); registry = new EffectRegistry();
            InfluenceEffectExecutor.Register(registry, service);
            var state = State(players); state.Map.OpenLocationIds.Clear();
            var red = map.Locations.First(l => l.IsRedZone);
            var route = map.Routes.First(r => r.CoveredLocationIds.Contains(red.LocationId) || r.FromLocationId == red.LocationId || r.ToLocationId == red.LocationId);
            string slot = InfluenceService.GetRouteSlotId(route.RouteId, 0);
            Assert.That(service.GetLegalPlacementSlotIds(state, 1), Does.Contain(slot));
            Assert.That(Place(state, 1, slot).Status, Is.EqualTo(EffectNodeStatus.Completed));
            string pointSlot = InfluenceService.GetLocationSlotId(red.LocationId, 0);
            Assert.That(service.GetLegalPlacementSlotIds(state, 1), Does.Not.Contain(pointSlot));
            Assert.That(Place(state, 1, pointSlot).Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(state.Map.Influences.Single().SlotId, Is.EqualTo(slot));
        }
        [TestCase(2, 1)] [TestCase(3, 2)] [TestCase(4, 2)]
        public void InitialInfluenceDoesNotOpenForStartingPlayerOrLargerGames(int players, int player)
        {
            var state = State(players); state.StartPlayerId = 1; ExpansionSetupFlow.Register(registry);
            Assert.That(Run(state, ExpansionSetupFlow.InitialInfluenceSpec(player)).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
            Assert.That(state.Map.Influences, Is.Empty);
        }
        [Test] public void TrueRemovalReturnsToOriginalOwnersSupplyOnce()
        {
            var state = State(); string id = PlacedId(Place(state, 2, "location:mine:0"));
            var node = Run(state, InfluenceEffectSpecFactory.RemoveInfluence(1, id)); state = Restore(state);
            Assert.That(state.FindPlayer(2).InfluenceSupply, Is.EqualTo(8)); Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(8));
            Assert.That(Value(node.NormalizedResult, "removedItemIds").Items.Single().StringValue, Is.EqualTo(id)); Assert.That(Value(node.NormalizedResult, "originalSubjectKeys").Items.Single().StringValue, Is.EqualTo("player:2"));
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False); Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.EqualTo(1));
        }
        [Test] public void ReplacementPreservesOldItemAndDistinguishesDepartureEvent()
        {
            var state = State(); string id = CompanyPlace(state, "mine"); var all = state.ItemZones.Items.Select(item => item.ItemId).ToArray();
            var node = Run(state, InfluenceEffectSpecFactory.ReplaceInfluence(1, id));
            Assert.That(Value(node.NormalizedResult, "removedItemIds").Items, Is.Empty); Assert.That(Value(node.NormalizedResult, "replacedItemIds").Items.Single().StringValue, Is.EqualTo(id));
            Assert.That(Value(node.NormalizedResult, "originalSubjectKeys").Items.Single().StringValue, Is.EqualTo("company:stonehoove_security"));
            Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.Zero); Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == InfluenceEventTypeIds.InfluenceReplaced), Is.EqualTo(1));
            Assert.That(state.ItemZones.Items.Select(item => item.ItemId), Is.EqualTo(all)); Assert.That(state.ItemZones.Items.Single(item => item.ItemId == id).OwnerSubject.Kind, Is.EqualTo(ItemSubjectKind.Company));
            Assert.That(Value(node.NormalizedResult, "actualEventTypes").Items.Select(item => item.StringValue), Is.EqualTo(new[] { InfluenceEventTypeIds.InfluencePlaced, InfluenceEventTypeIds.InfluenceReplaced }));
        }
        [TestCase(false)][TestCase(true)] public void CardStorageReceivesRealCompanyCubeAndRecoversPendingDestination(bool replacement)
        {
            var state = State(); string zone = StoreZone(state); string id = CompanyPlace(state, "mine"); var all = state.ItemZones.Items.Select(item => item.ItemId).ToArray();
            RegisterDestinationResponse(replacement ? InfluenceEventTypeIds.InfluenceReplaced : InfluenceEventTypeIds.InfluenceRemoved, zone, true);
            var node = Run(state, replacement ? InfluenceEffectSpecFactory.ReplaceInfluence(1, id) : InfluenceEffectSpecFactory.RemoveInfluence(1, id));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Blocked)); Assert.That(state.ItemZones.Zones.Single(value => value.ZoneId == state.ItemZones.Items.Single(item => item.ItemId == id).ZoneId).OwnerEffectId, Is.EqualTo(node.EffectId));
            state = Restore(state); Answer(state, "accept"); state = Restore(state);
            Assert.That(state.ItemZones.Items.Single(item => item.ItemId == id).ZoneId, Is.EqualTo(zone)); Assert.That(state.ItemZones.Items.Select(item => item.ItemId), Is.EqualTo(all));
            Assert.That(state.ItemZones.Items.Count(item => item.ZoneId == FindZone(state, "company_markers").ZoneId), Is.EqualTo(7));
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False); Assert.That(state.EffectRuntime.DispatchReceipts.Count(item => item.SubscriptionId == "test.capture"), Is.EqualTo(1));
        }
        [Test] public void AlternativeMovementPreservesItemAndNeverEmitsRemoved()
        {
            var state = State(); string id = PlacedId(Place(state, 2, "location:mine:0")); var city = state.ItemZones.Items.Single(item => item.PhysicalKind == ItemPhysicalKind.MobileCity && item.OwnerSubject.PlayerId == 2);
            SetPosition(state, city.ItemId, Child(state, "quay", "core.zone.map_location", "core.zone.city_dock").ZoneId);
            var node = Run(state, InfluenceEffectSpecFactory.MoveInfluence(1, id, "location:quay:0")); Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(new ItemZoneQuery().FindItem(state, id).SlotId, Is.EqualTo("location:quay:0")); Assert.That(Value(node.NormalizedResult, "removedItemIds").Items, Is.Empty); Assert.That(state.EffectRuntime.RuleEvents.Any(item => item.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.False);
        }
        [Test] public void MovementChecksOriginalOwnersCityRestriction()
        {
            var state = State(); string id = PlacedId(Place(state, 2, "location:mine:0")); var city = state.ItemZones.Items.Single(item => item.PhysicalKind == ItemPhysicalKind.MobileCity && item.OwnerSubject.PlayerId == 1);
            SetPosition(state, city.ItemId, Child(state, "quay", "core.zone.map_location", "core.zone.city_dock").ZoneId);
            Assert.That(Run(state, InfluenceEffectSpecFactory.MoveInfluence(1, id, "location:quay:0")).Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(new ItemZoneQuery().FindItem(state, id).SlotId, Is.EqualTo("location:mine:0"));
        }
        [Test] public void DestinationSelectionRestoresAndPreservesOriginalPosition()
        {
            var state = State(); string id = PlacedId(Place(state, 2, "location:mine:0"));
            var node = Run(state, InfluenceEffectSpecFactory.MoveInfluence(1, id, "", InfluenceCauseKinds.MoveCity));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Blocked)); state = Restore(state); Answer(state, "location:pier:0");
            var result = state.EffectRuntime.EffectNodes.Single(item => item.EffectId == node.EffectId).NormalizedResult;
            Assert.That(Text(Value(result, "originalPosition"), "slotId"), Is.EqualTo("location:mine:0")); Assert.That(Text(Value(result, "destination"), "slotId"), Is.EqualTo("location:pier:0"));
            Assert.That(new ItemZoneQuery().FindItem(state, id).OwnerSubject.PlayerId, Is.EqualTo(2)); Assert.That(state.EffectRuntime.RuleEvents.Any(item => item.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.False);
        }
        [TestCase(false)][TestCase(true)] public void BatchMovementCommitsEveryInstanceOrNone(bool occupied)
        {
            var state = State(); string first = PlacedId(Place(state, 1, "location:mine:0")), second = PlacedId(Place(state, 2, "location:pier:0"));
            if (occupied) Place(state, 1, "location:quay:1");
            var node = Run(state, InfluenceEffectSpecFactory.MoveInfluences(1, new List<KeyValuePair<string, string>> {
                new KeyValuePair<string, string>(first, "location:quay:0"), new KeyValuePair<string, string>(second, "location:quay:1") }));
            Assert.That(node.Status, Is.EqualTo(occupied ? EffectNodeStatus.Failed : EffectNodeStatus.Completed));
            Assert.That(new ItemZoneQuery().FindItem(state, first).SlotId, Is.EqualTo(occupied ? "location:mine:0" : "location:quay:0"));
            Assert.That(new ItemZoneQuery().FindItem(state, second).SlotId, Is.EqualTo(occupied ? "location:pier:0" : "location:quay:1"));
            Assert.That(node.PositionChanges.Count, Is.EqualTo(occupied ? 0 : 2));
            if (!occupied) Assert.That(Value(node.NormalizedResult, "originalSubjectKeys").Items.Select(item => item.StringValue), Is.EqualTo(new[] { "player:1", "player:2" }));
            Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == InfluenceEventTypeIds.InfluenceMoved), Is.EqualTo(occupied ? 0 : 1));
        }
        [Test] public void FailedReplacementBecomesOneTrueRemoval()
        {
            var state = State(); string original = CompanyPlace(state, "mine");
            for (int index = 0; index < 8; index++) Place(state, 1, "route:channel_" + index + ":0");
            var node = Run(state, InfluenceEffectSpecFactory.ReplaceInfluence(1, original));
            Assert.That(Text(node.NormalizedResult, "outcome"), Is.EqualTo("removed_only")); Assert.That(Value(node.NormalizedResult, "replacedItemIds").Items, Is.Empty);
            Assert.That(Value(node.NormalizedResult, "removedItemIds").Items.Single().StringValue, Is.EqualTo(original)); Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Any(item => item.EventType == InfluenceEventTypeIds.InfluenceReplaced), Is.False);
        }
        [Test] public void DuplicateDestinationAndLaterCardExitDoNotRemoveAgain()
        {
            var state = State(); string zone = StoreZone(state), id = CompanyPlace(state, "mine"); RegisterDestinationResponse(InfluenceEventTypeIds.InfluenceRemoved, zone);
            Run(state, InfluenceEffectSpecFactory.RemoveInfluence(1, id)); var resolver = new InfluenceDestinationResolver();
            registry.Register("test.destination.repeat", context => { Assert.That(resolver.Resolve(context, id, zone).Changed, Is.False); return EffectStepResult.Completed(); });
            Run(state, EffectSpec.Create("test.destination.repeat", playerId: 1));
            var spec = ItemTransferEffectExecutor.MoveSpec(1, new[] { id }, zone, FindZone(state, "company_markers").ZoneId, "unordered", 1, "core.cube_storage");
            Assert.That(Run(state, spec).Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.EqualTo(1));
            Assert.That(state.ItemZones.Items.Single(item => item.ItemId == id).ZoneId, Is.EqualTo(FindZone(state, "company_markers").ZoneId));
        }
        [Test] public void CityMovementReplacementChoosesAndMovesSameCube()
        {
            var state = State(); state.Phase = GamePhase.ActionRound1; state.CurrentPlayerId = 1;
            var city = state.ItemZones.Items.Single(item => item.PhysicalKind == ItemPhysicalKind.MobileCity && item.OwnerSubject.PlayerId == 1);
            SetPosition(state, city.ItemId, Child(state, "quay", "core.zone.map_location", "core.zone.city_dock").ZoneId);
            string id = PlacedId(Place(state, 2, "location:mine:0"));
            var mapQuery = new MapQueryService(map); var travel = new TravelCostService(mapQuery);
            CityMoveEffectExecutor.Register(registry, mapQuery, service, new CityMovementService(mapQuery, service, travel), travel, new ResourceTokenService());
            var spec = CityMoveEffectSpecFactory.Move(1, "mine", waiveBaseCost: true, consumeMainAction: false);
            InfluenceDestinationResolver.Set(spec.NormalizedArguments, "clearInfluencePolicy", NormalizedValue.CreateString("move"));
            var node = Run(state, spec); Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Blocked)); state = Restore(state); Answer(state, "location:pier:0");
            Assert.That(state.EffectRuntime.EffectNodes.Single(item => item.EffectId == node.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("mine")); Assert.That(new ItemZoneQuery().FindItem(state, id).SlotId, Is.EqualTo("location:pier:0"));
            Assert.That(state.EffectRuntime.RuleEvents.Any(item => item.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.False); Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == CityMoveEventTypeIds.CityMoveCompleted), Is.EqualTo(1));
        }
        [TestCase("PositionChanges")][TestCase("ContinuationData")] public void RecoveryRejectsMissingMapFacts(string field)
        {
            var state = State(); string zone = StoreZone(state), id = CompanyPlace(state, "mine"); RegisterDestinationResponse(InfluenceEventTypeIds.InfluenceRemoved, zone, true);
            var node = Run(state, InfluenceEffectSpecFactory.RemoveInfluence(1, id));
            var json = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            var raw = (JArray)json["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"]; ((JObject)raw.Single(item => (string)item["EffectId"] == node.EffectId)).Remove(field);
            Assert.That(HostRecoveryService.RecoverJson(json.ToString(), registry).Succeeded, Is.False);
        }
        [Test] public void RecoveryRejectsLostPendingCubeIdentity()
        {
            var state = State(); string zone = StoreZone(state), id = CompanyPlace(state, "mine"); RegisterDestinationResponse(InfluenceEventTypeIds.InfluenceRemoved, zone, true);
            var node = Run(state, InfluenceEffectSpecFactory.RemoveInfluence(1, id));
            var json = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            var raw = (JArray)json["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"];
            var continuation = (JArray)raw.Single(item => (string)item["EffectId"] == node.EffectId)["ContinuationData"]["Properties"];
            continuation.Single(item => (string)item["Name"] == "pendingItemId")["Value"]["StringValue"] = "missing.cube";
            Assert.That(HostRecoveryService.RecoverJson(json.ToString(), registry).Succeeded, Is.False);
        }
        [Test] public void OpponentReplacementExcludesOriginalCompanyAfterRestore()
        {
            var state = State(3); string company = CompanyPlace(state, "mine"), otherCompany = CompanyPlace(state, "pier"); string opponent = PlacedId(Place(state, 2, "location:quay:0")); Place(state, 3, "route:channel_1:0");
            var node = Run(state, Select("replace", "opponent")); Answer(state, company); state = Restore(state);
            var request = state.EffectRuntime.InteractionRequests.Last(item => item.Status == "open"); Assert.That(request.CandidateIds, Does.Not.Contain(otherCompany)); Assert.That(request.CandidateIds, Does.Contain(opponent));
            Answer(state, opponent); Assert.That(state.EffectRuntime.EffectNodes.Single(item => item.EffectId == node.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == InfluenceEventTypeIds.InfluenceReplaced), Is.EqualTo(2));
        }
        [Test] public void PlayerRemovalExcludesCompaniesAndAlreadyProcessedPlayer()
        {
            var state = State(); CompanyPlace(state, "mine"); string first = PlacedId(Place(state, 2, "location:quay:0")), other = PlacedId(Place(state, 2, "location:pier:0")); string own = PlacedId(Place(state, 1, "route:channel_1:0"));
            Run(state, Select("remove", "player", 4)); Assert.That(state.EffectRuntime.InteractionRequests.Last(item => item.Status == "open").CandidateIds.Count, Is.EqualTo(3));
            Answer(state, first); var request = state.EffectRuntime.InteractionRequests.Last(item => item.Status == "open"); Assert.That(request.CandidateIds, Is.EqualTo(new[] { own })); Assert.That(request.CandidateIds, Does.Not.Contain(other)); Answer(state, own);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(item => item.Status == "open"), Is.False);
        }
        [Test] public void TwoPlayersProcessOnlyOneOpponentAndContinueSibling()
        {
            var state = State(); string id = PlacedId(Place(state, 2, "location:mine:0")); Place(state, 2, "location:pier:0");
            var root = EffectSpec.Create("test.siblings", playerId: 1); registry.Register("test.siblings", context => EffectStepResult.Completed().AddChild(Select("replace", "opponent")).AddChild(ResourceEffectSpecFactory.GainScore(1, 3)));
            Run(state, root); Answer(state, id); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(3)); Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == InfluenceEventTypeIds.InfluenceReplaced), Is.EqualTo(1));
        }
        [TestCase(0, false)][TestCase(4, true)] public void PaymentFailureAndNoTargetsRemainDistinct(int gold, bool success)
        {
            var state = State(); state.FindPlayer(1).Resources.GoldVoucher = gold;
            Run(state, EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1, ResourceType.GoldVoucher, 4) }, new[] { Select("replace", "opponent"), ResourceEffectSpecFactory.GainScore(1, 2) }));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(success ? 2 : 0)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero); Assert.That(state.EffectRuntime.InteractionRequests.Any(item => item.Status == "open"), Is.False);
        }
        [Test] public void RequiredRemovalCannotAwardWithEmptyTargets()
        {
            var state = State(); Run(state, EffectSpec.Condition(new[] { Select("remove", "self", 2, 2) }, new[] { ResourceEffectSpecFactory.GainScore(1, 5) })); Assert.That(state.FindPlayer(1).Score, Is.Zero);
        }
        [Test] public void RoadCoversCubeWithoutRemovalOrSupplyReturn()
        {
            var state = State(); string id = PlacedId(Place(state, 1, "route:channel_1:0")); int supply = state.FindPlayer(1).InfluenceSupply;
            Run(state, Road("channel_1")); Assert.That(new ItemZoneQuery().IsInfluence(state, id), Is.False); Assert.That(state.ItemZones.Zones.Single(zone => zone.ZoneId == new ItemZoneQuery().FindItem(state, id).ZoneId).TemplateId, Is.EqualTo("core.zone.covered_cubes"));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(supply)); Assert.That(service.CountInRegion(state, 1, "harbor", false), Is.Zero); Assert.That(state.EffectRuntime.RuleEvents.Any(item => item.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.False);
            Assert.That(new InfluenceSubjectQuery().GetCandidateItemIds(state, 1, "any"), Is.Empty); Assert.That(service.CanPlace(state, 1, "route:channel_1:1").IsValid, Is.False);
            Assert.That(new RouteTollService(new MapQueryService(map)).IsPaymentRequired(state, "channel_1", 2, RouteTollPaymentKeyMode.RouteId), Is.False);
        }
        [Test] public void EmptyRoadAndNinthRoadUseUnlimitedSupply()
        {
            var state = State(); for (int index = 0; index < 9; index++) Assert.That(Run(state, Road("channel_" + index)).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.Map.RoadRouteIds.Count, Is.EqualTo(9)); Assert.That(state.ItemZones.Items.Count(item => item.TypeId == "core.road_token"), Is.EqualTo(9)); Assert.That(service.GetRoadCandidateRouteIds(state), Does.Not.Contain("channel_1"));
            int count = state.ItemZones.Items.Count; Assert.That(Run(state, Road("channel_1")).FailureReason, Is.EqualTo("road_already_present")); Assert.That(state.ItemZones.Items.Count, Is.EqualTo(count));
        }
        [Test] public void ExplicitRemovalBeforeRoadReturnsCubeAndEmitsOnlyOneRemoval()
        {
            var state = State(); string id = PlacedId(Place(state, 2, "route:channel_1:0")); Run(state, InfluenceEffectSpecFactory.RemoveInfluence(1, id)); Run(state, Road("channel_1"));
            Assert.That(state.FindPlayer(2).InfluenceSupply, Is.EqualTo(8)); Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.EqualTo(1)); Assert.That(state.ItemZones.Items.Any(item => item.ZoneId == Child(state, "channel_1", "core.zone.map_route", "core.zone.covered_cubes").ZoneId), Is.False);
        }
        [Test] public void TokenUsesAdjacentOpenLocationsWithoutCityRules()
        {
            var state = State(); string token = state.ItemZones.Items.Single(item => item.TypeId == "infected.gavial_token").ItemId; SetPosition(state, token, Child(state, "quay", "core.zone.map_location", "core.zone.map_tokens").ZoneId);
            string influence = PlacedId(Place(state, 2, "location:mine:0")); int gold = state.FindPlayer(1).Resources.GoldVoucher;
            var profile = new TokenMovementProfileRegistry(new MapQueryService(map)); Assert.That(profile.GetDestinationLocationIds(state, token, "core.adjacent_location_token_move"), Is.EquivalentTo(new[] { "mine", "pier" }));
            var spec = EffectSpec.Create(LuaDomainEffectTypeIds.OperateToken, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "tokenItemId", NormalizedValue.CreateString(token), "operation", NormalizedValue.CreateString("move"), "destinationLocationId", NormalizedValue.CreateString("mine"), "operationProfileId", NormalizedValue.CreateString("core.adjacent_location_token_move")), 1);
            Run(state, spec); state = Restore(state); Assert.That(new ItemZoneQuery().IsInfluence(state, influence), Is.True); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold)); Assert.That(state.FindPlayer(1).HasMovedCityThisRound, Is.False);
            Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == "token.moved"), Is.EqualTo(1)); Assert.That(state.EffectRuntime.RuleEvents.Any(item => item.EventType == CityMoveEventTypeIds.CityMoveCompleted), Is.False);
        }
        [Test] public void UnregisteredTokenProfileDoesNotGrantCityMovement()
        {
            var state = State(); string token = state.ItemZones.Items.Single(item => item.TypeId == "infected.gavial_token").ItemId;
            Assert.Throws<InvalidOperationException>(() => new TokenMovementProfileRegistry(new MapQueryService(map)).GetDestinationLocationIds(state, token, "core.mobile_city_move"));
        }
        [Test] public void TokenCandidatesAndCommitShareInjectedDestinationRestriction()
        {
            var state = State(); string token = state.ItemZones.Items.Single(item => item.TypeId == "infected.gavial_token").ItemId;
            SetPosition(state, token, Child(state, "quay", "core.zone.map_location", "core.zone.map_tokens").ZoneId);
            var query = new MapQueryService(map); var profiles = new TokenMovementProfileRegistry(query) { DestinationAllowed = (current, item, location) => location == "pier" };
            Assert.That(profiles.GetDestinationLocationIds(state, token, "core.adjacent_location_token_move"), Is.EqualTo(new[] { "pier" }));
            registry = new EffectRegistry(); MapOperationEffectExecutor.Register(registry, query, profiles);
            var spec = EffectSpec.Create(LuaDomainEffectTypeIds.OperateToken, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "tokenItemId", NormalizedValue.CreateString(token),
                "operation", NormalizedValue.CreateString("move"), "destinationLocationId", NormalizedValue.CreateString("mine"), "operationProfileId", NormalizedValue.CreateString("core.adjacent_location_token_move")), 1);
            Assert.That(Run(state, spec).FailureReason, Is.EqualTo("token_destination_rejected")); Assert.That(TokenMovementProfileRegistry.GetLocationId(state, token), Is.EqualTo("quay"));
            InfluenceDestinationResolver.Set(spec.NormalizedArguments, "destinationLocationId", NormalizedValue.CreateString("pier"));
            Assert.That(Run(state, spec).Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(TokenMovementProfileRegistry.GetLocationId(state, token), Is.EqualTo("pier")); Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == "token.moved"), Is.EqualTo(1));
        }
        [Test] public void CompanyPlacementRequiresOpenMarkerPointWithNoInfluenceOrCity()
        {
            var state = State(); var query = new InfluenceSubjectQuery(); var mapQuery = new MapQueryService(map);
            Assert.That(query.GetLegalResourcePointSlotIds(state, mapQuery), Does.Not.Contain("location:closed:0")); Place(state, 1, "location:quay:0");
            var city = state.ItemZones.Items.Single(item => item.PhysicalKind == ItemPhysicalKind.MobileCity && item.OwnerSubject.PlayerId == 1); SetPosition(state, city.ItemId, Child(state, "pier", "core.zone.map_location", "core.zone.city_dock").ZoneId);
            Assert.That(query.GetLegalResourcePointSlotIds(state, mapQuery), Is.EquivalentTo(new[] { "location:mine:0", "location:mine:1" }));
            Assert.That(query.GetLegalResourcePointSlotIds(state, mapQuery, slot => !slot.StartsWith("location:mine:", StringComparison.Ordinal)), Is.Empty);
        }
        [Test] public void NormalCompanyCollectionDeduplicatesAndExtraDoesNotInheritContract()
        {
            var state = State(); CompanyPlace(state, "mine"); Place(state, 1, "location:mine:1"); CompanyPlace(state, "pier");
            var query = new InfluenceSubjectQuery(); Assert.That(query.GetCollectionLocationIds(state, 1, "normal", true), Is.EquivalentTo(new[] { "mine", "pier" })); Assert.That(query.GetCollectionLocationIds(state, 1, "extra", true), Is.EqualTo(new[] { "mine" }));
            Assert.That(query.CountCompanyInRegion(state, "upland"), Is.EqualTo(1)); Assert.That(service.CountInRegion(state, 1, "upland", true), Is.EqualTo(1));
            Assert.That(service.CountInRegion(state, -1, "upland", true), Is.Zero); Assert.That(new InfluenceQueryService(new MapQueryService(map)).ListByPlayer(state, -1), Is.Empty);
        }
        [Test] public void RouteTollsDeduplicateActualRoutesAndAllowBankRecipient()
        {
            var state = State(); var tolls = new RouteTollService(new MapQueryService(map)); var routes = new[] { "channel_1", "channel_1", "channel_2" };
            Assert.That(tolls.TryBuildPaymentPlan(state, 1, routes, null, RouteTollPaymentKeyMode.RouteId, out var payments).IsValid, Is.True);
            Assert.That(payments.Count, Is.EqualTo(2)); Assert.That(payments.All(payment => payment.PaidToSupply), Is.True); Assert.That(tolls.SumPayments(payments), Is.EqualTo(4));
        }
        [TestCase(1, 1, 2, false)][TestCase(1, 2, 2, true)][TestCase(2, 1, 3, true)] public void ExpiryUsesActionRoundSequence(int round, int action, int expires, bool expected)
        {
            var state = State(); state.Round = round; state.ActionRound = action; Assert.That(TokenMovementProfileRegistry.IsExpiredBeforeActionRound(state, expires), Is.EqualTo(expected));
        }
        [Test] public void RouteRegionHasOneOwnerAcrossBoundary()
        {
            var state = State(); string id = PlacedId(Place(state, 1, "route:channel_1:0")); Assert.That(InfluenceSubjectQuery.GetRegionId(state, id), Is.EqualTo("harbor")); Assert.That(service.CountInRegion(state, 1, "upland", false), Is.Zero);
        }
        [Test] public void LuaRoadTokenAndSubjectFiltersCompileAndExecuteProductionRegistry()
        {
            string lua = "return function(ctx) return { Effect.PlaceRoad({apiVersion=1,player=ctx.playerId,routeId='channel_1'}), Effect.SelectInfluence({player=ctx.playerId,operation='remove',subjectFilter='player',excludedSubjectKeys={'player:2'},distinctPolicy='subject',count=2}) } end";
            var definition = new LuaScriptDefinition("content.influence", "test.map", "test.map", "1.0.0", lua, LuaContentHasher.ComputeSha256(lua));
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(definition, new LuaInvocationContext("event.map", "event.test", "1", 0, "1.0.0", 2, 0, 0)); Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var failure), Is.True, failure?.Diagnostic);
            var state = State(); foreach (var spec in specs) Run(state, spec); Assert.That(state.Map.RoadRouteIds, Does.Contain("channel_1"));
        }
        [TestCase("p1")][TestCase("1")]
        public void LuaMapOperationsNormalizeExecutingPlayerAndKeepRealTokenMovement(string player)
        {
            var state = State(); string token = state.ItemZones.Items.Single(item => item.TypeId == "infected.gavial_token").ItemId;
            SetPosition(state, token, Child(state, "quay", "core.zone.map_location", "core.zone.map_tokens").ZoneId);
            string source = "return function(ctx) return { Effect.PlaceRoad({apiVersion=1,player=ctx.playerId,routeId='channel_1'}), " +
                "Effect.OperateToken({apiVersion=1,player=ctx.playerId,tokenItemId='" + token + "',operation='move',destinationLocationId='pier',operationProfileId='core.adjacent_location_token_move'}) } end";
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("test.map.player", "test.map.player", "test.map.player", "1.0.0", source, LuaContentHasher.ComputeSha256(source)),
                new LuaInvocationContext("test.event", "test.event", player, 0, "1.0.0", 2, 0, 0));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(result, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            foreach (var spec in specs) { Assert.That(Text(spec.NormalizedArguments, "player"), Is.EqualTo("1")); Run(state, spec); }
            state = Restore(state); Assert.That(state.Map.RoadRouteIds, Does.Contain("channel_1"));
            Assert.That(TokenMovementProfileRegistry.GetLocationId(state, token), Is.EqualTo("pier")); Assert.That(state.EffectRuntime.RuleEvents.Count(item => item.EventType == "token.moved"), Is.EqualTo(1));
        }
        [TestCase("Effect.PlaceRoad({apiVersion=1,player='p2',routeId='channel_1'})")]
        [TestCase("Effect.OperateToken({apiVersion=1,player='p2',tokenItemId='token',operation='move',destinationLocationId='pier',operationProfileId='core.adjacent_location_token_move'})")]
        public void LuaMapOperationsStillRejectAnotherExecutingPlayer(string expression)
        {
            string source = "return function(ctx) return { " + expression + " } end";
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("test.map.permission", "test.map.permission", "test.map.permission", "1.0.0", source, LuaContentHasher.ComputeSha256(source)),
                new LuaInvocationContext("test.event", "test.event", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(result.IsSuccess, Is.False); Assert.That(result.FailureCode, Is.EqualTo(LuaFailureCode.InvalidTarget));
        }
    }
}
