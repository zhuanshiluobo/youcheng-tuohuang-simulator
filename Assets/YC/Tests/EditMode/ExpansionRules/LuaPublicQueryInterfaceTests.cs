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
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Facilities;
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
                CandidatePoolIds = new List<string> { own.ItemId },
                Context = ItemTransferEffectExecutor.Object("stateRevision", NormalizedValue.CreateInteger(f.State.EffectRuntime.StateRevision)) };
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

        [Test]
        public void FacilityDeckContextKeepsFormalPoolAndOrderingWithoutPersistingQueryGraph()
        {
            var f = CandidateFixture(); var deck = ItemTransferEffectExecutor.FindZone(f.State, "facility_deck");
            var expected = f.State.ItemZones.Items.Where(i => i.ZoneId == deck.ZoneId).Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            string before = JsonConvert.SerializeObject(f.State);
            var snapshot = f.Registry.FacilityBuildCandidates.Resolve(f.State, 1,
                new FacilityBuildCandidateOptions { SourceZone = "deck" }, stateRevision: f.State.EffectRuntime.StateRevision);
            Assert.That(snapshot.FinalItemIds, Is.EqualTo(expected)); Assert.That(snapshot.DefaultItemIds, Is.EquivalentTo(expected));
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            var requestNode = CandidateRun(f, EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId,
                ItemTransferEffectExecutor.Object("sourceZone", NormalizedValue.CreateString("deck"), "paymentMode", NormalizedValue.CreateString("free")), 1));
            var request = CandidateOpen(f);
            Assert.That(request.CandidateIds, Is.EqualTo(expected)); Assert.That(request.PromptParameters.IsValid(), Is.True);
            Assert.That(ItemTransferEffectExecutor.Value(request.PromptParameters, "candidateDefinitionIds").Items.Count, Is.EqualTo(expected.Length));
            Assert.That(f.State.EffectRuntime.CandidateResolutions.Single(r => r.CandidateSetId == request.CandidateSetId).FinalCandidateIds, Is.EqualTo(expected));
            Assert.That(request.StateRevision, Is.EqualTo(f.State.EffectRuntime.StateRevision));
            Assert.That(requestNode.Status, Is.EqualTo(EffectNodeStatus.Blocked));
        }

        [Test]
        public void FacilityCandidatePayloadKeepsFullLocalArraysAndRejectsStaleRevision()
        {
            var f = CandidateFixture(); var deck = ItemTransferEffectExecutor.FindZone(f.State, "facility_deck");
            var ids = f.State.ItemZones.Items.Where(i => i.ZoneId == deck.ZoneId).Select(i => i.ItemId).ToList();
            var outside = f.State.ItemZones.Items.First(i => i.ZoneId != deck.ZoneId);
            var draft = new CandidateSetDraft { CandidateSetId = "query.full-deck", CandidateKind = FacilityBuildCandidateQuery.CandidateKind,
                SourceEffectId = "query.owner", ExecutingPlayerId = 1, CandidatePoolIds = ids, DefaultCandidateIds = ids.ToList(), CurrentIds = ids.ToList(),
                Context = ItemTransferEffectExecutor.Object("stateRevision", NormalizedValue.CreateInteger(f.State.EffectRuntime.StateRevision),
                    "sourceZone", NormalizedValue.CreateString("deck"), "includeStoredFacilities", NormalizedValue.CreateBoolean(true)) };
            var localPayload = (LuaSnapshotValue)typeof(LuaEffectEventDispatcher).GetMethod("CreateCandidatePayload", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { draft });
            Assert.That(CandidateSnapshotElements(localPayload), Is.GreaterThan(NormalizedValue.MaxElements), "正式四人企业＋感染者配方须覆盖完整临时payload超出持久化预算的边界。");
            var script = Script("return function(ctx) assert(#ctx.payload.candidatePoolIds==" + ids.Count +
                " and #ctx.payload.defaultCandidateIds==" + ids.Count + " and #ctx.payload.currentIds==" + ids.Count + "); " +
                "local expected={" + string.Join(",", ids.Select(Q)) + "}; for i,id in ipairs(ctx.payload.candidatePoolIds) do assert(id==expected[i] and id==ctx.payload.defaultCandidateIds[i] and id==ctx.payload.currentIds[i]); assert(Global.Content.GetInstance(id)~=nil) end; " +
                "assert(#Global.Content.FindInstances()==" + ids.Count + " and #Global.Content.FindZones()==1); " +
                "assert(Global.Content.GetInstance(" + Q(outside.ItemId) + ")==nil); assert(Global.Map.GetMap().stateRevision==ctx.stateRevision); " +
                "return {Candidate.Add({ids=ctx.payload.candidatePoolIds,reasonCode='query.full-pool'})} end");
            var definition = new LuaHandlerDefinition("query.full-pool", "CandidateSetBuilding", "*", script);
            var dispatcher = new LuaEffectEventDispatcher(f.Registry); string before = JsonConvert.SerializeObject(f.State);
            var invoke = typeof(LuaEffectEventDispatcher).GetMethod("InvokeCandidatePatches", BindingFlags.Instance | BindingFlags.NonPublic);
            var patches = (IList<CandidatePatch>)invoke.Invoke(dispatcher, new object[] { definition, f.State, draft, outside.ItemId });
            Assert.That(patches.Single().CandidateIds, Is.EqualTo(ids.OrderBy(id => id, StringComparer.Ordinal))); Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            draft.Context.Properties.Single(p => p.Name == "stateRevision").Value = NormalizedValue.CreateInteger(f.State.EffectRuntime.StateRevision + 1);
            Assert.That(Assert.Throws<TargetInvocationException>(() => invoke.Invoke(dispatcher, new object[] { definition, f.State, draft, outside.ItemId })).InnerException.Message,
                Is.EqualTo("nmc_query_revision_mismatch"));
        }

        [Test]
        public void StoredFacilityPolicyUsesRealLuaForPreviewRequestAndOwnerScope()
        {
            var f = CandidateFixture();
            foreach (var zone in f.State.ItemZones.Zones.Where(z => z.TemplateId == "core.zone.facility_supply_slot" && f.State.ItemZones.Items.All(i => i.ZoneId != z.ZoneId)).ToList())
                CandidateRun(f, ItemTransferEffectExecutor.RefillSpec(1, zone.ZoneId));
            var carrier = f.State.ItemZones.Items.Single(i => i.DefinitionId == "operator.closure");
            CandidateRun(f, ItemTransferEffectExecutor.MoveSpec(1, new[] { carrier.ItemId }, carrier.ZoneId,
                ItemTransferEffectExecutor.FindZone(f.State, "hand", 1).ZoneId, "unordered", 1, "core.character_card_transfer"));
            CandidateRun(f, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); CandidateAnswer(f, NormalizedValue.CreateString(carrier.ItemId));
            var activation = CandidateRun(f, CharacterLifecycleService.CreateUseSpec(1, carrier.ItemId, "tactic"));
            Assert.That(activation.Status, Is.EqualTo(EffectNodeStatus.Blocked), activation.FailureReason + "；" +
                string.Join(",", f.State.EffectRuntime.EffectNodes.Select(n => n.EffectTypeId + ":" + n.Status + ":" + n.FailureReason)));
            string held = CandidateOpen(f).CandidateIds.First(); CandidateAnswer(f, NormalizedValue.CreateString(held));
            CandidateAnswer(f, NormalizedValue.CreateBoolean(false));
            var stored = f.State.ItemZones.Items.Single(i => i.ItemId == held);
            Assert.That(f.State.ItemZones.Zones.Single(z => z.ZoneId == stored.ZoneId).ParentId, Is.EqualTo(carrier.ItemId));
            var query = new BuildFacilityOptionQueryService(new BuildFacilityService(), f.Registry.FacilityBuildCandidates);
            string before = JsonConvert.SerializeObject(f.State); var snapshot = query.QueryCandidates(f.State, 1);
            Assert.That(snapshot.FinalItemIds, Does.Contain(held)); Assert.That(snapshot.Resolution.AppliedPatches.Single().SourceContentInstanceId, Is.EqualTo(carrier.ItemId));
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            Assert.That(f.Registry.FacilityBuildCandidates.Resolve(f.State, 2).FinalItemIds, Does.Not.Contain(held));
            Assert.That(f.Registry.FacilityBuildCandidates.Resolve(f.State, 1, new FacilityBuildCandidateOptions { IncludeStoredFacilities = false }).FinalItemIds, Does.Not.Contain(held));
            Assert.That(f.Registry.FacilityBuildCandidates.Resolve(f.State, 1, new FacilityBuildCandidateOptions { SourceZone = "deck" }).FinalItemIds, Does.Not.Contain(held));
            var view = GameStateViewProjector.Project(f.State, GameStateViewer.Player(1));
            var client = GameStateViewProjector.ToClientState(view); client.ContentDefinitions = GameStateCloneService.DeepClone(f.State.ContentDefinitions);
            string clientBefore = JsonConvert.SerializeObject(client); var local = query.QueryCandidates(client, 1, view.StateRevision);
            Assert.That(local.FinalItemIds, Is.EqualTo(snapshot.FinalItemIds)); Assert.That(local.Resolution.StateRevision, Is.EqualTo(view.StateRevision));
            Assert.That(client.EffectRuntime, Is.Null); Assert.That(JsonConvert.SerializeObject(client), Is.EqualTo(clientBefore));
            var expected = snapshot.FinalItemIds.ToArray();
            var selection = CandidateRun(f, EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, playerId: 1));
            Assert.That(CandidateOpen(f).CandidateIds, Is.EqualTo(expected)); Assert.That(CandidateOpen(f).PromptParameters.IsValid(), Is.True);
            Assert.That(CandidateOpen(f).InteractionTypeId, Is.EqualTo(FacilitySelectionEffectExecutor.NormalBuildInteractionType));
            var balances = JsonConvert.SerializeObject(f.State.Players.Select(p => p.Resources));
            string originalSource = stored.ZoneId; var request = CandidateOpen(f);
            Position(f.State, stored, ItemTransferEffectExecutor.FindZone(f.State, "retired"));
            string beforeInvalidSource = JsonConvert.SerializeObject(f.State);
            Assert.That(new EffectTreeExecutor(f.State, f.Registry).TryPrepareEffectBuildPayment(1, selection.EffectId,
                request.GetStableInteractionId(), request.CandidateSetId, request.CandidateSetVersion, held,
                ItemTransferEffectExecutor.Object("paymentMode", NormalizedValue.CreateString("gold"),
                    "expectedSourceZoneId", NormalizedValue.CreateString(originalSource)), f.State.EffectRuntime.StateRevision,
                "stored.source.changed", out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(beforeInvalidSource));
            // 历史候选仍可显示，但已经离区的实例不能获得付款准备授权。
            Assert.That(CandidateOpen(f).CandidateIds, Does.Contain(held));
            Assert.That(JsonConvert.SerializeObject(f.State.Players.Select(p => p.Resources)), Is.EqualTo(balances));
        }

        [Test]
        public void RaytheanDeckBuildKeepsFormalPaymentAndShuffleAfterAllOutcomes()
        {
            foreach (string outcome in new[] { "cancel", "no_resources", "no_slot", "built" })
            {
                var f = CandidateFixture();
                if (outcome == "no_resources") f.State.FindPlayer(1).Resources = new ResourceSet();
                if (outcome == "no_slot")
                {
                    var cells = f.State.ItemZones.Zones.Where(z => z.TemplateId == "core.zone.city_facility_cell" && z.OwnerPlayerId == 1).ToList();
                    foreach (var cell in cells.Where(z => f.State.ItemZones.Items.All(i => i.ZoneId != z.ZoneId)))
                        Position(f.State, f.State.ItemZones.Items.First(i => i.TypeId == "core.facility_card" && i.ZoneId == ItemTransferEffectExecutor.FindZone(f.State, "facility_deck").ZoneId), cell);
                }
                var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
                var ability = pack.Definitions.Single(d => d.DefinitionId == "enterprise.raythean").Abilities.Single(a => a.AbilityId == "enterprise.raythean.level_5.special");
                string source = pack.GetScript(ability.Script);
                var script = new LuaScriptDefinition("enterprise.raythean", ability.AbilityId, "query.raythean", ability.Version, source, LuaContentHasher.ComputeSha256(source));
                new LuaEffectEventDispatcher(f.Registry).Register(new LuaHandlerDefinition("query.raythean", "QueryRaytheanActivated", "", script, EffectHandlerRole.Primary));
                f.Registry.Register("query.raythean.source", c => EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType = "QueryRaytheanActivated", PlayerId = 1 }));
                var deck = ItemTransferEffectExecutor.FindZone(f.State, "facility_deck");
                var expectedIds = f.State.ItemZones.Items.Where(i => i.ZoneId == deck.ZoneId).Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                var supplyIds = f.State.ItemZones.Items.Where(i => f.State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot")).Select(i => i.ItemId).ToArray();
                var player = f.State.FindPlayer(1); var originalResources = JsonConvert.SerializeObject(player.Resources);
                int gold = player.Resources.GoldVoucher, actions = player.RemainingMainActionsThisTurn;
                Func<int> shuffles = () => f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == LuaDomainEffectTypeIds.ShuffleFacilityDeck && n.Status == EffectNodeStatus.Completed);
                var root = CandidateRun(f, EffectSpec.Create("query.raythean.source", playerId: 1));
                var selection = CandidateOpen(f);
                Assert.That(expectedIds, Is.Not.Empty, outcome);
                Assert.That(selection.InteractionTypeId, Is.EqualTo(FacilitySelectionEffectExecutor.NormalBuildInteractionType), outcome);
                Assert.That(selection.CandidateIds, Is.EqualTo(expectedIds), outcome);
                Assert.That(selection.CandidateIds.Distinct().Count(), Is.EqualTo(expectedIds.Length), outcome);
                Assert.That(selection.PromptParameters.IsValid(), Is.True, outcome);
                Assert.That(ItemTransferEffectExecutor.Text(selection.PromptParameters, "expectedSourceZoneId"), Is.EqualTo(deck.ZoneId), outcome);
                Assert.That(root.Status, Is.EqualTo(EffectNodeStatus.Blocked), outcome);
                Assert.That(shuffles(), Is.Zero, outcome);
                Assert.That(JsonConvert.SerializeObject(f.State.FindPlayer(1).Resources), Is.EqualTo(originalResources), outcome);
                string selected = null; int price = 0;
                if (outcome == "cancel") CandidateAnswer(f, NormalizedValue.CreateBoolean(false));
                else
                {
                    selected = expectedIds.First(id => {
                        var definition = FacilityCardDatabase.Get(f.State, f.State.ItemZones.Items.Single(i => i.ItemId == id).DefinitionId);
                        return definition.GoldVoucherCost > 0 && (outcome != "built" || definition.EffectId == "gain_iron_4");
                    });
                    price = FacilityCardDatabase.Get(f.State, f.State.ItemZones.Items.Single(i => i.ItemId == selected).DefinitionId).GoldVoucherCost;
                    var engine = new EffectTreeExecutor(f.State, f.Registry);
                    var arguments = ItemTransferEffectExecutor.Object("paymentMode", NormalizedValue.CreateString("gold"),
                        "expectedSourceZoneId", NormalizedValue.CreateString(deck.ZoneId));
                    string beforePreparation = JsonConvert.SerializeObject(f.State);
                    bool prepared = engine.TryPrepareEffectBuildPayment(1, selection.OwnerEffectId, selection.GetStableInteractionId(),
                        selection.CandidateSetId, selection.CandidateSetVersion, selected, arguments, f.State.EffectRuntime.StateRevision,
                        "query.raythean.prepare." + outcome, out string reason);
                    if (outcome == "no_slot")
                    {
                        Assert.That(prepared, Is.False, outcome);
                        Assert.That(reason, Is.EqualTo("invalid_build_slot"));
                        Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(beforePreparation), outcome);
                        Assert.That(CandidateOpen(f).GetStableInteractionId(), Is.EqualTo(selection.GetStableInteractionId()));
                        Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.Payment?.Preparation?.AuthorizationKind == "effect_build"), Is.False);
                        Assert.That(shuffles(), Is.Zero); CandidateAnswer(f, NormalizedValue.CreateBoolean(false));
                    }
                    else
                    {
                        Assert.That(prepared, Is.True, outcome + ":" + reason);
                        Assert.That(selection.Status, Is.EqualTo("invalidated"), outcome);
                        var payment = f.State.EffectRuntime.EffectNodes.Single(n => n.Payment?.Preparation?.AuthorizationKind == "effect_build");
                        var payInput = CandidateOpen(f);
                        Assert.That(payInput.OwnerEffectId, Is.EqualTo(payment.EffectId), outcome);
                        Assert.That(payInput.InteractionTypeId, Is.EqualTo(ResourceEffectExecutor.PaymentConfirmationInteractionTypeId), outcome);
                        Assert.That(payment.Payment.Preparation.SelectionOwnerEffectId, Is.EqualTo(selection.OwnerEffectId));
                        Assert.That(payment.Payment.Preparation.TargetId, Is.EqualTo(selected));
                        var view = GameStateViewProjector.ProjectForPlayer(f.State, 1).PreparedActionPayments.Single();
                        Assert.That(view.IsReady, Is.True, outcome); Assert.That(view.CanConfirm, Is.EqualTo(outcome == "built"), outcome);
                        Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build), Is.False, outcome);
                        Assert.That(shuffles(), Is.Zero, outcome);
                        Assert.That(JsonConvert.SerializeObject(f.State.FindPlayer(1).Resources), Is.EqualTo(originalResources), outcome);
                        if (outcome == "no_resources")
                        {
                            string beforePay = JsonConvert.SerializeObject(f.State);
                            Assert.That(new EffectTreeExecutor(f.State, f.Registry).TrySubmitInteraction(payInput.GetStableInteractionId(), 1,
                                payInput.StateRevision, NormalizedValue.CreateString("pay"), out _), Is.False);
                            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(beforePay));
                            Assert.That(shuffles(), Is.Zero); CandidateAnswer(f, NormalizedValue.CreateString("decline"));
                            Assert.That(payment.Payment.IsCommitted, Is.False);
                        }
                        else
                        {
                            CandidateAnswer(f, NormalizedValue.CreateString("pay"));
                            var placement = CandidateOpen(f);
                            Assert.That(placement.InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
                            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - price));
                            Assert.That(shuffles(), Is.Zero);
                            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == payment.EffectId).Payment.IsCommitted, Is.True);
                            CandidateAnswer(f, NormalizedValue.CreateString(placement.CandidateIds.First()));
                            Assert.That(f.State.ItemZones.Zones.Single(z => z.ZoneId == f.State.ItemZones.Items.Single(i => i.ItemId == selected).ZoneId).TemplateId, Is.EqualTo("core.zone.city_facility_cell"));
                            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - price));
                            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build).Status, Is.EqualTo(EffectNodeStatus.Completed));
                            string afterBuild = JsonConvert.SerializeObject(f.State);
                            Assert.That(new EffectTreeExecutor(f.State, f.Registry).TrySubmitInteraction(payInput.GetStableInteractionId(), 1,
                                f.State.EffectRuntime.StateRevision, NormalizedValue.CreateString("pay"), out _), Is.False);
                            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(afterBuild));
                        }
                        Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.Payment?.Preparation?.AuthorizationKind == "effect_build"), Is.EqualTo(1), outcome);
                        Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.SourceEffectId == payment.EffectId && e.EventType == ResourcePaymentPreparationService.BeforeResourcePayment), Is.EqualTo(1), outcome);
                    }
                }
                Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False, outcome);
                Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed), outcome);
                Assert.That(shuffles(), Is.EqualTo(1), outcome);
                Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build), Is.EqualTo(outcome == "built" ? 1 : 0), outcome);
                Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.Payment?.Preparation?.AuthorizationKind == "effect_build" && n.Payment.IsCommitted), Is.EqualTo(outcome == "built" ? 1 : 0), outcome);
                Assert.That(f.State.ItemZones.Items.Where(i => i.ZoneId == deck.ZoneId).Select(i => i.ItemId), Is.EquivalentTo(expectedIds.Where(id => outcome != "built" || id != selected)), outcome);
                Assert.That(f.State.ItemZones.Items.Where(i => f.State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot")).Select(i => i.ItemId), Is.EqualTo(supplyIds), outcome);
                Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(actions), outcome);
                if (outcome != "built") Assert.That(JsonConvert.SerializeObject(f.State.FindPlayer(1).Resources), Is.EqualTo(originalResources), outcome);
            }
        }

        private static Fixture CandidateFixture()
        {
            var config = new GameRuleConfiguration { PlayerCount = 4, MapId = StaticMapDefinitions.FourPlayerMapId,
                EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
            var catalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core")), config);
            config.ContentPackHash = catalog.ContentPackHash;
            var f = new Fixture { State = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1,
                Enumerable.Range(1, 4).Select(i => new PlayerSeat { PlayerId = i }).ToList(), config, 321, catalog.SetupRecipe,
                catalog.ContentDefinitions, new[] { 1, 2, 3, 4 }, "query.facility.four-player-infected", null),
                Map = new MapQueryService(StaticMapDefinitions.Resolve(config.MapId)), Registry = new EffectRegistry() };
            f.Influence = new InfluenceService(f.Map); InfluenceEffectExecutor.Register(f.Registry, f.Influence); ResourceEffectExecutor.Register(f.Registry);
            GenericLuaEffectExecutor.Register(f.Registry); FacilityEntryEffectExecutor.Register(f.Registry); CharacterCoverEffectExecutor.Register(f.Registry); catalog.Register(f.Registry);
            f.State.Round = 1; f.State.ActionRound = 1; f.State.Phase = GamePhase.ActionRound1;
            f.State.FindPlayer(1).Resources = new ResourceSet { Originium = 5, OriginiumShard = 2, Iron = 2, GoldVoucher = 20 };
            return f;
        }
        private static int CandidateSnapshotElements(LuaSnapshotValue value) => 1 + value.Items.Sum(CandidateSnapshotElements) + value.Properties.Sum(p => CandidateSnapshotElements(p.Value));
        private static EffectNodeRuntimeState CandidateRun(Fixture f, EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(f.State, f.Registry); string id = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage); return engine.GetNode(id);
        }
        private static InteractionRequest CandidateOpen(Fixture f) => f.State.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
        private static void CandidateAnswer(Fixture f, NormalizedValue answer)
        {
            var request = CandidateOpen(f); var engine = new EffectTreeExecutor(f.State, f.Registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision, answer, out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
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
