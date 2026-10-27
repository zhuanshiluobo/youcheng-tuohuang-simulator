using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 真实Lua隔离规则定位。指定结果只用于分支构树，不计正常骰子或正常界面验收。
    public sealed class TiagoInvestmentLuaTests
    {
        private TiagoInvestmentTestFixture f;
        [SetUp] public void Begin() => f = new TiagoInvestmentTestFixture();
        [TearDown] public void End() => f.Dispose();
        private static string[] Options(LuaEffectSpec e) => Value(e.RawArguments, "options").Items.Select(v => v.StringValue).ToArray();
        [TestCase("D01", "D03", "D05", 6, 1, 2, 3)]
        [TestCase("D01", "D03", "D06", 10, 0, 3, 4)]
        [TestCase("D01", "D04", "D05", 2, 2, 2, 2)]
        [TestCase("D01", "D04", "D06", 6, 1, 3, 3)]
        [TestCase("D02", "D03", "D05", 8, 1, 2, 3)]
        [TestCase("D02", "D03", "D06", 12, 0, 3, 4)]
        [TestCase("D02", "D04", "D05", 4, 2, 2, 2)]
        [TestCase("D02", "D04", "D06", 8, 1, 3, 3)]
        public void AllOfficialCombinationsUseOneCompleteInvestmentPayment(string one, string three, string five, int gold, int total, int dice, int reputation)
        {
            var faces = new[] { one, three, five };
            foreach (string entry in new[] { TiagoInvestmentTestFixture.Ability, "entrepreneur.D07.quick", "entrepreneur.D08.quick" })
            {
                var invocation = f.Invoke(entry, faces); var investment = TiagoInvestmentTestFixture.Investment(invocation, entry != TiagoInvestmentTestFixture.Ability);
                Assert.That(investment.NestedEffectGroups[0].Count, Is.EqualTo(1));
                var args = investment.NestedEffectGroups[0].Single().RawArguments;
                Assert.That(Number(Value(args, "cost"), "GoldVoucher"), Is.EqualTo(gold));
                Assert.That(Number(args, "amount"), Is.EqualTo(total));
                Assert.That(Text(args, "resourceType"), Is.EqualTo(total > 0 ? "total" : ""));
                var roll = investment.NestedEffectGroups[1].Single(); var p = Value(roll.ContinuationContext, "parameters");
                Assert.That(Number(p, "dice"), Is.EqualTo(dice)); Assert.That(Number(p, "reputation"), Is.EqualTo(reputation));
                Assert.That(Number(roll.RawArguments, "count"), Is.EqualTo(1)); Assert.That(Number(roll.RawArguments, "sides"), Is.EqualTo(6));
                if (entry != TiagoInvestmentTestFixture.Ability)
                {
                    TiagoInvestmentTestFixture.AssertTokenCost(invocation.Effects.Single().NestedEffectGroups[0].Single(),
                        entry.Contains("D07") ? 3 : 2, entry.Contains("D07") ? "retired" : "original_supply");
                }
                f.Artifacts.WriteLabeledText("真实Lua完整投资需求-" + entry, JsonConvert.SerializeObject(invocation.Effects));
            }
        }
        [Test] public void RoundThreeUsesTheActualD03AndD04FacesWithoutFifthRoundModifiers()
        {
            var a = TiagoInvestmentTestFixture.Investment(f.Invoke(faces: new[] { "D02", "D03" }));
            var b = TiagoInvestmentTestFixture.Investment(f.Invoke(faces: new[] { "D02", "D04" }));
            Assert.That(Number(Value(a.NestedEffectGroups[0][0].RawArguments, "cost"), "GoldVoucher"), Is.EqualTo(8));
            Assert.That(Number(Value(b.NestedEffectGroups[0][0].RawArguments, "cost"), "GoldVoucher"), Is.EqualTo(4));
            Assert.That(Number(b.NestedEffectGroups[0][0].RawArguments, "amount"), Is.EqualTo(1));
        }
        [Test] public void PhysicalDiceAndAdjustedPointsRemainSeparateAtZeroAndSeven()
        {
            var r = f.Rolled(new[] { "D04" }, new[] { 1, 6 }); Assert.That(r.IsSuccess, Is.True, r.Diagnostic);
            Assert.That(Options(r.Effects.Single()), Is.EqualTo(new[] { "0", "1", "2", "5", "6", "7" }));
            var c = r.Effects.Single().ContinuationContext;
            Assert.That(Value(c, "dice").Items.Select(v => v.IntegerValue), Is.EqualTo(new long[] { 1, 6 }));
            Assert.That(Number(Value(c, "origins"), "0"), Is.EqualTo(1)); Assert.That(Number(Value(c, "origins"), "7"), Is.EqualTo(2));
        }
        [Test] public void SamePhysicalValuesDeduplicateWithoutLosingTheirIdentityMask()
        {
            var r = f.Rolled(Array.Empty<string>(), new[] { 2, 2 });
            Assert.That(Options(r.Effects.Single()), Is.EqualTo(new[] { "2" }));
            Assert.That(Number(Value(r.Effects.Single().ContinuationContext, "origins"), "2"), Is.EqualTo(3));
        }
        [Test] public void NormalSameDiceDoNotCreateOrChargeTheExtraCondition()
        {
            var faces = new[] { "D03" }; var main = f.Rolled(faces, new[] { 2, 2 }).Effects.Single();
            var selected = f.Invoke(faces: faces, continuation: main.ContinuationContext, result: NormalizedValue.CreateString("2"));
            Assert.That(selected.IsSuccess, Is.True, selected.Diagnostic); Assert.That(selected.Effects.Count, Is.EqualTo(1));
            Assert.That(selected.Effects[0].Amount, Is.EqualTo(15));
        }
        [TestCase(false)][TestCase(true)] public void ExtraIsIndependentAndRetainsTwoDiceDirectVersusThreeDiceChoice(bool triple)
        {
            var faces = triple ? new[] { "D03", "D06" } : new[] { "D03" };
            var main = f.Rolled(faces, triple ? new[] { 2, 4, 4 } : new[] { 2, 5 }).Effects.Single();
            var r = f.Invoke(faces: faces, continuation: main.ContinuationContext, result: NormalizedValue.CreateString("2"));
            Assert.That(r.IsSuccess, Is.True, r.Diagnostic); Assert.That(r.Effects.Count, Is.EqualTo(2));
            var extra = r.Effects[1]; TiagoInvestmentTestFixture.AssertTokenCost(extra.NestedEffectGroups[0][0], 2, "original_supply");
            if (triple) Assert.That(Options(extra.NestedEffectGroups[1].Single()), Is.EqualTo(new[] { "4" }));
            else Assert.That(extra.NestedEffectGroups[1].Count, Is.EqualTo(3));
        }
        [TestCase(false)][TestCase(true)] public void SevenADrawsEveryDieButBothChoicesAllowTheSamePoint(bool triple)
        {
            var faces = triple ? new[] { "D03", "D06" } : new[] { "D03" }; const string entry = "entrepreneur.D07.quick";
            var main = f.Rolled(faces, triple ? new[] { 2, 2, 2 } : new[] { 2, 2 }, entry).Effects.Single();
            Assert.That(Options(main), Is.EqualTo(Enumerable.Range(0, 8).Select(n => n.ToString())));
            var r = f.Invoke(entry, faces, main.ContinuationContext, NormalizedValue.CreateString("2"));
            var extraChoice = r.Effects[1].NestedEffectGroups[1].Single(); Assert.That(Options(extraChoice), Does.Contain("2")); Assert.That(Options(extraChoice).Length, Is.EqualTo(8));
            var extra = f.Invoke(entry, faces, extraChoice.ContinuationContext, NormalizedValue.CreateString("2"));
            Assert.That(extra.IsSuccess, Is.True, extra.Diagnostic); Assert.That(extra.Effects.Single().Amount, Is.EqualTo(15));
        }
        [TestCase(0)][TestCase(1)][TestCase(2)][TestCase(3)][TestCase(4)][TestCase(5)][TestCase(6)][TestCase(7)]
        public void EveryPointProducesItsRealOrdinaryEffect(int n)
        {
            const string entry = "entrepreneur.D07.quick"; var main = f.Rolled(Array.Empty<string>(), new[] { 1, 1 }, entry).Effects.Single();
            var r = f.Invoke(entry, Array.Empty<string>(), main.ContinuationContext, NormalizedValue.CreateString(n.ToString()));
            Assert.That(r.IsSuccess, Is.True, r.Diagnostic); Assert.That(r.Effects, Is.Not.Empty);
            if (n == 3 || n == 7) Assert.That(Number(r.Effects.Single().RawArguments, "levels"), Is.EqualTo(n == 3 ? 1 : 2));
            if (n == 5) Assert.That(r.Effects.Select(e => e.Amount), Is.EqualTo(new[] { 2, 2, 1 }));
            if (n == 6) Assert.That(r.Effects.Count, Is.EqualTo(2));
            if (n == 4) { Assert.That(Text(r.Effects[0].RawArguments, "saleMode"), Is.EqualTo("batch")); Assert.That(Text(r.Effects[0].RawArguments, "pricePolicy"), Is.EqualTo("core.resource_sale")); }
            f.Artifacts.WriteLabeledText("点数" + n + "实际Lua树", JsonConvert.SerializeObject(r.Effects));
        }
        [Test] public void BatchSaleFinishesBeforeTheExtraConditionAndDoesNotIssueDuplicateRevenue()
        {
            var faces = new[] { "D03" }; var main = f.Rolled(faces, new[] { 4, 5 }).Effects.Single();
            var r = f.Invoke(faces: faces, continuation: main.ContinuationContext, result: NormalizedValue.CreateString("4"));
            for (int i = 0; i < 2; i++)
            {
                Assert.That(r.Effects.Count, Is.EqualTo(1)); var sale = r.Effects[0];
                r = f.Invoke(faces: faces, continuation: sale.ContinuationContext, result: Object("revenue", NormalizedValue.CreateInteger(3), "soldCount", NormalizedValue.CreateInteger(1), "finished", NormalizedValue.CreateBoolean(false)));
                Assert.That(r.IsSuccess, Is.True, r.Diagnostic);
            }
            r = f.Invoke(faces: faces, continuation: r.Effects[0].ContinuationContext, result: Object("finished", NormalizedValue.CreateBoolean(true)));
            Assert.That(r.IsSuccess, Is.True, r.Diagnostic); TiagoInvestmentTestFixture.AssertTokenCost(r.Effects.Single().NestedEffectGroups[0].Single(), 2, "original_supply");
        }
        [TestCase("D01")][TestCase("D02")]
        public void FormalExchangeLuaConsumesAllPagesAndOnlyTheSelectedOfficialFiveADiscounts(string face)
        {
            var all = TiagoInvestmentTestFixture.IsolatedExchangeSources(true, face);
            Assert.That(all.Count, Is.GreaterThan(SourceFactsLuaCatalog.PageSize));
            string before = all.ToString();
            var discounted = f.ExchangeAvailability(all);
            Assert.That(discounted.IsSuccess, Is.True, discounted.Diagnostic);
            Assert.That(discounted.SettlementAvailability.Enabled, Is.True);
            Assert.That(all.ToString(), Is.EqualTo(before), "纯资格不能改来源或库存");
            var ordinary = f.ExchangeAvailability(TiagoInvestmentTestFixture.IsolatedExchangeSources(false, face));
            Assert.That(ordinary.IsSuccess, Is.True, ordinary.Diagnostic); Assert.That(ordinary.SettlementAvailability.Enabled, Is.False);
            var enough = f.ExchangeAvailability(TiagoInvestmentTestFixture.IsolatedExchangeSources(false, face), markers: 3);
            Assert.That(enough.IsSuccess, Is.True, enough.Diagnostic); Assert.That(enough.SettlementAvailability.Enabled, Is.True);
        }
        [TestCase("wrong_definition")][TestCase("wrong_instance")][TestCase("unselected")]
        [TestCase("wrong_face")][TestCase("wrong_player")][TestCase("wrong_ability")]
        [TestCase("receipt_mismatch")][TestCase("exited")][TestCase("carrier_exited")]
        public void DiySameNamedOrNoLongerEffectiveFiveACannotLowerTheOfficialCost(string failure)
        {
            var all = TiagoInvestmentTestFixture.IsolatedExchangeSources(true); var selected = (JObject)all.Last;
            switch (failure)
            {
                case "wrong_definition": selected["definitionId"] = "diy.round.D.5"; break;
                case "wrong_instance": selected["abilityOwnerItemId"] = "another.carrier"; break;
                case "unselected": selected["selectionReceipt"] = null; break;
                case "wrong_face": selected["activeFaceId"] = "D06"; break;
                case "wrong_player": selected["authorizedPlayerId"] = "p2"; break;
                case "wrong_ability": selected["abilityId"] = "diy.same_name.D05"; break;
                case "receipt_mismatch": selected["selectionReceipt"]["faceId"] = "D06"; break;
                case "exited": all.Remove(selected); break;
                case "carrier_exited": all.RemoveAt(1); break;
                default: Assert.Fail(failure); break;
            }
            var result = f.ExchangeAvailability(all);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.SettlementAvailability.Enabled, Is.False);
        }
        [TestCase("wrong_definition")][TestCase("wrong_receipt")][TestCase("wrong_owner_instance")]
        [TestCase("no_receipt")][TestCase("wrong_player")]
        public void AnUnprovenExchangeOwnerCannotUseEvenThreeMarkers(string failure)
        {
            var all = TiagoInvestmentTestFixture.IsolatedExchangeSources(false); var owner = (JObject)all[0];
            if (failure == "wrong_definition") owner["definitionId"] = "diy.round.D.1";
            else if (failure == "wrong_receipt") owner["selectionReceipt"]["faceId"] = "D02";
            else if (failure == "wrong_owner_instance") owner["itemId"] = "another.card";
            else if (failure == "no_receipt") owner["selectionReceipt"] = null;
            else owner["authorizedPlayerId"] = "p2";
            var result = f.ExchangeAvailability(all, markers: 3);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.SettlementAvailability.Enabled, Is.False);
        }
        [Test] public void ASecondAliasForFiveADoesNotApplyTheDiscountTwiceOrSpendUnobtainedReputation()
        {
            var all = TiagoInvestmentTestFixture.IsolatedExchangeSources(true); var alias = (JObject)all.Last.DeepClone();
            alias["sourceKey"] = "second.alias"; all.Add(alias);
            var insufficient = f.ExchangeAvailability(all, markers: 1);
            Assert.That(insufficient.IsSuccess, Is.True, insufficient.Diagnostic); Assert.That(insufficient.SettlementAvailability.Enabled, Is.False);
            var enough = f.ExchangeAvailability(all, markers: 2);
            Assert.That(enough.IsSuccess, Is.True, enough.Diagnostic); Assert.That(enough.SettlementAvailability.Enabled, Is.True);
        }
        [TestCase("wrong_definition")][TestCase("wrong_instance")][TestCase("wrong_player")][TestCase("exited")][TestCase("unselected")]
        public void InvestmentIgnoresAnUnofficialOrInactiveCardInsteadOfTrustingItsFaceLabel(string failure)
        {
            var result = f.Invoke(faces: new[] { "D05" }, alterIsolatedContent: content => {
                var card = content["instances"].OfType<JObject>().Single(i => (string)i["id"] == "card.5");
                if (failure == "wrong_definition") card["definitionId"] = "diy.round.D.5";
                else if (failure == "wrong_instance") card["entrepreneurAbilityItemId"] = "another.carrier";
                else if (failure == "wrong_player") card["controller"] = "p2";
                else if (failure == "exited") card["zoneId"] = "pending";
                else content["instances"].OfType<JObject>().Single(i => (string)i["id"] == "carrier")["roundChoices"][0]["selectedCandidateId"] = "";
            });
            var pay = TiagoInvestmentTestFixture.Investment(result).NestedEffectGroups[0].Single().RawArguments;
            Assert.That(Number(Value(pay, "cost"), "GoldVoucher"), Is.EqualTo(4));
            Assert.That(Number(pay, "amount"), Is.Zero);
        }
        [TestCase("carrier")][TestCase("owner")][TestCase("operation")][TestCase("type")][TestCase("shortage")]
        public void PureExchangeAvailabilityCannotBorrowUnrelatedTokensOrOldAggregateCounts(string mismatch)
        {
            var sources = TiagoInvestmentTestFixture.IsolatedExchangeSources(true);
            var tokens = TiagoInvestmentTestFixture.IsolatedTokens(mismatch == "shortage" ? 1 : 8);
            foreach (JObject token in tokens)
            {
                if (mismatch == "carrier") token["carrierItemId"] = "other.carrier";
                else if (mismatch == "owner") token["ownerSubject"] = "p2";
                else if (mismatch == "operation") token["operations"] = new JArray("transfer.retired");
                else if (mismatch == "type") token["typeId"] = "entrepreneur.gang_marker";
            }
            var result = f.ExchangeAvailability(sources, markers: 99, tokens: tokens);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.SettlementAvailability.Enabled, Is.False);
        }
        [Test] public void ImmediateLoanDoesNotTreatLosingScoreAsAPaymentCondition()
        {
            var r = f.Invoke("entrepreneur.D02.immediate"); Assert.That(r.IsSuccess, Is.True, r.Diagnostic);
            Assert.That(r.Effects.Count, Is.EqualTo(2)); Assert.That(r.Effects[0].EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.LoseScore)); Assert.That(r.Effects[1].Amount, Is.EqualTo(42));
        }
    }
}
