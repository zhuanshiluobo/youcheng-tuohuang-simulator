using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.State;
using YC.Domain.Rules;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class ResponsePreviewReadOnlyGuardTests
    {
        [Serializable] private sealed class Shapes
        {
            public int? Number;
            public float Single;
            public double Double;
            public decimal Decimal;
            public Array Values;
            public Shapes Next;
        }
        private static readonly MethodInfo Exact = typeof(GameStateCloneService).GetMethod("HasIdenticalReadOnlyBaseline", BindingFlags.NonPublic | BindingFlags.Static);
        private static bool Identical(GameState baseline, GameState current) => (bool)Exact.Invoke(null, new object[] { baseline, current });
        [Test]
        public void NullableArraysAndFloatingPointBitsAreComparedWithoutThePublicationCopyRestrictions()
        {
            var compare=typeof(GameStateCloneService).GetMethod("HaveIdenticalReadOnlyFields",BindingFlags.NonPublic|BindingFlags.Static);
            bool Same(object a,object b)=>(bool)compare.Invoke(null,new[]{a,b});
            var source=new Shapes { Number=3, Single=BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00001)),
                Double=-0.0d,Decimal=1.00m,Values=Array.CreateInstance(typeof(int),new[]{2},new[]{-1}),Next=new Shapes() };
            source.Values.SetValue(4,-1); source.Values.SetValue(5,0);
            // 本隔离测试直接建立独立数组基线，不扩大旧深复制器对非零下界数组的输入支持范围。
            var baseline=new Shapes { Number=source.Number,Single=source.Single,Double=source.Double,Decimal=source.Decimal,
                Values=(Array)source.Values.Clone(),Next=new Shapes() };
            Assert.That(Same(baseline,source),Is.True);
            source.Number=null;Assert.That(Same(baseline,source),Is.False);source.Number=3;
            source.Double=0.0d;Assert.That(Same(baseline,source),Is.False);source.Double=-0.0d;
            source.Single=BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00002));Assert.That(Same(baseline,source),Is.False);source.Single=baseline.Single;
            source.Decimal=1.0m;Assert.That(Same(baseline,source),Is.False);source.Decimal=baseline.Decimal;
            source.Values=Array.CreateInstance(typeof(int),new[]{2},new[]{0});Assert.That(Same(baseline,source),Is.False);
            source.Values=(Array)baseline.Values.Clone();Assert.That(Same(baseline,source),Is.True);
            source.Next=source;Assert.That(Same(baseline,source),Is.False,"新增环不能越过有限基线的末端");
        }
        private static GameState State()
        {
            var state = new GameState { GameId = "isolated.read_only_guard", RandomRuntime = new GameplayRandomState() };
            state.Players.Add(new PlayerState { PlayerId = 1, Name = "原玩家" });
            state.RandomRuntime.Streams.Add(new GameplayRandomStreamState { StreamId = "stream", State = 5 });
            state.Logs.Add(new GameLogEntry { Message = "显示文案" });
            state.EffectRuntime.RuleEvents.Add(new RuleEvent { EventId = "event", Payload = NormalizedValue.CreateObject(new[] {
                new NormalizedValueEntry { Name = "value", Value = NormalizedValue.CreateInteger(1) } }) });
            return state;
        }
        [Test]
        public void IndependentBaselineAcceptsUnchangedFieldsAndKeepsEveryMutableBranchDetached()
        {
            var state = State(); var baseline = GameStateCloneService.DeepClone(state);
            Assert.That(Identical(baseline, state), Is.True);
            state.EffectRuntime.RuleEvents[0].Payload.Properties[0].Value.IntegerValue = 3;
            Assert.That(Identical(baseline, state), Is.False);
            Assert.That(baseline.EffectRuntime.RuleEvents[0].Payload.Properties[0].Value.IntegerValue, Is.EqualTo(1));
        }
        [Test]
        public void MutationAndDigestExcludedDisplayCasesKeepTheOriginalDigestDecision()
        {
            var cases = new (string Name, Action<GameState> Change, bool SameDigest)[] {
                ("资源", s => s.Players[0].Resources.Iron++, false),
                ("玩家身份", s => s.Players[0].PlayerId++, false),
                ("玩家名", s => s.Players[0].Name = "更改", false),
                ("随机游标", s => s.RandomRuntime.Streams[0].WordsConsumed++, false),
                ("历史归一化值", s => s.EffectRuntime.RuleEvents[0].Payload.Properties[0].Value.IntegerValue++, false),
                ("闲置归一化字段", s => s.EffectRuntime.RuleEvents[0].Payload.Properties[0].Value.StringValue = "闲置值", false),
                ("空列表转null", s => s.EffectRuntime.RuleEvents[0].Payload.Items = null, false),
                ("追加日志", s => s.Logs.Add(new GameLogEntry()), false),
                ("清空历史", s => s.EffectRuntime.RuleEvents.Clear(), false),
                ("只改显示日志", s => s.Logs[0].Message = "另一文案", true),
                ("同值独立字符串", s => s.Players[0].Name = new string(s.Players[0].Name.ToCharArray()), true),
                ("同值独立历史", s => s.EffectRuntime.RuleEvents[0] = GameStateCloneService.DeepClone(s.EffectRuntime.RuleEvents[0]), true)
            };
            foreach (var test in cases)
            {
                var state = State(); var baseline = GameStateCloneService.DeepClone(state);
                string before = RuleStateDigestService.Compute(state);
                test.Change(state);
                bool exact = Identical(baseline, state);
                bool same = RuleStateDigestService.Compute(state) == before;
                Assert.That(same, Is.EqualTo(test.SameDigest), test.Name);
                Assert.That(!exact || same, Is.True, "快速确认不能漏掉原摘要可发现的改写：" + test.Name);
                Assert.That(exact || RuleStateDigestService.Compute(state) == RuleStateDigestService.Compute(baseline), Is.EqualTo(same), test.Name);
            }
        }
        [Test]
        public void HistoricalGraphActuallyUsesExactFieldPathWithoutComputingTwoDigests()
        {
            var state = State();
            for (int i = 1; i <= 1000; i++) state.EffectRuntime.RuleEvents.Add(new RuleEvent { EventId = "event" + i,
                Payload = NormalizedValue.CreateObject(new[] { new NormalizedValueEntry { Name = "payload", Value = NormalizedValue.CreateString("未修改历史") } }) });
            var clock = Stopwatch.StartNew(); var baseline = GameStateCloneService.DeepClone(state);
            Assert.That(Identical(baseline, state), Is.True); double fields = clock.Elapsed.TotalSeconds;
            clock.Restart(); string before = RuleStateDigestService.Compute(state); Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before));
            TestContext.WriteLine("局部对照：独立全字段基线=" + fields + "秒；原两次完整摘要=" + clock.Elapsed.TotalSeconds + "秒；非正常UI验收。");
        }
    }
}
