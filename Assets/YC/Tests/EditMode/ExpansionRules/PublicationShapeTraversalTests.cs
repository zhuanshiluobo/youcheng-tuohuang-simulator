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
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class PublicationShapeTraversalTests
    {
        [Serializable] private sealed class DerivedValues : List<NormalizedValue> { public NormalizedValue Extra; }
        [Serializable] private sealed class DerivedPlayers : List<PlayerState> { public NormalizedValue Extra; }
        private static readonly MethodInfo Prove = typeof(GameStateCloneService).GetMethod("HasUniqueMutableReferences", BindingFlags.NonPublic | BindingFlags.Static);
        private static bool Unique(GameState state) => (bool)Prove.Invoke(null, new object[] { state });
        private static GameState State()
        {
            var state = new GameState { GameId = "isolated.publication.shape" };
            state.Players.Add(new PlayerState { PlayerId = 1 });
            state.EffectRuntime.RuleEvents.Add(new RuleEvent { EventId = "event", Payload = NormalizedValue.CreateObject(new[] {
                new NormalizedValueEntry { Name = "array", Value = NormalizedValue.CreateArray(new[] { NormalizedValue.CreateString("甲"), NormalizedValue.CreateString("乙") }) } }) });
            return state;
        }
        [Test]
        public void AliasesCyclesDormantListsAndDerivedContainersMatchTheOriginalFullTraversal()
        {
            var changes = new Action<GameState>[] {
                s => { },
                s => s.EffectRuntime.RuleEvents.Add(new RuleEvent { EventId = "alias", Payload = s.EffectRuntime.RuleEvents[0].Payload }),
                s => { var v=s.EffectRuntime.RuleEvents[0].Payload.Properties[0].Value; v.Items[1]=v.Items[0]; },
                s => { var v=s.EffectRuntime.RuleEvents[0].Payload.Properties[0].Value; v.Items[1].Properties=v.Items[0].Properties; },
                s => { var v=s.EffectRuntime.RuleEvents[0].Payload.Properties[0].Value; v.Items.Add(v); },
                s => { var v=s.EffectRuntime.RuleEvents[0].Payload; v.Kind=NormalizedValueKind.Integer; v.Items.Add(v); },
                s => { var v=s.EffectRuntime.RuleEvents[0].Payload; v.Items=null; v.Properties[0].Value.Properties=null; },
                s => { var v=s.EffectRuntime.RuleEvents[0].Payload; v.Items=new DerivedValues { Extra=v }; },
                s => { var v=s.EffectRuntime.RuleEvents[0].Payload; v.Items=new DerivedValues { Extra=NormalizedValue.CreateInteger(3) }; },
                s => s.Players.Add(s.Players[0]),
                s => { var values=new DerivedPlayers { Extra=s.EffectRuntime.RuleEvents[0].Payload }; values.Add(s.Players[0]); s.Players=values; },
                s => { var values=new DerivedPlayers { Extra=NormalizedValue.CreateInteger(4) }; values.Add(s.Players[0]); s.Players=values; },
                s => s.EffectRuntime.PlayerOrderSnapshot = s.LaunchPlayerOrder,
                s => { s.LaunchPlayerOrder=null; s.Players=null; },
                s => { s.LaunchPlayerOrder.AddRange(new[] { 1, 2, 3, 1 }); }
            };
            for (int i=0;i<changes.Length;i++)
            {
                var state=State(); changes[i](state); bool reference;
                using (GameStateCloneService.UseFullPublicationShapeTraversalForDiagnostics()) reference=Unique(state);
                Assert.That(Unique(state),Is.EqualTo(reference),"案例"+i);
                using (GameStateCloneService.VerifySchedulingPublicationCopies()) Assert.That(Unique(state),Is.EqualTo(reference));
            }
        }

        [Test]
        public void OptionalRecordedGraphChecksMatchOriginalTraversalAndMeasureBothPaths()
        {
            string Arg(string key) => Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith(key + "="))?.Substring(key.Length + 1);
            string sources = Arg("--yc-publication-profile-sources"), report = Arg("--yc-publication-profile-report");
            if (sources == null || report == null) Assert.Ignore("仅显式提供原检查点与新报告路径时执行；不计正常界面验收。");
            var results = new List<object>();
            foreach (string source in sources.Split('|'))
            {
                var data = JsonConvert.DeserializeObject<MatchSaveData>((string)JObject.Parse(File.ReadAllText(source))["Payload"]);
                var settings = (JsonSerializerSettings)typeof(HostRecoveryService).GetField("ArchiveSettings", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                var state = JsonConvert.DeserializeObject<HostSessionArchiveDto>(data.ArchiveJson, settings).Snapshot.State;
                var baseline = GameStateCloneService.DeepClone(state);
                bool original;
                using (GameStateCloneService.UseFullPublicationShapeTraversalForDiagnostics()) original = Unique(state);
                Assert.That(Unique(state), Is.EqualTo(original));
                for (int i = 0; i < 4; i++)
                    foreach (bool full in i % 2 == 0 ? new[] { true, false } : new[] { false, true })
                    {
                        using var mode = full ? GameStateCloneService.UseFullPublicationShapeTraversalForDiagnostics() : null;
                        long allocated = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
                        bool actual = Unique(state); clock.Stop();
                        results.Add(new { Source = Path.GetFileName(source), Index = i, FullTraversal = full, Seconds = clock.Elapsed.TotalSeconds,
                            AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated, Unique = actual });
                        Assert.That(actual, Is.EqualTo(original));
                    }
                using (GameStateCloneService.VerifySchedulingPublicationCopies()) Assert.That(Unique(state), Is.EqualTo(original));
                Assert.That(GameStateCloneService.AreEquivalent(state, baseline), Is.True, "引用检查不能改写原检查点。");
            }
            File.WriteAllText(report, JsonConvert.SerializeObject(new { Scope = "七处原检查点引用检查隔离对照，不计正常点击P95", AllEquivalent = true,
                OriginalUnchanged = true, ForcedGc = false, Results = results }, Formatting.Indented));
        }
    }
}
