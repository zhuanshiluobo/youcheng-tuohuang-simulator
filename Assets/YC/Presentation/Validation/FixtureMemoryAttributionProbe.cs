#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Profiling;
using YC.Application.Sessions;

namespace YC.Presentation.Validation
{
    // 仅同档对照进程；不提交命令、不运行规则、不生成/覆盖UI资产。
    public sealed class FixtureMemoryAttributionProbe : MonoBehaviour
    {
        private HotseatUiValidationDriver driver;
        private WeakReference weakDriver;
        private string output,mode,gameId;
        private int revision;
        private object inventory;
        private readonly List<object> samples = new List<object>();
        public void Begin(string path,string comparison,HotseatUiValidationDriver original,string game,int version)
        {
            output=path;mode=comparison;driver=original;weakDriver=new WeakReference(original);gameId=game;revision=version;
            inventory=original.MemoryAttributionFixtureInventory();StartCoroutine(Observe());
        }
        private GameSession Session()
        {
            var mobile=FindObjectOfType<MobileCityInteractionController>();
            return mobile==null?null:(GameSession)typeof(MobileCityInteractionController).GetField("session",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(mobile);
        }
        private object Capture(string phase)
        {
            var session=Session();var state=session?.State;var process=System.Diagnostics.Process.GetCurrentProcess();process.Refresh();
            return new {Utc=DateTime.UtcNow.ToString("o"),Phase=phase,Mode=mode,Pid=process.Id,Frame=Time.frameCount,
                GameId=state?.GameId,Revision=state?.EffectRuntime.StateRevision,IdentityMatches=state?.GameId==gameId&&state?.EffectRuntime.StateRevision==revision,
                FixtureManagedObjectAlive=weakDriver.IsAlive,FixtureComponentPresent=FindObjectOfType<HotseatUiValidationDriver>()!=null,
                ManagedApproximate=GC.GetTotalMemory(false),MonoUsed=Profiler.GetMonoUsedSizeLong(),MonoReserved=Profiler.GetMonoHeapSizeLong(),
                UnityUsed=Profiler.GetTotalAllocatedMemoryLong(),UnityReserved=Profiler.GetTotalReservedMemoryLong(),UnityUnusedReserved=Profiler.GetTotalUnusedReservedMemoryLong(),
                WorkingSetBytes=process.WorkingSet64,PrivateBytes=process.PrivateMemorySize64,PeakWorkingSetBytes=process.PeakWorkingSet64,
                EventNodes=state?.EffectRuntime.EffectNodes.Count,JournalEntries=state?.EffectRuntime.Journal.Count,
                GcCount0=GC.CollectionCount(0),GcCount1=GC.CollectionCount(1),GcCount2=GC.CollectionCount(2)};
        }
        private void Detach()
        {
            driver.DetachForMemoryAttribution();driver=null;
        }
        private IEnumerator Observe()
        {
            // 两组同样等待、同样受控GC；仅诊断对照进程执行，不触碰正在正常测试的进程。
            yield return new WaitForSecondsRealtime(15);
            samples.Add(Capture("before_detach_and_gc"));
            if(mode=="detach")Detach();
            yield return null;yield return null;
            GC.Collect();
            for(int i=0;i<5;i++)yield return null;
            GC.Collect();
            yield return new WaitForSecondsRealtime(5);
            for(int i=0;i<3;i++)
            {
                samples.Add(Capture("steady_after_gc_"+i));
                yield return new WaitForSecondsRealtime(10);
            }
            File.WriteAllText(Path.Combine(output,"内存归因结果.json"),JsonConvert.SerializeObject(new {
                ComparisonOnly=true,NormalGameAcceptance=false,Mode=mode,GameId=gameId,ExpectedRevision=revision,
                SavedFixtureInventory=inventory,Samples=samples,NoGameplayCommands=true,
                Scope="同档正常读档后静置对照；A保留夹具，B恢复原端口/解除订阅并销毁夹具。两组同样GC，不拿保留堆容量当存活对象。",
                Limitation="只覆盖读档后夹具常驻成本，不等于已累计大量动作的日志分配成本；正常运行进程未被暂停、强制GC或替换。"
            },Formatting.Indented));
            UnityEngine.Application.Quit(0);
        }
    }
}
#endif
