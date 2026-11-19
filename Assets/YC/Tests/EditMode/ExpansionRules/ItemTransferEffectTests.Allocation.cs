using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        private static EffectRegistry AllocationRegistry()
        {
            var r=Registry();AllocationEffects.Register(r);
            r.Register(new EffectRegistration("test.allocation.affine",c=> { int amount=(int)Value(c.Node.NormalizedArguments,"digit").IntegerValue; c.State.FindPlayer(1).Score=c.State.FindPlayer(1).Score*10+amount;return EffectStepResult.Completed(Object("digit",NormalizedValue.CreateInteger(amount))); }));
            r.Register(new EffectRegistration("test.allocation.fail",c=> { c.State.FindPlayer(1).Resources.GoldVoucher--;return EffectStepResult.Failed("actual_business_failure"); }));
            r.Register(new EffectRegistration("test.allocation.fault",c=>throw new InvalidOperationException("actual_child_fault")));
            r.Register(new EffectRegistration("test.allocation.gate",c=>
            {
                if(c.GetLatestInteractionAnswer()!=null)return EffectStepResult.Completed(Object("answered",NormalizedValue.CreateBoolean(true)));
                var q=new EffectInteractionSpec{InteractionTypeId="test.allocation.gate",PromptKey="test.allocation.gate",AnsweringPlayerId=1,AnswerSchema="candidate_id",MinSelections=1,MaxSelections=1};q.CandidateIds.Add("continue");return EffectStepResult.Continue("gate_waiting").AddInteraction(q);
            },EffectExecutorKind.IntrinsicFlow));
            return r;
        }
        private static EffectSpec Digit(int n)=>EffectSpec.Create("test.allocation.affine",Object("digit",NormalizedValue.CreateInteger(n)),1);
        private static EffectSpec AllocationSpec(int total=3,bool end=false,int max=99,params EffectSpec[][] roots)
        {
            if(roots.Length==0)roots=new[]{new[]{Digit(1)},new[]{Digit(2)}};
            var spec=EffectSpec.Create(AllocationEffects.TypeId,Object("apiVersion",NormalizedValue.CreateInteger(1),"player",NormalizedValue.CreateString("1"),"totalExecutions",NormalizedValue.CreateInteger(total),"allowEndAction",NormalizedValue.CreateBoolean(end)),1);
            spec.AllocationOptions=new List<AllocationOptionV1>();
            for(int i=0;i<roots.Length;i++) {spec.AllocationOptions.Add(new AllocationOptionV1{Id=((char)('A'+i)).ToString(),MaxExecutions=max,Text=new GameplayTextRef("core:interaction.effects.allocate.prompt")});spec.NestedEffectGroups.Add(roots[i].ToList());}
            return spec;
        }
        private static NormalizedValue AllocationAnswer(InteractionRequest q,string sequence,string kind="confirm")=>Object("sequence",Strings(sequence.Length==0?Array.Empty<string>():sequence.Split(',')),"submitKind",NormalizedValue.CreateString(kind),"candidateSetId",NormalizedValue.CreateString(q.CandidateSetId),"candidateSetVersion",NormalizedValue.CreateInteger(q.CandidateSetVersion));
        private static void SubmitAllocation(GameState s,EffectRegistry r,string seq,string kind="confirm")
        {
            var q=Open(s);Assert.That(new EffectTreeExecutor(s,r).TrySubmitInteraction(q.GetStableInteractionId(),1,s.EffectRuntime.StateRevision,AllocationAnswer(q,seq,kind),out var reason),Is.True,reason);
        }
        [Test] public void AllocationSealsAndRunsABATwoRootsInTheExactOrder()
        {
            var s=State();var r=AllocationRegistry();var root=Run(s,AllocationSpec(3,false,2,new[]{Digit(1),Digit(3)},new[]{Digit(2)}),r);
            Assert.That(s.FindPlayer(1).Score,Is.Zero);Assert.That(root.ChildEffectIds,Is.Empty);
            s=Restore(s,r);SubmitAllocation(s,r,"A,B,A");s=Restore(s,r);
            Assert.That(new EffectTreeExecutor(s,r).RunUntilQuiescent().Faulted,Is.False,s.EffectRuntime.LastFaultMessage);
            root=s.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root.EffectId);
            Assert.That(s.FindPlayer(1).Score,Is.EqualTo(13213));Assert.That(root.AllocationV1.Result.CompletedCount,Is.EqualTo(3));Assert.That(root.AllocationV1.Result.Executions.SelectMany(e=>e.Roots).Select(x=>x.EffectId).Distinct().Count(),Is.EqualTo(5));
            s=Restore(s,r);Assert.That(new EffectTreeExecutor(s,r).Advance(),Is.False);Assert.That(s.FindPlayer(1).Score,Is.EqualTo(13213));
        }
        [TestCase("short")][TestCase("unknown")][TestCase("over_max")][TestCase("foreign_set")][TestCase("stale_version")][TestCase("extra_count")][TestCase("bad_kind")][TestCase("wrong_type")][TestCase("end_disabled")]
        public void AllocationRejectedAnswersLeaveTheWholeStateUnchanged(string damage)
        {
            var s=State();var r=AllocationRegistry();Run(s,AllocationSpec(3,false,2),r);var q=Open(s);var a=AllocationAnswer(q,"A,B,A");
            if(damage=="short")a=AllocationAnswer(q,"A");if(damage=="unknown")a=AllocationAnswer(q,"A,B,Z");if(damage=="over_max")a=AllocationAnswer(q,"A,A,A");
            if(damage=="foreign_set")Value(a,"candidateSetId").StringValue="foreign";if(damage=="stale_version")Value(a,"candidateSetVersion").IntegerValue++;
            if(damage=="extra_count")a.Properties.Add(new NormalizedValueEntry{Name="counts",Value=Object()});if(damage=="bad_kind")Value(a,"submitKind").StringValue="close";
            if(damage=="wrong_type")Value(a,"candidateSetVersion").Kind=NormalizedValueKind.String;if(damage=="end_disabled")a=AllocationAnswer(q,"","end_action");
            string before=JsonConvert.SerializeObject(s);Assert.That(new EffectTreeExecutor(s,r).TrySubmitInteraction(q.GetStableInteractionId(),1,s.EffectRuntime.StateRevision,a,out _),Is.False);Assert.That(JsonConvert.SerializeObject(s),Is.EqualTo(before));
            Assert.That(new EffectTreeExecutor(s,r).TrySubmitInteraction(q.GetStableInteractionId(),2,s.EffectRuntime.StateRevision,AllocationAnswer(q,"A,B,A"),out _),Is.False);Assert.That(JsonConvert.SerializeObject(s),Is.EqualTo(before));
        }
        [TestCase(false)][TestCase(true)] public void AllocationOrdinaryFailureStopsTheUnstartedSuffixAndKeepsActualChanges(bool condition)
        {
            var s=State();var r=AllocationRegistry();var fail=EffectSpec.Create("test.allocation.fail",Object(),1);
            var business=condition?EffectSpec.Condition(new[]{fail},new[]{Digit(9)}):fail;
            var root=Run(s,AllocationSpec(3,false,99,new[]{Digit(1)},new[]{business,Digit(2)}),r);int gold=s.FindPlayer(1).Resources.GoldVoucher;
            SubmitAllocation(s,r,"A,B,A");Assert.That(new EffectTreeExecutor(s,r).RunUntilQuiescent().Faulted,Is.False,s.EffectRuntime.LastFaultMessage);
            root=s.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root.EffectId);var result=root.AllocationV1.Result;
            Assert.That(root.Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(s.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold-1));
            Assert.That(result.Outcome,Is.EqualTo(condition?"completed":"stopped_on_failure"));Assert.That(result.AttemptedCount,Is.EqualTo(condition?3:2));Assert.That(result.CompletedCount,Is.EqualTo(condition?3:1));
            Assert.That(s.FindPlayer(1).Score,Is.EqualTo(condition?121:1));s=Restore(s,r);
        }
        [Test] public void AllocationFaultKeepsFaultAndNeverPublishesANormalResult()
        {
            var s=State();var r=AllocationRegistry();var root=Run(s,AllocationSpec(2,false,99,new[]{Digit(1)},new[]{EffectSpec.Create("test.allocation.fault",Object(),1)}),r);SubmitAllocation(s,r,"A,B");
            Assert.That(new EffectTreeExecutor(s,r).RunUntilQuiescent().Faulted,Is.True);root=s.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root.EffectId);Assert.That(root.AllocationV1.Result,Is.Null);Assert.That(s.FindPlayer(1).Score,Is.EqualTo(1));
        }
        [Test] public void AllocationReal99EntriesUseTypedReceiptsAndRecoverWithoutReplaying()
        {
            var s=State();var r=AllocationRegistry();var root=Run(s,AllocationSpec(99,false,99,new[]{ResourceEffectSpecFactory.GainScore(1,1)}),r);SubmitAllocation(s,r,string.Join(",",Enumerable.Repeat("A",99)));
            Assert.That(new EffectTreeExecutor(s,r).RunUntilQuiescent().Faulted,Is.False,s.EffectRuntime.LastFaultMessage);root=s.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root.EffectId);
            Assert.That(root.AllocationV1.Result.Sequence.Count,Is.EqualTo(99));Assert.That(root.AllocationV1.Result.Executions.Count,Is.EqualTo(99));Assert.That(root.AllocationV1.Result.CompletedCount,Is.EqualTo(99));Assert.That(root.NormalizedResult.IsValid(),Is.True);Assert.That(s.FindPlayer(1).Score,Is.EqualTo(99));
            s=Restore(s,r);Assert.That(new EffectTreeExecutor(s,r).Advance(),Is.False);Assert.That(s.FindPlayer(1).Score,Is.EqualTo(99));
        }
        [Test] public void AllocationZeroCapacityAllowsOnlyExplicitEmptyEnd()
        {
            var s=State();var r=AllocationRegistry();var root=Run(s,AllocationSpec(4,true,0),r);SubmitAllocation(s,r,"","end_action");Assert.That(new EffectTreeExecutor(s,r).RunUntilQuiescent().Faulted,Is.False);
            root=s.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root.EffectId);Assert.That(root.AllocationV1.Result.AllocatedCount,Is.Zero);Assert.That(root.AllocationV1.Result.AttemptedCount,Is.Zero);Assert.That(root.ChildEffectIds,Is.Empty);Restore(s,r);
        }
        [Test] public void AllocationEveryCommittedBoundaryRecoversAndResumesTheOriginalRoots()
        {
            var s=State();var r=AllocationRegistry();var id=new EffectTreeExecutor(s,r).CreateRoot(AllocationSpec(2,false,99,new[]{ResourceEffectSpecFactory.Pay(1,ResourceType.GoldVoucher,2),Digit(1)},new[]{Digit(2)}));
            int gold=s.FindPlayer(1).Resources.GoldVoucher;int frames=0;bool submitted=false;
            for(;frames<80;frames++)
            {
                s=Restore(s,r);
                if(!submitted && s.EffectRuntime.InteractionRequests.Any(q=>q.Status=="open")) {SubmitAllocation(s,r,"A,B");submitted=true;continue;}
                if(!new EffectTreeExecutor(s,r).Advance())break;
            }
            Assert.That(frames,Is.LessThan(80));Assert.That(submitted,Is.True);Assert.That(s.FindPlayer(1).Score,Is.EqualTo(12));Assert.That(s.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold-2));Assert.That(s.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id).AllocationV1.Result.CompletedCount,Is.EqualTo(2));
        }
        [TestCase("missing_version")][TestCase("string_version")][TestCase("bad_digest")][TestCase("result_ref")][TestCase("counts")][TestCase("receipt")][TestCase("tail")][TestCase("definition")][TestCase("missing_text")]
        public void AllocationRawTamperingIsRejectedWithoutRewritingTheArchive(string damage)
        {
            var s=State();var r=AllocationRegistry();var root=Run(s,AllocationSpec(2,false,99,new[]{Digit(1)}),r);SubmitAllocation(s,r,"A,A");Assert.That(new EffectTreeExecutor(s,r).RunUntilQuiescent().Faulted,Is.False);
            var raw=JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(s)));var n=raw.SelectTokens("$..EffectNodes[*]").OfType<JObject>().First(v=>v.Value<string>("EffectId")==root.EffectId);
            if(damage=="missing_version")((JObject)n["AllocationV1"]).Property("Version").Remove();if(damage=="string_version")n["AllocationV1"]["Version"]="1";
            if(damage=="bad_digest")n["AllocationV1"]["Result"]["ResultDigest"]="forged";if(damage=="result_ref")n["NormalizedResult"]["Properties"].Children<JObject>().Single(p=>p.Value<string>("Name")=="resultRef")["Value"]["StringValue"]="foreign";
            if(damage=="counts")n["AllocationV1"]["AllocatedCounts"][0]["Count"]=1;if(damage=="receipt")n["AllocationV1"]["Executions"][0]["Roots"][0]["EffectId"]="forged";
            if(damage=="tail")n["AllocationV1"]["Sequence"][1]="B";if(damage=="definition")n["DefinitionVersion"]="9.0.0";if(damage=="missing_text")((JObject)n["AllocationOptions"][0]).Property("Text").Remove();
            string before=raw.ToString();var recovered=HostRecoveryService.RecoverJson(before,r);Assert.That(recovered.Succeeded,Is.False,damage);Assert.That(raw.ToString(),Is.EqualTo(before));artifacts.WriteLabeledText("有序分配拒绝坏档_"+damage,before);
        }
        private const string AllocationLua="return function(ctx) return {Effect.AllocateEffects({apiVersion=1,player=ctx.playerId,totalExecutions=3,allowEndAction=false,options={{id='A',maxExecutions=2,text=Text.Ref('core:interaction.effects.allocate.prompt'),effects={Effect.GainScore({player=ctx.playerId,amount=1})}},{id='B',maxExecutions=1,text=Text.Ref('core:interaction.effects.allocate.prompt'),effects={Effect.GainScore({player=ctx.playerId,amount=2})}}}})} end";
        [Test] public void AllocationLuaConstructorCompilesTypedIndependentOptions()
        {
            var script=ShuffleScript(AllocationLua);var invocation=new MoonSharpLuaRuntimeHost().Invoke(script,new LuaInvocationContext("allocation","test","p1",0,"1.0.0",2,0,0));Assert.That(invocation.IsSuccess,Is.True,invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation,new LuaEffectCompilationContext{PlayerId=1},out var specs,out var fault),Is.True,fault?.Diagnostic);Assert.That(specs[0].AllocationOptions.Select(o=>o.Id),Is.EqualTo(new[]{"A","B"}));
            var s=State();var r=AllocationRegistry();Run(s,specs[0],r);SubmitAllocation(s,r,"A,B,A");Assert.That(new EffectTreeExecutor(s,r).RunUntilQuiescent().Faulted,Is.False,s.EffectRuntime.LastFaultMessage);Assert.That(s.FindPlayer(1).Score,Is.EqualTo(4));
        }
        [TestCase("apiVersion=1,","","")][TestCase("apiVersion=1","apiVersion='1'","")][TestCase("totalExecutions=3","totalExecutions=3.5","")][TestCase("allowEndAction=false","allowEndAction='false'","")]
        [TestCase("id='B'","id='A'","")][TestCase("maxExecutions=2","maxExecutions=-1","")][TestCase("maxExecutions=2","maxExecutions=0","")][TestCase("text=Text.Ref('core:interaction.effects.allocate.prompt'),","","")]
        [TestCase("effects={Effect.GainScore({player=ctx.playerId,amount=1})}","effects={Effect.Repeat({apiVersion=1,count=1,effects={}})}","")]
        public void AllocationLuaRejectsInvalidDeclarations(string old,string value,string unused)
        {
            var source=AllocationLua.Replace(old,value);var invocation=new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript(source),new LuaInvocationContext("allocation","test","p1",0,"1.0.0",2,0,0));Assert.That(invocation.IsSuccess,Is.False,source);
        }
        [TestCase("nested")][TestCase("retired")][TestCase("unknown")][TestCase("groups")][TestCase("options_raw")][TestCase("permission")]
        public void AllocationManualKernelSpecsCannotBypassDeclarationPreflight(string damage)
        {
            var s=State();var r=AllocationRegistry();var spec=AllocationSpec();
            if(damage=="nested")spec.NestedEffectGroups[0][0]=AllocationSpec();if(damage=="retired")spec.NestedEffectGroups[0][0]=EffectSpec.Create(LuaDomainEffectTypeIds.Repeat,Object(),1);
            if(damage=="unknown")spec.NestedEffectGroups[0][0]=EffectSpec.Create("test.unregistered",Object(),1);if(damage=="groups")spec.NestedEffectGroups[0].Clear();
            if(damage=="options_raw")spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry{Name="options",Value=Object()});if(damage=="permission")Value(spec.NormalizedArguments,"player").StringValue="2";
            Assert.That(new EffectTreeExecutor(s,r).TryCreateRoot(spec,out _),Is.False);Assert.That(s.EffectRuntime.InteractionRequests,Is.Empty);Assert.That(s.FindPlayer(1).Score,Is.Zero);
        }
        [Test] public void AllocationWaitsAtTheActualChildPageAfterPaymentAndRecoversTheInFlightRoot()
        {
            var s=State();var r=AllocationRegistry();int gold=s.FindPlayer(1).Resources.GoldVoucher;
            var root=Run(s,AllocationSpec(2,false,99,new[]{ResourceEffectSpecFactory.Pay(1,ResourceType.GoldVoucher,2),EffectSpec.Create("test.allocation.gate",Object(),1),Digit(1)},new[]{Digit(2)}),r);
            SubmitAllocation(s,r,"A,B");Assert.That(new EffectTreeExecutor(s,r).RunUntilQuiescent().Faulted,Is.False);Assert.That(s.FindPlayer(1).Score,Is.Zero);Assert.That(s.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold-2));
            s=Restore(s,r);root=s.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root.EffectId);Assert.That(root.AllocationV1.Executions.Count,Is.EqualTo(1));Assert.That(root.AllocationV1.Executions[0].Roots.Count,Is.EqualTo(2));
            AnswerRequest(s,r,"continue");Assert.That(s.FindPlayer(1).Score,Is.EqualTo(12));Assert.That(s.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold-2));Restore(s,r);
        }
        [Test] public void AllocationRecoversEveryBoundaryIncludingAnUnconsumedOrdinaryFailure()
        {
            var s=State();var r=AllocationRegistry();int gold=s.FindPlayer(1).Resources.GoldVoucher;string id=new EffectTreeExecutor(s,r).CreateRoot(AllocationSpec(3,false,99,new[]{Digit(1)},new[]{EffectSpec.Create("test.allocation.fail",Object(),1),Digit(9)}));bool submitted=false,sawUnconsumed=false;
            for(int step=0;step<80;step++)
            {
                s=Restore(s,r);var owner=s.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id);
                if(s.EffectRuntime.EffectNodes.Any(n=>n.ParentEffectId==id && n.Status==EffectNodeStatus.Failed) && owner.AllocationV1.Result==null)sawUnconsumed=true;
                if(!submitted && s.EffectRuntime.InteractionRequests.Any(q=>q.Status=="open")) {SubmitAllocation(s,r,"A,B,A");submitted=true;continue;}
                if(!new EffectTreeExecutor(s,r).Advance())break;
            }
            Assert.That(sawUnconsumed,Is.True);Assert.That(s.FindPlayer(1).Score,Is.EqualTo(1));Assert.That(s.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold-1));Assert.That(s.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id).AllocationV1.Result.UnexecutedFrom,Is.EqualTo(3));
        }
        [TestCase("nodes")][TestCase("nested_roots")][TestCase("depth")][TestCase("normalized")][TestCase("cycle")][TestCase("children")]
        public void AllocationPreservesExistingBudgetsBeforeOpeningTheRequest(string budget)
        {
            var s=State(validMapIdentity:true);var r=AllocationRegistry();var spec=AllocationSpec(99,false,99,new[]{Digit(1)});EffectRuntimeLimits limits=null;
            if(budget=="nodes")limits=new EffectRuntimeLimits(10000,64,10,4096,16384);
            if(budget=="nested_roots")spec.NestedEffectGroups[0][0]=EffectSpec.Condition(Enumerable.Range(0,17).Select(_=>Digit(1)).ToArray(),Array.Empty<EffectSpec>());
            if(budget=="depth")for(int i=0;i<17;i++)spec.NestedEffectGroups[0][0]=EffectSpec.Condition(new[]{spec.NestedEffectGroups[0][0]},Array.Empty<EffectSpec>());
            if(budget=="normalized")spec.NestedEffectGroups[0][0].NormalizedArguments=Object("oversize",NormalizedValue.CreateArray(Enumerable.Range(0,129).Select(_=>NormalizedValue.CreateInteger(1)).ToList()));
            if(budget=="cycle")spec.NestedEffectGroups[0][0].NestedEffectGroups.Add(new List<EffectSpec>{spec.NestedEffectGroups[0][0]});
            if(budget=="children")spec.NestedEffectGroups[0][0].Children.Add(Digit(1));
            Assert.That(new EffectTreeExecutor(s,r,limits).TryCreateRoot(spec,out _),Is.False);Assert.That(s.EffectRuntime.InteractionRequests,Is.Empty);
        }
        [Test] public void AllocationSixteenOptionsWithSixteenRootsKeepIndependentValueBudgets()
        {
            string source="return function(ctx) local options={} for i=1,16 do local roots={} for j=1,16 do roots[j]=Effect.GainScore({player=ctx.playerId,amount=1}) end options[i]={id='option'..i,maxExecutions=1,text=Text.Ref('core:interaction.effects.allocate.prompt'),effects=roots} end return {Effect.AllocateEffects({apiVersion=1,player=ctx.playerId,totalExecutions=1,allowEndAction=false,options=options})} end";
            var invocation=new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript(source),new LuaInvocationContext("allocation","test","p1",0,"1.0.0",2,0,0));Assert.That(invocation.IsSuccess,Is.True,invocation.Diagnostic);Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation,new LuaEffectCompilationContext{PlayerId=1},out var specs,out var fault),Is.True,fault?.Diagnostic);
            var s=State();var r=AllocationRegistry();var root=Run(s,specs[0],r);SubmitAllocation(s,r,"option16");Assert.That(new EffectTreeExecutor(s,r).RunUntilQuiescent().Faulted,Is.False,s.EffectRuntime.LastFaultMessage);Assert.That(s.FindPlayer(1).Score,Is.EqualTo(16));Restore(s,r);
        }
    }
}
