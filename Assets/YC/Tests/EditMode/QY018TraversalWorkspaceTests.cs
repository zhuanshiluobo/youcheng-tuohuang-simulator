using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
#if !UNITY_EDITOR
    [NonParallelizable]
#endif
    public sealed class QY018TraversalWorkspaceTests
    {
        static readonly Type Service = typeof(GameStateCloneService);
        static readonly FieldInfo Cache = Service.GetField("reusableComparisonPairs", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        static GameState State(long value)
        {
            var state = new GameState { GameId = "工作区合同" };
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { NormalizedArguments = NormalizedValue.CreateInteger(value) });
            return state;
        }
        static void AssertNoReferencesInCachedEntries()
        {
            var cache = Cache?.GetValue(null);
            if (cache == null) return; // 改前实现无缓存；仍运行相同公开语义合同。
            Assert.That((int)cache.GetType().GetProperty("Count").GetValue(cache), Is.Zero);
            var pairType = Service.GetNestedType("ReferencePair", BindingFlags.NonPublic);
            foreach (var arrayField in cache.GetType().GetFields(Fields).Where(f => f.FieldType.IsArray))
            {
                var valueField = arrayField.FieldType.GetElementType().GetFields(Fields).FirstOrDefault(f => f.FieldType == pairType);
                if (valueField == null) continue;
                var entries = (Array)arrayField.GetValue(cache);
                if (entries == null) continue;
                foreach (var entry in entries)
                {
                    var pair = valueField.GetValue(entry);
                    Assert.That(pairType.GetProperty("Left").GetValue(pair), Is.Null);
                    Assert.That(pairType.GetProperty("Right").GetValue(pair), Is.Null);
                }
            }
        }

        [Test]
        public void RepeatedTraversalReadsChangedStateAndPreservesHeldIdentity()
        {
            var source = State(1);
            var target = GameStateCloneService.DeepClone(source);
            var held = target.EffectRuntime.EffectNodes[0];
            for (int i = 2; i < 6; i++)
            {
                source.EffectRuntime.EffectNodes[0].NormalizedArguments.IntegerValue = i;
                Assert.That(GameStateCloneService.AreEquivalent(source, target), Is.False);
                GameStateCloneService.CopyTo(target, source);
                Assert.That(GameStateCloneService.AreEquivalent(source, target), Is.True);
                Assert.That(target.EffectRuntime.EffectNodes[0], Is.SameAs(held));
            }
            AssertNoReferencesInCachedEntries();
        }

        [Test]
        public void SharedTargetAliasKeepsOriginalGlobalVisitedOrder()
        {
            var one = NormalizedValue.CreateInteger(1);
            var two = NormalizedValue.CreateInteger(2);
            var shared = NormalizedValue.CreateInteger(9);
            var source = State(0);
            var target = State(0);
            source.EffectRuntime.EffectNodes[0].NormalizedArguments.Items.AddRange(new[] { one, two, one });
            target.EffectRuntime.EffectNodes[0].NormalizedArguments.Items.AddRange(new[] { shared, shared, shared });
            GameStateCloneService.CopyTo(target, source);
            var values = target.EffectRuntime.EffectNodes[0].NormalizedArguments.Items;
            Assert.That(values[0], Is.SameAs(shared));
            Assert.That(values[1], Is.SameAs(shared));
            Assert.That(values[2], Is.SameAs(shared));
            // 全局已访问(T,S1)在末次仍跳过，保改前S2结果；不能改成仅递归栈去重。
            Assert.That(shared.IntegerValue, Is.EqualTo(2));
            Assert.That(one.IntegerValue, Is.EqualTo(1));
        }

        [Test]
        public void FailedCloneBranchClearsReferencesBeforeNextUse()
        {
            var source = State(1);
            var value = source.EffectRuntime.EffectNodes[0].NormalizedArguments;
            value.Items.Add(value);
            Assert.Throws<InvalidOperationException>(() => GameStateCloneService.CopyTo(new GameState(), source));
            AssertNoReferencesInCachedEntries();
            var valid = State(7);
            var copy = GameStateCloneService.DeepClone(valid);
            Assert.That(GameStateCloneService.AreEquivalent(valid, copy), Is.True);
            AssertNoReferencesInCachedEntries();
        }

        [Test]
        public void ThreadAndNestedLeaseOwnersUseIndependentSets()
        {
            Exception error = null;
            var caches = new object[2];
            var threads = Enumerable.Range(0, 2).Select(index => new Thread(() =>
            {
                try
                {
                    var source = State(index);
                    var target = GameStateCloneService.DeepClone(source);
                    for (int i = 0; i < 4; i++) GameStateCloneService.CopyTo(target, source);
                    Assert.That(GameStateCloneService.AreEquivalent(source, target), Is.True);
                    caches[index] = Cache?.GetValue(null);
                    if (Cache != null)
                    {
                        var rent = Service.GetMethod("RentComparisonPairs", BindingFlags.Static | BindingFlags.NonPublic);
                        var release = Service.GetMethod("ReturnComparisonPairs", BindingFlags.Static | BindingFlags.NonPublic);
                        var outer = rent.Invoke(null, null);
                        var inner = rent.Invoke(null, null);
                        Assert.That(inner, Is.Not.SameAs(outer));
                        release.Invoke(null, new[] { inner });
                        release.Invoke(null, new[] { outer });
                    }
                    AssertNoReferencesInCachedEntries();
                }
                catch (Exception failure) { Interlocked.CompareExchange(ref error, failure, null); }
            })).ToArray();
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) thread.Join();
            if (error != null) throw error;
            if (Cache != null) Assert.That(caches[0], Is.Not.SameAs(caches[1]));
        }

        [Test]
        public void OversizedWorkspaceIsClearedAndDiscarded()
        {
            if (Cache == null) return;
            var rent = Service.GetMethod("RentComparisonPairs", BindingFlags.Static | BindingFlags.NonPublic);
            var release = Service.GetMethod("ReturnComparisonPairs", BindingFlags.Static | BindingFlags.NonPublic);
            int maximum = (int)Service.GetField("MaximumRetainedComparisonPairs", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
            var set = rent.Invoke(null, null);
            var add = set.GetType().GetMethod("Add");
            var pairType = Service.GetNestedType("ReferencePair", BindingFlags.NonPublic);
            var owner = new object();
            for (int i = 0; i <= maximum; i++) add.Invoke(set, new[] { Activator.CreateInstance(pairType, new[] { owner, new object() }) });
            release.Invoke(null, new[] { set });
            Assert.That((int)set.GetType().GetProperty("Count").GetValue(set), Is.Zero);
            Assert.That(Cache.GetValue(null), Is.Null);
        }
    }
}
