using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 只读预检基线的隔离合同，不计正常游戏或界面性能验收。
    public sealed class ReadOnlyQueryBaselineTests
    {
        [Serializable] private sealed class DerivedValues : List<NormalizedValue>
        {
            internal static Action Constructed;
            public NormalizedValue Extra;
            public DerivedValues() { Constructed?.Invoke(); }
        }
        private sealed class Baseline : IDisposable
        {
            private readonly object value;
            internal Baseline(GameState state) { value=typeof(GameStateCloneService).GetMethod("CaptureReadOnlyQueryBaseline",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{state}); }
            private object Call(string method,params object[] args) => value.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(value,args);
            internal bool Same(GameState state)=>(bool)Call("HasIdenticalFieldsTo",state);
            internal bool Equivalent(GameState state)=>(bool)Call("HasEquivalentFieldsTo",state);
            internal string Digest()=>(string)Call("ComputeRuleDigest");
            internal int EmptyLists()=>CountEmptyLists(value.GetType().GetField("snapshot",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value));
            public void Dispose()=>((IDisposable)value).Dispose();
        }
        private sealed class Identity : IEqualityComparer<object>
        {
            internal static readonly Identity Instance=new Identity();
            public new bool Equals(object a,object b)=>ReferenceEquals(a,b);
            public int GetHashCode(object value)=>RuntimeHelpers.GetHashCode(value);
        }
        // 仅测试读取私有基线，统计真实存在的独立空List，不把对象引用交给任何处理器。
        private static int CountEmptyLists(object root)
        {
            var pending=new Stack<object>();var seen=new HashSet<object>(Identity.Instance);var plans=new Dictionary<Type,FieldInfo[]>();int count=0;pending.Push(root);
            while(pending.Count>0)
            {
                object value=pending.Pop();if(value==null)continue;Type type=value.GetType();
                if(type.IsPrimitive||type.IsEnum||type==typeof(string)||type==typeof(decimal)||type==typeof(DateTime)||type==typeof(DateTimeOffset)||type==typeof(TimeSpan)||type==typeof(Guid))continue;
                if(!seen.Add(value))continue;
                if(type.IsGenericType&&type.GetGenericTypeDefinition()==typeof(List<>))
                {
                    var list=(System.Collections.IList)value;if(list.Count==0)count++;
                    else foreach(object child in list)pending.Push(child);
                    continue;
                }
                if(value is Array array){foreach(object child in array)pending.Push(child);continue;}
                if(!plans.TryGetValue(type,out var fields))
                {
                    var all=new List<FieldInfo>();
                    for(Type owner=type;owner!=null&&owner!=typeof(object);owner=owner.BaseType)
                        all.AddRange(owner.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly).Where(f=>!f.IsNotSerialized));
                    fields=all.ToArray();plans.Add(type,fields);
                }
                foreach(var field in fields)pending.Push(field.GetValue(value));
            }
            return count;
        }
        private static bool OriginalSame(GameState baseline,GameState state)=>(bool)typeof(GameStateCloneService)
            .GetMethod("HasIdenticalReadOnlyBaseline",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{baseline,state});
        private static string GuardDecision(Func<bool> same,Func<string> digest,GameState current)
        {
            try { return same()||RuleStateDigestService.Compute(current)==digest()?"accept":"reject"; }
            catch(InvalidOperationException exception){return exception.GetType().FullName+":"+exception.Message;}
        }
        private static GameState State()
        {
            var state=new GameState{GameId="isolated.query.baseline"};state.Players.Add(new PlayerState{PlayerId=1});
            state.EffectRuntime.RuleEvents.Add(new RuleEvent{EventId="event",Payload=NormalizedValue.CreateObject(new[]{
                new NormalizedValueEntry{Name="array",Value=NormalizedValue.CreateArray(new[]{NormalizedValue.CreateString("甲"),NormalizedValue.CreateString("乙")})}})});
            return state;
        }
        [TestCase(0)][TestCase(1)][TestCase(2)][TestCase(3)][TestCase(4)][TestCase(5)][TestCase(6)][TestCase(7)][TestCase(8)][TestCase(9)]
        public void EveryMutationMatchesTheOriginalIndependentBaseline(int change)
        {
            var state=State();var original=GameStateCloneService.DeepClone(state);
            using var baseline=new Baseline(state);string digest=baseline.Digest();
            Assert.That(digest,Is.EqualTo(RuleStateDigestService.Compute(original)));
            var payload=state.EffectRuntime.RuleEvents[0].Payload;
            switch(change)
            {
                case 1:payload.Items.Add(NormalizedValue.CreateInteger(1));break; // 闲置空集合也必须受保护。
                case 2:payload.Items=null;break;
                case 3:payload.Items=new DerivedValues{Extra=NormalizedValue.CreateInteger(1)};break;
                case 4:state.LaunchPlayerOrder.Add(1);break;
                case 5:state.Players[0].Score++;break;
                case 6:state.GameId=new string(state.GameId.ToCharArray());break;
                case 7:payload.Properties[0].Value.Items[0].StringValue="变动";break;
                case 8:state.EffectRuntime.RuleEvents[0].Payload=NormalizedValue.CreateObject(Array.Empty<NormalizedValueEntry>());break;
                case 9:state.Logs.Add(new GameLogEntry());break;
            }
            Assert.That(baseline.Same(state),Is.EqualTo(OriginalSame(original,state)));
            Assert.That(baseline.Equivalent(state),Is.EqualTo(GameStateCloneService.AreEquivalent(original,state)));
            Assert.That(baseline.Digest(),Is.EqualTo(digest),"回调式修改不得改变私有基线。");
            Assert.That(GuardDecision(()=>baseline.Same(state),baseline.Digest,state),Is.EqualTo(GuardDecision(()=>OriginalSame(original,state),()=>RuleStateDigestService.Compute(original),state)),"摘要排除字段及不支持容器的原异常行为必须保留。");
        }
        [Test]
        public void PublicClonesAndNestedConstructorClonesNeverReceiveTheHiddenEmptyContainers()
        {
            var state=State();state.EffectRuntime.RuleEvents[0].Payload.Items=new DerivedValues{Extra=NormalizedValue.CreateInteger(4)};
            var nested=new List<NormalizedValue>();
            DerivedValues.Constructed=()=>nested.Add(GameStateCloneService.DeepClone(NormalizedValue.CreateInteger(9)));
            try
            {
                using var baseline=new Baseline(state);
                Assert.That(nested.Count,Is.GreaterThan(0));
                var one=GameStateCloneService.DeepClone(state);var two=GameStateCloneService.DeepClone(state);
                foreach(var value in nested)value.Items.Add(NormalizedValue.CreateInteger(7));
                one.EffectRuntime.RuleEvents[0].Payload.Properties[0].Value.Items[0].Items.Add(NormalizedValue.CreateInteger(8));
                Assert.That(baseline.Same(state),Is.True);
                Assert.That(two.EffectRuntime.RuleEvents[0].Payload.Properties[0].Value.Items[0].Items,Is.Empty);
                Assert.That(state.EffectRuntime.RuleEvents[0].Payload.Properties[0].Value.Items[0].Items,Is.Empty);
            }
            finally { DerivedValues.Constructed=null; }
        }
        [Test]
        public void CyclesKeepTheOriginalCloneExceptionAndNeverCorruptOtherBaselines()
        {
            var state=State();var payload=state.EffectRuntime.RuleEvents[0].Payload;payload.Items.Add(payload);
            var original=Assert.Throws<InvalidOperationException>(()=>GameStateCloneService.DeepClone(state));
            var actual=Assert.Throws<TargetInvocationException>(()=>{using var baseline=new Baseline(state);});
            Assert.That(actual.InnerException.GetType(),Is.EqualTo(original.GetType()));Assert.That(actual.InnerException.Message,Is.EqualTo(original.Message));
            var clean=State();using var unaffected=new Baseline(clean);Assert.That(unaffected.Same(clean),Is.True);
        }
        [Test]
        public void OptionalRecordedBaselinesKeepEveryFieldAndMeasureAllocation()
        {
            string Arg(string key)=>Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith(key+"="))?.Substring(key.Length+1);
            string sources=Arg("--yc-query-baseline-profile-sources"),report=Arg("--yc-query-baseline-profile-report");
            if(sources==null||report==null)Assert.Ignore("仅显式提供原检查点及新报告路径时执行隔离计时。");
            var results=new List<object>();
            long allocationProbeStart=GC.GetAllocatedBytesForCurrentThread();var allocationProbe=new byte[8192];
            bool allocationAvailable=GC.GetAllocatedBytesForCurrentThread()-allocationProbeStart>=allocationProbe.Length;GC.KeepAlive(allocationProbe);
            foreach(string source in sources.Split('|'))
            {
                var data=JsonConvert.DeserializeObject<MatchSaveData>((string)JObject.Parse(File.ReadAllText(source))["Payload"]);
                var settings=(JsonSerializerSettings)typeof(HostRecoveryService).GetField("ArchiveSettings",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
                // 读取原归档后沿正式克隆完成兼容绑定，再对受预检的实际绑定态做对照。
                var state=GameStateCloneService.DeepClone(JsonConvert.DeserializeObject<HostSessionArchiveDto>(data.ArchiveJson,settings).Snapshot.State);
                var original=GameStateCloneService.DeepClone(state);string digest=RuleStateDigestService.Compute(original);
                int fullEmpty=-1,compactEmpty=-1;
                for(int i=0;i<4;i++)foreach(bool full in i%2==0?new[]{true,false}:new[]{false,true})
                {
                    using var mode=full?GameStateCloneService.UseFullReadOnlyQueryBaselinesForDiagnostics():null;
                    long allocated=GC.GetAllocatedBytesForCurrentThread();var clock=Stopwatch.StartNew();using var baseline=new Baseline(state);clock.Stop();
                    long? allocatedBytes=allocationAvailable?GC.GetAllocatedBytesForCurrentThread()-allocated:(long?)null;
                    int? emptyLists=null;
                    if(i==0){emptyLists=baseline.EmptyLists();if(full)fullEmpty=emptyLists.Value;else compactEmpty=emptyLists.Value;}
                    results.Add(new{Source=Path.GetFileName(source),Index=i,FullCopy=full,Seconds=clock.Elapsed.TotalSeconds,AllocatedBytes=allocatedBytes,DistinctEmptyLists=emptyLists});
                    Assert.That(baseline.Same(state),Is.EqualTo(OriginalSame(original,state)));Assert.That(baseline.Equivalent(state),Is.True);
                    if(i==0)Assert.That(baseline.Digest(),Is.EqualTo(digest));
                }
                Assert.That(compactEmpty,Is.LessThan(fullEmpty),"必须真实减少私有基线内的独立空容器，不能凭不可用的分配计数宣称收益。");
                Assert.That(GameStateCloneService.AreEquivalent(original,state),Is.True);
            }
            File.WriteAllText(report,JsonConvert.SerializeObject(new{Scope="原检查点只读基线隔离对照，不计正常点击P95",AllFieldsEquivalent=true,OriginalUnchanged=true,ForcedGc=false,AllocationBytesAvailable=allocationAvailable,Results=results},Formatting.Indented));
        }
    }
}
