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
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 同档保存校验的隔离定位；不计正常菜单、点击或P95验收。
    public sealed class RecordedLateSavePerformanceTests
    {
        [Test]
        public void OptionalLateSaveReplayKeepsOriginalStateAndMatchesFullCopyValidation()
        {
            string Arg(string name)=>Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith(name+"="))?.Substring(name.Length+1);
            string source=Arg("--yc-save-profile-source"),report=Arg("--yc-save-profile-report");
            if(source==null||report==null)Assert.Ignore("仅显式提供原档及新报告路径才运行保存隔离对照");
            var data=JsonConvert.DeserializeObject<MatchSaveData>((string)JObject.Parse(File.ReadAllText(source))["Payload"]);
            var bootstrapper=Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp",true);
            var result=bootstrapper.GetMethod("Restore",BindingFlags.Static|BindingFlags.Public).Invoke(null,new object[]{null,data});
            var session=(GameSession)result.GetType().GetField("Session").GetValue(result);
            var baseline=GameStateCloneService.DeepClone(session.State);
            bool oldDeparture = LegacyInfluenceDepartureValidationReference.Validate(session.State, out var oldDepartureReason);
            Assert.That(YC.Domain.Effects.InfluenceDepartureProtocol.Validate(session.State, out var departureReason), Is.EqualTo(oldDeparture));
            Assert.That(departureReason, Is.EqualTo(oldDepartureReason));
            var cloneProbes = new List<object>();
            if (Arg("--yc-save-clone-probe") == "true")
                for (int i = 0; i < 4; i++)
                {
                    long managed = GC.GetTotalMemory(false); var clock = Stopwatch.StartNew();
                    var copy = GameStateCloneService.DeepClone(session.State); clock.Stop();
                    cloneProbes.Add(new { Index = i, Seconds = clock.Elapsed.TotalSeconds, ManagedDelta = GC.GetTotalMemory(false) - managed });
                    Assert.That(GameStateCloneService.AreEquivalent(copy, baseline), Is.True);
                }
            var copyProbes = new List<object>();
            if (Arg("--yc-save-clone-probe") == "true")
            {
                var changed = GameStateCloneService.DeepClone(session.State);
                changed.Players[0].Score++;
                changed.EffectRuntime.EffectNodes.Last().FlowStage += ".copy_probe";
                var beginUnique = typeof(GameStateCloneService).GetMethod("BeginUniqueCopyTarget", BindingFlags.Static | BindingFlags.NonPublic);
                var unique = typeof(GameStateCloneService).GetMethod("HasUniqueMutableReferences", BindingFlags.Static | BindingFlags.NonPublic);
                for (int i = 0; i < 4; i++)
                    foreach (bool full in i % 2 == 0 ? new[] { true, false } : new[] { false, true })
                    {
                        var target = GameStateCloneService.DeepClone(session.State);
                        Assert.That((bool)unique.Invoke(null, new object[] { target }), Is.True);
                        using var scope = (IDisposable)beginUnique.Invoke(null, new object[] { target, true });
                        using var mode = full ? GameStateCloneService.UseFullHistoricalCopyForDiagnostics() : null;
                        var clock = Stopwatch.StartNew(); GameStateCloneService.CopyTo(target, changed); clock.Stop();
                        copyProbes.Add(new { Index = i, FullHistoricalCopy = full, Seconds = clock.Elapsed.TotalSeconds });
                        Assert.That(GameStateCloneService.AreEquivalent(target, changed), Is.True);
                    }
            }
            var measurements=new List<object>();
            HostSnapshotDto Measure(bool full)
            {
                var phases=new Dictionary<string,double>();var calls=new Dictionary<string,int>();
                using var mode=full?ItemTransferService.UseFullRecoveryReplayForDiagnostics():null;
                using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{
                    phases[phase]=(phases.TryGetValue(phase,out var old)?old:0)+ticks/(double)Stopwatch.Frequency;
                    calls[phase]=(calls.TryGetValue(phase,out var count)?count:0)+1;
                });
                long managed=GC.GetTotalMemory(false);var clock=Stopwatch.StartNew();
                var snapshot=HostRecoveryService.CreateSnapshot(session.State);clock.Stop();
                measurements.Add(new {FullRecoveryCopy=full,Seconds=clock.Elapsed.TotalSeconds,ManagedDelta=GC.GetTotalMemory(false)-managed,Phases=phases,Calls=calls});
                return snapshot;
            }
            var original=Measure(true);var optimized=Measure(false);
            Assert.That(GameStateCloneService.AreEquivalent(original.State,optimized.State),Is.True);
            Assert.That(GameStateCloneService.AreEquivalent(baseline,session.State),Is.True,"两条重演路径均不得改写原档状态");
            using(ItemTransferService.VerifyRecoveryReplayCopies())HostRecoveryService.CreateSnapshot(session.State);
            Assert.That(GameStateCloneService.AreEquivalent(baseline,session.State),Is.True);
            Assert.That(RuleStateDigestService.Compute(optimized.State),Is.EqualTo(LegacyRuleStateDigestReference.ComputeCurrent(optimized.State)));
            File.WriteAllText(report,JsonConvert.SerializeObject(new {Scope="同原档保存校验隔离对照；不是正常界面或P95验收",data.GameId,Revision=baseline.EffectRuntime.StateRevision,Measurements=measurements,CloneProbes=cloneProbes,CopyProbes=copyProbes,FullFieldsEquivalent=true,OriginalUnchanged=true,ForcedGc=false},Formatting.Indented));
        }
    }
}
