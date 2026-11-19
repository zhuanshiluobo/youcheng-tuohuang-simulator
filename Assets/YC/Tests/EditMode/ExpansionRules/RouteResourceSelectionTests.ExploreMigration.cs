using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Facilities;
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
    public sealed partial class RouteResourceSelectionTests
    {
        private string StartFormalExploreBody(GameState state, EffectRegistry registry, string path)
        {
            // 真实正式脚本正文的隔离消费者；不冒充能力授权、正常入场或正常页面验收。
            var pack = ExternalContentPack.Current; string source = pack.GetScript(path);
            var definition = new LuaScriptDefinition(pack.PackId, "api15.formal", "api15.formal", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var handler = new LuaHandlerDefinition("api15.formal", "fixture.facility.entry", "", definition);
            var ruleEvent = new RuleEvent { EventId = "api15.formal.event", EventType = handler.EventType, PlayerId = 1, Payload = Object() };
            var eventContext = new EffectEventHandlerContext(state, ruleEvent, new EffectNodeRuntimeState { EffectId = "api15.formal.owner", PlayerId = 1 }, null);
            var dispatcher = new LuaEffectEventDispatcher(registry);
            var create = typeof(LuaEffectEventDispatcher).GetMethod("CreateInvocationContext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(create, Is.Not.Null);
            var luaContext = (LuaInvocationContext)create.Invoke(dispatcher, new object[] { handler, eventContext });
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(definition, luaContext);
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler(presentationPack: pack).TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1, SourceId = path }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            registry.Register(new EffectRegistration("api15.formal.body", c => {
                if (c.Node.FlowStage == "") return EffectStepResult.Continue("waiting").AddChildren(specs);
                if (c.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed || n.Status == EffectNodeStatus.Faulted)) return EffectStepResult.Failed("api15.formal.child_failed");
                if (c.ChildNodes.Any(n => n.Status != EffectNodeStatus.Completed)) return EffectStepResult.NoProgress();
                return EffectStepResult.Completed();
            }, EffectExecutorKind.IntrinsicFlow));
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(EffectSpec.Create("api15.formal.body", playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            exploreArtifacts.WriteLabeledText("正式探索正文身份", path + "\n" + definition.ContentHash); return id;
        }

        [TestCase("")]
        [TestCase("event_green")]
        [TestCase("event_yellow")]
        [TestCase("event_red")]
        public void VehicleOnlyOffersExploreWhenAnEventDeckHasCards(string remainingRole)
        {
            Production(out var state, out var registry, out _, 4);
            var decks = state.ItemZones.Zones.Where(zone => zone.TemplateId == "core.zone.card_deck" &&
                new[] { "event_green", "event_yellow", "event_red" }.Contains(zone.LocalZoneId)).ToArray();
            Assert.That(decks.Length, Is.EqualTo(3));
            string retired = state.ItemZones.Zones.Single(zone => zone.LocalZoneId == "retired").ZoneId;
            var move = typeof(ItemZoneStateMigrator).GetMethod("SetPosition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            foreach (var deck in decks)
            {
                var cards = state.ItemZones.Items.Where(item => item.ZoneId == deck.ZoneId).ToArray();
                int keep = deck.LocalZoneId == remainingRole ? 1 : 0;
                Assert.That(cards.Length, Is.GreaterThanOrEqualTo(keep));
                foreach (var card in cards.Skip(keep)) move.Invoke(null, new object[] { state, card, retired });
            }
            Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.True, reason);
            StartFormalExploreBody(state, registry, "lua/facilities/remove_then_dispatch_or_explore.lua");
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(request.CandidateIds, Does.Contain("vehicle.remove_move"));
            Assert.That(request.CandidateIds.Contains("vehicle.explore"), Is.EqualTo(remainingRole.Length > 0));
            Assert.That(request.Presentation.Options.Select(option => option.CandidateId), Is.EquivalentTo(request.CandidateIds));
        }
        [Test]
        public void FormalVehicleExploreAtRoundStartWaitsForOwnerAndCompletesRedExploration()
        {
            Production(out var state, out var registry, out var exploration, 4);
            state.Round = 4; state.Phase = GamePhase.RoundStart; state.CurrentPlayerId = 2;
            state.FindPlayer(1).Resources.GoldVoucher = 40;
            string body = StartFormalExploreBody(state, registry, "lua/facilities/remove_then_dispatch_or_explore.lua");
            var branch = state.EffectRuntime.InteractionRequests.Single(request => request.Status == "open");
            Assert.That(branch.AnsweringPlayerId, Is.EqualTo(1));
            Submit(state, registry, branch, new[] { "vehicle.explore" });
            var request = Selection(state);
            var path = exploration.FindDefaultPath(state, 1, "E-03");
            Assert.That(path, Is.Not.Null);
            Submit(state, registry, request, SelectionAnswer(request, "E-03", path.RouteIds.ToArray()));
            FinishEvents(state, registry);
            var explore = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ExplorationEffectTypeIds.Explore && ExplorationContract.IsV3(n.NormalizedArguments));
            Assert.That(Text(explore.NormalizedResult, "outcome"), Is.EqualTo("completed"));
            Assert.That(Text(explore.NormalizedResult, "targetLocationId"), Is.EqualTo("E-03"));
            Assert.That(ExploreRoot(state, body).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.Zero);
        }
        [Test]
        public void FormalPowerFacilityAtRoundStartLetsOwnerMoveAndPlaceRouteInfluence()
        {
            Production(out var state, out var registry, out _, 4);
            state.Round = 4; state.Phase = GamePhase.RoundStart; state.CurrentPlayerId = 2;
            var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId));
            var influence = new InfluenceService(map); var travel = new TravelCostService(map);
            var movement = new CityMovementService(map, influence, travel);
            CityMoveEffectExecutor.Register(registry, map, influence, movement, travel, new ResourceTokenService());
            FacilityEntryEffectExecutor.Register(registry);
            FacilityLuaCatalog.Register(registry, ExternalContentPack.Current);
            var facility = state.ContentDefinitions.Facilities.First(value => value.EffectId == "free_city_move_and_deploy_route_influence");
            var item = state.ItemZones.Items.First(value => value.DefinitionId == facility.FacilityId);
            var cell = state.ItemZones.Zones.Single(value => value.OwnerPlayerId == 1 && value.LocalZoneId == "city_cell_0");
            // 隔离布置已建成设施；后续从正式入场事件及Lua执行，不计正常游戏验收。
            typeof(ItemZoneStateMigrator).GetMethod("SetPosition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { state, item, cell.ZoneId });
            item.ControllerSubject = ItemSubjectRef.Player(1);
            string origin = state.FindPlayer(1).CityLocationId;
            string target = map.GetAdjacentLocations(origin).First(value => !value.IsRedZone && state.Players.All(p => p.CityLocationId != value.LocationId)).LocationId;
            Assert.That(movement.CanMoveCity(state, 1, target).IsValid, Is.False, "普通移动仍须遵循行动阶段");
            var engine = new EffectTreeExecutor(state, registry);
            string entry = engine.CreateRoot(FacilityEntryEffectSpecFactory.Activate(1, facility.FacilityId, item.ItemId, 0));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.SingleOrDefault(value => value.Status == "open" && value.InteractionTypeId == CityMoveEffectExecutor.TargetInteractionTypeId);
            Assert.That(request, Is.Not.Null, string.Join("；", state.EffectRuntime.EffectNodes.Select(n => n.EffectTypeId + ":" + n.FailureReason)));
            Assert.That(request.AnsweringPlayerId, Is.EqualTo(1));
            Assert.That(request.CandidateIds, Does.Contain(target));
            Submit(state, registry, request, new[] { target });
            FinishEvents(state, registry);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo(target));
            Assert.That(ExploreRoot(state, entry).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.Zero);
            string route = map.FindRoute(origin, target).RouteId;
            Assert.That(state.Map.Influences.Count(value => value.PlayerId == 1 && value.RouteId == route), Is.EqualTo(1));
        }
        [TestCase(false, false)][TestCase(false, true)][TestCase(true, false)][TestCase(true, true)]
        public void ExploreV3FormalVehicleAndMaylanderKeepBranchCostAndFollowingRemoval(bool maylander, bool cancel)
        {
            Production(out var state, out var registry, out _); int budget = state.FindPlayer(1).RemainingMainActionsThisTurn;
            string path = maylander ? "lua/enterprises/infected.enterprise.maylander_foundation.level_2.special.lua" : "lua/facilities/remove_then_dispatch_or_explore.lua";
            StartFormalExploreBody(state, registry, path);
            if (!maylander)
            {
                var choice = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
                Submit(state, registry, choice, new[] { "vehicle.explore" });
            }
            var root = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ExplorationEffectTypeIds.Explore && ExplorationContract.IsV3(n.NormalizedArguments));
            Assert.That(Flag(root.NormalizedArguments, "consumeMainAction"), Is.False);
            string removalId = "";
            if (maylander)
            {
                var removal = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence);
                removalId = removal.EffectId;
                Assert.That(removal.Status, Is.EqualTo(EffectNodeStatus.Created));
                Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.OwnerEffectId == removal.EffectId), Is.False);
            }
            state = ExploreRecover(state, registry, "正式正文探索选择待答"); var q = Selection(state);
            Submit(state, registry, q, cancel ? new[] { q.CandidateIds.Single(id => id.EndsWith(".cancel", StringComparison.Ordinal)) } : SelectionAnswer(q, "A-01", "A1", "A2"));
            if (!cancel)
            {
                Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.EqualTo(4));
                Assert.That(ExploreRoot(state, root.EffectId).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
                if (maylander) Assert.That(ExploreRoot(state, removalId).Status, Is.EqualTo(EffectNodeStatus.Created));
                FinishEvents(state, registry);
            }
            Assert.That(Text(ExploreRoot(state, root.EffectId).NormalizedResult, "outcome"), Is.EqualTo(cancel ? "cancelled" : "completed"));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(budget));
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ContentPrimitiveEffectExecutor.RetiredExplorationSelectionTypeId), Is.False);
            if (maylander) Assert.That(ExploreRoot(state, removalId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            else Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence), Is.False);
            ExploreRecover(state, registry, "正式探索及原后续结束");
        }
    }
}
