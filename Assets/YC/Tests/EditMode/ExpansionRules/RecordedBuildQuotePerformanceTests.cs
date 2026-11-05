using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 原档/原命令形成的只读检查点；只定位查询，不计正常UI验收。
    public sealed class RecordedBuildQuotePerformanceTests
    {
        [Test]
        public void OptionalOriginalQuoteKeepsAffordabilityAndReportsQueryCost()
        {
            string Arg(string key)=>Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith(key+"="))?.Substring(key.Length+1);
            string input=Arg("--yc-quote-source"),report=Arg("--yc-quote-report");
            if(input==null || report==null)Assert.Ignore("仅显式原档与新报告路径执行隔离查询");
            var data=JsonConvert.DeserializeObject<MatchSaveData>((string)JObject.Parse(File.ReadAllText(input))["Payload"]);
            var restored=Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp",true)
                .GetMethod("Restore",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{null,data});
            var session=(GameSession)restored.GetType().GetField("Session").GetValue(restored);
            var registry=(EffectRegistry)restored.GetType().GetField("EffectRegistry").GetValue(restored);
            // 正常宿主会装配执行器并绑定费用查询；隔离探针也须装配同一现役registry。
            _=new EffectTreeExecutor(session.State,registry);
            int player=int.TryParse(Arg("--yc-quote-player"),out int requested)?requested:session.State.CurrentPlayerId;
            var query=new BuildFacilityOptionQueryService(new BuildFacilityService().UseQueries(registry.FacilityBuildQueries),registry.FacilityBuildCandidates);
            string digest=RuleStateDigestService.Compute(session.State);
            session.ExecutionTimings.Enabled=true;
            long probeBefore=GC.GetAllocatedBytesForCurrentThread();var allocationProbe=new byte[8192];
            bool allocationAvailable=GC.GetAllocatedBytesForCurrentThread()-probeBefore>=allocationProbe.Length;
            GC.KeepAlive(allocationProbe);
            Action<string,string,long> sample=(phase,definition,ticks)=>session.ExecutionTimings.AddSample(phase,ticks);
            YC.Infrastructure.Lua.LuaEffectEventDispatcher.CpuTimingSample+=sample;
            var scopeSamples=new System.Collections.Generic.Dictionary<string,int>();
            Action<string> scopeSample=phase=>scopeSamples[phase]=(scopeSamples.TryGetValue(phase,out int count)?count:0)+1;
            ReadOnlyStateQueryScope.DiagnosticSample+=scopeSample;
            try
            {
                var projectionBefore=session.ExecutionTimings.Snapshot();
                var clock=Stopwatch.StartNew();GameStateView view;
                using(GameStateCloneService.BeginTimingScope(session.ExecutionTimings.AddSample))view=session.ProjectFor(GameStateViewer.Player(player));
                clock.Stop();
                double projectionSeconds=clock.Elapsed.TotalSeconds;
                var projectionAfter=session.ExecutionTimings.Snapshot();
                var before=session.ExecutionTimings.Snapshot();long managed=GC.GetTotalMemory(false),allocated=GC.GetAllocatedBytesForCurrentThread();
                clock.Restart();System.Collections.Generic.IReadOnlyList<BuildFacilityOptionQueryResult> options;
                using(GameStateCloneService.BeginTimingScope(session.ExecutionTimings.AddSample))options=query.Query(session.State,player,view);
                clock.Stop();var after=session.ExecutionTimings.Snapshot();double querySeconds=clock.Elapsed.TotalSeconds;
                File.WriteAllText(report,JsonConvert.SerializeObject(new {Scope="原档建设只读查询隔离定位，非正常UI验收",data.GameId,
                    Revision=session.State.EffectRuntime.StateRevision,Player=player,ProjectionSeconds=projectionSeconds,QuerySeconds=querySeconds,
                    registry.HasAbilityCatalog,Declarations=registry.AbilityDeclarations.Where(d=>d.QueryKinds.Contains(ResourcePaymentSourceService.QueryKind)).Select(d=>d.AbilityId).ToArray(),
                    Sources=registry.QuerySources(session.State,player,"ruleQuery",queryKind:ResourcePaymentSourceService.QueryKind),
                    ReadScopeSamples=scopeSamples,
                    ProjectionTimings=projectionAfter.Where(p=>p.Key.EndsWith(".ticks") || p.Key.EndsWith(".calls"))
                        .ToDictionary(p=>p.Key,p=>p.Value-(projectionBefore.TryGetValue(p.Key,out long old)?old:0)),
                    ManagedDelta=GC.GetTotalMemory(false)-managed,AllocatedBytes=allocationAvailable ? GC.GetAllocatedBytesForCurrentThread()-allocated : (long?)null,Options=options,Timings=after.Where(p=>p.Key.EndsWith(".ticks") || p.Key.EndsWith(".calls"))
                        .ToDictionary(p=>p.Key,p=>p.Value-(before.TryGetValue(p.Key,out long old)?old:0))},Formatting.Indented));
                Assert.That(options.Count,Is.GreaterThan(0));
                var owner=new EffectNodeRuntimeState {EffectTypeId=ResourceEffectTypeIds.Pay,PlayerId=player,
                    EffectId=StableIdFactory.Create("resource-feasibility",session.State.GameId,player.ToString(CultureInfo.InvariantCulture))};
                foreach(var option in options)
                {
                    var definition=FacilityCardDatabase.Get(session.State,option.DefinitionId);
                    var cost=new FacilityBuildCostService().GetEffectiveResourceCost(session.State,session.State.FindPlayer(player),definition);
                    Assert.That(ResourcePaymentCostPreview.CanPay(session.State,registry,owner,cost),
                        Is.EqualTo(LegacyResourcePaymentCostPreview.CanPay(session.State,registry,owner,cost)),option.DefinitionId);
                }
                Assert.That(RuleStateDigestService.Compute(session.State),Is.EqualTo(digest),"查询不能改动权威状态");
            }
            finally {YC.Infrastructure.Lua.LuaEffectEventDispatcher.CpuTimingSample-=sample;ReadOnlyStateQueryScope.DiagnosticSample-=scopeSample;}
        }
    }
}
