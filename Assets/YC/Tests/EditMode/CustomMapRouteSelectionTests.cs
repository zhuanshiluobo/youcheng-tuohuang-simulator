using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Exploration;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Tests.EditMode
{
    public sealed class CustomMapRouteSelectionTests
    {
        [Test]
        public void CustomMap_HarvestTypedAnswerAfterRestorePaysAndCompletesOnce()
        {
            var map = CreateMap();
            var state = CreateState(map);
            var registry = CreateRegistry(map);
            var executor = new EffectExecutor(state, registry);
            Assert.That(executor.TryCreateRoot(RouteResourceSelectionEffectExecutor.CreateHarvest(1, false), out var ownerId), Is.True, executor.LastDiagnostic);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = Selection(state);
            var requestId = request.GetStableInteractionId();
            var candidates = request.CandidateIds.ToArray();
            Assert.That(request.MaxSelections, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));

            var restored = JsonConvert.DeserializeObject<GameState>(JsonConvert.SerializeObject(state),
                new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });
            var restoredMap = new MapQueryService(CreateMap().Map);
            var restoredRegistry = CreateRegistry(restoredMap);
            var restoredRequest = Selection(restored);
            Assert.That(restoredRequest.GetStableInteractionId(), Is.EqualTo(requestId));
            Assert.That(restoredRequest.OwnerEffectId, Is.EqualTo(ownerId));
            Assert.That(restoredRequest.CandidateIds, Is.EqualTo(candidates));
            Submit(restored, restoredRegistry, restoredRequest, Answer(restoredRequest,
                RouteSelectionCandidateKind.Commit, "T", "S-T"));

            var owner = restored.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
            Assert.That(owner.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(restored.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(restored.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(restored.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
            Assert.That(restored.FindPlayer(1).HasCollectedResourcesThisRound, Is.True);
            Assert.That(RouteTollEscrowService.QueryBalance(restored, 1).Reserved, Is.Zero);
            Assert.That(owner.NormalizedResult.Properties.Single(p => p.Name == "paidRouteIds").Value.Items.Select(v => v.StringValue), Is.EqualTo(new[] { "S-T" }));
            var completed = JsonConvert.SerializeObject(restored);
            Assert.That(new EffectExecutor(restored, restoredRegistry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(JsonConvert.SerializeObject(restored), Is.EqualTo(completed));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
        }

        [TestCase("owned_round")]
        [TestCase("supplied_round")]
        [TestCase("explicit_registry")]
        public void Handler_CustomMapConstructorsOpenSelectionAndAcceptTypedCancel(string mode)
        {
            var map = CreateMap();
            var state = CreateState(map);
            state.Phase = GamePhase.ActionRound1;
            state.Map.ResourceTokens.Clear();
            state.Map.Influences.Clear();
            var service = new ExplorationService(map);
            Assert.That(service.MapQuery, Is.SameAs(map));
            ExploreLocationCommandHandler handler;
            var registry = CreateRegistry(map);
            if (mode == "owned_round") handler = new ExploreLocationCommandHandler(service);
            else
            {
                var round = new RoundAdvanceService(new RoundExecutionService(registry));
                handler = mode == "supplied_round" ? new ExploreLocationCommandHandler(service, round)
                    : new ExploreLocationCommandHandler(service, round, registry);
            }
            var command = new GameCommand { Kind = GameCommandKind.ExploreLocation, PlayerId = 1, CommandId = "custom_map_begin" };
            var result = mode == "explicit_registry" ? handler.BeginGrantedExplore(state, command, "T") : handler.Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            var request = Selection(state);
            var scope = Scope(request);
            Assert.That(request.CandidateIds, Does.Contain(RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Location, "T")));
            var ownerId = request.OwnerEffectId;
            Submit(state, registry, request, Answer(request, RouteSelectionCandidateKind.Cancel));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(state.Decks.EventDeckYellow, Is.EqualTo(new[] { "custom_map_event" }));
        }

        [TestCase(false, "route_selection_map_unknown")]
        [TestCase(true, "route_selection_map_mismatch")]
        public void UnknownOrMismatchedMap_RejectsBeforeSelectionAndResourceMutation(bool injected, string reason)
        {
            var map = CreateMap();
            var state = CreateState(map);
            state.MapId = "unknown_other_map";
            var before = JsonConvert.SerializeObject(state.Map);
            var registry = new EffectRegistry();
            if (injected) RouteResourceSelectionEffectExecutor.Register(registry, map);
            else RouteResourceSelectionEffectExecutor.Register(registry);
            var executor = new EffectExecutor(state, registry);
            Assert.That(executor.TryCreateRoot(RouteResourceSelectionEffectExecutor.CreateHarvest(1, false), out var ownerId), Is.True);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
            var owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
            Assert.That(owner.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(owner.FailureReason, Is.EqualTo(reason));
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
            Assert.That(JsonConvert.SerializeObject(state.Map), Is.EqualTo(before));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
            Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Register_KeepsFirstEnvironmentAndDoesNotPretendLateInjectionReplacesIt(bool injectedFirst)
        {
            var map = CreateMap();
            var state = CreateState(map);
            var registry = new EffectRegistry();
            if (injectedFirst) RouteResourceSelectionEffectExecutor.Register(registry, map);
            else RouteResourceSelectionEffectExecutor.Register(registry);
            var original = registry.Get(RouteResourceSelectionEffectExecutor.TypeId);
            if (injectedFirst) GenericLuaEffectExecutor.Register(registry);
            else RouteResourceSelectionEffectExecutor.Register(registry, map);
            Assert.That(registry.Get(RouteResourceSelectionEffectExecutor.TypeId), Is.SameAs(original));
            var executor = new EffectExecutor(state, registry);
            Assert.That(executor.TryCreateRoot(RouteResourceSelectionEffectExecutor.CreateHarvest(1, false), out var ownerId), Is.True);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
            if (injectedFirst) Assert.That(Selection(state).OwnerEffectId, Is.EqualTo(ownerId));
            else Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId).FailureReason, Is.EqualTo("route_selection_map_unknown"));
        }

        [Test]
        public void Handler_MismatchedMapRejectsWithoutCreatingEffectOrChangingState()
        {
            var map = CreateMap();
            var state = CreateState(map);
            state.Phase = GamePhase.ActionRound1;
            state.MapId = "unknown_other_map";
            var before = JsonConvert.SerializeObject(state);
            var handler = new ExploreLocationCommandHandler(new ExplorationService(map));
            var result = handler.Handle(state, new GameCommand { Kind = GameCommandKind.ExploreLocation, PlayerId = 1 });
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(result.Validation.Reason, Is.EqualTo("route_selection_map_mismatch"));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }

        private static MapQueryService CreateMap()
        {
            return new MapQueryService(new GameMapDefinition
            {
                MapId = "custom_route_map", MinPlayers = 2, MaxPlayers = 2,
                Locations = new List<MapLocationDefinition>
                {
                    new MapLocationDefinition { LocationId = "S", RegionId = "region", ResourceType = ResourceType.Iron, CanDockCity = true, InfluenceSlotCount = 2 },
                    new MapLocationDefinition { LocationId = "T", RegionId = "region", ResourceType = ResourceType.Iron, ResourceSlotCount = 1, InfluenceSlotCount = 2, EventSlotCount = 1 }
                },
                Routes = new List<MapRouteDefinition>
                {
                    new MapRouteDefinition { RouteId = "S-T", FromLocationId = "S", ToLocationId = "T", CoveredLocationIds = new List<string> { "S", "T" }, RegionId = "region", InfluenceSlotCount = 2, BaseCost = 2 }
                },
                Regions = new List<MapRegionDefinition>
                {
                    new MapRegionDefinition { RegionId = "region", LocationIds = new List<string> { "S", "T" }, RouteIds = new List<string> { "S-T" } }
                }
            });
        }

        private static GameState CreateState(IMapQueryService map)
        {
            var state = new GameState { GameId = "custom_map_route", MapId = map.Map.MapId, Phase = GamePhase.ResourceCollection, Round = 2, ActionRound = 1, CurrentPlayerId = 1,
                ContentDefinitions = new GameContentDefinitions(), Players = new List<PlayerState>
                {
                    new PlayerState { PlayerId = 1, CityLocationId = "S", InfluenceSupply = 6, ResourceCollectionStartGoldVoucher = 4, Resources = new ResourceSet { GoldVoucher = 4 } },
                    new PlayerState { PlayerId = 2, Resources = new ResourceSet() }
                } };
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, LocationId = "T", SlotId = InfluenceService.GetLocationSlotId("T", 0) });
            state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = "T", ResourceType = ResourceType.Iron, Amount = 1 });
            state.ContentDefinitions.Events.Add(new EventCardDefinition { CardId = "custom_map_event", Color = EventColor.Yellow, ChoiceRewards = new List<ResourceSet> { new ResourceSet() } });
            state.Decks.EventDeckYellow.Add("custom_map_event");
            return state;
        }

        private static EffectRegistry CreateRegistry(IMapQueryService map)
        {
            var registry = new EffectRegistry();
            RouteResourceSelectionEffectExecutor.Register(registry, map);
            return registry;
        }

        private static InteractionRequest Selection(GameState state) => state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection");
        private static string Scope(InteractionRequest request) => request.PromptParameters.Properties.Single(p => p.Name == "scopeKey").Value.StringValue;

        private static IReadOnlyList<string> Answer(InteractionRequest request, RouteSelectionCandidateKind kind, string location = "", string route = "")
        {
            var ids = new List<string> { RouteSelectionAnswerCodec.Encode(Scope(request), kind) };
            if (location.Length > 0) ids.Add(RouteSelectionAnswerCodec.Encode(Scope(request), RouteSelectionCandidateKind.Location, location));
            if (route.Length > 0) ids.Add(RouteSelectionAnswerCodec.Encode(Scope(request), RouteSelectionCandidateKind.RouteAndRecipient, route, "bank"));
            Assert.That(ids.All(request.CandidateIds.Contains), Is.True);
            Assert.That(new RouteSelectionAnswerCodec().TryParse(request, ids, out _).IsValid, Is.True);
            return ids;
        }

        private static void Submit(GameState state, EffectRegistry registry, InteractionRequest request, IReadOnlyList<string> ids)
        {
            var executor = new EffectExecutor(state, registry);
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, state.EffectRuntime.StateRevision,
                NormalizedValue.CreateArray(ids.Select(id => NormalizedValue.CreateStableReference("candidate", id)).ToList()), out var diagnostic), Is.True, diagnostic);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
    }
}
