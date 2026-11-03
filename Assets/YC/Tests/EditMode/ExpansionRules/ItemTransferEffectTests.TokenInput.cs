using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        [TestCase(1,false)][TestCase(1,true)][TestCase(3,false)][TestCase(3,true)][TestCase(4,false)][TestCase(4,true)]
        public void ExplicitShortageKeepsFailedOutcomeAndNeverLowersRequiredCount(int inputVersion,bool empty)
        {
            var state=State();var registry=Registry();string source=Zone(state,"supply",1).ZoneId,target=Zone(state,"retired").ZoneId;
            var ids=new ItemZoneQuery().GetItemIds(state,source).Take(empty?0:2).ToArray();int required=empty?1:3;
            var spec=inputVersion==1?MoveSpec(1,ids,source,target,"unordered",required,"core.player_marker_storage"):
                inputVersion==3?TokenSpec(1,ids,source,target,required,"core.player_marker_storage"):ItemMovementV4.TransferSpec(1,ids,source,target,"unordered",required,"core.player_marker_storage");
            string before=JsonConvert.SerializeObject(new{state.ItemZones,state.RandomRuntime});var node=Run(state,spec,registry);
            Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(node.FailureReason,Is.EqualTo("insufficient_items"));Assert.That(Number(node.NormalizedArguments,"apiVersion"),Is.EqualTo(4));Assert.That(Number(node.NormalizedArguments,"requiredCount"),Is.EqualTo(required));
            Assert.That(node.PositionChanges,Is.Empty);Assert.That(node.ItemMovementV4,Is.Null);Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);Assert.That(JsonConvert.SerializeObject(new{state.ItemZones,state.RandomRuntime}),Is.EqualTo(before));
            if(inputVersion!=4){Assert.That(Number(node.ItemMoveInput.OriginalArguments,"requiredCount"),Is.EqualTo(required));Assert.That(Number(node.ItemMoveInput.OriginalArguments,"apiVersion"),Is.EqualTo(inputVersion));}
            state=Restore(state,registry);Assert.That(new EffectTreeExecutor(state,registry).RunUntilQuiescent().Faulted,Is.False);Assert.That(JsonConvert.SerializeObject(new{state.ItemZones,state.RandomRuntime}),Is.EqualTo(before));
        }

        [Test]
        public void ExplicitCardShortageDoesNotMoveSubsetOrGrantConditionReward()
        {
            var state=State();var registry=Registry();var ids=Ids(state,"hand",1).Take(1).ToArray();int score=state.FindPlayer(1).Score;string before=ItemTransferService.MovementV4Digest(state.ItemZones);
            var move=ItemMovementV4.TransferSpec(1,ids,Zone(state,"hand",1).ZoneId,Zone(state,"discard",1).ZoneId,"unordered",2,"core.character_card_transfer");
            Run(state,EffectSpec.Condition(new[]{move},new[]{ResourceEffectSpecFactory.GainScore(1,5)}),registry);
            Assert.That(state.FindPlayer(1).Score,Is.EqualTo(score));Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==TypeId).FailureReason,Is.EqualTo("insufficient_items"));Assert.That(ItemTransferService.MovementV4Digest(state.ItemZones),Is.EqualTo(before));Restore(state,registry);
        }

        [Test]
        public void OneMissingIndependentTokenDoesNotUndoSuccessfulSiblingOrHidePermissionFailure()
        {
            var state=State();var registry=Registry();string source=Zone(state,"supply",1).ZoneId,target=Zone(state,"retired").ZoneId,id=new ItemZoneQuery().GetItemIds(state,source)[0];
            // 使用正式独立调度组，缺失为普通预期叶子失败；本夹具不是正常对局验收。
            registry.Register("fixture.marker.parent",c=>c.Node.FlowStage==""?EffectStepResult.Continue("waiting").AddIndependentChildren("markers",new[]{TokenSpec(1,new[]{id},source,target,1,"core.player_marker_storage"),TokenSpec(1,Array.Empty<string>(),source,target,1,"core.player_marker_storage")}):EffectStepResult.Completed());
            var parent=Run(state,EffectSpec.Create("fixture.marker.parent",playerId:1),registry);
            Assert.That(parent.Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==id).ZoneId,Is.EqualTo(target));
            var failed=state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==TypeId&&n.Status==EffectNodeStatus.Failed);Assert.That(IsExpectedIndependentFailure(failed),Is.True);
            var denied=ItemMovementV4.TransferSpec(1,Array.Empty<string>(),source,target,"unordered",1,"core.player_marker_storage");Value(denied.NormalizedArguments,"player").StringValue="2";var deniedNode=Run(state,denied,registry);
            Assert.That(deniedNode.FailureReason,Is.EqualTo("transfer_permission_denied"));Assert.That(IsExpectedIndependentFailure(deniedNode),Is.False);Restore(state,registry);
        }

        [TestCase(false)][TestCase(true)]
        public void TokenLegacyInputUsesActualNativeV4StateReceiptAndRecovery(bool originalSupply)
        {
            var state=State();var registry=Registry();string source=Zone(state,"supply",1).ZoneId,target=Zone(state,"retired").ZoneId;var ids=new ItemZoneQuery().GetItemIds(state,source).Take(2).ToArray();
            if(originalSupply){Run(state,ItemMovementV4.TransferSpec(1,ids,source,target,"unordered",2,"core.player_marker_storage"),registry);source=target;target=Zone(state,"supply",1).ZoneId;}
            var nativeState=GameStateCloneService.DeepClone(state);var legacy=TokenSpec(1,ids,source,originalSupply?null:target,2,"core.player_marker_storage");
            var native=ItemMovementV4.TransferSpec(1,ids,source,target,"unordered",2,"core.player_marker_storage");if(originalSupply)Value(native.NormalizedArguments,"destination").Properties=Object("kind",NormalizedValue.CreateString("original_supply")).Properties;
            var node=Run(state,legacy,registry);var direct=Run(nativeState,native,registry);
            Assert.That(Number(node.NormalizedArguments,"apiVersion"),Is.EqualTo(4));Assert.That(Number(node.ItemMoveInput.OriginalArguments,"apiVersion"),Is.EqualTo(3));Assert.That(node.TransferBatchRequest,Is.Null);Assert.That(node.ItemMovementV4.Receipt.ContractVersion,Is.EqualTo(ItemMovementV4.ContractVersion));
            Assert.That(ItemTransferService.MovementV4Digest(state.ItemZones),Is.EqualTo(ItemTransferService.MovementV4Digest(nativeState.ItemZones)));Assert.That(node.NormalizedResult.ToDeterministicString(),Is.EqualTo(direct.NormalizedResult.ToDeterministicString()));
            state=Restore(state,registry);string after=ItemTransferService.MovementV4Digest(state.ItemZones);new EffectTreeExecutor(state,registry).RunUntilQuiescent();Assert.That(ItemTransferService.MovementV4Digest(state.ItemZones),Is.EqualTo(after));
        }

        [TestCase(false)][TestCase(true)]
        public void TokenLegacyLuaCompilesCanonicalV4IncludingEmptyShortage(bool empty)
        {
            var state=State();string ids=empty?"{}":"{'"+new ItemZoneQuery().GetItemIds(state,Zone(state,"supply",1).ZoneId)[0]+"'}";
            string source="return function(ctx)return {Effect.MoveItem({apiVersion=3,player=ctx.playerId,itemIds="+ids+",expectedSource={zoneId='"+Zone(state,"supply",1).ZoneId+"'},destination={zoneId='"+Zone(state,"retired").ZoneId+"'},insertion={kind='unordered'},operationProfileId='core.player_marker_storage',requiredCount=1})} end";
            var result=new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript(source),new LuaInvocationContext("token.input","isolated","p1",0,"1.0.0",2,0,0));Assert.That(result.IsSuccess,Is.True,result.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(result,new LuaEffectCompilationContext{PlayerId=1},out var specs,out var fault),Is.True,fault?.Diagnostic);
            var node=Run(state,specs.Single());Assert.That(Number(node.NormalizedArguments,"apiVersion"),Is.EqualTo(4));Assert.That(node.Status,Is.EqualTo(empty?EffectNodeStatus.Failed:EffectNodeStatus.Completed));
        }

        [TestCase(false)][TestCase(true)]
        public void TokenLegacyOldNodesAndCurrentInputProofCorruptionAreRejected(bool journal)
        {
            var state=State();var registry=Registry();var archive=HostRecoveryService.CreateArchive(state);var node=Run(state,TokenSpec(1,new[]{new ItemZoneQuery().GetItemIds(state,Zone(state,"supply",1).ZoneId)[0]},Zone(state,"supply",1).ZoneId,Zone(state,"retired").ZoneId,1,"core.player_marker_storage"),registry);
            if(journal)HostRecoveryService.AppendJournal(archive,state);else archive=HostRecoveryService.CreateArchive(state);string healthy=HostRecoveryService.SerializeArchive(archive);Assert.That(HostRecoveryService.RecoverJson(healthy,registry).Succeeded,Is.True);
            foreach(string change in new[]{"old_v3","prior_execution","old_contract","input_digest"})
            {
                var raw=JObject.Parse(healthy);var saved=journal?raw["Journal"]["Entries"].Last["StateAfter"]:raw["Snapshot"]["State"];var item=saved["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==node.EffectId);
                if(change=="old_v3"){item["NormalizedArguments"]=item["ItemMoveInput"]["OriginalArguments"].DeepClone();item["ItemMoveInput"]=JValue.CreateNull();}
                else if(change=="prior_execution")item["DefinitionVersion"]="4.12.0";
                else if(change=="old_contract")item["ItemMovementV4"]["ContractVersion"]="item.move.v4.1";
                else item["ItemMoveInput"]["OriginalArgumentsDigest"]="forged";
                string original=raw.ToString();Assert.That(HostRecoveryService.RecoverJson(original,registry).Succeeded,Is.False,change);Assert.That(raw.ToString(),Is.EqualTo(original));artifacts.WriteLabeledText("v3共同输入拒绝_"+change,original);
            }
        }
    }
}
