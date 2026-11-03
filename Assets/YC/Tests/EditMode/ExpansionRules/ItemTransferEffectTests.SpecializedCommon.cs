using System;
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
    public sealed partial class ItemTransferEffectTests
    {
        // 隔离规则/恢复，不计正常独立程序或画面验收。
        [TestCase(false)][TestCase(true)]
        public void SpecializedCharacterUsesOnePublicV4BodyAndKeepsOriginalResult(bool reveal)
        {
            var initial=State();var registry=Registry();string card=Ids(initial,"hand",1)[0];
            var state=GameStateCloneService.DeepClone(initial);var dedicated=CharacterMove(state,card,Zone(state,"hand",2).ZoneId,false);
            if(reveal){dedicated.NormalizedArguments.Properties.Add(new NormalizedValueEntry{Name="reveal",Value=NormalizedValue.CreateBoolean(true)});dedicated.NormalizedArguments.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));}
            var node=Run(state,dedicated,registry);var body=SpecializedItemMovement.Body(state,node);
            Assert.That(node.PositionChanges,Is.Empty);Assert.That(body.NormalizedArguments.IsValid(),Is.True);
            Assert.That(Number(body.NormalizedArguments,"apiVersion"),Is.EqualTo(4));Assert.That(body.CommonItemMovementV4.Receipt.Items.Single().ItemId,Is.EqualTo(card));
            Assert.That(node.ChildEffectIds,Does.Contain(body.EffectId));
            var plain=GameStateCloneService.DeepClone(initial);var actual=Run(plain,EffectSpec.Create(TypeId,body.NormalizedArguments.Clone(),1),registry);
            Assert.That(JsonConvert.SerializeObject(state.ItemZones),Is.EqualTo(JsonConvert.SerializeObject(plain.ItemZones)));
            Assert.That(JsonConvert.SerializeObject(node.NormalizedResult),Is.EqualTo(JsonConvert.SerializeObject(Object("changed",Value(actual.NormalizedResult,"changed").Clone(),"movedCount",Value(actual.NormalizedResult,"movedCount").Clone(),"movedItemIds",Strings(actual.CommonItemMovementV4.Receipt.Result.MovedItemIds)))));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.EventType=="character.transferred"),Is.EqualTo(1));Restore(state,registry);
        }
        [TestCase("child_args",false)][TestCase("child_id",false)][TestCase("result",false)][TestCase("private_move",false)]
        [TestCase("old_definition",false)][TestCase("old_definition",true)]
        public void SpecializedCharacterRecoveryRejectsChangedWrapperAndOldDefinitions(string fault,bool journal)
        {
            var state=State();var registry=Registry();string card=Ids(state,"hand",1)[0];var archive=HostRecoveryService.CreateArchive(state);
            var node=Run(state,CharacterMove(state,card,Zone(state,"hand",2).ZoneId,false),registry);var body=SpecializedItemMovement.Body(state,node);
            if(journal)HostRecoveryService.AppendJournal(archive,state,state.ContentPackHash);else archive=HostRecoveryService.CreateArchive(state);
            string clean=HostRecoveryService.SerializeArchive(archive);var raw=JObject.Parse(clean);
            var checkpoint=journal?raw["Journal"]["Entries"].Last(e=>e["StateAfter"] is JObject)["StateAfter"]:raw["Snapshot"]["State"];
            var nodes=checkpoint["EffectRuntime"]["EffectNodes"];var parent=nodes.Single(n=>(string)n["EffectId"]==node.EffectId);var child=nodes.Single(n=>(string)n["EffectId"]==body.EffectId);
            if(fault=="child_args")child["NormalizedArguments"]["Properties"].Single(e=>(string)e["Name"]=="requiredCount")["Value"]["IntegerValue"]=0;
            if(fault=="child_id")parent["ChildEffectIds"][0]="foreign";
            if(fault=="result")parent["NormalizedResult"]["Properties"].Single(e=>(string)e["Name"]=="movedCount")["Value"]["IntegerValue"]=2;
            if(fault=="private_move")parent["PositionChanges"]=JToken.FromObject(body.PositionChanges);
            if(fault=="old_definition")parent["DefinitionVersion"]="2.2.0";
            string bad=raw.ToString();artifacts.WriteLabeledText("专用角色坏档_"+fault+"_"+journal,bad);
            Assert.That(HostRecoveryService.RecoverJson(bad,registry).Succeeded,Is.False);Assert.That(raw.ToString(),Is.EqualTo(bad));
            Assert.That(HostRecoveryService.RecoverJson(clean,registry).Succeeded,Is.True);
        }
    }
}
