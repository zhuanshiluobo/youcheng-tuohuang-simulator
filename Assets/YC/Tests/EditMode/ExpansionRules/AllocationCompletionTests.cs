using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 冻结私有保罗输入中的协议探针：真实注册来源/完成续接，不能计作保罗或林的卡面验收。
    public sealed class AllocationCompletionTests
    {
        private const string PathInPack="lua/entrepreneurs/paul.p05_p06.quick.lua", Ability="entrepreneur.face.P05";
        private static string Program(int count)=>@"return function(ctx)
          if ctx.eventType=='EffectCompleted' then
            local stage=ctx.payload.continuationContext and ctx.payload.continuationContext.stage
            if stage=='allocation_ack' then assert(ctx.payload.allocationResult==nil);return {Effect.GainScore({player=ctx.playerId,amount=7})} end
            if stage~='allocation_probe' then return {} end
            local r=ctx.payload.allocationResult;assert(r and r.version=='allocation_result_v1')
            assert(r.ownerEffectId==ctx.payload.effectId and r.resultDigest==ctx.payload.normalizedResult.resultDigest)
            assert(r.allocatedCount=="+count+@" and #r.sequence==r.allocatedCount and #r.executions==r.attemptedCount)
            assert(r.completedCount==r.allocatedCount and r.outcome=='completed' and r.unexecutedFrom==r.allocatedCount+1)
            local total=0;for _,row in ipairs(r.allocatedCounts) do total=total+row.count end;assert(total==r.allocatedCount)
            for i,row in ipairs(r.executions) do assert(row.ordinal==i and row.optionId==r.sequence[i] and row.state=='completed' and #row.roots==1 and row.roots[1].state=='Completed') end
            return {Effect.WithContext(Effect.Choice({player=ctx.playerId,options={'ack'},minSelections=1,maxSelections=1}),{stage='allocation_ack'})}
          end
          return {Effect.WithContext(Effect.AllocateEffects({apiVersion=1,player=ctx.playerId,totalExecutions="+Math.Max(1,count)+@",allowEndAction=true,
            options={{id='A',text=Text.Ref('core:interaction.effects.allocate.prompt'),maxExecutions=99,effects={Effect.GainScore({player=ctx.playerId,amount=1})}}}}),{stage='allocation_probe'})}
        end";
        [TestCase(0)][TestCase(3)][TestCase(99)]
        public void RegisteredSourceReadsFullTypedResultAndWaitsForItsActualContinuation(int count)
        {
            using var f=new PaulContentTestFixture(sourceOverrides:_=>new Dictionary<string,string>{{PathInPack,Program(count)}});
            f.Choose(5,"P05");int score=f.State.FindPlayer(1).Score;f.Invoke(Ability);
            var q=f.Open.Single(r=>r.InteractionTypeId==AllocationEffects.InteractionType);string id=q.OwnerEffectId;
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id).ChildEffectIds,Is.Empty);
            f.Restore("分配提交前真实注册来源");q=f.Open.Single(r=>r.InteractionTypeId==AllocationEffects.InteractionType);
            var answer=Object("sequence",Strings(Enumerable.Repeat("A",count)),"submitKind",NormalizedValue.CreateString(count==0?"end_action":"confirm"),"candidateSetId",NormalizedValue.CreateString(q.CandidateSetId),"candidateSetVersion",NormalizedValue.CreateInteger(q.CandidateSetVersion));
            Assert.That(f.Engine.TrySubmitInteraction(q.GetStableInteractionId(),1,f.State.EffectRuntime.StateRevision,answer,out var reason),Is.True,reason);
            f.Restore("分配封存后尚未执行");f.Pump();
            var owner=f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id);
            Assert.That(owner.Status,Is.EqualTo(EffectNodeStatus.Blocked));Assert.That(owner.AllocationV1.Result.Executions.Count,Is.EqualTo(count));Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(score+count));
            f.Restore("分配完成待原来源续接回答");q=f.Open.Single(r=>f.Within(r.OwnerEffectId,id) && r.CandidateIds.SequenceEqual(new[]{"ack"}));
            Assert.That(f.Engine.TrySubmitInteraction(q.GetStableInteractionId(),q.AnsweringPlayerId,NormalizedValue.CreateString("ack"),out reason),Is.True,reason);f.Pump();
            Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(score+count+7));f.Restore("分配完成续接结束");f.Pump();Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(score+count+7));
            VerifyViewScope(f,id,count);
        }
        private static void VerifyViewScope(PaulContentTestFixture f,string id,int count)
        {
            var owner=f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id);var e=f.State.EffectRuntime.RuleEvents.Single(v=>v.EventId==owner.CompletedEventId);
            var declaration=f.Registry.AbilityDeclarations.Single(d=>d.AbilityId==Ability);var content=f.Pack.Definitions.Single(d=>d.DefinitionId==declaration.DefinitionId);var handler=content.AbilityHandlers.Single(h=>h.HandlerId==declaration.HandlerRef);string source=f.Pack.GetScript(handler.Script);
            var definition=new LuaHandlerDefinition(handler.CompletionHandlerId,"EffectCompleted","",new LuaScriptDefinition(content.DefinitionId,Ability,handler.CompletionHandlerId,handler.Version,source,LuaContentHasher.ComputeSha256(source)),EffectHandlerRole.Continuation,contentInstanceId:content.DefinitionId,abilityId:Ability);
            Assert.That(f.Registry.TryGetContinuation(handler.CompletionHandlerId,"EffectCompleted",out var registration),Is.True);
            var method=typeof(LuaEffectEventDispatcher).GetMethod("CreateInvocationContext",BindingFlags.Instance|BindingFlags.NonPublic);var dispatcher=new LuaEffectEventDispatcher(f.Registry);
            foreach(string damage in new[]{"valid","copied_owner","copied_event","observer","foreign_handler"})
            {
                var call=new EffectEventHandlerContext(f.State,damage=="copied_event"?GameStateCloneService.DeepClone(e):e,damage=="copied_owner"?GameStateCloneService.DeepClone(owner):owner,registration);
                var candidate=damage=="observer"?new LuaHandlerDefinition("observer","EffectCompleted","",definition.Script,EffectHandlerRole.Observer):damage=="foreign_handler"?new LuaHandlerDefinition("foreign","EffectCompleted","",new LuaScriptDefinition(content.DefinitionId,Ability,"foreign",handler.Version,source,LuaContentHasher.ComputeSha256(source)),EffectHandlerRole.Continuation):definition;
                var context=(LuaInvocationContext)method.Invoke(dispatcher,new object[]{candidate,call});bool valid=damage=="valid";
                Assert.That(context.FacadeSnapshot.EventPayload.Get("allocationResult").Kind,Is.EqualTo(valid?LuaSnapshotValueKind.Object:LuaSnapshotValueKind.Null),damage);
                if(!valid)continue;
                string mutate="return function(ctx) ctx.payload.allocationResult.allocatedCount=900;return {} end";
                var result=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("view.probe","view.probe","view.probe",handler.Version,mutate,LuaContentHasher.ComputeSha256(mutate)),context);Assert.That(result.IsSuccess,Is.False);Assert.That(owner.AllocationV1.Result.AllocatedCount,Is.EqualTo(count));
                if(count==99)
                {
                    string tooLarge="return function(ctx) local r=ctx.payload.allocationResult;return {Effect.WithContext(Effect.GainScore({player=ctx.playerId,amount=1}),{sequence=r.sequence,executions=r.executions})} end";
                    var rejected=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("view.budget","view.budget","view.budget",handler.Version,tooLarge,LuaContentHasher.ComputeSha256(tooLarge)),context);Assert.That(rejected.IsSuccess,Is.False);
                }
            }
        }
        [Test] public void RegisteredSourceOrdinaryFailureDeliversOneNormalCallbackAndRetainsTheUnstartedTail()
        {
            const string source=@"return function(ctx)
              if ctx.eventType=='EffectCompleted' then
                local stage=ctx.payload.continuationContext and ctx.payload.continuationContext.stage
                if stage=='allocation_ack' then return {Effect.GainScore({player=ctx.playerId,amount=7})} end
                if stage~='allocation_probe' then return {} end
                local r=ctx.payload.allocationResult
                assert(r.outcome=='stopped_on_failure' and r.allocatedCount==3 and r.attemptedCount==2 and r.completedCount==1)
                assert(r.failedOrdinal==2 and r.unexecutedFrom==3 and #r.sequence==3 and r.sequence[3]=='A' and #r.executions==2)
                assert(r.executions[2].state=='failed' and r.executions[2].roots[1].state=='Failed' and r.failureReason~='')
                return {Effect.WithContext(Effect.Choice({player=ctx.playerId,options={'ack'},minSelections=1,maxSelections=1}),{stage='allocation_ack'})}
              end
              return {Effect.WithContext(Effect.AllocateEffects({apiVersion=1,player=ctx.playerId,totalExecutions=3,allowEndAction=false,options={
                {id='A',maxExecutions=2,text=Text.Ref('core:interaction.effects.allocate.prompt'),effects={Effect.GainScore({player=ctx.playerId,amount=1})}},
                {id='B',maxExecutions=1,text=Text.Ref('core:interaction.effects.allocate.prompt'),effects={Effect.PayResource({player=ctx.playerId,resourceType='gold_voucher',amount=100,allowSubstitution=false})}}}}),{stage='allocation_probe'})}
            end";
            using var f=new PaulContentTestFixture(sourceOverrides:_=>new Dictionary<string,string>{{PathInPack,source}});
            f.Choose(5,"P05");int score=f.State.FindPlayer(1).Score,gold=f.State.FindPlayer(1).Resources.GoldVoucher;Assert.That(gold,Is.LessThan(100));f.Invoke(Ability);
            var q=f.Open.Single(r=>r.InteractionTypeId==AllocationEffects.InteractionType);string id=q.OwnerEffectId;
            var answer=Object("sequence",Strings(new[]{"A","B","A"}),"submitKind",NormalizedValue.CreateString("confirm"),"candidateSetId",NormalizedValue.CreateString(q.CandidateSetId),"candidateSetVersion",NormalizedValue.CreateInteger(q.CandidateSetVersion));
            Assert.That(f.Engine.TrySubmitInteraction(q.GetStableInteractionId(),1,f.State.EffectRuntime.StateRevision,answer,out var reason),Is.True,reason);f.Pump();
            Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(score+1));Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold));f.Restore("真实普通失败待原完成续接");
            q=f.Open.Single(r=>f.Within(r.OwnerEffectId,id) && r.CandidateIds.SequenceEqual(new[]{"ack"}));Assert.That(f.Engine.TrySubmitInteraction(q.GetStableInteractionId(),1,NormalizedValue.CreateString("ack"),out reason),Is.True,reason);f.Pump();
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id).Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(score+8));f.Restore("真实普通失败完成续接结束");f.Pump();Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(score+8));
        }
    }
}
