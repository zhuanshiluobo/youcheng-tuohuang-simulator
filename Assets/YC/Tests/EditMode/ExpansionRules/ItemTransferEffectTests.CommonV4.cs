using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        private static EffectSpec CommonV4(NormalizedValue selector, string destination, string insertion, int required = 0, string profile = "core.facility_card_transfer", bool reveal = false)
        {
            var args = Object("apiVersion", NormalizedValue.CreateInteger(4), "player", NormalizedValue.CreateString("1"), "operation", NormalizedValue.CreateString("transfer"),
                "selector", selector, "destination", Object("zoneId", NormalizedValue.CreateString(destination)), "insertion", Object("kind", NormalizedValue.CreateString(insertion)),
                "operationProfileId", NormalizedValue.CreateString(profile), "requiredCount", NormalizedValue.CreateInteger(required));
            if (reveal) args.Properties.Add(new NormalizedValueEntry { Name = "reveal", Value = NormalizedValue.CreateBoolean(true) });
            args.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name)); return EffectSpec.Create(TypeId, args, 1);
        }
        private static NormalizedValue TopV4(GameState state, int n = 1) => Object("mode", NormalizedValue.CreateString("top"), "zoneId", NormalizedValue.CreateString(Zone(state, "facility_deck").ZoneId), "count", NormalizedValue.CreateInteger(n), "itemTypeId", NormalizedValue.CreateString("core.facility_card"));
        private static NormalizedValue SelectV4(IEnumerable<string> zones, string type = "core.facility_card", int count = 1, bool decline = false) => Object("mode", NormalizedValue.CreateString("select"), "zoneIds", Strings(zones), "itemTypeId", NormalizedValue.CreateString(type), "ownerFilter", NormalizedValue.CreateString("any"), "count", NormalizedValue.CreateInteger(count), "allowDecline", NormalizedValue.CreateBoolean(decline));
        private static NormalizedValue ExplicitV4(string source, params string[] ids) => Object("mode", NormalizedValue.CreateString("explicit"), "itemIds", Strings(ids), "expectedSource", Object("zoneId", NormalizedValue.CreateString(source)));
        [TestCase(0)][TestCase(1)][TestCase(9)]
        public void CommonV4TopBindsRealInstancesAndEmptyDeckDoesNotConsumeRandom(int count)
        {
            var state = State(count); var registry = Registry(); var original = Ids(state, "facility_deck"); string slot = Zone(state, "facility_supply_0").ZoneId;
            var random = JsonConvert.SerializeObject(state.RandomRuntime);
            var node = Run(state, CommonV4(TopV4(state), slot, "top"), registry);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(node.CommonItemMovementV4.Items.Select(f => f.ItemId), Is.EqualTo(original.Take(1)));
            Assert.That(Number(node.NormalizedResult, "movedCount"), Is.EqualTo(Math.Min(1,count))); Assert.That(Text(node.NormalizedResult, "outcome"), Is.EqualTo("completed"));
            Assert.That(Ids(state, "facility_deck"), Is.EqualTo(original.Skip(1))); Assert.That(new ItemZoneQuery().GetItemIds(state, slot), Is.EqualTo(original.Take(1)));
            Assert.That(JsonConvert.SerializeObject(state.RandomRuntime), Is.EqualTo(random)); state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(Ids(state, "facility_deck"), Is.EqualTo(original.Skip(1))); Assert.That(JsonConvert.SerializeObject(state.RandomRuntime), Is.EqualTo(random));
        }
        [TestCase("v4_bound")][TestCase("v4_committed")]
        public void CommonV4TopColdRestoresBoundAndCommittedWithoutDrawingAgain(string stage)
        {
            var state = State(); var registry = Registry(); var ids = Ids(state, "facility_deck"); string slot = Zone(state, "facility_supply_0").ZoneId;
            var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(CommonV4(TopV4(state), slot, "top"));
            for (int i=0;i<20 && engine.GetNode(root).FlowStage!=stage;i++) Assert.That(engine.Advance(), Is.True);
            Assert.That(engine.GetNode(root).FlowStage, Is.EqualTo(stage)); state = Restore(state, registry);
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(new ItemZoneQuery().GetItemIds(state, slot), Is.EqualTo(ids.Take(1))); Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root).PositionChanges.Count, Is.EqualTo(1)); Restore(state,registry);
        }
        [Test]
        public void CommonV4TopRevalidatesFrozenOrderInsteadOfDrawingNewTop()
        {
            var state=State(); var registry=Registry(); var engine=new EffectTreeExecutor(state,registry); string root=engine.CreateRoot(CommonV4(TopV4(state),Zone(state,"facility_supply_0").ZoneId,"top"));
            while(engine.GetNode(root).FlowStage!="v4_bound") Assert.That(engine.Advance(),Is.True);
            state=Restore(state,registry);
            // 隔离源变化反例：改变真实有序源，不计正常玩法或合法洗牌覆盖。
            var order=state.ItemZones.Orders.Single(o=>o.ZoneId==Zone(state,"facility_deck").ZoneId); order.ItemIds.Reverse();order.OrderRevision++;
            engine=new EffectTreeExecutor(state,registry);Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(root).FailureReason,Is.EqualTo("source_order_changed"));Assert.That(engine.GetNode(root).PositionChanges,Is.Empty);
        }
        [TestCase(false)][TestCase(true)]
        public void MoveItemRejectsInteractiveSelectionWithoutCreatingRequest(bool decline)
        {
            var state=State();Fill(state);var registry=Registry();
            var spec=CommonV4(SelectV4(Enumerable.Range(0,6).Select(n=>Zone(state,"facility_supply_"+n).ZoneId),decline:decline),Zone(state,"facility_deck").ZoneId,"bottom",1);
            string before=JsonConvert.SerializeObject(state.ItemZones);
            Assert.That(ItemMovementV4.ValidateArguments(spec.NormalizedArguments),Is.EqualTo("item_move_selection_not_supported"));
            var node=Run(state,spec,registry);Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(node.FailureReason,Is.EqualTo("item_move_selection_not_supported"));
            Assert.That(JsonConvert.SerializeObject(state.ItemZones),Is.EqualTo(before));Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);
        }
        [Test]
        public void MoveItemRejectsSelectionEvenForHiddenSource()
        {
            var state=State();var spec=CommonV4(SelectV4(new[]{Zone(state,"facility_deck").ZoneId}),Zone(state,"retired").ZoneId,"unordered");
            var node=Run(state,spec);Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(node.FailureReason,Is.EqualTo("item_move_selection_not_supported"));
            Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);
        }
        [TestCase(false)][TestCase(true)]
        public void MoveItemCoverRejectsInteractiveSelectorAtPublicBoundary(bool main)
        {
            var state=State();var args=Object("apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString("1"),"operation",NormalizedValue.CreateString("cover_character"),
                "selector",SelectV4(new[]{Zone(state,"hand",1).ZoneId},"core.character_card"),
                "cover",Object("mode",NormalizedValue.CreateString(main?"main":"additional"),"acceptedTypeId",NormalizedValue.CreateString("core.character_card"),"reusable",NormalizedValue.CreateBoolean(false),"blockedActionRoundSequence",NormalizedValue.CreateInteger(0)));
            Assert.That(ItemMovementV4.ValidateArguments(args),Is.EqualTo("item_move_selection_not_supported"));
            var catalog=new LuaEffectTypeCatalog();Assert.That(catalog.TryGet(TypeId,out var entry),Is.True);
            Assert.That(entry.Validator(LuaEffectSpec.FromArguments(TypeId,args)),Is.EqualTo("item_move_selection_not_supported"));
            var node=Run(state,EffectSpec.Create(TypeId,args,1));Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(node.PositionChanges,Is.Empty);Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);
        }
        [TestCase(false)][TestCase(true)]
        public void MoveItemPriorSelectionContractVersionIsRejectedWithoutRewritingArchive(bool journal)
        {
            var state=State();var registry=Registry();var archive=HostRecoveryService.CreateArchive(state);
            var node=Run(state,CommonV4(TopV4(state),Zone(state,"facility_supply_0").ZoneId,"top"),registry);
            if(journal)HostRecoveryService.AppendJournal(archive,state,state.ContentPackHash);else archive=HostRecoveryService.CreateArchive(state);
            string clean=HostRecoveryService.SerializeArchive(archive);var raw=JObject.Parse(clean);
            var checkpoint=journal?raw["Journal"]["Entries"].Last(e=>e["StateAfter"] is JObject)["StateAfter"]:raw["Snapshot"]["State"];
            checkpoint["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==node.EffectId)["DefinitionVersion"]="4.14.0";
            string original=raw.ToString();var rejected=HostRecoveryService.RecoverJson(original,registry);
            Assert.That(rejected.Succeeded,Is.False);Assert.That(rejected.Diagnostic,Does.Contain("item_move_contract_version_unsupported"));Assert.That(raw.ToString(),Is.EqualTo(original));
            Assert.That(HostRecoveryService.RecoverJson(clean,registry).Succeeded,Is.True);
        }
        [Test]
        public void CommonV4CharacterTransferPreservesOwnerAndEmitsOneTransferAndReveal()
        {
            var state=State();var registry=Registry(); CharacterLifecycleService.Register(registry);string card=Ids(state,"hand",1)[0];
            var node=Run(state,CommonV4(ExplicitV4(Zone(state,"hand",1).ZoneId,card),Zone(state,"hand",2).ZoneId,"unordered",1,"core.character_card_transfer",true),registry);
            var item=state.ItemZones.Items.Single(i=>i.ItemId==card);Assert.That(item.OwnerSubject.PlayerId,Is.EqualTo(1));Assert.That(item.ControllerSubject.PlayerId,Is.EqualTo(2));
            var fact=node.CommonItemMovementV4.Receipt.Items.Single();Assert.That(fact.Controller.PlayerId,Is.EqualTo(1));Assert.That(fact.ControllerAfter.PlayerId,Is.EqualTo(2));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.SourceEffectId==node.EffectId&&e.EventType=="character.transferred"),Is.EqualTo(1));Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.SourceEffectId==node.EffectId&&e.EventType=="character.cards.revealed"),Is.EqualTo(1));
            state=Restore(state,registry);new EffectTreeExecutor(state,registry).RunUntilQuiescent();Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.EventType=="character.transferred"),Is.EqualTo(1));
        }
        [TestCase("old_definition")][TestCase("receipt")][TestCase("missing_receipt")][TestCase("controller")][TestCase("summary")][TestCase("missing_field")][TestCase("bound_ids")]
        public void CommonV4RecoveryRejectsChangedReceiptIntentAndRawDefaults(string fault)
        {
            var state=State();var registry=Registry();var node=Run(state,CommonV4(TopV4(state),Zone(state,"facility_supply_0").ZoneId,"top"),registry);
            string clean=HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));var raw=JObject.Parse(clean);var saved=raw["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==node.EffectId);var plan=saved["CommonItemMovementV4"];
            if(fault=="old_definition")saved["DefinitionVersion"]="4.1.0";
            if(fault=="receipt")plan["Receipt"]["ReceiptId"]="foreign";
            if(fault=="missing_receipt")plan["Receipt"]=JValue.CreateNull();
            if(fault=="controller")plan["Receipt"]["Items"][0]["ControllerAfter"]["PlayerId"]=2;
            if(fault=="missing_field")((JObject)plan["Receipt"]["Items"][0]).Property("ControllerAfter").Remove();
            if(fault=="bound_ids")plan["BoundOrders"][0]["ItemIds"][0]="not-original-top";
            if(fault=="summary")((JArray)saved["NormalizedResult"]["Properties"]).Single(p=>(string)p["Name"]=="movedCount")["Value"]["IntegerValue"]=0;
            string bad=raw.ToString();artifacts.WriteLabeledText("共同v4坏档_"+fault,bad);Assert.That(HostRecoveryService.RecoverJson(bad,registry).Succeeded,Is.False);Assert.That(raw.ToString(),Is.EqualTo(bad));Assert.That(HostRecoveryService.RecoverJson(clean,registry).Succeeded,Is.True);
        }
        [Test]
        public void CommonV4BatchSwapsOccupiedFacilitySlotsAtomically()
        {
            var state=State();var registry=Registry();Fill(state,2);var a=Zone(state,"facility_supply_0").ZoneId;var b=Zone(state,"facility_supply_1").ZoneId;var first=new ItemZoneQuery().GetItemIds(state,a).Single();var second=new ItemZoneQuery().GetItemIds(state,b).Single();
            NormalizedValue Leg(string id,string from,string to){var v=CommonV4(ExplicitV4(from,id),to,"top",1).NormalizedArguments.Clone();v.Properties.RemoveAll(p=>new[]{"apiVersion","player","operation"}.Contains(p.Name));return v;}
            var spec=EffectSpec.Create(TypeId,Object("apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString("1"),"operation",NormalizedValue.CreateString("transfer_batch"),"transfers",NormalizedValue.CreateArray(new[]{Leg(first,a,b),Leg(second,b,a)}),"expectedOrders",NormalizedValue.CreateArray(new NormalizedValue[0])),1);
            var node=Run(state,spec,registry);Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(Number(node.NormalizedResult,"movedCount"),Is.EqualTo(2));Assert.That(new ItemZoneQuery().GetItemIds(state,a),Is.EqualTo(new[]{second}));Assert.That(new ItemZoneQuery().GetItemIds(state,b),Is.EqualTo(new[]{first}));Restore(state,registry);
        }
        [Test]
        public void MainCoverBusinessOwnsWholePoolBeyond128BeforeExplicitMove()
        {
            var state=State(characters:140);var registry=Registry();YC.Domain.Cards.CharacterCoverEffectExecutor.Register(registry);
            var node=Run(state,EffectSpec.Create(YC.Domain.Cards.CharacterCoverEffectExecutor.EffectTypeId,playerId:1),registry);
            var request=Open(state);Assert.That(request.OwnerEffectId,Is.EqualTo(node.EffectId));Assert.That(request.CandidateIds.Count,Is.EqualTo(140));
            Assert.That(node.ChildEffectIds,Is.Empty);Assert.That(node.PositionChanges,Is.Empty);
            state=Restore(state,registry);string selected=Open(state).CandidateIds.Last();AnswerRequest(state,registry,selected);
            node=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==node.EffectId);
            var move=YC.Domain.Cards.MainCoverAuthority.Body(state,node);Assert.That(move,Is.Not.Null);
            Assert.That(Text(Value(move.NormalizedArguments,"selector"),"mode"),Is.EqualTo("explicit"));
            Assert.That(move.CharacterCoverMovementV4.Receipt.Items.Single().ItemId,Is.EqualTo(selected));
            Assert.That(state.EffectRuntime.InteractionRequests.Where(r=>r.OwnerEffectId==move.EffectId),Is.Empty);Restore(state,registry);
        }
        [TestCase("occupied")][TestCase("insufficient")][TestCase("wrong_type")]
        public void CommonV4TopRejectsInvalidDemandAndOccupiedSlotWithoutMoving(string fault)
        {
            var state=State(1);var registry=Registry();if(fault=="occupied")Fill(state,1);var selector=TopV4(state,fault=="insufficient"?2:1);if(fault=="wrong_type")Value(selector,"itemTypeId").StringValue="core.character_card";
            var before=JsonConvert.SerializeObject(state.ItemZones);var node=Run(state,CommonV4(selector,Zone(state,"facility_supply_0").ZoneId,"top",fault=="insufficient"?2:0),registry);
            Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(node.PositionChanges,Is.Empty);Assert.That(JsonConvert.SerializeObject(state.ItemZones),Is.EqualTo(before));
        }
        [Test]
        public void CommonV4WaitsForRealRequiredTransferResponseAndRestoresOnce()
        {
            var state=State();var registry=Registry();string card=Ids(state,"hand",1)[0];
            registry.Register("api30.ack",c=>c.Node.FlowStage==""?EffectStepResult.Continue("answered").AddInteraction(new EffectInteractionSpec{InteractionTypeId="api30.ack",AnsweringPlayerId=2,Visibility="owner",AnswerSchema="candidate_id",MinSelections=1,MaxSelections=1,CandidateIds={"confirm"}}):EffectStepResult.Completed(),EffectExecutorKind.IntrinsicFlow);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("api30.ack","character.transferred","*","api30.ack",c=>new List<EffectSpec>{EffectSpec.Create("api30.ack",playerId:2)},role:EffectHandlerRole.Observer));
            var node=Run(state,CommonV4(ExplicitV4(Zone(state,"hand",1).ZoneId,card),Zone(state,"hand",2).ZoneId,"unordered",1,"core.character_card_transfer"),registry);
            Assert.That(node.Status,Is.Not.EqualTo(EffectNodeStatus.Completed));Assert.That(Open(state).AnsweringPlayerId,Is.EqualTo(2));Assert.That(Open(state).OwnerEffectId,Is.Not.EqualTo(node.EffectId));state=Restore(state,registry);AnswerRequest(state,registry,"confirm");
            node=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==node.EffectId);Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(node.PositionChanges.Count,Is.EqualTo(1));Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.EventType=="character.transferred"),Is.EqualTo(1));Restore(state,registry);
        }
        [Test]
        public void CommonV4ExplicitReturnsOtherOwnersLastMarkerAndWaitsForActualCarrierExit()
        {
            var state=State();var registry=Registry();CharacterLifecycleService.Register(registry);string card=Ids(state,"hand",2)[0],marker=Ids(state,"supply",2)[0];string storage=MarkerStorageV4(state,card,true);
            Run(state,MoveSpec(2,new[]{card},Zone(state,"hand",2).ZoneId,Zone(state,"persistent",2).ZoneId,"unordered",1,"core.character_card_transfer"),registry);
            Run(state,EnterpriseEffectTests.MarkerSpecV4(1,EnterpriseEffectTests.MarkerLegV4(marker,Zone(state,"supply",2).ZoneId,storage)),registry);
            var selector=ExplicitV4(storage,marker);var spec=CommonV4(selector,Zone(state,"supply",2).ZoneId,"unordered",1,"core.player_marker_storage");
            var root=Run(state,spec,registry).EffectId;state=Restore(state,registry);
            var node=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root);Assert.That(node.ItemMovementV4.Receipt.CarrierExits.Single().PlayerId,Is.EqualTo(2));Assert.That(Ids(state,"discard",2),Does.Contain(card));Assert.That(Ids(state,"supply",2),Does.Contain(marker));Assert.That(node.PlayerId,Is.EqualTo(1));Restore(state,registry);
        }
        [TestCase("version")][TestCase("receipt")][TestCase("raw_default")]
        public void CommonV4JournalRejectsVersionReceiptAndRequiredRawFields(string fault)
        {
            var state=State();var registry=Registry();var archive=HostRecoveryService.CreateArchive(state);var node=Run(state,CommonV4(TopV4(state),Zone(state,"facility_supply_0").ZoneId,"top"),registry);
            HostRecoveryService.AppendJournal(archive,state,state.ContentPackHash);string clean=HostRecoveryService.SerializeArchive(archive);var raw=JObject.Parse(clean);
            var checkpoint=raw["Journal"]["Entries"].Last(e=>e["StateAfter"] is JObject)["StateAfter"];var saved=checkpoint["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==node.EffectId);
            if(fault=="version")saved["DefinitionVersion"]="4.1.0";
            if(fault=="receipt")saved["CommonItemMovementV4"]["Receipt"]=JValue.CreateNull();
            if(fault=="raw_default")((JObject)saved["CommonItemMovementV4"]["Receipt"]).Property("Outcome").Remove();
            string bad=raw.ToString();var failed=HostRecoveryService.RecoverJson(bad,registry);artifacts.WriteLabeledText("共同v4日志坏档_"+fault,bad);Assert.That(failed.Succeeded,Is.False);Assert.That(raw.ToString(),Is.EqualTo(bad));
            if(fault=="version")Assert.That(failed.Diagnostic,Is.EqualTo("item_move_contract_version_unsupported"));
            var restored=HostRecoveryService.RecoverJson(clean,registry);Assert.That(restored.Succeeded,Is.True,restored.Diagnostic);Assert.That(new EffectTreeExecutor(restored.State,registry).RunUntilQuiescent().Faulted,Is.False);Assert.That(restored.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==node.EffectId).PositionChanges.Count,Is.EqualTo(1));
        }
        [Test]
        public void CommonV4SecretDestinationKeepsCompleteHostFactsOutOfPublicViews()
        {
            var state=State(1);var registry=Registry();string card=Ids(state,"facility_deck")[0];var spec=CommonV4(TopV4(state),Zone(state,"secret_retired").ZoneId,"unordered",1);spec.Visibility=GameStateVisibilityPolicy.Public;
            var node=Run(state,spec,registry);Assert.That(node.Visibility,Is.EqualTo(GameStateVisibilityPolicy.Owner));Assert.That(node.CommonItemMovementV4.Receipt.Items.Single().ItemId,Is.EqualTo(card));
            Assert.That(JsonConvert.SerializeObject(GameStateViewProjector.ProjectForSpectator(state)),Does.Not.Contain(card));Assert.That(JsonConvert.SerializeObject(GameStateViewProjector.ProjectForPlayer(state,2)),Does.Not.Contain(card));Restore(state,registry);
        }
        [Test]
        public void CommonV4RealLuaTopAndSelectionCompileWithStrictMutuallyExclusiveFields()
        {
            var state=State();string body="return function(ctx) return {Effect.MoveItem({apiVersion=4,player=ctx.playerId,operation='transfer',selector={mode='top',zoneId='"+Zone(state,"facility_deck").ZoneId+"',count=1,itemTypeId='core.facility_card'},destination={zoneId='"+Zone(state,"facility_supply_0").ZoneId+"'},insertion={kind='top'},operationProfileId='core.facility_card_transfer',requiredCount=0})} end";
            var result=new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript(body),new LuaInvocationContext("api30","isolated","p1",0,"1.0.0",2,0,0));Assert.That(result.IsSuccess,Is.True,result.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(result,new LuaEffectCompilationContext{PlayerId=1},out var effects,out var fault),Is.True,fault?.Diagnostic);Assert.That(Run(state,effects.Single()).Status,Is.EqualTo(EffectNodeStatus.Completed));
            var args=effects.Single().NormalizedArguments.Clone();Value(args,"selector").Properties.Add(new NormalizedValueEntry{Name="itemIds",Value=Strings(new[]{"fake"})});Assert.That(ItemMovementV4.ValidateArguments(args),Is.Not.Empty);
        }
    }
}
