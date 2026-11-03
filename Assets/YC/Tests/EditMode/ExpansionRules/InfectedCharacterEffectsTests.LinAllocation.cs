using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class InfectedCharacterEffectsTests
    {
        private GameState LinState(ResourceSet resources=null)
        {
            string frozen=artifacts.CopyContent(ContentRoot,"林未修改正式完整冻结输入");core=ExternalContentPack.Load(frozen);
            pack=core.CreateActiveSnapshot(new GameRuleConfiguration{MapId=YC.Domain.Maps.StaticMapDefinitions.ThreePlayerMapId,PlayerCount=3,EnabledExpansionIds=new List<string>{"core","enterprise","infected"}});
            var state=State(formalAbilities:true);if(resources!=null)state.FindPlayer(1).Resources=resources;
            artifacts.WriteLabeledText("林来源及版本_隔离准备",JsonConvert.SerializeObject(new {gameId=state.GameId,pack.ContentHash,definitionVersion=pack.Definitions.Single(d=>d.DefinitionId=="infected.character.lin").Version,strategySha256=LuaContentHasher.ComputeSha256(pack.GetScript("lua/characters/infected.character.lin.strategy.lua")),scope="正式冻结Lua及来源装配；沿现有隔离初态/Host盖放准备，不计正常菜单/开局/UI验收"}));
            StartCard(state,"lin","strategy");return state;
        }
        private static EffectNodeRuntimeState LinAllocator(GameState state)=>state.EffectRuntime.EffectNodes.Last(n=>n.EffectTypeId==AllocationEffects.TypeId && n.ContinuationAbilityId=="infected.character.lin.strategy");
        private void LinSeal(GameState state,IEnumerable<string> sequence,string kind="end_action")
        {
            var q=Open(state);Assert.That(q.InteractionTypeId,Is.EqualTo(AllocationEffects.InteractionType));
            var answer=Object("sequence",Strings(sequence),"submitKind",NormalizedValue.CreateString(kind),"candidateSetId",NormalizedValue.CreateString(q.CandidateSetId),"candidateSetVersion",NormalizedValue.CreateInteger(q.CandidateSetVersion));
            Assert.That(new EffectTreeExecutor(state,registry).TrySubmitInteraction(q.GetStableInteractionId(),1,state.EffectRuntime.StateRevision,answer,out var reason),Is.True,reason);
        }
        private void LinAdvanceToTrades(GameState state,int count,bool acceptPayment=true)
        {
            string source=Card(state,"lin").ItemId;
            for(int step=0;step<1500;step++)
            {
                if(TradeReceipts(View(state),source).Count>=count)return;
                var q=Open(state);var engine=new EffectTreeExecutor(state,registry);
                if(q!=null)
                {
                    Assert.That(q.InteractionTypeId,Is.EqualTo("action.decline_effect").Or.EqualTo(ResourceEffectExecutor.PaymentConfirmationInteractionTypeId),"只回答当前真实付款页；不自动结束新分配或其他来源。");
                    string id=acceptPayment && Flag(q.PromptParameters,"canPay")?(q.CandidateIds.Contains("continue")?"continue":"pay"):"decline";
                    Assert.That(q.CandidateIds,Does.Contain(id));Assert.That(engine.TrySubmitInteraction(q.GetStableInteractionId(),1,state.EffectRuntime.StateRevision,NormalizedValue.CreateString(id),out var reason),Is.True,reason);
                }
                else Assert.That(engine.Advance(),Is.True,state.EffectRuntime.LastFaultMessage);
                Assert.That(state.EffectRuntime.Status,Is.Not.EqualTo(EffectRuntimeStatus.PausedFault),state.EffectRuntime.LastFaultMessage);
            }
            Assert.Fail("林交易未在业务步数内完成。");
        }
        private void LinPump(GameState state)
        {Assert.That(new EffectTreeExecutor(state,registry).RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);Valid(state);}
        private void LinFinishBatch(GameState state,bool acceptPayment=true)
        {
            for(int step=0;step<40;step++)
            {
                LinPump(state);var q=Open(state);if(q==null || q.InteractionTypeId==AllocationEffects.InteractionType)return;
                Assert.That(q.InteractionTypeId,Is.EqualTo("action.decline_effect").Or.EqualTo(ResourceEffectExecutor.PaymentConfirmationInteractionTypeId));
                string id=acceptPayment && Flag(q.PromptParameters,"canPay")?(q.CandidateIds.Contains("continue")?"continue":"pay"):"decline";
                Assert.That(q.CandidateIds,Does.Contain(id));Answer(state,id);
            }
            Assert.Fail("林分配批次仍有未结清的真实付款页。");
        }
        [Test] public void LinAllocationAlwaysOffersEightTypedTradesAndEmptyEndNeverReopens()
        {
            var state=LinState(new ResourceSet());var q=Open(state);var node=LinAllocator(state);
            Assert.That(q.AnswerSchema,Is.EqualTo(AllocationEffects.AnswerSchema));Assert.That(q.CandidateIds,Is.EqualTo(new[]{"sell_originium","buy_originium","sell_originium_shard","buy_originium_shard","sell_iron","buy_iron","sell_pure_originium","buy_pure_originium"}));
            Assert.That(Number(q.PromptParameters,"totalExecutions"),Is.EqualTo(10));Assert.That(node.AllocationOptions.All(o=>o.MaxExecutions==10),Is.True);Assert.That(q.Presentation.QuantityRows.Count,Is.EqualTo(8));
            Assert.That(node.ChildEffectIds,Is.Empty);state=Restore(state);LinSeal(state,Array.Empty<string>());Assert.That(new EffectTreeExecutor(state,registry).RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            Assert.That(Open(state),Is.Null);Assert.That(LinAllocator(state).AllocationV1.Result.AttemptedCount,Is.Zero);state=Restore(state);Assert.That(new EffectTreeExecutor(state,registry).Advance(),Is.False);Assert.That(Open(state),Is.Null);
        }
        [TestCase(false)][TestCase(true)] public void LinAllocationActualSellBuyOrderUsesTheFundsAvailableAtEachTransaction(bool reverse)
        {
            var state=LinState(new ResourceSet{Originium=1});var sequence=reverse?new[]{"buy_originium","sell_originium"}:new[]{"sell_originium","buy_originium"};LinSeal(state,sequence);state=Restore(state);LinFinishBatch(state);
            var result=LinAllocator(state).AllocationV1.Result;Assert.That(result.Sequence,Is.EqualTo(sequence));Assert.That(result.AttemptedCount,Is.EqualTo(reverse?1:2));Assert.That(result.CompletedCount,Is.EqualTo(reverse?0:2));
            // 付款不足时原规则付款页只可放弃；ruleCondition的player_declined为普通Failed，不能偷偷改成Completed并执行尾项。
            Assert.That(result.Outcome,Is.EqualTo(reverse?"stopped_on_failure":"completed"));Assert.That(result.UnexecutedFrom,Is.EqualTo(reverse?2:3));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(reverse?0:2));Assert.That(state.FindPlayer(1).Resources.Originium,Is.EqualTo(1));
            Assert.That(TradeReceipts(View(state),Card(state,"lin").ItemId).Count,Is.EqualTo(reverse?0:2));Assert.That(Open(state),Is.Null);state=Restore(state);Assert.That(Open(state),Is.Null);
        }
        [TestCase(false)][TestCase(true)] public void LinAllocationRemainingQuotaCountsRealConditionSuccessAndNeverReplaysOldSuccess(bool allFail)
        {
            var state=LinState(new ResourceSet{Originium=allFail?0:1});LinSeal(state,Enumerable.Repeat("sell_originium",10),"confirm");LinFinishBatch(state);
            var allocators=state.EffectRuntime.EffectNodes.Where(n=>n.EffectTypeId==AllocationEffects.TypeId).ToArray();Assert.That(allocators.Length,Is.EqualTo(2));
            Assert.That(allocators[0].AllocationV1.Result.CompletedCount,Is.EqualTo(10));Assert.That(allocators[0].AllocationV1.Result.Executions.Count(e=>Text(e.Roots[0].NormalizedResult,"outcome")=="condition_met"),Is.EqualTo(allFail?0:1));
            Assert.That(Number(Open(state).PromptParameters,"totalExecutions"),Is.EqualTo(allFail?10:9));Assert.That(allocators[1].AllocationOptions.All(o=>o.MaxExecutions==(allFail?10:9)),Is.True);
            int gold=state.FindPlayer(1).Resources.GoldVoucher;Assert.That(gold,Is.EqualTo(allFail?0:3));state=Restore(state);
            LinSeal(state,Array.Empty<string>());LinFinishBatch(state);Assert.That(Open(state),Is.Null);Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold));
            state=Restore(state);Assert.That(new EffectTreeExecutor(state,registry).Advance(),Is.False);Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==AllocationEffects.TypeId),Is.EqualTo(2));
        }
        [Test] public void LinAllocationDecliningAnAffordablePurchaseIsNotASuccessAndEndDoesNotReopen()
        {
            var state=LinState(new ResourceSet{GoldVoucher=20});LinSeal(state,new[]{"buy_pure_originium"});LinPump(state);var q=Open(state);Assert.That(q.InteractionTypeId,Is.EqualTo("action.decline_effect"));
            var pay=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==q.OwnerEffectId);Assert.That(pay.Payment.AllowsSubstitution,Is.True);Assert.That(Flag(q.PromptParameters,"canPay"),Is.True);
            Assert.That(q.Presentation.Prompt.Key,Is.EqualTo("core:condition.character.lin.buy.pure_originium"));Assert.That(q.Presentation.Prompt.Arguments.Single(a=>a.Name=="cost").Value.IntegerValue,Is.EqualTo(13));
            Answer(state,"decline");LinFinishBatch(state);Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(20));Assert.That(state.FindPlayer(1).Resources.PureOriginium,Is.Zero);Assert.That(Open(state),Is.Null);
            Assert.That(LinAllocator(state).AllocationV1.Result.CompletedCount,Is.Zero);Assert.That(LinAllocator(state).AllocationV1.Result.Outcome,Is.EqualTo("stopped_on_failure"));Assert.That(LinAllocator(state).AllocationV1.Result.FailureReason,Is.EqualTo("player_declined"));
        }
        [Test] public void LinAllocationPreservesEveryPriceTextPaymentPolicyAndTheUnchangedTactic()
        {
            var state=LinState();var node=LinAllocator(state);int[] sell={3,4,5,15},buy={1,2,3,13};string[] resources={"originium","originium_shard","iron","pure_originium"};
            for(int i=0;i<8;i++)
            {
                bool selling=i%2==0;var option=node.AllocationOptions[i];string resource=resources[i/2];
                Assert.That(option.Text.Key,Is.EqualTo("core:interaction.trade."+(selling?"sell.":"buy.")+resource));Assert.That(option.Text.Arguments.Single().Value.IntegerValue,Is.EqualTo(selling?sell[i/2]:buy[i/2]));
                var condition=node.NestedEffects[i];Assert.That(Flag(condition.NormalizedArguments,"ruleCondition"),Is.EqualTo(!selling));var pay=condition.NestedEffects[0];Assert.That(Flag(pay.NormalizedArguments,"allowSubstitution"),Is.EqualTo(!selling));
                var cost=Value(pay.NormalizedArguments,"cost");Assert.That(cost.Kind,Is.EqualTo(NormalizedValueKind.Object));Assert.That(cost.Properties.Single().Value.IntegerValue,Is.EqualTo(selling?1:buy[i/2]));
            }
            var current=JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(ContentRoot,"character/infected.character.lin.json")));
            // API41已独立迁移计谋；本用例继续约束API39策略价格、付款政策与策略自身身份。
            Assert.That(current["abilities"].Single(a=>(string)a["mode"]=="strategy").Value<string>("version"),Is.EqualTo("3.0.0"));
            Assert.That(current["abilities"].Single(a=>(string)a["mode"]=="strategy").Value<string>("script"),Is.EqualTo("lua/characters/infected.character.lin.strategy.lua"));
        }
        [TestCase("handler_version")][TestCase("old_hash")][TestCase("old_stage")]
        public void LinAllocationRejectsOldInFlightIdentityAndDoesNotRewriteTheArchive(string damage)
        {
            var state=LinState();string clean=HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));var raw=JObject.Parse(clean);string id=LinAllocator(state).EffectId;
            var owner=raw.SelectTokens("$..EffectNodes[*]").OfType<JObject>().First(n=>n.Value<string>("EffectId")==id);
            if(damage=="handler_version")owner["ContinuationDefinitionVersion"]="2.0.0";
            if(damage=="old_hash")owner["ContinuationContentHash"]="04568439d6d6f9c52fe6afaf8018613d750858bcb0a22701adec4476f419164d";
            if(damage=="old_stage")owner["ContinuationContext"]["Properties"].Children<JObject>().Single(p=>p.Value<string>("Name")=="stage")["Value"]["StringValue"]="trade_selected";
            string before=raw.ToString();Assert.That(HostRecoveryService.RecoverJson(before,registry).Succeeded,Is.False);Assert.That(raw.ToString(),Is.EqualTo(before));artifacts.WriteLabeledText("林旧在途身份拒绝_"+damage,before);Assert.That(HostRecoveryService.RecoverJson(clean,registry).Succeeded,Is.True);
        }
        [Test] public void LinAllocationColdRecoveryAtPaidStepsAndTheActualPurchasePageDoesNotReplayTransactions()
        {
            var state=LinState(new ResourceSet{Originium=1});state=Restore(state);LinSeal(state,new[]{"sell_originium","buy_originium"});state=Restore(state);bool sellPaid=false,buyPage=false,buyPaid=false;
            for(int step=0;step<500;step++)
            {
                var pay=state.EffectRuntime.EffectNodes.FirstOrDefault(n=>n.Payment?.IsCommitted==true && n.Payment.PaymentCost.Originium==1);
                if(!sellPaid && pay!=null){sellPaid=true;state=Restore(state);}
                pay=state.EffectRuntime.EffectNodes.FirstOrDefault(n=>n.Payment?.IsCommitted==true && n.Payment.PaymentCost.GoldVoucher==1);
                if(!buyPaid && pay!=null){buyPaid=true;state=Restore(state);}
                var q=Open(state);var engine=new EffectTreeExecutor(state,registry);
                if(q!=null)
                {
                    Assert.That(q.InteractionTypeId,Is.EqualTo("action.decline_effect"));if(!buyPage){buyPage=true;state=Restore(state);q=Open(state);engine=new EffectTreeExecutor(state,registry);}
                    Assert.That(engine.TrySubmitInteraction(q.GetStableInteractionId(),1,state.EffectRuntime.StateRevision,NormalizedValue.CreateString("continue"),out var reason),Is.True,reason);
                }
                else if(!engine.Advance())break;
            }
            Assert.That(sellPaid && buyPage && buyPaid,Is.True);Assert.That(state.FindPlayer(1).Resources.Originium,Is.EqualTo(1));Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(2));state=Restore(state);Assert.That(Open(state),Is.Null);Assert.That(TradeReceipts(View(state),Card(state,"lin").ItemId).Count,Is.EqualTo(2));
        }
        [Test] public void LinAllocationColdRecoveryKeepsThePendingRemainingQuotaContinuation()
        {
            var state=LinState(new ResourceSet{Originium=1});LinSeal(state,Enumerable.Repeat("sell_originium",10),"confirm");LinFinishBatch(state);var old=state.EffectRuntime.EffectNodes.First(n=>n.EffectTypeId==AllocationEffects.TypeId);
            Assert.That(old.Status,Is.EqualTo(EffectNodeStatus.Blocked));Assert.That(old.AllocationV1.Result.CompletedCount,Is.EqualTo(10));string next=Open(state).GetStableInteractionId();state=Restore(state);Assert.That(Open(state).GetStableInteractionId(),Is.EqualTo(next));Assert.That(Number(Open(state).PromptParameters,"totalExecutions"),Is.EqualTo(9));
            LinSeal(state,Array.Empty<string>());LinFinishBatch(state);state=Restore(state);Assert.That(Open(state),Is.Null);Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(3));Assert.That(TradeReceipts(View(state),Card(state,"lin").ItemId).Count,Is.EqualTo(1));
        }
        [Test] public void LinAllocationPurchaseAllowsOnlyCurrentlyEligiblePaymentSubstitutionSources()
        {
            var state=LinState(new ResourceSet{GoldVoucher=3,Originium=3,Iron=3});LinSeal(state,new[]{"buy_originium"});LinPump(state);var q=Open(state);var pay=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==q.OwnerEffectId);
            Assert.That(pay.Payment.AllowsSubstitution,Is.True);var rules=ResourcePaymentSourceService.Query(state,registry,pay,pay.Payment.PaymentCost);
            Assert.That(rules,Is.Empty,"本次正式来源没有支持金券费用的替代规则，不能借只支持其他资源的规则免费买入。");Assert.That(Flag(q.PromptParameters,"canSubstitute"),Is.False);
            Answer(state,"continue");LinFinishBatch(state);Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(2));Assert.That(state.FindPlayer(1).Resources.Originium,Is.EqualTo(4));state=Restore(state);Assert.That(Open(state),Is.Null);
        }
    }
}
