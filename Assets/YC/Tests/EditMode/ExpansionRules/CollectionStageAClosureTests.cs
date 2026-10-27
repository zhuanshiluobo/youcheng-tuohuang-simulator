using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Cards;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离机制闭环；真实请求/付款/离场调度，不计正常游戏或页面验收。
    public sealed class CollectionStageAClosureTests
    {
        [Test] public void OneRegistryRejectsTheSameBanAtPaymentPreparationConfirmationPromotionAndRealFreeRaid()
        {
            using(var f=new CollectionPlanTestFixture()) {
                f.State.Phase=GamePhase.ActionRound1;f.State.FindPlayer(1).RemainingMainActionsThisTurn=1;
                string target=f.AdjacentPoint(2),start=f.State.FindPlayer(1).CityLocationId;f.Put(1,target,false);
                var influence=new InfluenceService(f.Map);var movement=new CityMovementService(f.Map,influence,new TravelCostService(f.Map)){DestinationRegistry=f.Registry};
                Assert.That(movement.CanRaidCityForCharacter(f.State,1,target).IsValid,Is.True);
                Assert.That(ResourcePaymentPreparationService.TryQuote(f.State,1,"city_move",target,Object(),out _,out var reason,f.Registry,"qy007.registry-quote"),Is.True,reason);
                var rounds=new RoundExecutionService(f.Registry);Assert.That(rounds.PrepareActionWindowForSmoke(f.State,1).IsValid,Is.True);
                var engine=rounds.CreateCommandExecutor(f.State);
                Assert.That(engine.TryPrepareActionPayment(1,"city_move",target,Object(),f.State.EffectRuntime.StateRevision,"qy007.registry-prepare",out string owner,out _,out _,out reason),Is.True,reason);
                var payment=engine.GetNode(owner);Assert.That(ResourcePaymentPreparationService.CanConfirm(f.State,payment,f.Registry),Is.True);
                var control=GameStateCloneService.DeepClone(f.State);
                Assert.That(new EffectTreeExecutor(control,f.Registry).TryCreatePreparedPlayerActionEffect(1,owner,CityMoveEffectSpecFactory.Move(1,target),"","qy007.registry-positive",out _,out reason),Is.True,reason);
                // 隔离同revision的真实来源/地图事实变化，不注入付款或已完成节点。
                f.Put(1,target,true);string before=f.Snapshot();
                Assert.That(ResourcePaymentPreparationService.TryQuote(f.State,1,"city_move",target,Object(),out _,out _,f.Registry,"qy007.registry-quote"),Is.False);
                Assert.That(ResourcePaymentPreparationService.CanConfirm(f.State,payment,f.Registry),Is.False);
                Assert.That(engine.TryCreatePreparedPlayerActionEffect(1,owner,CityMoveEffectSpecFactory.Move(1,target),"","qy007.registry-banned",out _,out _),Is.False);
                Assert.That(movement.CanRaidCityForCharacter(f.State,1,target).IsValid,Is.False);Assert.That(movement.RaidCityForCharacter(f.State,1,target).Succeeded,Is.False);
                Assert.That(f.Snapshot(),Is.EqualTo(before));Assert.That(f.State.FindPlayer(1).CityLocationId,Is.EqualTo(start));Assert.That(engine.GetNode(owner).Payment.IsCommitted,Is.False);
                f.Restore("同registry准备确认promote免费突袭一致拒绝并保留未付请求");
            }
        }
        private static void PutRoute(CollectionPlanTestFixture f,int player,string route)
        {
            string slot=InfluenceService.GetRouteSlotId(route,0);
            var zone=f.State.ItemZones.Zones.Single(z=>z.LegalSlotIds.Contains(slot));
            var item=f.State.ItemZones.Items.First(i=>i.TypeId=="core.player_marker" && i.OwnerSubject.PlayerId==player && i.ZoneId==FindZone(f.State,"supply",player).ZoneId);
            typeof(ItemZoneStateMigrator).GetMethod("SetPosition",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,new object[]{f.State,item,zone.ZoneId});item.SlotId=slot;
        }
        private static void AssertResources(CollectionPlanTestFixture f,ResourceSet before,IEnumerable<string> points,int fee)
        {
            var expected=before.Clone();expected.GoldVoucher-=fee;
            foreach(string id in points){var token=new ResourceTokenService().FindToken(f.State.Map,id);expected.Set(token.ResourceType,expected.Get(token.ResourceType)+token.Amount);}
            foreach(var t in PaymentResources.Types)Assert.That(f.State.FindPlayer(1).Resources.Get(t),Is.EqualTo(expected.Get(t)));
        }
        [Test] public void OneSharedPhysicalRouteChargesOnceForTwoRealSelectedResourcePoints()
        {
            using(var f=new CollectionPlanTestFixture()) {
                var route=new RouteTollService(f.Map).ReadRouteFacts(f.State,1).First(r=>r.CoveredLocationIds.Count>=3 && r.CoveredLocationIds.Count(id=>!f.Map.GetLocation(id).IsRedZone && f.State.Players.All(p=>p.PlayerId==1 || p.CityLocationId!=id))>=3);
                var points=route.CoveredLocationIds.Where(id=>!f.Map.GetLocation(id).IsRedZone && f.State.Players.All(p=>p.PlayerId==1 || p.CityLocationId!=id)).Take(3).ToArray();
                f.SetCity(1,points[0]);f.Put(1,points[1],false);f.Put(1,points[2],false);
                var before=f.State.FindPlayer(1).Resources.Clone();var engine=f.Harvest(false,out string owner);
                f.Restore("共享航道两个真实点未付恢复");engine=new EffectTreeExecutor(f.State,f.Registry);f.SubmitPlan(engine,points.Skip(1).ToArray(),new[]{route.RouteId});
                var authority=new RouteSelectionSessionAuthority(f.Map,f.Registry);
                Assert.That(authority.TryResolveFinalized(f.State,owner,out _,out var plan).IsValid,Is.True);
                Assert.That(plan.AcceptedPreview.PaymentPlan.Lines.Count,Is.EqualTo(1));Assert.That(plan.AcceptedPreview.PaymentPlan.TotalGoldVoucherAmount,Is.EqualTo(2));
                AssertResources(f,before,points.Skip(1),2);f.Restore("共享航道只收费一次且完整收益恢复");
            }
        }
        [Test] public void DistinctRealRouteOwnersReceiveTheirOwnFeesBeforeTheWholeSelectionRewards()
        {
            using(var f=new CollectionPlanTestFixture()) {
                var facts=new RouteTollService(f.Map).ReadRouteFacts(f.State,1);
                var pair=(from a in facts from b in facts where a.RouteId!=b.RouteId
                    from middle in a.CoveredLocationIds.Intersect(b.CoveredLocationIds)
                    from start in a.CoveredLocationIds.Except(b.CoveredLocationIds)
                    from end in b.CoveredLocationIds.Except(a.CoveredLocationIds)
                    where new[]{middle,start,end}.All(id=>!f.Map.GetLocation(id).IsRedZone && f.State.Players.All(p=>p.PlayerId==1 || p.CityLocationId!=id))
                    select new{a,b,middle,start,end}).First();
                f.SetCity(1,pair.start);f.Put(1,pair.middle,false);f.Put(1,pair.end,false);PutRoute(f,2,pair.a.RouteId);PutRoute(f,3,pair.b.RouteId);
                var before=f.State.FindPlayer(1).Resources.Clone();int p2=f.State.FindPlayer(2).Resources.GoldVoucher,p3=f.State.FindPlayer(3).Resources.GoldVoucher;
                var engine=f.Harvest(false,out string owner);f.SubmitPlan(engine,new[]{pair.middle,pair.end},new[]{pair.a.RouteId,pair.b.RouteId},
                    new[]{new RouteRecipientChoice(pair.a.RouteId,"player:2"),new RouteRecipientChoice(pair.b.RouteId,"player:3")});
                AssertResources(f,before,new[]{pair.middle,pair.end},4);Assert.That(f.State.FindPlayer(2).Resources.GoldVoucher,Is.EqualTo(p2+2));Assert.That(f.State.FindPlayer(3).Resources.GoldVoucher,Is.EqualTo(p3+2));
                f.Restore("不同真实收款者各收一次且采集不重付");string after=f.Snapshot();Assert.That(new EffectTreeExecutor(f.State,f.Registry).RunUntilQuiescent().Faulted,Is.False);Assert.That(f.Snapshot(),Is.EqualTo(after));
            }
        }
        [Test] public void MultipleFreeSourcesUnionIndependentOfLuaOrderAndOneWithdrawalRetainsTheOther()
        {
            const string other="infected.component.influence_lock_token";
            using(var f=new CollectionPlanTestFixture(configure:(token,replacements,manifest)=>{
                var second=JObject.Parse(File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),"Assets/StreamingAssets/Content/core/component/"+other+".json")));
                second["abilitySources"]=JArray.FromObject(new[]{new AbilitySourceDeclaration {AbilityId="qy009.second.free",Kind="ruleQuery",HandlerRef="qy009.second.free",DeclarationVersion="1.0.0",AuthorizationPolicy="owner",PresentationRef=other,
                    EffectiveZones=new List<string>{"core.zone.box_reserve"},QueryKinds=new List<string>{CollectionFreeLocationQuery.QueryKind}}});
                second["abilityHandlers"]=new JArray(new JObject{["handlerId"]="qy009.second.free",["version"]="1.0.0",["script"]="lua/qy009_second_free.lua"});
                replacements["component/"+other+".json"]=second.ToString();replacements["lua/qy009_second_free.lua"]=CollectionPlanTestFixture.FreeScript.Replace("return {queryId", "table.sort(items,function(a,b) return a.locationId>b.locationId end); return {queryId");
            },extraRecipe:new[]{new ItemRecipeEntry {EntryId="second-source",DefinitionId=other,TypeId="infected.influence_lock_token",Quantity=1,PerPlayer=true,InitialZoneRole="reserve"}})) {
                string a=f.RemotePoint(),b=f.AdjacentPoint();f.Put(1,a,true);f.Put(1,b,true);var both=f.Free();
                Assert.That(both.QuerySources.Count,Is.EqualTo(2));Assert.That(both.FreeLocationIds,Does.Contain(a).And.Contain(b));
                Assert.That(both.Evidence.Count(e=>e.Kind=="ability" && e.LocationId==a),Is.EqualTo(2));
                f.RetireSource(1);var remaining=f.Free();Assert.That(remaining.QuerySources.Count,Is.EqualTo(1));Assert.That(remaining.FreeLocationIds,Is.EqualTo(both.FreeLocationIds));
                f.Restore("多免费来源撤一保另一来源证据");
                var item=f.State.ItemZones.Items.Single(i=>i.DefinitionId==other && i.OwnerSubject.PlayerId==1);
                item.ZoneId=FindZone(f.State,"retired").ZoneId;var none=f.Free();Assert.That(none.QuerySources,Is.Empty);Assert.That(none.FreeLocationIds,Is.EqualTo(new[]{f.State.FindPlayer(1).CityLocationId}));
                f.Artifacts.WriteLabeledText("来源退出事实隔离说明","只改变私有夹具的真实来源位置验证撤销；不计正常流程，不注入付款或完成节点。");
            }
        }
        [Test] public void OldSchemaOneRequestRestoresWithoutNewFreeFactsAndRejectsDamagedFields()
        {
            using(var f=new CollectionPlanTestFixture()) {
                var spec=RouteResourceSelectionEffectExecutor.CreateHarvest(1,false,"qy007.old-schema1-model");spec.NormalizedArguments.Properties.RemoveAll(p=>p.Name=="collectionPlanProtocol");
                var engine=new EffectTreeExecutor(f.State,f.Registry);string id=engine.CreateRoot(spec);Assert.That(engine.RunUntilQuiescent().Faulted,Is.False);
                var authority=new RouteSelectionSessionAuthority(f.Map,f.Registry);Assert.That(authority.TryRestore(f.State,engine.GetNode(id),out var session,out _).IsValid,Is.True);Assert.That(session.SchemaVersion,Is.EqualTo(1));Assert.That(session.CollectionFreeLocations,Is.Null);
                f.Restore("旧schema1原请求恢复不加新规则");
                foreach(string field in new[]{"startGold","schemaVersion","unexpected"}) {
                    var bad=GameStateCloneService.DeepClone(f.State);var record=Value(bad.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id).ContinuationData,"routeSelection");
                    if(field=="unexpected")record.Properties.Add(new NormalizedValueEntry{Name=field,Value=NormalizedValue.CreateInteger(1)});
                    else if(field=="startGold")record.Properties.RemoveAll(p=>p.Name==field);else Value(record,field).Kind=NormalizedValueKind.String;
                    Assert.That(authority.TryRestore(bad,bad.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id),out _,out _).IsValid,Is.False);
                }
                f.Submit(new EffectTreeExecutor(f.State,f.Registry),f.State.FindPlayer(1).CityLocationId);f.Restore("旧schema1实际同笔采集完成恢复");
            }
        }
        [TestCase(false)] [TestCase(true)] public void RealDepartureResponseChangesTheBanBeforeOrdinaryOrFreeCityPositionCommit(bool free)
        {
            using(var f=new CollectionPlanTestFixture(configure:(token,replacements,manifest)=>{
                ((JArray)token["abilitySources"]).Add(JObject.FromObject(new AbilitySourceDeclaration {AbilityId="qy009.real-response",Kind="event",HandlerRef="qy009.real-response",DeclarationVersion="1.0.0",AuthorizationPolicy="owner",PresentationRef=CollectionPlanTestFixture.Token,
                    EffectiveZones=new List<string>{"core.zone.box_reserve"},ObservedEvents=new List<string>{"InfluenceRemoved"},AllowedOperations=new List<string>{"response.optional_effect","response.self"}}));
                ((JArray)token["abilityHandlers"]).Add(new JObject{["handlerId"]="qy009.real-response",["version"]="1.0.0",["script"]="lua/qy009_real_response.lua"});
                replacements["lua/qy009_real_response.lua"]=@"return function(ctx) if ctx.payload.ownerPlayerId~=tonumber(string.sub(ctx.playerId,2)) or ctx.payload.itemTypeId~='entrepreneur.gang_marker' then return {} end
local location=string.match(ctx.payload.fromSlotId,'^location:(.+):%d+$');if not location then return {} end
return {Effect.Choice({player=ctx.playerId,interactionType='effect.choice.execute',branches={{id='block',effects={
Effect.PlaceInfluence({executingPlayer=ctx.playerId,ownerSubject=ctx.playerId,influenceSource='qy009.response.gang',itemTypeId='entrepreneur.gang_marker',targetLocationId=location}),
Effect.PlaceInfluence({executingPlayer=ctx.playerId,ownerSubject=ctx.playerId,influenceSource='qy009.response.ordinary',itemTypeId='core.player_marker',targetLocationId=location})}},{id='leave',effects={}}}})} end";
            })) {
                f.State.Phase=GamePhase.ActionRound1;string start=f.State.FindPlayer(1).CityLocationId,target=f.AdjacentPoint(2);f.Put(2,target,true);
                int before=f.State.FindPlayer(1).Resources.OriginiumShard;var engine=new EffectTreeExecutor(f.State,f.Registry);engine.CreateRoot(CityMoveEffectSpecFactory.Move(1,target,free,false,"qy007.real-response"));
                var pending=engine.RunUntilQuiescent();Assert.That(pending.Faulted,Is.False,f.State.EffectRuntime.LastFaultMessage);
                Assert.That(f.State.EffectRuntime.EffectNodes.Where(n=>n.EffectTypeId==CityMoveEffectTypeIds.Move).Single().Status,Is.Not.EqualTo(EffectNodeStatus.Failed),
                    string.Join(";",f.State.EffectRuntime.EffectNodes.Where(n=>n.Status==EffectNodeStatus.Failed).Select(n=>n.EffectTypeId+":"+n.FailureReason+":"+n.NormalizedResult?.ToDeterministicString())));
                Assert.That(f.State.EffectRuntime.DeferredResponses.Single().Status,Is.EqualTo(DeferredResponseStatus.Pending));Assert.That(f.State.FindPlayer(1).CityLocationId,Is.EqualTo(start));
                int paid=before-f.State.FindPlayer(1).Resources.OriginiumShard;if(free)Assert.That(paid,Is.Zero);else Assert.That(paid,Is.GreaterThan(0));
                f.Restore("真实006响应待答城市位置仍未提交");engine=new EffectTreeExecutor(f.State,f.Registry);var request=f.State.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");
                Assert.That(request.AnsweringPlayerId,Is.EqualTo(2));Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),2,request.StateRevision,NormalizedValue.CreateString("block"),out var reason),Is.True,reason);
                Assert.That(engine.RunUntilQuiescent().Faulted,Is.True);Assert.That(f.State.EffectRuntime.LastFaultCode,Is.EqualTo("city_destination_commit_rejected"));
                Assert.That(f.State.FindPlayer(1).CityLocationId,Is.EqualTo(start));Assert.That(f.State.FindPlayer(1).Resources.OriginiumShard,Is.EqualTo(before-paid));f.Restore("响应改变禁入后保留真实付款和首次故障");
            }
        }
    }
}
