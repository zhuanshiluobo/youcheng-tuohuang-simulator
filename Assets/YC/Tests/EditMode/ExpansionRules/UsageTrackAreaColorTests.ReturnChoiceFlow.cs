using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Domain.Rules;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class UsageTrackAreaColorTests
    {
        private static EffectSpec ReturnChoiceFlow()=>EffectSpec.Create(CityStyleMarkerReturnChoice.TypeId,Object("apiVersion",NormalizedValue.CreateInteger(1),"player",NormalizedValue.CreateString("1")),1);
        [Test]
        public void ExternalReturnChoiceOwnsLogicalRequestAndOnlyActualReturnUnlocksReward()
        {
            var state=State(finite:true);string marker=DeclareFinite(state,1);string history=HistoricalFacts(state,1);int gold=state.FindPlayer(1).Resources.GoldVoucher;
            string before=JsonConvert.SerializeObject(state.ItemZones);string root=BeginStyle(state,ReturnChoiceFlow());
            var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==request.OwnerEffectId).EffectTypeId,Is.EqualTo(LuaDomainEffectTypeIds.Choice));
            Assert.That(request.InteractionTypeId,Is.EqualTo("effect.choice.execute"));Assert.That(request.CandidateIds,Is.EqualTo(new[]{marker}));
            Assert.That(state.EffectRuntime.EffectNodes.Where(n=>n.EffectTypeId==ContentLifecycleEffectExecutor.ReturnMarkerTypeId),Is.Empty);
            Assert.That(JsonConvert.SerializeObject(state.ItemZones),Is.EqualTo(before));
            state=Restore(state,"独立返还逻辑选择确认前");SelectStyle(state,marker);
            var returned=state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==ContentLifecycleEffectExecutor.ReturnMarkerTypeId);
            Assert.That(Number(returned.NormalizedArguments,"apiVersion"),Is.EqualTo(2));Assert.That(Text(returned.NormalizedResult,"markerItemId"),Is.EqualTo(marker));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r=>r.OwnerEffectId==returned.EffectId),Is.False);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root).Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(HistoricalFacts(state,1),Is.EqualTo(history));Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold));Restore(state,"独立返还完成后");
        }
        [Test]
        public void EmptyExternalReturnChoiceFailsWithoutMovementOrReward()
        {
            var state=State(finite:true);var engine=new EffectTreeExecutor(state,registry);
            int pure=state.FindPlayer(1).Resources.PureOriginium;
            engine.CreateRoot(EffectSpec.Condition(new[]{ReturnChoiceFlow()},new[]{ResourceEffectSpecFactory.Gain(1,ResourceType.PureOriginium,1)}));Run(engine);
            Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);Assert.That(state.FindPlayer(1).Resources.PureOriginium,Is.EqualTo(pure));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==CityStyleMarkerReturnChoice.TypeId).Status,Is.EqualTo(EffectNodeStatus.Failed));Restore(state,"空标记逻辑失败");
        }
        [TestCase(false)][TestCase(true)]
        public void ExplicitReturnWrapperMatchesDirectMoveWithoutItsOwnInteraction(bool dedicated)
        {
            var state=State(finite:true);string marker=DeclareFinite(state,1);var item=state.ItemZones.Items.Single(i=>i.ItemId==marker);
            var args=Object("apiVersion",NormalizedValue.CreateInteger(2),"player",NormalizedValue.CreateString("1"),"selector",Object("mode",NormalizedValue.CreateString("explicit"),"itemIds",Strings(new[]{marker}),"expectedSource",Object("zoneId",NormalizedValue.CreateString(item.ZoneId),"slotId",NormalizedValue.CreateString(item.SlotId))));
            string root=BeginStyle(state,dedicated?EffectSpec.Create(ContentLifecycleEffectExecutor.ReturnMarkerTypeId,args,1):StyleReturnSpec(state,1,marker));
            Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root).Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==marker).ZoneId,Is.EqualTo(item.OriginalSupplyZoneId));Restore(state,"显式返还无选择_"+dedicated);
        }
        [TestCase(false)][TestCase(true)]
        public void ExternalChoiceFrozenTargetsRejectForgeryInSnapshotAndJournal(bool journal)
        {
            var state=State(finite:true);DeclareFinite(state,1);var archive=HostRecoveryService.CreateArchive(state);BeginStyle(state,ReturnChoiceFlow());
            if(journal)HostRecoveryService.AppendJournal(archive,state,state.ContentPackHash);else archive=HostRecoveryService.CreateArchive(state);
            string clean=HostRecoveryService.SerializeArchive(archive);Assert.That(HostRecoveryService.RecoverJson(clean,registry).Succeeded,Is.True);
            var raw=JObject.Parse(clean);var checkpoint=journal?raw["Journal"]["Entries"].Last(e=>e["StateAfter"] is JObject)["StateAfter"]:raw["Snapshot"]["State"];
            var choice=checkpoint["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectTypeId"]==LuaDomainEffectTypeIds.Choice);
            choice["NestedEffects"][0]["NormalizedArguments"]["Properties"].Single(p=>(string)p["Name"]=="selector")["Value"]["Properties"].Single(p=>(string)p["Name"]=="itemIds")["Value"]["Items"][0]["StringValue"]="forged";
            string original=raw.ToString();Assert.That(HostRecoveryService.RecoverJson(original,registry).Succeeded,Is.False);Assert.That(raw.ToString(),Is.EqualTo(original));
        }
    }
}
