using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class CardMarkerContractTests
    {



        [TestCase("token", "100")] [TestCase("token", "-1")] [TestCase("token", "0")]
        [TestCase("token", "1.5")] [TestCase("token", "4294967298")]
        public void B2V3CompilerRejectsOutOfRangeFractionalAndOverflowCountsWithoutMutatingState(string operation, string count)
        {
            State(supply: 29, initialGold: 100);
            string before = JsonConvert.SerializeObject(state);
            string expression = TokenTransferExpression(count);
            Assert.That(B2CompileMarkerLua(expression, out _, out string diagnostic), Is.False, diagnostic);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Assert.That(Stored, Is.Empty); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100));
        }
        [Test]
        public void ExistingGlobalValueAndTransferBudgetsRemainUnchanged()
        {
            State(supply: 29);
            Assert.That(Stored, Is.Empty); Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(NormalizedValue.MaxElements, Is.EqualTo(128));
            // 总元素预算含根数组：127 个叶值 + 1 个数组根是 128。
            var atLimit = NormalizedValue.CreateArray(Enumerable.Range(0, 127).Select(_ => NormalizedValue.CreateNull()).ToList());
            Assert.That(atLimit.TryValidate(out string reason), Is.True, reason);
            atLimit.Items.Add(NormalizedValue.CreateNull()); Assert.That(atLimit.TryValidate(out _), Is.False);
            Assert.That(GameStateViewTransfer.MaximumBytes, Is.EqualTo(8 * 1024 * 1024));
            Assert.That(GameStateViewTransfer.Encode(new string('x', 8 * 1024 * 1024)).Length, Is.EqualTo(8 * 1024 * 1024));
            Assert.Throws<ArgumentException>(() => GameStateViewTransfer.Encode(new string('x', 8 * 1024 * 1024 + 1)));
        }
    }
}
