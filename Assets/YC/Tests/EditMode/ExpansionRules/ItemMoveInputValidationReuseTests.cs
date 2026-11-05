using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class ItemMoveInputValidationReuseTests
    {
        private static IDisposable Scope() => (IDisposable)typeof(ItemMoveInputNormalization).GetMethod("BeginSynchronousValidationScope", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        private static EffectSpec Spec()
        {
            var spec = MoveSpec(1, new[] { "item-1" }, "source", "destination", "top", 1);
            Assert.That(ItemMoveInputNormalization.TryNormalize(spec, out var reason), Is.True, reason);
            return spec;
        }
        private static bool Valid(EffectSpec spec, out string reason) => ItemMoveInputNormalization.TryValidate(spec.EffectTypeId, spec.NormalizedArguments, spec.ItemMoveInput, out reason);
        private static EffectSpec Copy(EffectSpec spec) => new EffectSpec(spec.EffectTypeId, spec.NormalizedArguments.Clone()) { ItemMoveInput = GameStateCloneService.DeepClone(spec.ItemMoveInput) };

        [Test]
        public void EqualIndependentInputsReuseButEveryInputMutationIsRevalidated()
        {
            int hits = 0;
            using var timing = GameStateCloneService.BeginTimingScope((phase, ticks) => { if (phase == "item_move_input.reuse") hits++; });
            using var scope = Scope();
            var source = Spec();
            Assert.That(Valid(source, out var reason), Is.True, reason);
            var clone = Copy(source);
            Assert.That(Valid(clone, out reason), Is.True, reason);
            Assert.That(hits, Is.GreaterThan(0));
            foreach (string mutation in new[] { "protocol", "original", "canonical", "unusedOriginal", "unusedCanonical", "actual" })
            {
                clone = Copy(source);
                if (mutation == "protocol") clone.ItemMoveInput.ProtocolVersion = "changed";
                else if (mutation == "original") clone.ItemMoveInput.OriginalArgumentsDigest = "forged";
                else if (mutation == "canonical") clone.ItemMoveInput.CanonicalArgumentsDigest = "forged";
                else if (mutation == "unusedOriginal") clone.ItemMoveInput.OriginalArguments.BooleanValue = true;
                else if (mutation == "unusedCanonical") clone.NormalizedArguments.StringValue = "闲置字段也参与原摘要";
                else Value(clone.NormalizedArguments, "player").StringValue = "2";
                Assert.That(Valid(clone, out reason), Is.False, mutation);
                Assert.That(reason, Is.EqualTo(ItemMoveInputNormalization.InvalidProvenance));
                Assert.That(Valid(source, out reason), Is.True, "损坏输入不得污染独立副本");
            }
        }

        [Test]
        public void ScopeExitAndCustomSerializerSettingsDoNotReusePriorValidation()
        {
            var source = Spec(); int hits = 0;
            using var timing = GameStateCloneService.BeginTimingScope((phase, ticks) => { if (phase == "item_move_input.reuse") hits++; });
            using (Scope()) { Assert.That(Valid(source, out _), Is.True); Assert.That(Valid(source, out _), Is.True); }
            int before = hits;
            Assert.That(Valid(source, out _), Is.True);
            Assert.That(hits, Is.EqualTo(before));
            var prior = JsonConvert.DefaultSettings;
            try
            {
                using var scope = Scope();
                Assert.That(Valid(source, out _), Is.True);
                JsonConvert.DefaultSettings = () => new JsonSerializerSettings { Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() } };
                Assert.That(Valid(source, out _), Is.False, "全局序列化设置变化应按原摘要重新校验");
                Assert.That(hits, Is.EqualTo(before));
            }
            finally { JsonConvert.DefaultSettings = prior; }
        }
    }
}
