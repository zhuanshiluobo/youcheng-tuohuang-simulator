using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Harvest;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Rules;
using YC.Domain.Travel;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CollectionPlanLifecycleTests
    {
        private static int Total(ResourceSet r)=>r.GoldVoucher+r.Originium+r.OriginiumShard+r.Iron+r.PureOriginium;

        [TestCase(false,StaticMapDefinitions.FourPlayerMapId)]
        [TestCase(true,StaticMapDefinitions.FourPlayerMapId)]
        [TestCase(false,StaticMapDefinitions.ThreePlayerMapId)]
        [TestCase(true,StaticMapDefinitions.ThreePlayerMapId)]
        public void FullFreeNormalAndExtraHarvestUseOnePlanAndRejectDuplicateAnswers(bool extra,string mapId)
        {
            using(var f=new CollectionPlanTestFixture(mapId:mapId))
            {
                string point=f.RemotePoint();f.Put(1,point,true);
                f.State.FindPlayer(1).Resources.GoldVoucher=0;f.State.FindPlayer(1).ResourceCollectionStartGoldVoucher=0;
                var locations=new[]{f.State.FindPlayer(1).CityLocationId,point};
                int before=Total(f.State.FindPlayer(1).Resources);
                int reward=locations.Sum(id=>new ResourceTokenService().FindToken(f.State.Map,id).Amount);
                var engine=f.Harvest(extra,out _);var request=f.Selection();
                f.Submit(engine,locations);
                Assert.That(Total(f.State.FindPlayer(1).Resources),Is.EqualTo(before+reward));
                Assert.That(f.State.FindPlayer(1).HasCollectedResourcesThisRound,Is.EqualTo(!extra));
                string snapshot=f.Snapshot();
                Assert.That(engine.TrySubmitInteraction(request.InteractionId,1,f.State.EffectRuntime.StateRevision,
                    request.NormalizedAnswer.Clone(),out _),Is.False);
                Assert.That(f.Snapshot(),Is.EqualTo(snapshot));
            }
        }

        [Test]
        public void MixedPlanPaysOnlyTheRoutePartitionBeforeGrantingBothRewards()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                string paid=f.AdjacentPoint();
                string free=f.Map.Map.Locations.First(l=>!l.IsRedZone&&l.LocationId!=paid&&f.State.Players.All(p=>p.CityLocationId!=l.LocationId)).LocationId;
                f.Put(1,paid,false);f.Put(1,free,true);
                string start=f.State.FindPlayer(1).CityLocationId;string route=f.Map.FindRoute(start,paid).RouteId;
                int cost=new RouteTollService(f.Map).ReadRouteFacts(f.State,1).Single(r=>r.RouteId==route).TollGoldVoucherAmount;
                int goldBefore=f.State.FindPlayer(1).Resources.GoldVoucher;
                var tokens=new[]{paid,free}.Select(id=>new ResourceTokenService().FindToken(f.State.Map,id)).ToArray();
                int goldReward=tokens.Where(t=>t.ResourceType==ResourceType.GoldVoucher).Sum(t=>t.Amount);
                var engine=f.Harvest(false,out _);f.SubmitPlan(engine,new[]{free,paid},new[]{route});
                Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(goldBefore-cost+goldReward));
                Assert.That(f.State.EffectRuntime.EffectNodes.Last(n=>n.EffectTypeId==RouteResourceSelectionEffectExecutor.TypeId).Status,Is.EqualTo(EffectNodeStatus.Completed));
            }
        }

        [Test]
        public void FreePointCannotSeedASeparateRouteComponent()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                string start=f.State.FindPlayer(1).CityLocationId;
                var near=new HashSet<string>(f.Map.GetAdjacentLocations(start).Select(x=>x.LocationId));
                string free=f.Map.Map.Locations.First(l=>!l.IsRedZone&&l.LocationId!=start&&!near.Contains(l.LocationId)&&
                    f.Map.GetAdjacentLocations(l.LocationId).Any(n=>!n.IsRedZone&&n.LocationId!=start&&!near.Contains(n.LocationId))).LocationId;
                string target=f.Map.GetAdjacentLocations(free).First(l=>!l.IsRedZone&&l.LocationId!=start&&!near.Contains(l.LocationId)&&f.State.Players.All(p=>p.CityLocationId!=l.LocationId)).LocationId;
                f.Put(1,free,true);f.Put(1,target,false);f.Harvest(false,out var owner);
                var authority=new RouteSelectionSessionAuthority(f.Map,f.Registry);
                Assert.That(authority.TryRestore(f.State,f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==owner),out var session,out var binding).IsValid,Is.True);
                string route=f.Map.FindRoute(free,target).RouteId;
                var draft=new RouteSelectionDraft(session.SessionId,0,new[]{route},new[]{free,target},new[]{new RouteRecipientChoice(route,"bank")});
                string before=f.Snapshot();
                Assert.That(new RouteResourceSelectionService(f.Map,f.Registry).TryFinalize(f.State,session,binding,draft,session.CollectionFreeLocations.LegalLocationIds,out _).IsValid,Is.False);
                Assert.That(f.Snapshot(),Is.EqualTo(before));
            }
        }

        [Test]
        public void WaitingSourceExitRejectsOldPlanRatherThanAddingACharge()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                string point=f.RemotePoint();f.Put(1,point,true);f.Harvest(false,out var owner);
                var authority=new RouteSelectionSessionAuthority(f.Map,f.Registry);
                Assert.That(authority.TryRestore(f.State,f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==owner),out var session,out var binding).IsValid,Is.True);
                var draft=new RouteSelectionDraft(session.SessionId,0,Array.Empty<string>(),new[]{point},Array.Empty<RouteRecipientChoice>());
                f.RetireSource(1);string before=f.Snapshot();
                Assert.That(new RouteResourceSelectionService(f.Map,f.Registry).TryFinalize(f.State,session,binding,draft,session.CollectionFreeLocations.LegalLocationIds,out _).IsValid,Is.False);
                Assert.That(draft.SelectedLocationIds,Is.EqualTo(new[]{point}));
                Assert.That(f.Snapshot(),Is.EqualTo(before));
            }
        }

        [Test]
        public void FrozenRecordKeepsIsolatedFreeEvidenceAfterSourceLeaves()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                string point=f.RemotePoint();f.Put(1,point,true);f.Harvest(false,out var ownerId);
                var owner=f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==ownerId);
                var authority=new RouteSelectionSessionAuthority(f.Map,f.Registry);
                Assert.That(authority.TryRestore(f.State,owner,out var session,out var binding).IsValid,Is.True);
                var draft=new RouteSelectionDraft(session.SessionId,0,Array.Empty<string>(),new[]{point},Array.Empty<RouteRecipientChoice>());
                Assert.That(new RouteResourceSelectionService(f.Map,f.Registry).TryFinalize(f.State,session,binding,draft,session.CollectionFreeLocations.LegalLocationIds,out var plan).IsValid,Is.True);
                // 仅记录编码/恢复隔离例：手动Seal不是正常流程的“已付未发”存档。
                var saved=(YC.Domain.Commands.ValidationResult)typeof(RouteSelectionSessionAuthority).GetMethod("Seal",BindingFlags.Instance|BindingFlags.NonPublic)
                    .Invoke(authority,new object[]{f.State,owner,session,plan});
                Assert.That(saved.IsValid,Is.True);f.RetireSource(1);
                Assert.That(authority.TryResolveFinalized(f.State,ownerId,out _,out var restored).IsValid,Is.True);
                Assert.That(restored.AcceptedPreview.Collection.SelectedFreeLocationIds,Is.EqualTo(new[]{point}));
                Assert.That(restored.AcceptedPreview.ConnectivityWitness.Any(w=>w.LocationId==point),Is.False);
                f.Artifacts.WriteLabeledText("免费封存记录",f.Snapshot());
            }
        }

        [Test]
        public void TamperedPartitionOrMissingEvidenceCannotResolveAsASealedPlan()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                string point=f.RemotePoint();f.Put(1,point,true);f.Harvest(false,out var ownerId);
                var owner=f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==ownerId);
                var authority=new RouteSelectionSessionAuthority(f.Map,f.Registry);
                authority.TryRestore(f.State,owner,out var session,out var binding);
                var draft=new RouteSelectionDraft(session.SessionId,0,Array.Empty<string>(),new[]{point},Array.Empty<RouteRecipientChoice>());
                Assert.That(new RouteResourceSelectionService(f.Map,f.Registry).TryFinalize(f.State,session,binding,draft,session.CollectionFreeLocations.LegalLocationIds,out var plan).IsValid,Is.True);
                typeof(RouteSelectionSessionAuthority).GetMethod("Seal",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(authority,new object[]{f.State,owner,session,plan});
                var record=owner.ContinuationData.Properties.Single(p=>p.Name=="routeSelection").Value;
                record.Properties.RemoveAll(p=>p.Name=="collection");
                string before=f.Snapshot();
                Assert.That(authority.TryResolveFinalized(f.State,ownerId,out _,out _).IsValid,Is.False);
                Assert.That(f.Snapshot(),Is.EqualTo(before));
            }
        }

        [Test]
        public void HostArchiveAtTheRealWaitingAndCompletedBoundariesDoesNotDoubleGrant()
        {
            using(var f=new CollectionPlanTestFixture())
            {
                string point=f.RemotePoint();f.Put(1,point,true);f.Harvest(false,out _);
                f.Restore("真实采集等待");int before=Total(f.State.FindPlayer(1).Resources);
                int amount=new ResourceTokenService().FindToken(f.State.Map,point).Amount;
                var engine=new EffectTreeExecutor(f.State,f.Registry);f.Submit(engine,point);
                Assert.That(Total(f.State.FindPlayer(1).Resources),Is.EqualTo(before+amount));
                f.Restore("真实采集完成");var restored=f.Snapshot();
                new EffectTreeExecutor(f.State,f.Registry).RunUntilQuiescent();
                Assert.That(f.Snapshot(),Is.EqualTo(restored));
            }
        }

        [TestCase(ResourceType.GoldVoucher,7)]
        [TestCase(ResourceType.Iron,3)]
        [TestCase(ResourceType.PureOriginium,2)]
        public void LegacyDirectCityFreeCollectionPreservesActualTokenAmount(ResourceType type,int amount)
        {
            var map=new MapQueryService(new GameMapDefinition {MapId="qy009.legacy.amount",MinPlayers=2,MaxPlayers=2,
                Locations=new List<MapLocationDefinition>{new MapLocationDefinition {LocationId="S",InfluenceSlotCount=2}},
                Routes=new List<MapRouteDefinition>(),Regions=new List<MapRegionDefinition>()});
            var state=new GameState {MapId=map.Map.MapId,Phase=GamePhase.ResourceCollection,CurrentPlayerId=1,Round=1,
                Players=new List<PlayerState>{new PlayerState {PlayerId=1,CityLocationId="S",Resources=new ResourceSet()},
                    new PlayerState {PlayerId=2,Resources=new ResourceSet()}}};
            state.Map.ResourceTokens.Add(new ResourceTokenState {LocationId="S",ResourceType=type,Amount=amount});
            var result=new ResourceCollectionService(map).Collect(state,1,new[]{"S"},Array.Empty<string>(),new Dictionary<string,int>());
            Assert.That(result.Succeeded,Is.True,result.Validation.Reason);
            Assert.That(Total(state.FindPlayer(1).Resources),Is.EqualTo(amount));
            Assert.That(result.Payments,Is.Empty);
        }

        [Test]
        public void FreeGoldRewardCannotFinanceAnotherSelectedPointsToll()
        {
            var map=new MapQueryService(new GameMapDefinition {MapId="qy009.legacy.budget",MinPlayers=2,MaxPlayers=2,
                Locations=new List<MapLocationDefinition>{new MapLocationDefinition {LocationId="S",InfluenceSlotCount=2},
                    new MapLocationDefinition {LocationId="T",InfluenceSlotCount=2}},
                Routes=new List<MapRouteDefinition>{new MapRouteDefinition {RouteId="R",FromLocationId="S",ToLocationId="T",
                    CoveredLocationIds=new List<string>{"S","T"},InfluenceSlotCount=2}},Regions=new List<MapRegionDefinition>()});
            var state=new GameState {MapId=map.Map.MapId,Phase=GamePhase.ResourceCollection,CurrentPlayerId=1,Round=1,
                Players=new List<PlayerState>{new PlayerState {PlayerId=1,CityLocationId="S",ResourceCollectionStartGoldVoucher=0,Resources=new ResourceSet()},
                    new PlayerState {PlayerId=2,Resources=new ResourceSet()}}};
            state.Map.ResourceTokens.Add(new ResourceTokenState {LocationId="S",ResourceType=ResourceType.GoldVoucher,Amount=8});
            state.Map.ResourceTokens.Add(new ResourceTokenState {LocationId="T",ResourceType=ResourceType.Iron,Amount=3});
            state.Map.Influences.Add(new InfluencePlacement {InfluenceId="qy009.legacy.marker",LocationId="T",SlotId="location:T:0",
                PlayerId=1,OwnerSubject=RuleSubjectReference.ForPlayer(1)});
            string before=Newtonsoft.Json.JsonConvert.SerializeObject(state);
            var result=new ResourceCollectionService(map).Collect(state,1,new[]{"S","T"},new[]{"R"},new Dictionary<string,int>{{"R",-1}});
            Assert.That(result.Succeeded,Is.False);
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(state),Is.EqualTo(before));
        }
    }
}
