using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.State;
using YC.Domain.Rules;

namespace YC.Tests.EditMode
{
    public sealed class QY018OperationAllocationContractsTests
    {
        [Test]
        public void PublicDefaultsStillCreateIndependentChildren()
        {
            var left = new NormalizedValue();
            var right = new NormalizedValue();
            Assert.That(left.Kind, Is.EqualTo(NormalizedValueKind.Null));
            Assert.That(left.Items, Is.Empty);
            Assert.That(left.Properties, Is.Empty);
            Assert.That(left.Items, Is.Not.SameAs(right.Items));
            Assert.That(left.Properties, Is.Not.SameAs(right.Properties));
            var entry = new NormalizedValueEntry();
            Assert.That(entry.Value, Is.Not.Null);
            Assert.That(entry.Value.Items, Is.Not.Null);
        }

        [Test]
        public void BothCloneEntrypointsPreserveValuesAndIsolateMutableBranches()
        {
            var value = NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "payload", Value = NormalizedValue.CreateArray(new List<NormalizedValue>
                {
                    NormalizedValue.CreateString("原文"), NormalizedValue.CreateInteger(42)
                }) }
            });
            foreach (var clone in new[] { value.Clone(), GameStateCloneService.DeepClone(value) })
            {
                Assert.That(clone.Properties[0].Value.Items[0].StringValue, Is.EqualTo("原文"));
                Assert.That(clone.Properties[0].Value.Items[1].IntegerValue, Is.EqualTo(42));
                clone.Properties[0].Value.Items[0].StringValue = "副本";
                clone.Properties[0].Value.Items.Add(NormalizedValue.CreateNull());
                Assert.That(value.Properties[0].Value.Items.Count, Is.EqualTo(2));
                Assert.That(value.Properties[0].Value.Items[0].StringValue, Is.EqualTo("原文"));
            }
        }

        [Test]
        public void ClonePreservesNullsAndSeparatesSiblingAliases()
        {
            var child = new NormalizedValue { Items = null, Properties = null };
            var value = new NormalizedValue { Items = new List<NormalizedValue> { child, child, null }, Properties = null };
            foreach (var clone in new[] { value.Clone(), GameStateCloneService.DeepClone(value) })
            {
                Assert.That(clone.Properties, Is.Null);
                Assert.That(clone.Items[0].Items, Is.Null);
                Assert.That(clone.Items[0].Properties, Is.Null);
                Assert.That(clone.Items[2], Is.Null);
                Assert.That(clone.Items[0], Is.Not.SameAs(child));
                Assert.That(clone.Items[0], Is.Not.SameAs(clone.Items[1]));
            }
        }

        [Test]
        public void GenericCloneStillRejectsCycles()
        {
            var value = new NormalizedValue();
            value.Items.Add(value);
            Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(value));
        }

        [Test]
        public void EqualityAndCopyKeepScalarNullAndRuntimeContracts()
        {
            var left = new GameState { GameId = "对局", ContentPackHash = null, Round = 2, UseSeatTurnOrder = true };
            var right = GameStateCloneService.DeepClone(left);
            Assert.That(GameStateCloneService.AreEquivalent(left, right), Is.True);
            right.Round++;
            Assert.That(GameStateCloneService.AreEquivalent(left, right), Is.False);
            GameStateCloneService.CopyTo(right, left);
            right.ContentPackHash = "";
            Assert.That(GameStateCloneService.AreEquivalent(left, right), Is.False);
            GameStateCloneService.CopyTo(right, left);
            right.EffectRuntime.StateRevision++;
            Assert.That(GameStateCloneService.AreEquivalent(left, right), Is.False);
            Assert.That(GameStateCloneService.AreEquivalentIgnoringEffectRuntime(left, right), Is.True);
            right.Phase = (GamePhase)((int)left.Phase + 1);
            Assert.That(GameStateCloneService.AreEquivalentIgnoringEffectRuntime(left, right), Is.False);
        }
    }
}
