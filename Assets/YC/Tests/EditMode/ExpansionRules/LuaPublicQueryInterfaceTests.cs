using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离接口检查：实体由正式目录装配，位置调整仅构造接口边界，不计正常UI/整局验收。
    public sealed class LuaPublicQueryInterfaceTests
    {
        [Test]
        public void DispatcherQueriesResolveRealIdsTopologySlotsRoadResourceAndFrozenDefinition()
        {
            var f = Create("core", "enterprise", "infected"); var map = f.Map.Map;
            var route = map.Routes.First(); var location = map.Locations.First();
            if (f.State.Map.ResourceTokens.Count == 0)
            {
                var marker = Grant(f, "core.resource_point_token", Child(f.State, location.LocationId, "core.zone.resource_point_markers"));
                marker.LocalState = ItemTransferEffectExecutor.Object("resourceType", NormalizedValue.CreateInteger((int)ResourceType.Iron), "amount", NormalizedValue.CreateInteger(2));
            }
            var city = f.State.ItemZones.Items.First(i => i.TypeId == "core.mobile_city" && i.OwnerSubject.PlayerId == 1);
            Position(f.State, city, Child(f.State, location.LocationId, "core.zone.city_dock"));
            Grant(f, "core.road_token", Child(f.State, route.RouteId, "core.zone.route_road"));
            var slot = f.Influence.GetLegalPlacementSlotIds(f.State, 1).First();
            string influence = Place(f, slot);
            var token = f.State.Map.ResourceTokens.First();
            var unmarked = map.Locations.First(point => f.State.Map.ResourceTokens.All(marker => marker.LocationId != point.LocationId));
            var instance = new ItemZoneQuery().Project(f.State, GameStateViewer.Player(1)).Items.First(i => i.PhysicalKind == ItemPhysicalKind.Card);
            string region = map.Regions.First().RegionId;
            string body = "local m=Global.Map.GetMap(); assert(m.mapId==" + Q(map.MapId) + " and m.stateRevision==ctx.stateRevision); " +
                "local r=Global.Map.GetRoute(" + Q(route.RouteId) + "); assert(r.routeId==" + Q(route.RouteId) + " and r.fromLocationId==" + Q(route.FromLocationId) +
                " and r.toLocationId==" + Q(route.ToLocationId) + " and r.baseCost==" + route.BaseCost + "); " +
                "assert(Global.Map.GetRegion(" + Q(region) + ").regionId==" + Q(region) + "); " +
                "local between=Global.Map.GetRoutesBetween(" + Q(route.FromLocationId) + "," + Q(route.ToLocationId) + "); assert(#between>0); " +
                "assert(#Global.Map.GetConnectedLocations(" + Q(route.FromLocationId) + ")>0); " +
                "assert(Global.Map.IsRoad(" + Q(route.RouteId) + ")); " +
                "assert(Global.Map.GetInfluenceSlot(" + Q(slot) + ").influenceId==" + Q(influence) + "); " +
                "assert(Global.Map.GetResourcePoint(" + Q(token.LocationId) + ").amount==" + token.Amount + "); " +
                "assert(Global.Map.GetLocation(" + Q(token.LocationId) + ").hasResourcePointIndicator); " +
                "assert(not Global.Map.GetLocation(" + Q(unmarked.LocationId) + ").hasResourcePointIndicator and Global.Map.GetResourcePoint(" + Q(unmarked.LocationId) + ")==nil); " +
                "assert(Global.Map.GetCitiesAt(" + Q(location.LocationId) + ")[1].playerId=='p1'); " +
                "assert(Global.Content.GetInstance(" + Q(instance.ItemId) + ").id==" + Q(instance.ItemId) + "); " +
                "assert(Global.Content.GetDefinition(" + Q(instance.DefinitionId) + ").definitionId==" + Q(instance.DefinitionId) + "); " +
                "assert(Global.Map.GetLocation('missing')==nil and Global.Map.GetRoute('missing')==nil and Global.Map.GetRegion('missing')==nil); " +
                "assert(Global.Map.GetInfluenceSlot('missing')==nil and Global.Map.GetResourcePoint('missing')==nil); " +
                "assert(#Global.Map.GetConnectedLocations('missing')==0 and #Global.Map.GetCitiesAt('missing')==0 and #Global.Map.GetRoutesBetween('missing','missing')==0); " +
                "assert(not Global.Map.IsRoad('missing') and Global.Content.GetInstance('missing')==nil and Global.Content.GetDefinition('missing')==nil);";
            Success(f.State, body);
            var defaultScript = Script("return function(ctx) " + body + " return {} end");
            var defaultResult = new MoonSharpLuaRuntimeHost().Invoke(defaultScript, Context(f.State, defaultScript));
            Assert.That(defaultResult.IsSuccess, Is.True, defaultResult.FailureCode + ":" + defaultResult.Diagnostic);
        }

        [Test]
        public void PureCandidateContextPreservesSuppliedScopeAndRejectsMismatchedRevision()
        {
            var f = Create("core"); var own = new ItemZoneQuery().Project(f.State, GameStateViewer.Player(1)).Items.First();
            var draft = new CandidateSetDraft { CandidateSetId = "query.candidates", CandidateKind = "facility.build.card", SourceEffectId = "query.owner", ExecutingPlayerId = 1,
                Context = ItemTransferEffectExecutor.Object("stateRevision", NormalizedValue.CreateInteger(f.State.EffectRuntime.StateRevision),
                    "instances", NormalizedValue.CreateArray(new List<NormalizedValue> { ItemTransferEffectExecutor.Object("id", NormalizedValue.CreateString(own.ItemId), "definitionId", NormalizedValue.CreateString(own.DefinitionId)) }),
                    "zones", NormalizedValue.CreateArray(new List<NormalizedValue>())) };
            var script = Script("return function(ctx) assert(GameData.IsDlcEnabled('core')); assert(#Global.Content.FindInstances()==1); assert(Global.Content.GetInstance('outside')==nil); return {} end");
            var definition = new LuaHandlerDefinition("query.subscription", "CandidateSetBuilding", "*", script);
            var factory = typeof(LuaEffectEventDispatcher).GetMethod("CreatePureCandidateContext", BindingFlags.Static | BindingFlags.NonPublic);
            var context = (LuaInvocationContext)factory.Invoke(null, new object[] { definition, f.State, draft, NormalizedValue.CreateNull() });
            Assert.That(Host().Invoke(script, context).IsSuccess, Is.True);
            Assert.That(context.FacadeSnapshot.GlobalContent.Get("instances").Items.Single().Get("controller").Kind, Is.EqualTo(LuaSnapshotValueKind.String));
            draft.Context.Properties.Single(p => p.Name == "stateRevision").Value = NormalizedValue.CreateInteger(f.State.EffectRuntime.StateRevision + 1);
            var error = Assert.Throws<TargetInvocationException>(() => factory.Invoke(null, new object[] { definition, f.State, draft, NormalizedValue.CreateNull() }));
            Assert.That(error.InnerException.Message, Is.EqualTo("nmc_query_revision_mismatch"));
        }

        [Test]
        public void FindFiltersUseAuthorizedIdentitiesDirectOrDescendantZonesAndDefinitionMetadata()
        {
            var f = Create("core", "enterprise", "infected");
            var hand = f.State.ItemZones.Zones.Single(z => z.LocalZoneId == "hand" && z.OwnerPlayerId == 1);
            var root = f.State.ItemZones.Zones.Single(z => z.ZoneId == hand.ParentId);
            var own = f.State.ItemZones.Items.First(i => i.ZoneId == hand.ZoneId);
            int handCount = f.State.ItemZones.Items.Count(i => i.ZoneId == hand.ZoneId);
            var secret = f.State.ItemZones.Items.First(i => f.State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.LocalZoneId == "hand" && z.OwnerPlayerId == 2));
            var definition = f.State.ContentDefinitions.Facilities.First(d => d.Keywords.Count > 0);
            var facility = f.State.ItemZones.Items.First(i => i.DefinitionId == definition.FacilityId);
            var supply = f.State.ItemZones.Zones.First(z => z.LocalZoneId == "facility_supply_0");
            foreach (var occupied in f.State.ItemZones.Items.Where(i => i.ZoneId == supply.ZoneId).ToArray())
                Position(f.State, occupied, f.State.ItemZones.Zones.First(z => z.LocalZoneId == "facility_deck"));
            Position(f.State, facility, supply);
            string slot = f.Influence.GetLegalPlacementSlotIds(f.State, 1).First(); string placed = Place(f, slot);
            string original = JsonConvert.SerializeObject(f.State);
            Success(f.State, "local items=Global.Content.FindInstances(); assert(#items>0); " +
                "assert(Global.Content.GetInstance(" + Q(secret.ItemId) + ")==nil); " +
                "assert(#Global.Content.FindInstances({id=" + Q(secret.ItemId) + "})==0); " +
                "assert(#Global.Content.FindInstances({zoneId=" + Q(hand.ZoneId) + "})==" + handCount + "); " +
                "assert(#Global.Content.FindInstances({zoneId=" + Q(root.ZoneId) + "})==0, 'direct root'); " +
                "assert(#Global.Content.FindInstances({zoneId=" + Q(root.ZoneId) + ",includeDescendants=true})>=" + handCount + ", 'descendant root'); " +
                "local zones=Global.Content.FindZones({parentId=" + Q(root.ZoneId) + "}); for _,z in ipairs(zones) do assert(z.parentId==" + Q(root.ZoneId) + ") end; " +
                "assert(#Global.Content.FindZones({parentId=" + Q(root.ZoneId) + ",includeDescendants=true})>=#zones); " +
                "assert(#Global.Content.FindZones({id=" + Q(hand.ZoneId) + ",templateId='core.zone.character_hand',localZoneId='hand',owner='p1'})==1); " +
                "assert(#Global.Content.FindInstances({id=" + Q(own.ItemId) + ",definitionId=" + Q(own.DefinitionId) + ",owner='p1',controller='p1',typeId=" + Q(own.TypeId) + "})==1); " +
                "assert(#Global.Content.FindInstances({id=" + Q(facility.ItemId) + ",definitionId=" + Q(definition.FacilityId) + ",contentType='facility',tag=" + Q(definition.Keywords[0]) + "})==1, 'definition tag'); " +
                "assert(#Global.Content.FindInstances({zoneId='missing'})==0); " +
                "assert(#Global.Map.FindInfluences({influenceId=" + Q(placed) + ",ownerPlayerId='p1',owner='p1',slotId=" + Q(slot) + ",slotIds={" + Q(slot) + ",'missing'}})==1); " +
                "assert(#Global.Map.FindInfluences({ownerPlayerId='p99'})==0); " +
                "assert(#Global.Content.FindInstances({limit=1})==1 and #Global.Content.FindZones({limit=1})==1);");
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(original));
        }

        [Test]
        public void InvalidFiltersArityTypesAndLimitsFailExplicitly()
        {
            var f = Create("core");
            foreach (var expression in new[] { "Global.Content.FindInstances({unknown=true})", "Global.Content.FindZones({unknown='x'})", "Global.Map.FindInfluences({unknown=1})" })
                Failure(f.State, expression, LuaFailureCode.UnexpectedField);
            foreach (var expression in new[] { "Global.Map.GetRoutesBetween('A1')", "Global.Map.GetRoute('A1','A2')", "Global.Map.GetLocation(1)",
                "Global.Content.FindInstances(nil)", "Global.Content.FindZones('hand')", "Global.Map.FindInfluences({}, {})", "Global.Content.FindInstances({owner=1})",
                "Global.Content.FindInstances({includeDescendants=true})", "Global.Content.FindInstances({zoneId='x',includeDescendants='yes'})",
                "Global.Map.FindInfluences({slotIds={'x','x'}})", "Global.Map.FindInfluences({slotIds={[2]='x'}})" })
                Failure(f.State, expression, LuaFailureCode.InvalidFieldType);
            foreach (var value in new[] { "0", "1025", "1.5", "'1'" }) Failure(f.State, "Global.Content.FindInstances({limit=" + value + "})", LuaFailureCode.OutOfBounds);
        }

        [Test]
        public void QueriesKeepSnapshotReadOnlyAndRejectMismatchedRevisionOrUnavailableSources()
        {
            var f = Create("core"); string original = JsonConvert.SerializeObject(f.State);
            Failure(f.State, "Global.Map.GetRoute('A1').hasRoad=true", LuaFailureCode.ReadOnlySnapshot, statement: true);
            Failure(f.State, "Global.Content.FindInstances()[1].zoneId='changed'", LuaFailureCode.ReadOnlySnapshot, statement: true);
            var script = Script("return function(ctx) return {} end"); var context = Context(f.State, script);
            context.FacadeSnapshot.StateRevision = context.StateRevision + 1;
            Assert.That(Host().Invoke(script, context).FailureCode, Is.EqualTo(LuaFailureCode.VersionMismatch));
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(original));
            f.State.ContentDefinitions = null;
            Failure(f.State, "Global.Content.GetDefinition('known')", LuaFailureCode.InvalidDefinition);
            f.State.RuleConfiguration = null;
            Failure(f.State, "GameData.GetEnabledDlcIds()", LuaFailureCode.InvalidDefinition);
            Assert.That(original, Is.Not.Empty);
        }

        [Test]
        public void ExcessResultsFailWithoutImplicitTruncationAndExplicitLimitReturnsRequestedPrefix()
        {
            var f = Create("core"); var script = Script("return function(ctx) local x=Global.Content.FindInstances(); return {} end");
            var context = Context(f.State, script); var items = context.FacadeSnapshot.GlobalContent.Get("instances");
            var sample = items.Items.First();
            while (items.Items.Count <= LuaQueryLimits.MaximumResults) items.Items.Add(sample);
            Assert.That(Host().Invoke(script, context).FailureCode, Is.EqualTo(LuaFailureCode.ReturnLimitExceeded));
            script = Script("return function(ctx) assert(#Global.Content.FindInstances({limit=2})==2); return {} end");
            Assert.That(Host().Invoke(script, context).IsSuccess, Is.True);
        }

        [Test]
        public void EnabledExpansionsAndActiveDefinitionsRemainFrozenAcrossArchiveRecovery()
        {
            foreach (var ids in new[] { new[] { "core" }, new[] { "core", "enterprise" }, new[] { "core", "enterprise", "infected" } })
            {
                var f = Create(ids);
                string checks = "assert(#GameData.GetEnabledDlcIds()==" + ids.Length + "); " +
                    string.Join(" ", ids.Select(id => "assert(GameData.IsDlcEnabled(" + Q(id) + "));")) +
                    " assert(not GameData.IsDlcEnabled('missing')); " +
                    (ids.Contains("infected") ? "assert(Global.Content.GetDefinition('infected.character.muelsyse')~=nil);" : "assert(Global.Content.GetDefinition('infected.character.muelsyse')==nil);");
                Success(f.State, checks);
                var archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State));
                var restored = HostRecoveryService.RecoverJson(archive, f.Registry);
                Assert.That(restored.Succeeded, Is.True, restored.FaultCode + ":" + restored.Diagnostic);
                Success(restored.State, checks);
            }
        }

        [Test]
        public void SameRevisionFreshDispatcherUsesCurrentFactsAndEarlierContextRemainsDetached()
        {
            var f = Create("core", "enterprise"); var route = f.Map.Map.Routes.First();
            var road = Grant(f, "core.road_token", f.State.ItemZones.Zones.First(z => z.LocalZoneId == "reserve"));
            var script = Script("return function(ctx) assert(not Global.Map.IsRoad(" + Q(route.RouteId) + ")); return {} end");
            var old = Context(f.State, script); string frozen = JsonConvert.SerializeObject(old.FacadeSnapshot);
            Position(f.State, road, Child(f.State, route.RouteId, "core.zone.route_road"));
            Success(f.State, "assert(Global.Map.IsRoad(" + Q(route.RouteId) + ")); assert(Global.Map.GetMap().stateRevision==ctx.stateRevision);");
            Assert.That(Host().Invoke(script, old).IsSuccess, Is.True);
            Assert.That(JsonConvert.SerializeObject(old.FacadeSnapshot), Is.EqualTo(frozen));
            Assert.That(old.StateRevision, Is.EqualTo(f.State.EffectRuntime.StateRevision));
        }

        [Test]
        public void FormalEmptyDeployRejectsWithoutStateChangeWhileMinimalPlaceStillRequestsTarget()
        {
            var f = Create("core"); PrepareWindow(f);
            var handler = new DeployInfluenceCommandHandler(f.Influence, new RoundAdvanceService(f.Round), f.Registry);
            foreach (var target in new[] { "", " ", null })
            {
                string before = JsonConvert.SerializeObject(f.State);
                var result = handler.Handle(f.State, new GameCommand { Kind = GameCommandKind.DeployInfluence, PlayerId = 1, TargetId = target });
                Assert.That(result.Succeeded, Is.False); Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
                Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            }
            var engine = new EffectTreeExecutor(f.State, f.Registry); engine.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1, ""));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && !r.IsInternalMainlineInteraction()), Is.True);
            Assert.That(f.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.Zero);
        }

        [Test]
        public void FormalSelectedDeployUsesExactTargetAndConsumesOneMainAction()
        {
            var f = Create("core"); PrepareWindow(f); f.State.FindPlayer(1).RemainingMainActionsThisTurn = 3;
            string target = f.Influence.GetLegalPlacementSlotIds(f.State, 1).First();
            var result = new DeployInfluenceCommandHandler(f.Influence, new RoundAdvanceService(f.Round), f.Registry).Handle(f.State,
                new GameCommand { Kind = GameCommandKind.DeployInfluence, PlayerId = 1, TargetId = target });
            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(f.State.Map.Influences.Any(i => i.PlayerId == 1 && i.SlotId == target), Is.True);
            Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(2));
            Assert.That(f.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1));
        }

        private sealed class Fixture { public GameState State; public IMapQueryService Map; public InfluenceService Influence; public EffectRegistry Registry; public RoundExecutionService Round; }
        private static Fixture Create(params string[] ids)
        {
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            var config = new GameRuleConfiguration { PlayerCount = 3, MapId = StaticMapDefinitions.FourPlayerMapId, EnabledExpansionIds = ids.ToList() };
            var catalog = ActiveContentCatalogBuilder.Build(pack, config); config.ContentPackHash = catalog.ContentPackHash;
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, Enumerable.Range(1, 3).Select(i => new PlayerSeat { PlayerId = i }).ToList(),
                config, 321, catalog.SetupRecipe, catalog.ContentDefinitions, new[] { 1, 2, 3 }, "lua.public_queries." + string.Join(".", ids), null);
            // 隔离查询上下文需要有效行动轮次；初始化状态尚未推进主流程。
            state.Round = 1; state.ActionRound = 1;
            var f = new Fixture { State = state, Map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)), Registry = new EffectRegistry() };
            f.Influence = new InfluenceService(f.Map); InfluenceEffectExecutor.Register(f.Registry, f.Influence); ResourceEffectExecutor.Register(f.Registry);
            return f;
        }
        private static void PrepareWindow(Fixture f)
        {
            var builder = new RoundMainChainBuilder(); var order = new List<int> { 1, 2, 3 };
            var chain = builder.Build(f.State.GameId, builder.CreateRoundExecutionId(f.State.GameId, 2, order), 2, order, 1);
            var window = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && n.PlayerId == 1 && n.ActionRound == 1);
            foreach (var node in chain.Nodes.Where(n => n.MainlineIndex < window.MainlineIndex)) node.Status = EffectNodeStatus.Completed;
            var runtime = f.State.EffectRuntime; runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = 2; runtime.CurrentRoundStartPlayerId = 1;
            runtime.PlayerOrderSnapshot = order; runtime.FirstMainNodeId = chain.FirstNodeId; runtime.MainNodes.AddRange(chain.Nodes); runtime.ActiveMainNodeId = window.NodeId; window.Status = EffectNodeStatus.Ready;
            f.Round = new RoundExecutionService(f.Registry); Assert.That(f.Round.Advance(f.State).IsValid, Is.True);
        }
        private static LuaScriptDefinition Script(string source) => new LuaScriptDefinition("query.content", "query.ability", "query.handler", "1", source, LuaContentHasher.ComputeSha256(source));
        private static MoonSharpLuaRuntimeHost Host() => new MoonSharpLuaRuntimeHost(new LuaExecutionBudget(100000, 2000, 8, 128, 16, 32768));
        private static LuaInvocationContext Context(GameState state, LuaScriptDefinition script)
        {
            var definition = new LuaHandlerDefinition("query.subscription", "QueryEvent", "*", script);
            var dispatcher = new LuaEffectEventDispatcher(new EffectRegistry());
            return (LuaInvocationContext)typeof(LuaEffectEventDispatcher).GetMethod("CreateInvocationContext", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(dispatcher,
                new object[] { definition, new EffectEventHandlerContext(state, new RuleEvent { EventId = "query.event", EventType = "QueryEvent", PlayerId = 1 }, new EffectNodeRuntimeState { PlayerId = 1 }, null) });
        }
        private static void Success(GameState state, string statements)
        {
            var script = Script("return function(ctx) local step=0; local check=assert; assert=function(value,message) step=step+1; return check(value,message or ('query assertion '..step)) end; " + statements + " return {} end");
            var result = Host().Invoke(script, Context(state, script)); Assert.That(result.IsSuccess, Is.True, result.FailureCode + ":" + result.Diagnostic);
        }
        private static void Failure(GameState state, string expression, LuaFailureCode code, bool statement = false)
        {
            var script = Script("return function(ctx) " + (statement ? expression : "local value=" + expression) + "; return {} end");
            var result = Host().Invoke(script, Context(state, script)); Assert.That(result.FailureCode, Is.EqualTo(code), expression + ":" + result.Diagnostic);
        }
        private static string Place(Fixture f, string slot)
        {
            var engine = new EffectTreeExecutor(f.State, f.Registry); string id = engine.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1, slot));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            return engine.GetNode(id).NormalizedResult.Properties.Single(p => p.Name == "influenceId").Value.ReferenceId;
        }
        private static ItemInstanceState Grant(Fixture f, string type, ZoneInstanceState zone)
        {
            const string effectType = "query.fixture.grant";
            if (!f.Registry.TryGet(effectType, out _)) f.Registry.Register(new EffectRegistration(effectType, _ => EffectStepResult.Completed(), EffectExecutorKind.IntrinsicFlow, "1"));
            var engine = new EffectTreeExecutor(f.State, f.Registry); string source = engine.CreateRoot(new EffectSpec(effectType) { PlayerId = 1, DefinitionVersion = "1" });
            string definition = f.State.ContentDefinitions.Components.FirstOrDefault(d => d.TypeId == type)?.DefinitionId;
            if (definition == null && type == "core.resource_point_token") definition = "core.resource_point";
            Assert.That(definition, Is.Not.Null, type);
            Assert.That(new ItemZoneStateFactory().TryCreateGrantedItem(f.State, source, type, definition, type, zone.ZoneId, new ItemSubjectRef(), out var id, out var reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
            return f.State.ItemZones.Items.Single(i => i.ItemId == id);
        }
        private static ZoneInstanceState Child(GameState state, string local, string template)
        {
            var parent = state.ItemZones.Zones.Single(z => z.LocalZoneId == local);
            return state.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == template);
        }
        private static void Position(GameState state, ItemInstanceState item, ZoneInstanceState zone) => typeof(ItemZoneStateMigrator)
            .GetMethod("SetPosition", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { state, item, zone.ZoneId });
        private static string Q(string value) => JsonConvert.SerializeObject(value);
    }
}
