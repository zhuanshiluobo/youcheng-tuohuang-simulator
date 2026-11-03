using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class CharacterLifecycleTests
    {
        private static GameState DoubleUseReady(out EffectRegistry registry)
        {
            var state=State();registry=Registry(tacticType:"persistent",settlementSource:true);var cards=Ids(state,"hand",1);
            Cover(state,registry,cards[0]);Use(state,registry,cards[0],"tactic");Cover(state,registry,cards[1]);Use(state,registry,cards[1],"");
            return state;
        }
        private static void InvokeDoubleUse(GameState state,EffectRegistry registry)
        {
            var option=CharacterSettlementOptionQuery.GetOptions(state,1,state.CharacterUses.Last().UseId).Single();
            Assert.That(new EffectTreeExecutor(state,registry).TryInvokeCharacterSettlementOption(1,option.OptionId,option.TargetUseId,option.StateRevision,out _,out string reason),Is.True,reason);
        }
        private static EffectNodeRuntimeState Consume(GameState state)=>state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==CharacterLifecycleService.ConsumeSourceTypeId);
        private static EffectNodeRuntimeState ConsumptionMove(GameState state)=>state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.ParentEffectId==Consume(state).EffectId&&n.EffectTypeId==TypeId);
        private static void AdvanceConsumption(GameState state,EffectRegistry registry,Func<bool> target)
        {
            var engine=new EffectTreeExecutor(state,registry);for(int i=0;i<300&&!target();i++)Assert.That(engine.Advance(),Is.True,state.EffectRuntime.LastFaultMessage);
            Assert.That(target(),Is.True);
        }
        [TestCase(false)][TestCase(true)]
        public void DoubleUseCommonStepReturnsActualCarrierContentsAndOnlyTheRuleRecordsConsumption(bool facilities)
        {
            var state=DoubleUseReady(out var registry);string source=state.CharacterUses.First().CardInstanceId;
            string[] contents=facilities?Ids(state,"facility_deck").ToArray():new[]{Ids(state,"supply",1)[0]};
            var declaration=new CardZoneDefinition{LocalZoneId="double_use_contents",ZoneTemplateId=facilities?"core.zone.card_facility_storage":"core.zone.card_cube_storage",
                AcceptedItemKinds=new List<ItemPhysicalKind>{facilities?ItemPhysicalKind.Card:ItemPhysicalKind.Cube},AcceptedTypeIds=new List<string>{facilities?"core.facility_card":"core.player_marker"},
                CapacityPolicy="unbounded",OrderingKind=ZoneOrderingKind.Unordered,EntryPolicyId="core.explicit_transfer",ExitPolicyId="core.explicit_transfer",CarrierExitPolicyId="core.return_contents_before_exit",VisibilityPolicyId=ItemZoneVisibility.Owner};
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state,source,declaration,out string storage,out string reason),Is.True,reason);
            Run(state,registry,MoveSpec(1,contents,FindZone(state,facilities?"facility_deck":"supply",facilities?-1:1).ZoneId,storage,"unordered",contents.Length,facilities?"core.facility_card_transfer":"core.cube_storage"));
            InvokeDoubleUse(state,registry);Assert.That(new EffectTreeExecutor(state,registry).RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            var parent=Consume(state);var body=ConsumptionMove(state);Assert.That(parent.PositionChanges,Is.Empty);Assert.That(parent.DefinitionVersion,Is.EqualTo(CharacterDoubleUseConsumption.DefinitionVersion));
            Assert.That(Number(body.NormalizedArguments,"apiVersion"),Is.EqualTo(4));Assert.That(body.CommonItemMovementV4.Receipt.Result.MovedItemIds,Is.EqualTo(new[]{source}));
            Assert.That(body.CommonItemMovementV4.Receipt.LifecycleItems.Select(f=>f.ItemId),Is.EquivalentTo(contents));Assert.That(new ItemZoneQuery().GetItemIds(state,storage),Is.Empty);
            Assert.That(state.CharacterUses.Last().SourceConsumed&&state.CharacterUses.Last().DoubleUseGranted,Is.True);Assert.That(Ids(state,"discard",1),Does.Contain(source));
            state=Restore(state,registry);AnswerRequest(state,registry,"strategy-first");Assert.That(state.FindPlayer(1).Score,Is.EqualTo(8));
        }
        [TestCase("child_created")][TestCase("move_completed")][TestCase("recorded")]
        public void DoubleUseCommonStepRestoresAcrossMovementAndRuleRecordWithoutReplay(string boundary)
        {
            var state=DoubleUseReady(out var registry);InvokeDoubleUse(state,registry);
            AdvanceConsumption(state,registry,()=>state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==CharacterLifecycleService.ConsumeSourceTypeId)&&
                (boundary=="child_created"?ConsumptionMove(state)!=null:boundary=="move_completed"?ConsumptionMove(state)?.Status==EffectNodeStatus.Completed:state.CharacterUses.Last().SourceConsumed));
            if(boundary!="recorded")Assert.That(state.CharacterUses.Last().SourceConsumed,Is.False);Assert.That(state.CharacterUses.Last().DoubleUseGranted,Is.False);
            string source=state.CharacterUses.Last().DoubleUseSourceItemId;state=Restore(state,registry);Assert.That(new EffectTreeExecutor(state,registry).RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            state=Restore(state,registry);Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.ParentEffectId==Consume(state).EffectId&&n.EffectTypeId==TypeId),Is.EqualTo(1));
            Assert.That(ConsumptionMove(state).PositionChanges.Count(p=>p.ItemId==source),Is.EqualTo(1));Assert.That(state.CharacterUses.Last().SourceConsumed&&state.CharacterUses.Last().DoubleUseGranted,Is.True);
        }
        [TestCase("old_version")][TestCase("source_identity")][TestCase("child_link")][TestCase("parent_move")]
        public void DoubleUseCommonStepRejectsOldOrForgedSavedFactsWithoutChangingTheArchive(string damage)
        {
            var state=DoubleUseReady(out var registry);InvokeDoubleUse(state,registry);AdvanceConsumption(state,registry,()=>state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==CharacterLifecycleService.ConsumeSourceTypeId)&&ConsumptionMove(state)?.Status==EffectNodeStatus.Completed);
            string clean=HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));var raw=JObject.Parse(clean);var parent=raw.SelectTokens("$..EffectNodes[*]").OfType<JObject>().First(n=>n.Value<string>("EffectId")==Consume(state).EffectId);
            if(damage=="old_version")parent["DefinitionVersion"]="2.0.0";
            if(damage=="source_identity")parent["NormalizedArguments"]["Properties"].Children<JObject>().Single(p=>p.Value<string>("Name")=="sourceItemId")["Value"]["StringValue"]="forged_source";
            if(damage=="child_link")parent["ChildEffectIds"][0]="missing_common_child";
            if(damage=="parent_move")parent["PositionChanges"]=JArray.FromObject(ConsumptionMove(state).PositionChanges);
            string original=raw.ToString(Formatting.None);var result=HostRecoveryService.RecoverJson(original,registry);Assert.That(result.Succeeded,Is.False);Assert.That(raw.ToString(Formatting.None),Is.EqualTo(original));
            artifacts.WriteLabeledText("双用坏档拒绝_"+damage,original);Assert.That(HostRecoveryService.RecoverJson(clean,registry).Succeeded,Is.True);
        }
        [Test] public void OrdinaryCommonCardMovementDoesNotConsumeOrGrantDoubleUse()
        {
            var state=DoubleUseReady(out var registry);string source=state.CharacterUses.First().CardInstanceId;
            var args=Object("apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString("1"),"operation",NormalizedValue.CreateString("transfer"),
                "selector",Object("mode",NormalizedValue.CreateString("explicit"),"itemIds",Strings(new[]{source}),"expectedSource",Object("zoneId",NormalizedValue.CreateString(FindZone(state,"persistent",1).ZoneId))),
                "destination",Object("zoneId",NormalizedValue.CreateString(FindZone(state,"discard",1).ZoneId)),"insertion",Object("kind",NormalizedValue.CreateString("unordered")),"operationProfileId",NormalizedValue.CreateString("core.character_card_transfer"),"requiredCount",NormalizedValue.CreateInteger(1));
            Run(state,registry,EffectSpec.Create(TypeId,args,1));Assert.That(state.CharacterUses.Last().SourceConsumed||state.CharacterUses.Last().DoubleUseGranted,Is.False);Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==CharacterLifecycleService.ConsumeSourceTypeId),Is.False);
        }
    }
}
