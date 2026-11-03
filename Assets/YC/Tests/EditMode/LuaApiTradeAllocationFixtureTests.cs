using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Commands;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    public sealed class LuaApiTradeAllocationFixtureTests
    {
        [TestCase(0, 0, 0)]
        [TestCase(129, 0, 0)]
        [TestCase(130, 0, 10)]
        [TestCase(26, 8, 10)]
        [TestCase(0, 10, 10)]
        public void CountPlanDoesNotSpendUnpayableGoldOrProjectedSaleIncome(int gold, int stock, int expectedCount)
        {
            var request = Request(10, 13);
            var wallet = new ResourceSet { GoldVoucher = gold, Originium = stock };
            var original = JsonConvert.SerializeObject(new { request, wallet });
            var sequence = Plan(request, wallet);
            Assert.That(sequence.Length, Is.EqualTo(expectedCount));
            Assert.That(sequence.Count(id => id == "buy_pure_originium") * 13, Is.LessThanOrEqualTo(gold));
            Assert.That(sequence.Count(id => id == "sell_originium"), Is.LessThanOrEqualTo(stock));
            Assert.That(JsonConvert.SerializeObject(new { request, wallet }), Is.EqualTo(original));
        }

        [Test]
        public void RealRowOrderWithNoGoldChoosesKnownStockInsteadOfTenUnaffordablePurchases()
        {
            var request = Request(10, 13);
            request.CandidateIds.Reverse(); request.Presentation.QuantityRows.Reverse();
            Assert.That(Plan(request, new ResourceSet { Originium = 17 }), Is.EqualTo(Enumerable.Repeat("sell_originium", 10)));
        }

        [TestCase(98, 0)]
        [TestCase(99, 99)]
        public void FixturePlannerHonorsFullDeclaredBudgetWithoutChangingGameCandidateLimits(int gold, int expected)
        {
            var request = Request(99, 1);
            Assert.That(Plan(request, new ResourceSet { GoldVoucher = gold }).Length, Is.EqualTo(expected));
        }

        [Test]
        public void MissingFormalPriceDoesNotProduceBlindCountPlan()
        {
            var request = Request(10, 13); request.Presentation.QuantityRows[0].Label.Arguments.Clear();
            var args = new object[] { request, new ResourceSet { GoldVoucher = 130 }, null };
            Assert.That((bool)Planner.Invoke(null, args), Is.False);
        }

        [TestCase("confirm")]
        [TestCase("end_action")]
        public void ActualFormalAnswerEncodingMatchesPlannedSequenceAndSubmissionKind(string submitKind)
        {
            var sequence = submitKind == "confirm" ? Enumerable.Repeat("sell_originium", 10).ToArray() : Array.Empty<string>();
            var formal = Object("sequence", Strings(sequence), "submitKind", NormalizedValue.CreateString(submitKind),
                "candidateSetId", NormalizedValue.CreateString("observed-trade"), "candidateSetVersion", NormalizedValue.CreateInteger(1));
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction };
            command.Parameters[AllocationSequenceAnswerJson.Parameter] = AllocationSequenceAnswerJson.Encode(formal);
            var method = Planner.DeclaringType.GetMethod("TradeAllocationAnswerMatchesPlan", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That((bool)method.Invoke(null, new object[] { command, sequence, submitKind == "end_action" }), Is.True);
            Assert.That((bool)method.Invoke(null, new object[] { command, sequence, submitKind != "end_action" }), Is.False);
        }

        private static MethodInfo Planner => Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true)
            .GetMethod("TryPlanTradeAllocation", BindingFlags.Static | BindingFlags.NonPublic);
        private static string[] Plan(InteractionRequestProjection request, ResourceSet wallet)
        {
            var args = new object[] { request, wallet, null };
            Assert.That((bool)Planner.Invoke(null, args), Is.True);
            return (string[])args[2];
        }
        private static InteractionRequestProjection Request(int total, int cost) => new InteractionRequestProjection
        {
            InteractionTypeId = AllocationEffects.InteractionType, AnswerSchema = AllocationEffects.AnswerSchema,
            CandidateIds = new List<string> { "buy_pure_originium", "sell_originium" },
            PromptParameters = Object("totalExecutions", NormalizedValue.CreateInteger(total), "allowEndAction", NormalizedValue.CreateBoolean(true),
                "rowMaximums", Object("buy_pure_originium", NormalizedValue.CreateInteger(total), "sell_originium", NormalizedValue.CreateInteger(total))),
            Presentation = new GameplayInteractionPresentation { QuantityRows = new List<GameplayQuantityRowPresentation> {
                new GameplayQuantityRowPresentation { RowId = "buy_pure_originium", Label = new GameplayTextRef("core:interaction.trade.buy.pure_originium",
                    new GameplayTextArgument { Name = "cost", Kind = "integer", Value = NormalizedValue.CreateInteger(cost) }) },
                new GameplayQuantityRowPresentation { RowId = "sell_originium", Label = new GameplayTextRef("core:interaction.trade.sell.originium",
                    new GameplayTextArgument { Name = "price", Kind = "integer", Value = NormalizedValue.CreateInteger(3) }) } } }
        };
    }
}
