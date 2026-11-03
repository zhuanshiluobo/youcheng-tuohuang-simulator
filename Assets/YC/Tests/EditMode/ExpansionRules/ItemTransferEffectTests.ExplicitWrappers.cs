using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Domain.Interactions;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        private static EffectSpec ExplicitCycleV4(GameState state,string itemId)=>EffectSpec.Create(LuaDomainEffectTypeIds.MoveFacilityCard,
            Object("apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString("1"),"operation",NormalizedValue.CreateString("cycle_supply"),
                "selector",ExplicitV4(state.ItemZones.Items.Single(i=>i.ItemId==itemId).ZoneId,itemId)),1);
        private static EffectSpec BranchChoice(string id,EffectSpec effect,string kind="facility.supply.select")
        {
            var choice=EffectSpec.Create(LuaDomainEffectTypeIds.Choice,Object("player",NormalizedValue.CreateString("1"),"options",Strings(new[]{id}),"interactionType",NormalizedValue.CreateString(kind)),1);
            choice.NestedEffectGroups.Add(new List<EffectSpec>{effect});return choice;
        }
        [TestCase(false)][TestCase(true)]
        public void ExplicitCycleDoesNotChooseAndKeepsOriginalSlotRefill(bool stacked)
        {
            var state=State();Fill(state,1);string slot=Zone(state,"facility_supply_0").ZoneId;string under=Ids(state,"facility_supply_0")[0];
            if(stacked)Run(state,MoveSpec(1,new[]{Ids(state,"facility_deck")[0]},Zone(state,"facility_deck").ZoneId,slot,"top",1));
            string selected=Ids(state,"facility_supply_0")[0],next=Ids(state,"facility_deck")[0];var registry=Registry();
            var parent=Run(state,ExplicitCycleV4(state,selected),registry);
            Assert.That(parent.Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);
            Assert.That(Ids(state,"facility_supply_0"),Is.EqualTo(new[]{stacked?under:next}));Assert.That(Ids(state,"facility_deck").Last(),Is.EqualTo(selected));
            Assert.That(Flag(parent.NormalizedResult,"refilled"),Is.EqualTo(!stacked));Assert.That(Text(parent.NormalizedResult,"selectedItemId"),Is.EqualTo(selected));
            Assert.That(parent.PositionChanges,Is.Empty);Restore(state,registry);
        }
        [TestCase(3)][TestCase(4)]
        public void FacilityMovementCannotCreateSelectionWhenTargetIsOmitted(int api)
        {
            var state=State();Fill(state,1);string before=JsonConvert.SerializeObject(state.ItemZones);
            var args=Object("apiVersion",NormalizedValue.CreateInteger(api),"player",NormalizedValue.CreateString("1"),"operation",NormalizedValue.CreateString("cycle_supply"));
            Assert.That(ValidateArguments(LuaDomainEffectTypeIds.MoveFacilityCard,args),Is.EqualTo(api==3?RetiredFacilitySelection:"movement_explicit_target_required"));
            var node=Run(state,EffectSpec.Create(LuaDomainEffectTypeIds.MoveFacilityCard,args,1));
            Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);Assert.That(JsonConvert.SerializeObject(state.ItemZones),Is.EqualTo(before));
        }
        [TestCase(1)][TestCase(2)]
        public void StyleMarkerMovementCannotCreateSelectionWhenTargetIsOmitted(int api)
        {
            var state=State();var registry=Registry();ContentLifecycleEffectExecutor.Register(registry);
            var args=Object("apiVersion",NormalizedValue.CreateInteger(api),"player",NormalizedValue.CreateString("1"));
            Assert.That(ContentLifecycleEffectExecutor.ValidateArguments(ContentLifecycleEffectExecutor.ReturnMarkerTypeId,args),Is.EqualTo(api==1?ContentLifecycleEffectExecutor.RetiredReturnSelection:"movement_explicit_target_required"));
            Assert.That(new EffectTreeExecutor(state,registry).TryCreateRoot(EffectSpec.Create(ContentLifecycleEffectExecutor.ReturnMarkerTypeId,args,1),"","",out _),Is.False);Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);
        }
        [Test]
        public void IndependentCardChoiceOwnsRequestBeforeExplicitCycleAndRestoresOnce()
        {
            var state=State();Fill(state,1);string selected=Ids(state,"facility_supply_0")[0];var registry=Registry();GenericLuaEffectExecutor.Register(registry);
            string before=JsonConvert.SerializeObject(state.ItemZones);var choice=Run(state,BranchChoice(selected,ExplicitCycleV4(state,selected)),registry);
            var request=Open(state);Assert.That(request.OwnerEffectId,Is.EqualTo(choice.EffectId));Assert.That(request.InteractionTypeId,Is.EqualTo("facility.supply.select"));
            Assert.That(choice.ChildEffectIds,Is.Empty);Assert.That(JsonConvert.SerializeObject(state.ItemZones),Is.EqualTo(before));
            state=Restore(state,registry);AnswerRequest(state,registry,selected);var move=state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==LuaDomainEffectTypeIds.MoveFacilityCard);
            Assert.That(move.ParentEffectId,Is.EqualTo(choice.EffectId));Assert.That(state.EffectRuntime.InteractionRequests.Where(r=>r.OwnerEffectId==move.EffectId),Is.Empty);
            Assert.That(move.Status,Is.EqualTo(EffectNodeStatus.Completed));Restore(state,registry);
        }
        [Test]
        public void IndependentFacilityChoiceUsesSupplyModelWithoutAnyArtworkLookup()
        {
            var state=State();Fill(state,1);string id=Ids(state,"facility_supply_0")[0];var registry=Registry();GenericLuaEffectExecutor.Register(registry);
            Run(state,BranchChoice(id,ExplicitCycleV4(state,id)),registry);var request=Open(state);
            var projection=InteractionRequestProjector.ProjectForPlayer(request,1);var items=new ItemZoneQuery().Project(state,GameStateViewer.Player(1));
            var modelType=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Assembly-CSharp").GetType("YC.Presentation.FacilitySupplySelectionModel",true);
            var args=new object[]{projection,items,1,request.StateRevision,null};
            Assert.That((bool)modelType.GetMethod("TryCreate").Invoke(null,args),Is.True);
            Assert.That((bool)modelType.GetMethod("CanSelect").Invoke(args[4],new object[]{id}),Is.True);
            artifacts.WriteLabeledText("无卡面解析的路由资格","只验证正式设施类型进入独立供应选择模型，不传入或依赖贴图。实际缺图卡牌画面未在本用例验收；未实例化普通条目、覆盖预制体或运行生成器。");
        }
        [TestCase(17)][TestCase(257)]
        public void MarkerChoiceTreeKeepsEveryFrozenEntryWithinSixteenPerNode(int count)
        {
            artifacts.WriteLabeledText("结构边界范围","以冻结描述符验证17及257项分组结构，不计合法标记候选、实际返还或正常页面覆盖；真实资格及单枚移动由其他用例验证。");
            var flow=typeof(CityStyleMarkerReturnChoice);var target=flow.GetNestedType("Target",System.Reflection.BindingFlags.NonPublic);
            var list=(System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(target));
            var ids=Enumerable.Range(0,count).Select(i=>"frozen-marker-"+i.ToString("D4")).ToList();
            foreach(var id in ids)
            {
                var value=Activator.CreateInstance(target,true);target.GetField("Id").SetValue(value,id);target.GetField("Zone").SetValue(value,"frozen-source");target.GetField("Slot").SetValue(value,"");target.GetField("Style").SetValue(value,"frozen-style");list.Add(value);
            }
            var root=(EffectSpec)flow.GetMethod("Build",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,new object[]{1,list});
            var actual=new List<string>();
            void Visit(EffectSpec spec)
            {
                var options=Value(spec.NormalizedArguments,"options").Items;Assert.That(options.Count,Is.InRange(1,16));Assert.That(spec.NestedEffectGroups.Count,Is.EqualTo(options.Count));Assert.That(spec.NormalizedArguments.IsValid(),Is.True);
                for(int i=0;i<options.Count;i++)
                {
                    Assert.That(spec.NestedEffectGroups[i].Count,Is.EqualTo(1));var child=spec.NestedEffectGroups[i][0];
                    if(child.EffectTypeId==LuaDomainEffectTypeIds.Choice)Visit(child);
                    else{Assert.That(child.EffectTypeId,Is.EqualTo(ContentLifecycleEffectExecutor.ReturnMarkerTypeId));string id=Value(Value(child.NormalizedArguments,"selector"),"itemIds").Items[0].StringValue;Assert.That(options[i].StringValue,Is.EqualTo(id));actual.Add(id);}
                }
            }
            Visit(root);Assert.That(actual,Is.EqualTo(ids));Assert.That(actual.Distinct().Count(),Is.EqualTo(count));
        }
        [Test]
        public void FailedActualChoiceBranchCannotUnlockConditionReward()
        {
            var state=State();Fill(state,1);string selected=Ids(state,"facility_supply_0")[0];var registry=Registry();GenericLuaEffectExecutor.Register(registry);
            var bad=ExplicitCycleV4(state,selected);Value(Value(Value(bad.NormalizedArguments,"selector"),"expectedSource"),"zoneId").StringValue=Zone(state,"facility_supply_1").ZoneId;
            var choice=BranchChoice(selected,bad,"effect.choice.execute");
            Run(state,EffectSpec.Condition(new[]{choice},new[]{ResourceEffectSpecFactory.GainScore(1,10)}),registry);
            state=Restore(state,registry);AnswerRequest(state,registry,selected);
            Assert.That(state.FindPlayer(1).Score,Is.Zero);Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==LuaDomainEffectTypeIds.Choice).Status,Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(Ids(state,"facility_supply_0"),Does.Contain(selected));Restore(state,registry);
        }
        [TestCase(false)][TestCase(true)]
        public void PreviousChoiceOutcomeContractIsRejectedInSnapshotAndJournal(bool journal)
        {
            var state=State();Fill(state,1);var registry=Registry();GenericLuaEffectExecutor.Register(registry);var archive=HostRecoveryService.CreateArchive(state);
            string selected=Ids(state,"facility_supply_0")[0];var choice=Run(state,BranchChoice(selected,ExplicitCycleV4(state,selected)),registry);
            if(journal)HostRecoveryService.AppendJournal(archive,state,state.ContentPackHash);else archive=HostRecoveryService.CreateArchive(state);
            var raw=JObject.Parse(HostRecoveryService.SerializeArchive(archive));var checkpoint=journal?raw["Journal"]["Entries"].Last(e=>e["StateAfter"] is JObject)["StateAfter"]:raw["Snapshot"]["State"];
            checkpoint["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==choice.EffectId)["DefinitionVersion"]="1.0.0";
            string original=raw.ToString();var recovered=HostRecoveryService.RecoverJson(original,registry);
            Assert.That(recovered.Succeeded,Is.False);Assert.That(recovered.Diagnostic,Is.EqualTo("choice_effect_outcome_version_unsupported"));Assert.That(raw.ToString(),Is.EqualTo(original));
        }
    }
}
