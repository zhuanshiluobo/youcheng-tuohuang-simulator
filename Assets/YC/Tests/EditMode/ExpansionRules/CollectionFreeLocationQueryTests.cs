using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CollectionFreeLocationQueryTests
    {
        [TestCase("normal",StaticMapDefinitions.FourPlayerMapId)]
        [TestCase("extra",StaticMapDefinitions.FourPlayerMapId)]
        [TestCase("normal",StaticMapDefinitions.ThreePlayerMapId)]
        [TestCase("extra",StaticMapDefinitions.ThreePlayerMapId)]
        public void RealLuaAddsOnlyAuthorizedPlayersMapGangPointsAndKeepsStatePure(string purpose,string mapId)
        {
            using(var f=new CollectionPlanTestFixture(mapId:mapId))
            {
                string own=f.RemotePoint(2); f.Put(1,own,true); f.Put(1,own,true);
                string other=f.Map.Map.Locations.First(l=>!l.IsRedZone&&l.LocationId!=own&&f.State.Players.All(p=>p.CityLocationId!=l.LocationId)).LocationId;
                f.Put(2,other,true);
                string before=f.Snapshot();var facts=f.Free(1,purpose);
                Assert.That(facts.FreeLocationIds,Is.EquivalentTo(new[]{f.State.FindPlayer(1).CityLocationId,own}));
                Assert.That(facts.FreeLocationIds,Does.Not.Contain(other));
                Assert.That(facts.Evidence.Where(e=>e.Kind=="ability").All(e=>e.SourceRef.AuthorizedPlayerId==1),Is.True);
                Assert.That(facts.Evidence.Select(e=>e.Identity).Distinct().Count(),Is.EqualTo(facts.Evidence.Count));
                Assert.That(f.Snapshot(),Is.EqualTo(before));
            }
        }

        [Test]
        public void SupplyGangDoesNotGrantFreePointAndSourceExitInvalidatesTheFrozenBasis()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                Assert.That(f.Free().FreeLocationIds,Is.EquivalentTo(new[]{f.State.FindPlayer(1).CityLocationId}));
                string point=f.RemotePoint();f.Put(1,point,true);var original=f.Free();f.RetireSource(1);
                Assert.That(f.Free().FreeLocationIds,Does.Not.Contain(point));
                Assert.That(f.Free().BasisDigest,Is.Not.EqualTo(original.BasisDigest));
            }
        }

        [TestCase("return function(ctx) return {Effect.GainResource({recipient=ctx.playerId,resourceType='GoldVoucher',amount=9})} end")]
        [TestCase("return function(ctx) return {queryId=ctx.payload.queryId,basisDigest=ctx.payload.basisDigest,items={{kind='free_location',locationId='unknown.point'}}} end")]
        [TestCase("return function(ctx) return {queryId=ctx.payload.queryId,basisDigest=ctx.payload.basisDigest,items={{kind='gain_resource',locationId='A-03'}}} end")]
        public void IllegalLuaReturnFailsExplicitlyAndCannotMutateState(string script)
        {
            using(var f=new CollectionPlanTestFixture(script))
            {
                string before=f.Snapshot();
                Assert.Throws<InvalidOperationException>(()=>f.Free());
                Assert.That(f.Snapshot(),Is.EqualTo(before));
            }
        }

        [Test]
        public void UnrelatedCurrentPlayerChangeDoesNotAlterTheFreeFactBasis()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                f.Put(1,f.RemotePoint(),true);var first=f.Free();
                f.State.CurrentPlayerId=2;var second=f.Free();
                Assert.That(second.BasisDigest,Is.EqualTo(first.BasisDigest));
                Assert.That(second.ResultDigest,Is.EqualTo(first.ResultDigest));
            }
        }

        [Test]
        public void PresenterDefaultsFreeSelectionWithoutPretendingTopologicalReachability()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                string point=f.RemotePoint();f.Put(1,point,true);f.Harvest(false,out var owner);
                var authority=new RouteSelectionSessionAuthority(f.Map,f.Registry);
                Assert.That(authority.TryRestore(f.State,f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==owner),out var session,out var binding).IsValid,Is.True);
                var presenter=new RouteResourceSelectionPresenter(()=>f.State,new RouteResourceSelectionService(f.Map));
                presenter.Begin(session,binding,session.CollectionFreeLocations.LegalLocationIds);
                Assert.That(presenter.Draft.SelectedLocationIds,Is.EquivalentTo(new[]{session.StartLocationId,point}));
                var option=presenter.Projection.LocationOptions.Single(x=>x.LocationId==point);
                Assert.That(option.IsFreeCollection,Is.True);
                Assert.That(option.IsReachableInOriginalDraft,Is.False);
                Assert.That(presenter.ToggleLocation(point),Is.True);
                presenter.RefreshFromState();
                Assert.That(presenter.Draft.SelectedLocationIds,Does.Not.Contain(point),"手动取消后刷新不能重新勾选");
                Assert.That(presenter.ToggleLocation(point),Is.True);
                Assert.That(presenter.Draft.SelectedLocationIds,Does.Contain(point));
                Assert.That(presenter.Projection.Preview.PaymentPlan.TotalGoldVoucherAmount,Is.Zero);
                Assert.That(presenter.Projection.Preview.Collection.RouteRequiredLocationIds,Is.Empty);
            }
        }

        // R49：实际P04声明/来源及正式Lua，经真实采集请求投影到共用草稿。
        // 沿既有私有内容/初始位置/预算隔离夹具，不计正常菜单、页面或联机验收。
        [TestCase(false, false)][TestCase(true, false)]
        [TestCase(false, true)][TestCase(true, true)]
        public void R49FormalSourceDefaultsAndUserEditsSurviveRoutesAndRequestRebinding(bool extra, bool activeP04)
        {
            using var f = new PaulContentTestFixture();
            f.FinishPreparation(); f.Choose(3, activeP04 ? "P04" : "P03"); f.DockCity(f.Location);
            var map = new MapQueryService(f.Map);
            var all = f.Map.Locations.Select(l => l.LocationId).ToArray();
            var facts = new RouteTollService(map).ReadRouteFacts(f.State, 1);
            var graph = RouteSelectionGraphQuery.Query(all, facts.Select(r => new RouteGraphRoute(r.RouteId,
                r.CoveredLocationIds, r.FreeReason != RouteFreeReason.None, r.TollGoldVoucherAmount)).ToArray(),
                f.Location, Array.Empty<string>(), all, Array.Empty<string>());
            Assert.That(graph.IsValid, Is.True, graph.Diagnostic);
            var remote = f.Map.Locations.Where(l => !l.IsRedZone && l.InfluenceSlotCount > 0 &&
                !graph.ReachableLocationIds.Contains(l.LocationId) &&
                !f.State.Map.Influences.Any(i => i.SlotId == InfluenceService.GetLocationSlotId(l.LocationId, 0))).Take(2).ToArray();
            Assert.That(remote, Has.Length.EqualTo(2));
            string gangPoint = remote[0].LocationId, paidPoint = remote[1].LocationId;
            f.Place(gangPoint, 0, type: "entrepreneur.gang_marker"); f.Place(paidPoint, 0);
            f.State.Phase = GamePhase.ResourceCollection;
            f.State.FindPlayer(1).Resources.GoldVoucher = f.State.FindPlayer(1).ResourceCollectionStartGoldVoucher = 12;
            f.Artifacts.WriteLabeledText("R49隔离预算", "采集选择前置12金券；正式声明、选面、Lua和请求不替换，不计正常资源取得。");
            RouteResourceSelectionEffectExecutor.Register(f.Registry, map); ResourceCollectionEffectExecutor.Register(f.Registry);
            string root = f.Run(extra ? EffectSpec.Create(ResourceCollectionEffectExecutor.TypeId,
                ItemTransferEffectExecutor.Object("apiVersion", NormalizedValue.CreateInteger(1),
                    "player", NormalizedValue.CreateString("1"), "scope", NormalizedValue.CreateString("extra")), 1)
                : RouteResourceSelectionEffectExecutor.CreateHarvest(1, false, "qy017.r49"));
            var request = f.Request(root, "route_resource.selection");
            var owner = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId);
            var authority = new RouteSelectionSessionAuthority(map, f.Registry);
            Assert.That(authority.TryRestore(f.State, owner, out var session, out var binding).IsValid, Is.True);
            Assert.That(session.Purpose, Is.EqualTo(extra ? RouteSelectionPurpose.ExtraHarvest : RouteSelectionPurpose.NormalHarvest));
            var presenter = new RouteResourceSelectionPresenter(() => f.State, new RouteResourceSelectionService(map));
            string before = JsonConvert.SerializeObject(f.State);
            presenter.Begin(session, binding, session.CollectionFreeLocations.LegalLocationIds);
            Assert.That(presenter.Projection.Validation.IsValid, Is.True, presenter.Projection.Validation.Reason);
            Assert.That(presenter.Draft.SelectedLocationIds, Does.Contain(f.Location));
            var gang = presenter.Projection.LocationOptions.Single(o => o.LocationId == gangPoint);
            Assert.That(gang.IsFreeCollection, Is.EqualTo(activeP04));
            Assert.That(gang.IsReachableInOriginalDraft, Is.False);
            Assert.That(presenter.Draft.SelectedLocationIds.Contains(gangPoint), Is.EqualTo(activeP04));
            Assert.That(presenter.Draft.SelectedLocationIds, Does.Not.Contain(paidPoint));
            Assert.That(presenter.Projection.Preview.PaymentPlan.TotalGoldVoucherAmount, Is.Zero);
            Assert.That(presenter.Projection.Preview.Collection.RouteRequiredLocationIds, Is.Empty);
            Assert.That(presenter.ToggleLocation(f.Location), Is.True);
            if (activeP04) Assert.That(presenter.ToggleLocation(gangPoint), Is.True);
            presenter.RefreshFromState();
            Assert.That(presenter.Draft.SelectedLocationIds, Does.Not.Contain(f.Location));
            Assert.That(presenter.Draft.SelectedLocationIds, Does.Not.Contain(gangPoint));
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before), "草稿默认与编辑不预扣、不发收益、不提交");
            Assert.That(authority.TryRestore(f.State, owner, out var rebound, out var sameBinding).IsValid, Is.True);
            Assert.That(presenter.TryRefreshOpenBinding(rebound, sameBinding, rebound.CollectionFreeLocations.LegalLocationIds, out _), Is.True);
            Assert.That(authority.TryPrepareRequest(f.State, owner, rebound, rebound.CollectionFreeLocations.LegalLocationIds,
                out var nextBinding, out _).IsValid, Is.True);
            Assert.That(presenter.TryRefreshOpenBinding(rebound, nextBinding, rebound.CollectionFreeLocations.LegalLocationIds, out _), Is.True);
            Assert.That(presenter.Draft.SelectedLocationIds, Does.Not.Contain(f.Location));
            Assert.That(presenter.Draft.SelectedLocationIds, Does.Not.Contain(gangPoint));
            if (activeP04) Assert.That(presenter.ToggleLocation(gangPoint), Is.True);
            before = JsonConvert.SerializeObject(f.State);
            var route = presenter.Projection.RouteOptions.First(o => o.FreeReason == RouteFreeReason.None && o.IsFrontier);
            Assert.That(presenter.SelectRoute(route.RouteId), Is.Not.Null);
            Assert.That(presenter.ConfirmRecipient(route.RouteId, route.EligibleRecipients.First().SubjectKey), Is.True);
            Assert.That(presenter.Draft.ConfirmedRouteIds, Does.Contain(route.RouteId));
            // 非免费点可能因刚确认的路网而默认勾选；按原草稿合同显式取消，再核撤路不勾回。
            if (!activeP04 && presenter.Draft.SelectedLocationIds.Contains(gangPoint))
                Assert.That(presenter.ToggleLocation(gangPoint), Is.True);
            Assert.That(presenter.RemoveRoute(route.RouteId), Is.True);
            presenter.RefreshFromState();
            Assert.That(presenter.Draft.SelectedLocationIds.Contains(gangPoint), Is.EqualTo(activeP04));
            Assert.That(presenter.Draft.SelectedLocationIds, Does.Not.Contain(f.Location));
            Assert.That(presenter.Draft.SelectedLocationIds.Count, Is.LessThanOrEqualTo(session.MaximumLocations));
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            Assert.That(request.Status, Is.EqualTo("open"));
        }

        [Test]
        public void R49FormalFreeLuaDoesNotSelectForeignGangOrPointsWithoutResourceTokens()
        {
            string script = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),
                "Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p04.collection_free.lua"));
            // 此例只隔离正式Lua查询合同；P04真实选面/来源由上面的四个用例覆盖。
            using var f = new CollectionPlanTestFixture(freeScript: script,
                resourceLocationFilter: location => location.LocationId == "C-02");
            string own = f.RemotePoint(); f.Put(1, own, true);
            string foreign = "C-02";
            f.Put(2, foreign, true);
            string city = f.State.FindPlayer(1).CityLocationId;
            Assert.That(f.State.Map.ResourceTokens.Select(t => t.LocationId), Is.EqualTo(new[] { foreign }),
                "夹具直接按私有配方只建对手点资源，不把地图物件强移到盒区");
            var free = f.Free();
            Assert.That(free.LegalLocationIds, Does.Not.Contain(city));
            Assert.That(free.LegalLocationIds, Does.Not.Contain(own));
            Assert.That(free.LegalLocationIds, Does.Not.Contain(foreign));
            Assert.That(free.FreeLocationIds, Is.Empty);
            f.Harvest(false, out var owner);
            var authority = new RouteSelectionSessionAuthority(f.Map, f.Registry);
            Assert.That(authority.TryRestore(f.State, f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == owner),
                out var session, out var binding).IsValid, Is.True);
            var presenter = new RouteResourceSelectionPresenter(() => f.State, new RouteResourceSelectionService(f.Map));
            presenter.Begin(session, binding, free.LegalLocationIds);
            Assert.That(presenter.Draft.SelectedLocationIds, Is.Empty);
            Assert.That(presenter.ToggleLocation(foreign), Is.False);
            Assert.That(presenter.ToggleLocation(own), Is.False);
        }

        [Test]
        public void R49DefaultLimitAndExplorationKeepTheirExistingSelectionContracts()
        {
            var map = new MapQueryService(new GameMapDefinition { MapId = "qy017.r49.selection", MinPlayers = 2, MaxPlayers = 2,
                Locations = new List<MapLocationDefinition> { new MapLocationDefinition { LocationId = "S" }, new MapLocationDefinition { LocationId = "T" } },
                Routes = new List<MapRouteDefinition> { new MapRouteDefinition { RouteId = "road", FromLocationId = "S", ToLocationId = "T", CoveredLocationIds = new List<string> { "S", "T" } } },
                Regions = new List<MapRegionDefinition>() });
            var state = new GameState { MapId = map.Map.MapId, Phase = GamePhase.ResourceCollection, CurrentPlayerId = 1,
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, CityLocationId = "S", ResourceCollectionStartGoldVoucher = 10,
                    Resources = new ResourceSet { GoldVoucher = 10 } }, new PlayerState { PlayerId = 2 } } };
            state.Map.RoadRouteIds.Add("road");
            var authority = new RouteSelectionSessionAuthority(map);
            var service = new RouteResourceSelectionService(map);
            var presenter = new RouteResourceSelectionPresenter(() => state, service);
            foreach (bool explore in new[] { false, true })
            {
                var owner = new EffectNodeRuntimeState { EffectId = "qy017.r49." + explore, PlayerId = 1, EffectTypeId = RouteResourceSelectionEffectExecutor.TypeId };
                state.EffectRuntime.EffectNodes.Add(owner);
                var locations = explore ? new[] { "T" } : new[] { "S", "T" };
                var options = new RouteSelectionOptions(explore ? RouteSelectionPurpose.Exploration : RouteSelectionPurpose.NormalHarvest,
                    1, "S", locations, explore ? 1 : 0, 1, explore);
                Assert.That(authority.TryOpen(state, owner, options, out var session).IsValid, Is.True);
                Assert.That(authority.TryPrepareRequest(state, owner, session, locations, out var binding, out _).IsValid, Is.True);
                presenter.Begin(session, binding, locations);
                Assert.That(presenter.Projection.LocationOptions.Single(o => o.LocationId == "T").CanSelect, Is.True);
                if (explore) Assert.That(presenter.Draft.SelectedLocationIds, Is.Empty, "探索仍由玩家手动选择目标");
                else Assert.That(presenter.Draft.SelectedLocationIds, Is.EqualTo(new[] { "S" }), "可达点默认也不能突破上限");
                Assert.That(presenter.ToggleLocation("T"), Is.True);
                presenter.RefreshFromState();
                Assert.That(presenter.Draft.SelectedLocationIds, Is.EqualTo(new[] { "T" }));
                presenter.Clear();
            }
        }

        [Test]
        public void OwnerProjectionWithoutRuntimeKeepsEvenEmptyQuerySourceEvidence()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                f.Harvest(false,out var owner);
                var authority=new RouteSelectionSessionAuthority(f.Map,f.Registry);
                Assert.That(authority.TryRestore(f.State,f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==owner),out var session,out var binding).IsValid,Is.True);
                Assert.That(session.CollectionFreeLocations.QuerySources,Has.Count.EqualTo(1));
                Assert.That(session.CollectionFreeLocations.Evidence.Where(e=>e.Kind=="ability"),Is.Empty);
                var draft=new RouteSelectionDraft(session.SessionId,0,Array.Empty<string>(),
                    new[]{f.State.FindPlayer(1).CityLocationId},Array.Empty<RouteRecipientChoice>());
                var runtime=f.State.EffectRuntime;
                try
                {
                    f.State.EffectRuntime=null;
                    string before=f.Snapshot();
                    Assert.That(new RouteResourceSelectionService(f.Map).TryValidatePlan(f.State,session,binding,draft,
                        session.CollectionFreeLocations.LegalLocationIds,out var preview).IsValid,Is.True);
                    Assert.That(preview.PaymentPlan.TotalGoldVoucherAmount,Is.Zero);
                    Assert.That(f.Snapshot(),Is.EqualTo(before));
                }
                finally {f.State.EffectRuntime=runtime;}
            }
        }
        [TestCase(StaticMapDefinitions.FourPlayerMapId)]
        [TestCase(StaticMapDefinitions.ThreePlayerMapId)]
        public void CompanyContractOnlyAddsNormalEligibilityAndDoesNotMakeTheCompanyPointFree(string mapId)
        {
            using(var f=new CollectionPlanTestFixture(mapId:mapId))
            {
                string point=f.RemotePoint();f.PutCompany(point);
                // 隔离指定已有合约资格；免费判定仍调用上面的真实Lua来源。
                var service=new ResourceCollectionService(f.Map){RuleRegistry=f.Registry,HasCompanyContract=(state,player)=>true};
                var normal=service.GetSelectableLocationIds(f.State,1,false);
                var extra=service.GetSelectableLocationIds(f.State,1,true);
                Assert.That(normal,Does.Contain(point));
                Assert.That(extra,Does.Not.Contain(point));
                string before=f.Snapshot();
                var facts=new CollectionFreeLocationQuery(f.Registry,f.Map).Query(f.State,
                    new CollectionQueryContext(1,"normal","fixture.contract","fixture.contract.session",
                        f.State.FindPlayer(1).CityLocationId,f.State.ContentPackHash,normal));
                Assert.That(facts.FreeLocationIds,Does.Not.Contain(point));
                Assert.That(f.Snapshot(),Is.EqualTo(before));
            }
        }

    }
}
