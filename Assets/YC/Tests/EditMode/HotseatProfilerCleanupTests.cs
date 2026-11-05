using System;
using System.Linq;
using System.Reflection;
using System.IO;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using YC.Domain.State;
using YC.Tests.EditMode.ExpansionRules;
using Object=UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class HotseatProfilerCleanupTests
    {
        [TestCase(false)] [TestCase(true)]
        public void FullGameResourceCountersRestoreTheirPreviousOwnersOnDestroy(bool previouslyEnabled)
        {
            var artwork=Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp",true);
            var icons=Type.GetType("YC.Presentation.GameplayIconResourceResolver, Assembly-CSharp",true);
            bool Enabled(Type type) { var value=type.GetMethod("ReadMemoryDiagnostics").Invoke(null,null); return (bool)value.GetType().GetField("Enabled").GetValue(value); }
            void Enable(Type type,bool value)=>type.GetMethod("SetMemoryDiagnosticsEnabled").Invoke(null,new object[]{value});
            bool oldArtwork=Enabled(artwork),oldIcons=Enabled(icons);
            var host=new GameObject("隔离整局资源计数清理");
            var type=Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp",true);
            var driver=host.AddComponent(type);const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            try
            {
                Enable(artwork,previouslyEnabled);Enable(icons,previouslyEnabled);
                type.GetMethod("BeginFullGameMemoryDiagnostics",flags).Invoke(driver,null);
                type.GetMethod("BeginFullGameMemoryDiagnostics",flags).Invoke(driver,null);
                Assert.That(Enabled(artwork)&&Enabled(icons),Is.True);
                type.GetMethod("OnDestroy",flags).Invoke(driver,null);
                type.GetMethod("OnDestroy",flags).Invoke(driver,null);
                Assert.That(Enabled(artwork),Is.EqualTo(previouslyEnabled));Assert.That(Enabled(icons),Is.EqualTo(previouslyEnabled));
            }
            finally
            {
                type.GetMethod("OnDestroy",flags).Invoke(driver,null);Object.DestroyImmediate(host);
                Enable(artwork,oldArtwork);Enable(icons,oldIcons);
            }
        }

        [Test]
        public void StreamingEvidenceKeepsTheOriginalFullJsonBytesIncludingDormantStateFields()
        {
            var artifacts = IsolatedTestArtifacts.BeginCase();
            var host = new GameObject("隔离流式证据写入");
            try
            {
                var type = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
                var driver = host.AddComponent(type);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                type.GetField("output", flags).SetValue(driver, artifacts.ResultsDirectory);
                var state = new GameState();
                state.Logs.Add(new GameLogEntry { Message = "中文\\引号\"\n日志" });
                var value = NormalizedValue.CreateString("当前值");
                value.Items.Add(NormalizedValue.CreateString("闲置字段"));
                state.EffectRuntime.Journal.Add(new RuleJournalEntry { Payload = value });
                var expected = JsonConvert.SerializeObject(state, Formatting.Indented);
                type.GetMethod("Write", flags).Invoke(driver, new object[] { "完整证据.json", state });
                var bytes = File.ReadAllBytes(Path.Combine(artifacts.ResultsDirectory, "完整证据.json"));
                Assert.That(bytes, Is.EqualTo(new System.Text.UTF8Encoding(false).GetBytes(expected)));
                Assert.That(state.EffectRuntime.Journal[0].Payload.Items[0].StringValue, Is.EqualTo("闲置字段"));
            }
            finally { Object.DestroyImmediate(host); artifacts.Finish(); }
        }

        [Test]
        public void DriverDestroyCallbackDetachesItsHooksAndKeepsOtherMetricListeners()
        {
            bool enabled=UiRegressionMetrics.Enabled;int own=0,other=0;
            Action<UiRegressionMetrics.Sample> owned=_=>own++,external=_=>other++;
            Action<string,string,long> lua=(_,__,___)=>{};
            var host=new GameObject("隔离性能订阅清理");
            try
            {
                var type=Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp",true);
                var driver=host.AddComponent(type);const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
                type.GetField("fullGameUiMetricsWereEnabled",flags).SetValue(driver,enabled);
                type.GetField("fullGameUiTiming",flags).SetValue(driver,owned);
                type.GetField("fullGameLuaTiming",flags).SetValue(driver,lua);
                UiRegressionMetrics.Recorded+=owned;UiRegressionMetrics.Recorded+=external;UiRegressionMetrics.Enabled=true;
                LuaEffectEventDispatcher.CpuTimingSample+=lua;
                // EditMode未进入运行生命周期，不能依赖DestroyImmediate派发MonoBehaviour消息。
                // 显式调用实际销毁回调；随后销毁再次清理也必须安全。
                type.GetMethod("OnDestroy",flags).Invoke(driver,null);
                Object.DestroyImmediate(host);
                Assert.That(UiRegressionMetrics.Enabled,Is.EqualTo(enabled));
                UiRegressionMetrics.Enabled=true;UiRegressionMetrics.Note("isolated.cleanup","probe");
                Assert.That(own,Is.Zero);Assert.That(other,Is.EqualTo(1));
                var callbacks=LuaEffectEventDispatcher.CpuTimingSample;
                Assert.That(callbacks?.GetInvocationList().Contains(lua)??false,Is.False);
            }
            finally
            {
                if(host!=null)Object.DestroyImmediate(host);
                UiRegressionMetrics.Recorded-=owned;UiRegressionMetrics.Recorded-=external;UiRegressionMetrics.Enabled=enabled;
                LuaEffectEventDispatcher.CpuTimingSample-=lua;
            }
        }
    }
}
