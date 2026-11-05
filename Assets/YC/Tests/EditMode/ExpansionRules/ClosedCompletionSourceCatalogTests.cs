using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class ClosedCompletionSourceCatalogTests
    {
        private static bool Proven(EffectEventHandlerRegistration handler)=>(bool)typeof(EffectEventHandlerRegistration)
            .GetProperty("HandlerPreservesUniqueReferences",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(handler);
        private static EffectEventHandlerRegistration Find(EffectRegistry registry,string id)=>(EffectEventHandlerRegistration)typeof(EffectRegistry)
            .GetMethod("FindEventHandler",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(registry,new object[]{id});
        [TestCase(false,false)][TestCase(true,false)][TestCase(false,true)][TestCase(true,true)]
        public void FixedObserverPreservesProofButAnOrdinaryObserverStillDowngradesAndIsolatesAliases(bool ordinary,bool verify)
        {
            using var fixture=new PaymentTestFixture();fixture.Registry.FreezeAbilityCatalog(Array.Empty<AbilitySourceDeclaration>());
            Assert.That(AbilityCompletionSourceCatalog.TryRegister(fixture.Registry,fixture.State.ContentPackHash),Is.True);
            const string after="isolated.closed_completion.after";
            fixture.Registry.Register(after,c=>{c.State.EffectRuntime.Diagnostics[0].Message="after";return EffectStepResult.Completed();});
            if(ordinary)fixture.Registry.RegisterEventHandler(new EffectEventHandlerRegistration("isolated.closed_completion.alias","EffectCompleted","*","isolated.closed_completion.alias",c=>
            {
                var row=new EffectDiagnosticRuntimeState{Code="isolated",Message="before",NodeId=c.OwnerNode.EffectId};
                c.State.EffectRuntime.Diagnostics.Add(row);c.State.EffectRuntime.Diagnostics.Add(row);return new[]{EffectSpec.Create(after,playerId:1)};
            }){EffectTypeFilter=ResourceEffectTypeIds.GainScore}.WithNonRetainedStateContract(()=>true));
            fixture.State=GameStateCloneService.DeepClone(fixture.State);var engine=fixture.Engine;engine.CreateRoot(ResourceEffectSpecFactory.GainScore(1,1));
            using var validation=verify?EffectTreeExecutor.VerifyIndependentExecutionWritesForDiagnostics():null;
            int proofs=0;using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="kernel.independent_execution_writes")proofs++;});
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(proofs>0,Is.EqualTo(!ordinary));Assert.That(fixture.State.Players[0].Score,Is.EqualTo(1));
            if(ordinary)
            {
                Assert.That(fixture.State.EffectRuntime.Diagnostics[0],Is.Not.SameAs(fixture.State.EffectRuntime.Diagnostics[1]));
                Assert.That(fixture.State.EffectRuntime.Diagnostics[0].Message,Is.EqualTo("after"));Assert.That(fixture.State.EffectRuntime.Diagnostics[1].Message,Is.EqualTo("before"));
            }
        }
        [Test]
        public void UnfrozenCatalogAndExternallyReplacedConditionKeepTheConservativeContract()
        {
            var registry=new EffectRegistry();Assert.That(AbilityCompletionSourceCatalog.TryRegister(registry,"isolated"),Is.False);
            registry.FreezeAbilityCatalog(Array.Empty<AbilitySourceDeclaration>());
            Assert.That(AbilityCompletionSourceCatalog.TryRegister(registry,"isolated"),Is.True);
            var handler=Find(registry,"ability-source.event.EffectCompleted");Assert.That(Proven(handler),Is.True);
            handler.WithNonRetainedStateContract(()=>true);Assert.That(Proven(handler),Is.False,"重设任意外部条件不能继续沿用内置只读证明。");
        }
        [TestCase(5736)][TestCase(6109)][TestCase(7461)][TestCase(8485)][TestCase(9300)][TestCase(9916)][TestCase(10036)]
        public void OptionalOriginalCommandMatchesEveryFieldWithOriginalCatalog(int revision)
        {
            string Arg(string key)=>Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith(key+"="))?.Substring(key.Length+1);
            string checkpoints=Arg("--yc-closed-completion-checkpoints"),commands=Arg("--yc-closed-completion-commands"),output=Arg("--yc-closed-completion-output");
            if(checkpoints==null||commands==null||output==null)Assert.Ignore("仅显式提供原命令检查点和新报告路径时进行隔离逐字段对照，不计正常UI验收。");
            Directory.CreateDirectory(output);
            string payload=(string)JObject.Parse(File.ReadAllText(Path.Combine(checkpoints,"replay.json.before-"+revision+".json")))["Payload"];
            var commandRow=File.ReadLines(commands).Select(JObject.Parse).Single(r=>(int)r["BeforeRevision"]==revision);
            var measurements=new List<object>();
            long probeStart=GC.GetAllocatedBytesForCurrentThread();var probe=new byte[8192];bool allocationAvailable=GC.GetAllocatedBytesForCurrentThread()-probeStart>=probe.Length;GC.KeepAlive(probe);
            GameState Execute(bool original)
            {
                using var mode=original?AbilityCompletionSourceCatalog.UseOriginalCatalogForDiagnostics():null;
                var bootstrapper=Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp",true);
                var restored=bootstrapper.GetMethod("Restore",BindingFlags.Static|BindingFlags.Public).Invoke(null,new object[]{null,JsonConvert.DeserializeObject<MatchSaveData>(payload)});
                var session=(GameSession)restored.GetType().GetField("Session").GetValue(restored);
                var registry=(EffectRegistry)restored.GetType().GetField("EffectRegistry").GetValue(restored);
                Assert.That(Proven(Find(registry,"ability-source.event.EffectCompleted")),Is.EqualTo(!original),"必须实际分别登记原观察器和封闭观察器。");
                session.EnableConfirmedRecoveryCapture();session.ExecutionTimings.Enabled=true;
                using var verify=Arg("--yc-closed-completion-verify-writes")=="true"?EffectTreeExecutor.VerifyIndependentExecutionWritesForDiagnostics():null;
                var before=session.ExecutionTimings.Snapshot();long allocated=GC.GetAllocatedBytesForCurrentThread();var clock=Stopwatch.StartNew();
                var result=session.Submit(commandRow["Command"].ToObject<GameCommand>());clock.Stop();var after=session.ExecutionTimings.Snapshot();
                Assert.That(result.Succeeded,Is.EqualTo((bool)commandRow["Result"]["CommandResult"]["Succeeded"]),result.Validation?.Reason);
                Assert.That(session.State.EffectRuntime.StateRevision,Is.EqualTo((int)commandRow["AfterRevision"]));
                measurements.Add(new{OriginalCatalog=original,Seconds=clock.Elapsed.TotalSeconds,AllocatedBytes=allocationAvailable?GC.GetAllocatedBytesForCurrentThread()-allocated:(long?)null,
                    Calls=after.Where(p=>p.Key.EndsWith(".calls")).ToDictionary(p=>p.Key,p=>p.Value-(before.TryGetValue(p.Key,out long n)?n:0))});
                return session.State;
            }
            var originalState=Execute(true);var optimizedState=Execute(false);
            Assert.That(GameStateCloneService.AreEquivalent(originalState,optimizedState),Is.True,"两种观察器必须得到完全相同的全部持久字段、来源、回执、日志与随机状态。");
            File.WriteAllText(Path.Combine(output,"completion-"+revision+".json"),JsonConvert.SerializeObject(new{Scope="同原命令封闭完成观察器隔离逐字段对照，不计正常点击P95",BeforeRevision=revision,
                AllPersistentFieldsEquivalent=true,ForcedGc=false,AllocationBytesAvailable=allocationAvailable,Measurements=measurements},Formatting.Indented));
        }
    }
}
