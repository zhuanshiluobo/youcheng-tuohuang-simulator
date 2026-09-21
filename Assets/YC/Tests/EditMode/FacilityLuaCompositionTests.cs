using System;
using System.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class FacilityLuaCompositionTests
    {
        private static GameState State()
        {
            var state = new GameState { GameId = "facility-lua", MapId = StaticMapDefinitions.CreateFourPlayerMap().MapId,
                Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1 };
            state.Players.Add(new PlayerState { PlayerId = 1, CityLocationId = "A-01", InfluenceSupply = 30,
                RemainingMainActionsThisTurn = 1, Resources = new ResourceSet { Originium = 3, OriginiumShard = 3, Iron = 3 } });
            state.Players.Add(new PlayerState { PlayerId = 2, CityLocationId = "G-01", InfluenceSupply = 30 });
            foreach (string location in new[] { "A-01", "A-02", "B-01" }) new ResourceTokenService().PlaceToken(state.Map, location, ResourceType.Iron, 2);
            return state;
        }
        private static EffectRegistry Registry()
        {
            var registry = new EffectRegistry(); new RoundExecutionService(registry);
            var map = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()); var influence = new InfluenceService(map);
            var deck = new EventDeckService(73); var tokens = new ResourceTokenService(); var cost = new TravelCostService(map);
            var move = new CityMovementService(map, influence, cost, deck, tokens);
            ResourceEffectExecutor.Register(registry); InfluenceEffectExecutor.Register(registry, influence);
            CityMoveEffectExecutor.Register(registry, map, influence, move, cost, tokens);
            EventCardEffectExecutor.Register(registry, map, influence, deck, tokens); EventCardLuaCatalog.Register(registry);
            ExplorationEffectExecutor.Register(registry, map, new YC.Domain.Exploration.ExplorationService(map, influence, deck, tokens), deck, tokens);
            FacilityEntryEffectExecutor.Register(registry); FacilityLuaCatalog.Register(registry); CharacterCardLuaCatalog.Register(registry);
            return registry;
        }
        private static void Activate(GameState state, EffectRegistry registry, string facilityId)
        {
            var placement = new FacilityPlacement { PlayerId = 1, FacilityCardId = facilityId, CityBoardSlotIndex = 0 };
            state.Map.Facilities.Add(placement); FacilityInstanceStateService.EnsureIdentity(state, placement);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(FacilityEntryEffectSpecFactory.Activate(1, facilityId, placement.ContentInstanceId, 0));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Behavior || n.EffectTypeId == LuaDomainEffectTypeIds.ExecuteMainAction), Is.False);
            Assert.That(state.PendingChoice, Is.Null); Assert.That(state.PendingCardSession, Is.Null);
        }
        private static InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
        private static GameCommand Answer(GameState state, EffectRegistry registry, string answer, bool allocation = false)
        {
            var command = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(Open(state), 1), 1,
                allocation ? null : new[] { answer });
            if (allocation) command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = answer;
            var result = new AnswerInteractionCommandHandler(registry).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason + state.EffectRuntime.LastFaultMessage);
            Assert.That(state.EffectRuntime.LastFaultMessage, Is.Empty, state.EffectRuntime.LastFaultMessage);
            return command;
        }
        [Test]
        public void CopyAdjacent_OnlyOffersOwnNonRainbowNeighborAndResumesOnce()
        {
            var state = State(); var registry = Registry();
            string rewardId = FacilityCardDatabase.All.First(d => d.EffectId == "gain_iron_4").FacilityId;
            FacilityPlacement target = null;
            foreach (var pair in new[] { new[] { 1, 1 }, new[] { 1, 5 }, new[] { 2, 3 } })
            {
                var placement = new FacilityPlacement { PlayerId = pair[0], CityBoardSlotIndex = pair[1], FacilityCardId = rewardId };
                state.Map.Facilities.Add(placement); FacilityInstanceStateService.EnsureIdentity(state, placement);
                if (pair[0] == 1 && pair[1] == 1) target = placement;
            }
            Activate(state, registry, FacilityCardDatabase.AffiliatedEnergyFacility);
            Assert.That(Open(state).CandidateIds, Is.EqualTo(new[] { target.ContentInstanceId }));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(3));
            state = GameStateCloneService.DeepClone(state); registry = Registry();
            var command = Answer(state, registry, target.ContentInstanceId);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(7));
            new AnswerInteractionCommandHandler(registry).Handle(state, command);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(7));
        }
        [Test]
        public void CopyAdjacent_NoEligibleNeighborCompletesWithoutInteraction()
        {
            var state = State(); Activate(state, Registry(), FacilityCardDatabase.AffiliatedEnergyFacility);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(3));
        }
        [TestCase("resources")]
        [TestCase("gold")]
        public void AdditionalBuild_SelectsPaymentAndSlotWithoutSpendingAction(string mode)
        {
            var state = State(); var registry = Registry();
            state.FindPlayer(1).Resources = new ResourceSet { Originium = 20, OriginiumShard = 20, Iron = 20, GoldVoucher = 50 };
            string card = FacilityCardDatabase.All.First(d => d.EffectId == "gain_iron_4").FacilityId;
            state.Decks.FacilitySupply.Add(card);
            Activate(state, registry, FacilityCardDatabase.SimpleEngineeringCamp);
            Answer(state, registry, card); Assert.That(Open(state).CandidateIds, Does.Contain(mode));
            Answer(state, registry, mode);
            Assert.That(state.Map.Facilities.Any(f => f.FacilityCardId == card), Is.False);
            state = GameStateCloneService.DeepClone(state); registry = Registry();
            var command = Answer(state, registry, "build-slot:5");
            Assert.That(state.Map.Facilities.Single(f => f.FacilityCardId == card).CityBoardSlotIndex, Is.EqualTo(5));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            var resources = state.FindPlayer(1).Resources.Iron;
            new AnswerInteractionCommandHandler(registry).Handle(state, command);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(resources));
            Assert.That(state.Map.Facilities.Count(f => f.FacilityCardId == card), Is.EqualTo(1));
        }
        [Test]
        public void ReserveBuild_SelectsActualSlotAndCanDecline()
        {
            var state = State(); var registry = Registry(); Activate(state, registry, FacilityCardDatabase.LogisticsHub);
            string card = Open(state).CandidateIds.First(id => id != "choice.skip");
            Answer(state, registry, card); Answer(state, registry, "free");
            Answer(state, registry, "build-slot:8");
            Assert.That(state.Map.Facilities.Single(f => f.FacilityCardId == card).CityBoardSlotIndex, Is.EqualTo(8));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            state = State(); Activate(state, registry, FacilityCardDatabase.LogisticsHub); Answer(state, registry, "choice.skip");
            Assert.That(state.Map.Facilities.Count, Is.EqualTo(1));
        }
        [Test]
        public void FederalCouncil_ReservesNextStarterWithoutChangingCurrentTurn()
        {
            var state = State(); var registry = Registry(); Activate(state, registry, FacilityCardDatabase.FederalOffice);
            Assert.That(state.EffectRuntime.PendingNextRoundStartPlayerId, Is.EqualTo(1));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            var placement = new FacilityPlacement { PlayerId = 2, FacilityCardId = FacilityCardDatabase.FederalOffice, CityBoardSlotIndex = 1 };
            state.Map.Facilities.Add(placement); FacilityInstanceStateService.EnsureIdentity(state, placement);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(FacilityEntryEffectSpecFactory.Activate(2, placement.FacilityCardId, placement.ContentInstanceId, 1));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            state = GameStateCloneService.DeepClone(state); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(state.EffectRuntime.PendingNextRoundStartPlayerId, Is.EqualTo(2));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
        }
        [Test]
        public void VehicleRemovalAndDispatch_WaitsForTwoSeparateChoices()
        {
            var state = State(); var registry = Registry();
            var influence = new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
            string enemy = InfluenceService.GetLocationSlotId("A-02", 0);
            influence.Place(state, 2, enemy); influence.Place(state, 1, InfluenceService.GetLocationSlotId("A-01", 0));
            string card = FacilityCardDatabase.All.First(d => d.EffectId == "remove_then_dispatch_or_explore").FacilityId;
            Activate(state, registry, card); Answer(state, registry, "vehicle.remove_move");
            Assert.That(state.Map.Influences.Any(x => x.SlotId == enemy), Is.True);
            Answer(state, registry, enemy); Assert.That(state.Map.Influences.Any(x => x.SlotId == enemy), Is.False);
            state = GameStateCloneService.DeepClone(state); registry = Registry();
            Answer(state, registry, Open(state).CandidateIds[0]);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
        }
        [Test]
        public void VehicleExplore_SelectsRouteAndSlotBeforePayingOrRevealing()
        {
            var state = State(); var registry = Registry(); state.Map.ResourceTokens.Clear();
            state.FindPlayer(1).Resources.GoldVoucher = 30;
            state.Decks.EventDeckGreen.Add("event_green_01");
            string card = FacilityCardDatabase.All.First(d => d.EffectId == "remove_then_dispatch_or_explore").FacilityId;
            Activate(state, registry, card); Answer(state, registry, "vehicle.explore");
            string step = Open(state).CandidateIds.First(id => id.StartsWith("explore.step:") && id.EndsWith(":A-02"));
            Answer(state, registry, step);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(30));
            Assert.That(state.Decks.EventDeckGreen.Count, Is.EqualTo(1));
            state = GameStateCloneService.DeepClone(state); registry = Registry();
            Answer(state, registry, Open(state).CandidateIds.First(id => id.StartsWith("explore.finish:")));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.LessThan(30));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(EventCardEffectExecutor.OptionInteractionTypeId));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
        }
        [Test]
        public void ColorDiscount_UsesDefinitionDataForUnrecognizedEffectId()
        {
            var state = State();
            state.FindPlayer(1).BuiltFacilityIds.Add(FacilityCardDatabase.All.First(d => d.Color == "blue").FacilityId);
            var card = new FacilityCardDefinition { EffectId = "external.cost_rule", ResourceCost = new ResourceSet { Iron = 5 },
                CostReductionPerDistinctBuiltColor = new ResourceSet { Iron = 2 } };
            Assert.That(new FacilityBuildCostService().GetEffectiveResourceCost(state, state.FindPlayer(1), card).Iron, Is.EqualTo(3));
        }
        [Test]
        public void ExplorationBacktracking_UsesFreshInteractionIdsAfterRecovery()
        {
            var state = State(); var registry = Registry();
            string card = FacilityCardDatabase.All.First(d => d.EffectId == "remove_then_dispatch_or_explore").FacilityId;
            Activate(state, registry, card); Answer(state, registry, "vehicle.explore");
            var first = Open(state); string step = first.CandidateIds.First(id => id.StartsWith("explore.step:"));
            Answer(state, registry, step); state = GameStateCloneService.DeepClone(state); registry = Registry();
            Answer(state, registry, "explore.back");
            Assert.That(Open(state).InteractionId, Is.Not.EqualTo(first.InteractionId));
            Assert.That(Open(state).CandidateIds, Does.Contain(step));
            Answer(state, registry, "choice.skip");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
        }
        [Test]
        public void AllEnabledFacilityEntries_UseLuaWithoutLegacyActionAdapter()
        {
            var pack = ExternalContentPack.Current;
            foreach (var definition in pack.CreateFacilities().Where(d => d.HasEntryEffect))
                Assert.That(pack.GetFacilityScript(definition.EffectId), Does.Not.Contain("ExecuteMainAction"), definition.FacilityId);
        }
        [Test]
        public void StartPlayerOverride_IsConsumedByRoundBoundaryAndDoesNotLeak()
        {
            var state = State(); state.StartPlayerId = 1;
            var registry = Registry(); var round = RoundLifecycleTestDriver.EnterCollection(state, new RoundExecutionService(registry));
            var placement = new FacilityPlacement { PlayerId = 2, FacilityCardId = FacilityCardDatabase.FederalOffice, CityBoardSlotIndex = 1 };
            state.Map.Facilities.Add(placement); FacilityInstanceStateService.EnsureIdentity(state, placement);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(FacilityEntryEffectSpecFactory.Activate(2, placement.FacilityCardId, placement.ContentInstanceId, 1));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            Assert.That(state.StartPlayerId, Is.EqualTo(1));
            RoundLifecycleTestDriver.FinishCollection(state, round);
            for (int guard = 0; state.Round == 1 && guard < 8; guard++)
            {
                var result = round.EndCurrentPlayerWindow(state, state.CurrentPlayerId);
                Assert.That(result.IsValid, Is.True, result.Reason);
            }
            Assert.That(state.Round, Is.EqualTo(2)); Assert.That(state.StartPlayerId, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.PendingNextRoundStartPlayerId, Is.EqualTo(-1));
        }
        [Test]
        public void ExplorationToll_ChoosesRecipientBeforeDeductingAndResumesOnce()
        {
            var state = State(); var registry = Registry(); state.Map.ResourceTokens.Clear();
            state.Players.Add(new PlayerState { PlayerId = 3, InfluenceSupply = 30 });
            state.FindPlayer(1).Resources.GoldVoucher = 30; state.Decks.EventDeckGreen.Add("event_green_01");
            var map = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()); var influence = new InfluenceService(map);
            var route = map.FindRoute("A-01", "A-02");
            var tolls = new YC.Domain.Travel.RouteTollService(map);
            string key = tolls.GetRoutePaymentKey(route.RouteId, YC.Domain.Travel.RouteTollPaymentKeyMode.SharedRegion);
            var regionRoutes = map.Map.Routes.Where(r => tolls.GetRoutePaymentKey(r.RouteId, YC.Domain.Travel.RouteTollPaymentKeyMode.SharedRegion) == key).ToList();
            var slots = regionRoutes.SelectMany(r => Enumerable.Range(0, r.InfluenceSlotCount).Select(n => InfluenceService.GetRouteSlotId(r.RouteId, n))).Take(2).ToList();
            Assert.That(slots.Count, Is.EqualTo(2));
            Assert.That(influence.Place(state, 2, slots[0]).Succeeded, Is.True);
            Assert.That(influence.Place(state, 3, slots[1]).Succeeded, Is.True);
            string card = FacilityCardDatabase.All.First(d => d.EffectId == "remove_then_dispatch_or_explore").FacilityId;
            Activate(state, registry, card); Answer(state, registry, "vehicle.explore");
            Answer(state, registry, Open(state).CandidateIds.First(id => id.StartsWith("explore.step:") && id.EndsWith(":A-02")));
            Answer(state, registry, Open(state).CandidateIds.First(id => id.StartsWith("explore.finish:")));
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { "explore.pay:2", "explore.pay:3" }));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(30));
            state = GameStateCloneService.DeepClone(state); registry = Registry();
            var command = Answer(state, registry, "explore.pay:3");
            Assert.That(state.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.Zero);
            new AnswerInteractionCommandHandler(registry).Handle(state, command);
            Assert.That(state.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(2));
        }
        [Test]
        public void RetiredFacilityBehavior_FaultsInsteadOfSilentlyApplyingOldRules()
        {
            var state = State(); var executor = new EffectTreeExecutor(state, Registry());
            executor.CreateRoot(FacilityEntryEffectSpecFactory.Behavior(1, FacilityCardEffectIds.SellResources, 0));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.True);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
        }
        [TestCase("sale|iron|2", 1, 8)]
        [TestCase("sale|originium|0", 3, 0)]
        public void Sale_UsesSharedEffectAndRestoresWithoutDuplicatePayout(string answer, int iron, int gold)
        {
            var state = State(); var registry = Registry(); Activate(state, registry, FacilityCardDatabase.TradeDistrict);
            Assert.That(Open(state).PromptKey, Is.EqualTo("effect.resource.sell"));
            state = GameStateCloneService.DeepClone(state); registry = Registry();
            var command = Answer(state, registry, answer);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(iron)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            new AnswerInteractionCommandHandler(registry).Handle(state, command);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
        }
        [Test]
        public void ResourceAllocation_UsesConfiguredTotalAndPersistsBeforeAward()
        {
            var state = State(); var registry = Registry(); Activate(state, registry, FacilityCardDatabase.MiningPowerShovel);
            Assert.That(Open(state).CandidateIds, Does.Contain("total:5")); Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(3));
            state = GameStateCloneService.DeepClone(state); registry = Registry();
            var command = Answer(state, registry, "Originium=2,Iron=3", true);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(5)); Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(6));
            new AnswerInteractionCommandHandler(registry).Handle(state, command);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(6));
        }
        [TestCase("Iron=4")]
        [TestCase("Iron=3,Iron=2")]
        [TestCase("GoldVoucher=5")]
        [TestCase("Iron=-1,Originium=6")]
        public void InvalidAllocation_DoesNotGrantAnyResources(string allocation)
        {
            var state = State(); var registry = Registry(); Activate(state, registry, FacilityCardDatabase.MiningPowerShovel);
            var command = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(Open(state), 1), 1, null);
            command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = allocation;
            Assert.That(new AnswerInteractionCommandHandler(registry).Handle(state, command).Succeeded, Is.False);
            Assert.That(Open(state), Is.Not.Null);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(3)); Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
        }
        [TestCase("replace-influence")]
        [TestCase("place-influence")]
        public void InfluenceBranch_OnlySelectedOperationExecutes(string branch)
        {
            var state = State(); var registry = Registry();
            var influence = new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
            string slot = InfluenceService.GetLocationSlotId("A-02", 0);
            Assert.That(influence.Place(state, 2, slot).Succeeded, Is.True);
            Activate(state, registry, FacilityCardDatabase.MercenaryCommand);
            Answer(state, registry, branch);
            state = GameStateCloneService.DeepClone(state); registry = Registry();
            string target = Open(state).CandidateIds[0]; Answer(state, registry, target);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            if (branch == "replace-influence") Assert.That(state.Map.Influences.Any(i => i.SlotId == slot && i.PlayerId == 1), Is.True);
            else Assert.That(state.Map.Influences.Any(i => i.SlotId == slot && i.PlayerId == 2), Is.True);
        }
        [Test]
        public void AdjacentGold_IsCalculatedByLuaFromPublicFacilitySnapshot()
        {
            var state = State(); var registry = Registry();
            state.Map.Facilities.Add(new FacilityPlacement { PlayerId = 1, FacilityCardId = FacilityCardDatabase.SimpleEngineeringCamp, CityBoardSlotIndex = 4 });
            state.Map.Facilities.Add(new FacilityPlacement { PlayerId = 1, FacilityCardId = FacilityCardDatabase.SourceStoneRefinery, CityBoardSlotIndex = 6 });
            state.Map.Facilities.Add(new FacilityPlacement { PlayerId = 2, FacilityCardId = FacilityCardDatabase.TradeDistrict, CityBoardSlotIndex = 8 });
            Activate(state, registry, FacilityCardDatabase.UrbanizedArea);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
        }
        [Test]
        public void FreeMove_WaitsForMovementBeforeOfferingTraversedRoutePlacement()
        {
            var state = State(); var registry = Registry(); Activate(state, registry, FacilityCardDatabase.HighPerformancePowerFacility);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(CityMoveEffectExecutor.TargetInteractionTypeId));
            Answer(state, registry, "A-02");
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-02"));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(Open(state).CandidateIds.All(id => id.StartsWith("route:", StringComparison.Ordinal)), Is.True);
            state = GameStateCloneService.DeepClone(state); registry = Registry();
            string slot = Open(state).CandidateIds[0]; Answer(state, registry, slot);
            Assert.That(state.Map.Influences.Any(i => i.SlotId == slot && i.PlayerId == 1), Is.True);
        }
    }
}
