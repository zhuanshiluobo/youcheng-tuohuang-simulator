using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CompiledStateComparisonTests
    {
        [Serializable] public class Base { private long stamp = 73; public void ChangeStamp() { stamp++; } }
        [Serializable] public sealed class Derived : Base { public double Number = double.NaN; public readonly int ReadOnly = 9; public object Value; [NonSerialized] public string Transient; }
        [Serializable] public sealed class ExtraList : List<NormalizedValue> { public string Extra; }
        [Serializable] public sealed class Bits { public double Number; public float Small; public decimal Amount; public object Unknown; }
        private static bool Equal(object left, object right) => (bool)typeof(GameStateCloneService).GetMethod("AreEquivalentPersistentValues", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { left, right, typeof(object) });
        private static bool CopyEqual(Bits left, Bits right) => (bool)typeof(GameStateCloneService).GetMethod("HasIdenticalCopyValue", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { left, right, typeof(Bits) });

        [Test]
        public void UnchangedCopyRequiresExactFloatingPointDecimalAndStringReferences()
        {
            var a = new Bits { Number = 0d, Small = 0f, Amount = 1m }; var b = GameStateCloneService.DeepClone(a);
            Assert.That(CopyEqual(a, b), Is.True);
            b.Number = BitConverter.Int64BitsToDouble(long.MinValue); Assert.That(Equal(a, b), Is.True); Assert.That(CopyEqual(a, b), Is.False);
            b = GameStateCloneService.DeepClone(a); b.Small = BitConverter.Int32BitsToSingle(int.MinValue); Assert.That(CopyEqual(a, b), Is.False);
            b = GameStateCloneService.DeepClone(a); b.Amount = 1.00m; Assert.That(Equal(a, b), Is.True); Assert.That(CopyEqual(a, b), Is.False);
            a.Number = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000001)); b = GameStateCloneService.DeepClone(a);
            b.Number = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000002)); Assert.That(Equal(a, b), Is.True); Assert.That(CopyEqual(a, b), Is.False);
            a.Unknown = new List<string> { "值" }; b = GameStateCloneService.DeepClone(a);
            Assert.That(CopyEqual(a, b), Is.False, "声明为object的动态类型仍走原复制");
        }

        [Test]
        public void PlansKeepPrivateBaseReadonlyNaNTransientAndNestedChanges()
        {
            var a = new Derived { Value = new List<NormalizedValue> { NormalizedValue.CreateInteger(8) }, Transient = "a" };
            var b = GameStateCloneService.DeepClone(a); b.Transient = "b";
            Assert.That(Equal(a, b), Is.True);
            ((List<NormalizedValue>)b.Value)[0].StringValue = "闲置字段";
            Assert.That(Equal(a, b), Is.False);
            b = GameStateCloneService.DeepClone(a); b.ChangeStamp();
            Assert.That(Equal(a, b), Is.False);
            b = GameStateCloneService.DeepClone(a); b.Number = 3;
            Assert.That(Equal(a, b), Is.False);
        }

        [Test]
        public void NormalizedPlansKeepNullEmptyDerivedListsCyclesAndDormantFields()
        {
            var a = NormalizedValue.CreateInteger(3); var b = GameStateCloneService.DeepClone(a);
            Assert.That(Equal(a, b), Is.True);
            b.Items = null; Assert.That(Equal(a, b), Is.False);
            b = GameStateCloneService.DeepClone(a); b.Items = new ExtraList { Extra = "附加字段" };
            Assert.That(Equal(a, b), Is.False);
            a.Items = new ExtraList { Extra = "原" }; b = GameStateCloneService.DeepClone(a);
            Assert.That(Equal(a, b), Is.True);
            ((ExtraList)b.Items).Extra = "新"; Assert.That(Equal(a, b), Is.False);
            a = NormalizedValue.CreateNull(); b = NormalizedValue.CreateNull(); a.Items.Add(a); b.Items.Add(b);
            Assert.That(Equal(a, b), Is.True);
            b.IntegerValue = 1; Assert.That(Equal(a, b), Is.False);
        }

        [Test]
        public void IgnoringRuntimeStillComparesEveryOtherRootFieldIncludingLogs()
        {
            var a = new GameState(); var b = GameStateCloneService.DeepClone(a);
            b.EffectRuntime.StepCount++;
            Assert.That(GameStateCloneService.AreEquivalent(a, b), Is.False);
            Assert.That(GameStateCloneService.AreEquivalentIgnoringEffectRuntime(a, b), Is.True);
            b.Logs.Add(new GameLogEntry { Message = "变化" });
            Assert.That(GameStateCloneService.AreEquivalentIgnoringEffectRuntime(a, b), Is.False);
        }
    }
}
