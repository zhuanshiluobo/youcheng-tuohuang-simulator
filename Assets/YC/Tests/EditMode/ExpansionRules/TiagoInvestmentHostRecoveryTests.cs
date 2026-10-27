using System;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 真正PlayerCleanupStarted、来源、付款及Host恢复；隔离基线不声称正常开局或多局覆盖。
    public sealed class TiagoInvestmentHostRecoveryTests
    {
        private TiagoInvestmentTestFixture f;
        [SetUp] public void Begin() => f = new TiagoInvestmentTestFixture();
        [TearDown] public void End() => f.Dispose();
        private static string Dice(GameState state) => JsonConvert.SerializeObject(state.EffectRuntime.EffectNodes
            .Where(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice).Select(n => new { n.EffectId, n.NormalizedResult }).ToList());
        [Test] public void OnceCleanupUsesTheFormalSourceAndOnePaymentThenRestoresWithoutRerolling()
        {
            f.CreateHost(); f.Restore("正式D09收尾来源待受理"); f.StartInvestment(); f.Restore("投资完整费用待确认"); f.ToPointChoice();
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(46));
            var dice = f.State.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice).ToList();
            Assert.That(dice.Count, Is.EqualTo(2)); Assert.That(dice.All(n => Number(n.NormalizedResult,"diceResult") >= 1 && Number(n.NormalizedResult,"diceResult") <= 6 && Number(n.ContinuationData,"diceResult") == Number(n.NormalizedResult,"diceResult")), Is.True);
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.Payment?.IsCommitted == true), Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.AbilitySourceUses.Count(s => s.AbilityId == TiagoInvestmentTestFixture.Ability && s.PlayerId == 1), Is.EqualTo(1));
            string recorded = Dice(f.State), random = JsonConvert.SerializeObject(f.State.RandomRuntime); var options = f.Open.CandidateIds.ToArray();
            f.Restore("已付款两骰已保存及声望拒绝后"); f.Drain();
            Assert.That(Dice(f.State), Is.EqualTo(recorded)); Assert.That(JsonConvert.SerializeObject(f.State.RandomRuntime), Is.EqualTo(random));
            Assert.That(f.Open.CandidateIds, Is.EqualTo(options)); Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(46));
        }
        [Test] public void InvestmentCanRefuseAllReputationWithoutBlockingThePointChoice()
        {
            f.CreateHost(markerSupply: 0); f.StartInvestment(); f.ToPointChoice();
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice), Is.EqualTo(2));
            var storage = f.State.ItemZones.Zones.Single(z => z.ParentId == f.Carrier && z.LocalZoneId == "reputation");
            Assert.That(f.State.ItemZones.Items.Count(i => i.ZoneId == storage.ZoneId), Is.Zero);
            Assert.That(f.State.EffectRuntime.InteractionRequests.Where(q => q.Status == "open").All(q =>
                Text(f.Engine.GetNode(q.OwnerEffectId).ContinuationContext, "stage") != "reputation_selected"), Is.True);
            f.Restore("零供应不弹空数量且点数待答");
        }
        [Test] public void DecliningTheInvestmentDoesNotSpendMoneyRollDiceOrGainReputation()
        {
            f.CreateHost(); f.StartInvestment(); var q = f.Open;
            Assert.That(q.InteractionTypeId, Is.EqualTo("action.decline_effect"));
            Assert.That(q.Presentation.Prompt.Key, Is.EqualTo("core:entrepreneur.D.investment.condition"));
            Assert.That(q.Presentation.Progress.Key, Is.EqualTo("core:resource.payment.condition.prompt"), "total=0保持固定付款提示");
            string display = JsonConvert.SerializeObject(q.Presentation);
            f.Restore("R14 total为0原固定条件正文"); q = f.Open;
            Assert.That(JsonConvert.SerializeObject(q.Presentation), Is.EqualTo(display));
            Assert.That(f.Engine.TrySubmitInteraction(q.GetStableInteractionId(), 1, q.StateRevision, NormalizedValue.CreateBoolean(false), out string reason), Is.True, reason); f.Drain();
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(50));
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice), Is.False);
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.Payment?.IsCommitted == true), Is.False);
            f.Restore("放弃投资终态不补付或重开");
        }
        [Test] public void SelectedInvestmentTotalKeepsFourParametersThroughDraftAndRecovery()
        {
            f.CreateSelectedHost(new[] { "D04" }, startingResources: new ResourceSet {
                GoldVoucher = 50, Originium = 10, OriginiumShard = 10, Iron = 10 });
            f.OpenSelectedWindow(cleanup:true); var before = f.State.FindPlayer(1).Resources.Clone(); f.StartInvestment();
            var q = f.Open; var owner = f.Engine.GetNode(q.OwnerEffectId);
            Assert.That(q.InteractionTypeId, Is.EqualTo(ResourcePaymentChoiceEffectExecutor.InteractionTypeId));
            Assert.That(owner.Payment.Demand.TotalCount, Is.EqualTo(1));
            Assert.That(q.Presentation.Prompt.Key, Is.EqualTo("core:entrepreneur.D.investment.condition"));
            Assert.That(q.Presentation.Prompt.Arguments.Select(a => a.Name), Is.EquivalentTo(new[] { "gold", "total", "dice", "reputation" }));
            Assert.That(q.Presentation.Prompt.Arguments.Single(a => a.Name == "total").Value.IntegerValue, Is.EqualTo(1));
            Assert.That(q.Presentation.Prompt.Arguments.Single(a => a.Name == "gold").Value.IntegerValue, Is.EqualTo(owner.Payment.Demand.FixedCost.GoldVoucher));
            Assert.That(q.Presentation.Progress.Key, Is.EqualTo("core:resource.payment.choice.prompt"));
            string display = JsonConvert.SerializeObject(q.Presentation), id = q.GetStableInteractionId();
            Assert.That(f.Engine.TryPrepareTotalPaymentDraft(1, owner.EffectId, id, owner.Payment.PaymentGeneration,
                new ResourceSet { Iron = 1 }, f.State.EffectRuntime.StateRevision, "R14正式投资合计改配比", out var why), Is.True, why);
            Assert.That(JsonConvert.SerializeObject(f.Open.Presentation), Is.EqualTo(display));
            f.Restore("R14正式投资合计正文与草稿恢复"); q = f.Open;
            Assert.That(q.GetStableInteractionId(), Is.EqualTo(id)); Assert.That(JsonConvert.SerializeObject(q.Presentation), Is.EqualTo(display));
            Assert.That(f.Engine.GetNode(q.OwnerEffectId).Payment.SelectedAllocation.Iron, Is.EqualTo(1));
            Assert.That(f.Engine.TrySubmitInteraction(id, 1, q.StateRevision, NormalizedValue.CreateBoolean(false), out why), Is.True, why); f.Drain();
            Assert.That(PaymentResources.Same(f.State.FindPlayer(1).Resources, before), Is.True);
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice), Is.False);
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.Payment?.IsCommitted == true), Is.False);
        }
        [Test] public void WrongAnswererCannotConsumeARealPointRequestOrChangeSavedRandomState()
        {
            f.CreateHost(); f.StartInvestment(); f.ToPointChoice(); var q = f.Open;
            string before = JsonConvert.SerializeObject(f.State.RandomRuntime); int gold = f.State.FindPlayer(1).Resources.GoldVoucher;
            Assert.That(f.Engine.TrySubmitInteraction(q.GetStableInteractionId(), 2, q.StateRevision, NormalizedValue.CreateString(q.CandidateIds[0]), out _), Is.False);
            Assert.That(q.Status, Is.EqualTo("open")); Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            Assert.That(JsonConvert.SerializeObject(f.State.RandomRuntime), Is.EqualTo(before));
        }
        [Test] public void GenuineDiceResultsAndTheirComponentReferencesSurviveEveryCompletedRollNode()
        {
            f.CreateHost(); f.StartInvestment(); f.ToPointChoice();
            var dice = f.State.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice).ToList();
            foreach (var node in dice)
            {
                int result = Number(node.NormalizedResult, "diceResult"); Assert.That(result, Is.InRange(1, 6));
                Assert.That(Value(node.ContinuationContext, "dieIds").Items.Count, Is.EqualTo(3));
            }
            string original = Dice(f.State); f.Restore("真实逐骰收据及组件身份"); f.Drain(); Assert.That(Dice(f.State), Is.EqualTo(original));
        }
        [TestCase("D01")][TestCase("D02")]
        public void OfficialSelectionReceiptsEnableFiveAExchangeAndSurviveRealHostRecovery(string firstFace)
        {
            f.CreateSelectedHost(new[] { firstFace, "D05" }, initialReputation: 2); f.OpenSelectedWindow(cleanup: true);
            var owner = f.Registry.QuerySources(f.State, 1).Single(s => s.AbilityId == "entrepreneur." + firstFace + ".exchange");
            var facts = SourceFactsLuaCatalog.Create(f.State, f.Registry, owner);
            Assert.That(facts.Get("sources").Items.Count, Is.GreaterThan(SourceFactsLuaCatalog.PageSize));
            var five = facts.Get("sources").Items.Single(s => s.Get("abilityId").StringValue == "entrepreneur.face.D05");
            Assert.That(five.Get("abilityOwnerItemId").StringValue, Is.EqualTo(f.Carrier));
            Assert.That(five.Get("definitionId").StringValue, Is.EqualTo("entrepreneur.round.D.5"));
            Assert.That(five.Get("selectionReceipt").Get("faceId").StringValue, Is.EqualTo("D05"));
            string before = RuleStateDigestService.Compute(f.State);
            Assert.That(f.ExchangeOption.IsEnabled, Is.True);
            Assert.That(RuleStateDigestService.Compute(f.State), Is.EqualTo(before));
            f.Restore("官方同实例已选5A纯资格待受理"); Assert.That(f.ExchangeOption.IsEnabled, Is.True);
            int score = f.State.FindPlayer(1).Score; f.StartExchange("r3.discount"); f.Restore("5A兑换费用确认前"); f.Confirm(true);
            Assert.That(f.Reputation, Is.Zero); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score + 1));
            f.Restore("5A兑换完成不得重扣或重加分"); Assert.That(f.Reputation, Is.Zero);
        }
        [Test] public void RepeatedCleanupHasDistinctInvocationsReplaysOnlyItsOwnCommandAndCanCloseAfterDeclining()
        {
            f.CreateSelectedHost(new[] { "D01", "D05" }, initialReputation: 6); f.OpenSelectedWindow(cleanup: true);
            var first = f.ExchangeOption; int score = f.State.FindPlayer(1).Score;
            Assert.That(f.Engine.TryInvokeCleanupSource(2, "wrong-player", first.CleanupRequest, out _, out _, out _), Is.False);
            string firstId = f.StartExchange("r3.exchange.1"); f.Restore("第一次兑换已受理未付"); f.Confirm(true);
            Assert.That(f.Reputation, Is.EqualTo(4)); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score + 1));
            Assert.That(f.ExchangeOption.OptionId, Is.EqualTo(first.OptionId));
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "r3.exchange.1", first.CleanupRequest, out bool replay, out string same, out string reason), Is.True, reason);
            Assert.That(replay, Is.True); Assert.That(same, Is.EqualTo(firstId)); f.Drain();
            Assert.That(f.Reputation, Is.EqualTo(4));
            string second = f.StartExchange("r3.exchange.2"); Assert.That(second, Is.Not.EqualTo(firstId));
            f.Restore("第二次兑换独立受理"); f.Confirm(true);
            Assert.That(f.Reputation, Is.EqualTo(2)); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score + 2));
            f.Restore("第二次完成回同一稳定入口"); f.StartExchange("r3.exchange.decline"); f.Confirm(false);
            var entry = f.CleanupWindow.Entries.Single(e => e.CleanupSource != null && e.SourceItemId == first.SourceItemId);
            Assert.That(entry.CleanupSource.Invocations.Count, Is.EqualTo(3));
            Assert.That(entry.CleanupSource.Invocations.Select(i => i.InvocationId).Distinct().Count(), Is.EqualTo(3));
            Assert.That(f.Reputation, Is.EqualTo(2)); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score + 2));
            string windowId = f.CleanupWindow.Context.Id;
            Assert.That(f.Engine.TryEndSettlementAction(1, windowId, "", "", f.State.EffectRuntime.StateRevision, "r3.close", out reason), Is.True, reason); f.Drain();
            f.Restore("重复窗口主动关闭及已付历史");
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "r3.after-close", first.CleanupRequest, out _, out _, out _), Is.False);
        }
        [Test] public void AnInsufficientRepeatEntryBecomesAvailableOnlyAfterRealInvestmentLoadsReputation()
        {
            f.CreateSelectedHost(new[] { "D01", "D03", "D06" }); f.OpenSelectedWindow(cleanup: true);
            Assert.That(f.ExchangeOption.IsEnabled, Is.False); int stock = f.Reputation;
            Assert.That(stock, Is.Zero); f.StartInvestment(); f.ToPointChoice(reputation: 4, recoverEachRoll: true);
            Assert.That(f.Reputation, Is.EqualTo(4));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice), Is.EqualTo(3));
            // 正常骰点不为来源资格作改写；此断点先保存，后续点数/额外仍须按实际请求完成。
            f.Restore("真实三骰及取得4声望点数待答");
            var repeated = f.CleanupWindow.Entries.Single(e => e.CleanupSource != null && e.AbilityId == "entrepreneur.D01.exchange");
            f.Artifacts.WriteLabeledText("真实投资后兑换资格原始事实", JsonConvert.SerializeObject(new {
                facts = CleanupSourceLuaCatalog.CreateFacts(f.State, f.CleanupWindow, repeated),
                sourceFacts = SourceFactsLuaCatalog.ForSettlement(f.State, f.Registry, SettlementEntryService.CreateQueryContext(f.State, f.CleanupWindow), repeated),
                sources = f.Registry.QuerySources(f.State, 1), availability = f.Registry.QuerySettlementEntry(f.State, f.CleanupWindow, repeated, true) }));
            var pending = f.Registry.QuerySettlementEntry(f.State, f.CleanupWindow, repeated, true);
            Assert.That(pending.Enabled, Is.False); Assert.That(pending.DisabledReasonKey, Is.EqualTo("settlement_execution_pending"));
            string investment = f.CleanupWindow.ActiveTargetEffectId;
            for (int guard = 0; guard < 40 && f.CleanupWindow.ActiveTargetEffectId == investment; guard++)
            {
                var request = f.State.EffectRuntime.InteractionRequests.Last(q => q.Status == "open" && q.AnsweringPlayerId == 1 &&
                    InfluenceDepartureProtocol.IsWithin(f.State, q.OwnerEffectId, investment));
                var answer = request.CandidateIds.Contains("2") ? NormalizedValue.CreateString("2") :
                    request.CandidateIds.Contains("5") ? NormalizedValue.CreateString("5") : FifthNumberContentTestFixture.AutomaticAnswer(request);
                Assert.That(f.Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, answer, out string reason), Is.True, reason); f.Drain();
            }
            Assert.That(f.CleanupWindow.ActiveTargetEffectId, Is.Empty);
            repeated = f.CleanupWindow.Entries.Single(e => e.CleanupSource != null && e.AbilityId == "entrepreneur.D01.exchange");
            Assert.That(f.Registry.QuerySettlementEntry(f.State, f.CleanupWindow, repeated, true).Enabled, Is.True);
            f.Restore("真实三骰原投资完整结清后兑换重新启用");
        }
        [TestCase("entrepreneur.D07.quick", 3, "retired")][TestCase("entrepreneur.D08.quick", 2, "original_supply")]
        public void FormalQuickPaysOuterThenFullInvestmentAndKeepsGenuineThreeDiceAcrossEveryRoll(string ability, int outer, string destination)
        {
            string face = ability == "entrepreneur.D07.quick" ? "D07" : "D08";
            f.CreateSelectedHost(new[] { "D01", "D03", "D06", face }, initialReputation: 6); f.OpenSelectedWindow(cleanup: false);
            int gold = f.State.FindPlayer(1).Resources.GoldVoucher; int score = f.State.FindPlayer(1).Score;
            var offered = f.Registry.QueryActions(f.State, 1).Single(o => o.ActionId == ability);
            Assert.That(f.Engine.TryInvokeAbilityAction(2, offered.OptionId, offered.RequestRef.InteractionId,
                offered.RequestRef.FactsDigest, offered.VersionRef.ContentPackHash, "r3.wrong-quick-player", Object(), out _, out _), Is.False);
            Assert.That(f.Reputation, Is.EqualTo(6)); Assert.That(f.State.EffectRuntime.AbilityActionInvocations, Is.Empty);
            string action = f.StartQuick(ability, "r3.quick"); f.Restore("快速已受理外层费待确认"); f.Confirm(true);
            Assert.That(f.Reputation, Is.EqualTo(6 - outer));
            var outerPayment = f.State.EffectRuntime.EffectNodes.Single(n => TiagoInvestmentTestFixture.IsTokenTransfer(n) && n.Status == EffectNodeStatus.Completed);
            f.AssertCommittedTokenCost(outerPayment, outer, destination);
            f.Restore("快速外层已付完整投资费用之前"); f.ToPointChoice(reputation: 4, recoverEachRoll: true);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - 10));
            Assert.That(f.Reputation, Is.EqualTo(10 - outer));
            var dice = f.State.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice).ToList();
            Assert.That(dice.Count, Is.EqualTo(3)); Assert.That(dice.All(n => Number(n.NormalizedResult, "diceResult") >= 1 && Number(n.NormalizedResult, "diceResult") <= 6), Is.True);
            string random = JsonConvert.SerializeObject(f.State.RandomRuntime), saved = Dice(f.State);
            f.Restore("快速原随机与主点数请求"); f.Drain();
            Assert.That(Dice(f.State), Is.EqualTo(saved)); Assert.That(JsonConvert.SerializeObject(f.State.RandomRuntime), Is.EqualTo(random));
            Assert.That(f.State.EffectRuntime.AbilityActionInvocations.Single(i => i.OwnerEffectId == action).ActionId, Is.EqualTo(ability));
            Assert.That(f.State.EffectRuntime.AbilitySourceUses.Any(u => u.AbilityId == TiagoInvestmentTestFixture.Ability), Is.False, "快速不占D09收尾once");
            if (face == "D07")
            {
                Assert.That(f.Open.CandidateIds, Is.EqualTo(Enumerable.Range(0, 8).Select(n => n.ToString())));
                f.Answer("2"); f.Restore("7A主效果完成与3A额外费用之间"); f.Confirm(true);
                Assert.That(f.Reputation, Is.EqualTo(8 - outer));
                Assert.That(f.Open.CandidateIds, Does.Contain("2")); f.Answer("2");
                Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - 10 + 30));
                Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score));
                Assert.That(Dice(f.State), Is.EqualTo(saved)); Assert.That(JsonConvert.SerializeObject(f.State.RandomRuntime), Is.EqualTo(random));
                f.Restore("7A主额外同点真实完成");
                Assert.That(f.Registry.QueryActions(f.State, 1).Single(o => o.ActionId == ability).Enabled, Is.False);
            }
        }
        [TestCase("D07", 3)][TestCase("D08", 2)]
        public void DecliningTheInnerInvestmentKeepsTheRealOuterPaymentAndTheSpentQuickTrack(string face, int outer)
        {
            f.CreateSelectedHost(new[] { "D01", "D03", "D06", face }, initialReputation: 6); f.OpenSelectedWindow(cleanup: false);
            int gold = f.State.FindPlayer(1).Resources.GoldVoucher; f.StartQuick("entrepreneur." + face + ".quick", "r3.decline-inner");
            f.Confirm(true); f.Restore("外层费已付内部投资可放弃"); f.Confirm(false);
            Assert.That(f.Reputation, Is.EqualTo(6 - outer)); Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice), Is.False);
            Assert.That(f.Registry.QueryActions(f.State, 1).Single(o => o.ActionId == "entrepreneur." + face + ".quick").Enabled, Is.False);
            f.Restore("内部放弃不退款不复位轨道");
        }
        [Test] public void InsufficientThreeBAndFiveACombinedResourcesDoNotPartiallySpendTheFixedGold()
        {
            f.CreateSelectedHost(new[] { "D02", "D04", "D05" }, startingResources: new ResourceSet { GoldVoucher = 20 });
            f.OpenSelectedWindow(cleanup: true); int gold = f.State.FindPlayer(1).Resources.GoldVoucher;
            f.StartInvestment(); f.Restore("3B加5A整笔费用不足或待决定");
            if (f.State.EffectRuntime.InteractionRequests.Any(q => q.Status == "open" && q.AnsweringPlayerId == 1)) f.Confirm(true);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.Payment?.IsCommitted == true), Is.False);
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice), Is.False);
            Assert.That(f.Reputation, Is.Zero); f.Restore("合计资源不足不部分扣固定券");
        }
        [Test] public void ExitingTheSelectedOfficialFiveAChangesAvailabilityAndRejectsTheStaleCleanupRequest()
        {
            f.CreateSelectedHost(new[] { "D01", "D05" }, initialReputation: 2); f.OpenSelectedWindow(cleanup: true);
            var old = f.ExchangeOption; Assert.That(old.IsEnabled, Is.True);
            var source = f.Registry.QuerySources(f.State, 1).Single(s => s.AbilityId == "entrepreneur.face.D05");
            string retired = f.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.box_retired").ZoneId;
            var face = f.State.ItemZones.Items.Single(i => i.DefinitionId == source.DefinitionId && i.TypeId == "entrepreneur.round_card" && i.ControllerSubject.PlayerId == 1);
            string transfer = f.Engine.CreateRoot(IsolatedEntrepreneurTransfer.ToRetired(f.State,f.Registry,1,face.ItemId)); f.Drain();
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == transfer).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == face.ItemId).ZoneId, Is.EqualTo(retired));
            Assert.That(f.Registry.QuerySources(f.State, 1).Any(s => s.SourceKey == source.SourceKey), Is.False);
            Assert.That(f.ExchangeOption.IsEnabled, Is.False);
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "r3.stale", old.CleanupRequest, out _, out _, out _), Is.False);
            Assert.That(f.Reputation, Is.EqualTo(2)); f.Restore("真实转移退出5A后保历史选面不再减费");
        }
        [TestCase("D01")][TestCase("D02")]
        public void RealTokenExchangeReturnsEachIdentityOnceAcrossAcceptanceCompletionAndRecovery(string face)
        {
            f.CreateSelectedHost(new[] { face, "D05" }, initialReputation: 4); f.OpenSelectedWindow(cleanup: true);
            int score = f.State.FindPlayer(1).Score; string random = JsonConvert.SerializeObject(f.State.RandomRuntime);
            f.StartExchange("token-r2.first"); f.Restore("token费用首次受理未提交"); f.Confirm(true);
            var first = f.State.EffectRuntime.EffectNodes.Single(n => TiagoInvestmentTestFixture.IsTokenTransfer(n) && n.Status == EffectNodeStatus.Completed);
            f.AssertCommittedTokenCost(first, 2, "original_supply"); var firstIds = Value(first.NormalizedArguments, "itemIds").Items.Select(v => v.StringValue).ToArray();
            string id = first.EffectId; f.Restore("token转移完成后回同一重复窗口"); f.Drain();
            f.AssertCommittedTokenCost(f.Engine.GetNode(id), 2, "original_supply");
            Assert.That(f.Reputation, Is.EqualTo(2)); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score + 1));
            f.StartExchange("token-r2.second"); f.Confirm(true);
            var transfers = f.State.EffectRuntime.EffectNodes.Where(n => TiagoInvestmentTestFixture.IsTokenTransfer(n) && n.Status == EffectNodeStatus.Completed).ToList();
            Assert.That(transfers.Count, Is.EqualTo(2));
            var second = transfers.Single(n => n.EffectId != id); f.AssertCommittedTokenCost(second, 2, "original_supply");
            Assert.That(Value(second.NormalizedArguments, "itemIds").Items.Select(v => v.StringValue).Intersect(firstIds), Is.Empty);
            Assert.That(f.Reputation, Is.Zero); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score + 2));
            Assert.That(JsonConvert.SerializeObject(f.State.RandomRuntime), Is.EqualTo(random));
            f.Restore("两次真实token费用/得分完成不重复"); f.Drain();
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => TiagoInvestmentTestFixture.IsTokenTransfer(n) && n.Status == EffectNodeStatus.Completed), Is.EqualTo(2));
            Assert.That(f.Reputation, Is.Zero); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score + 2));
        }
        [Test] public void InsufficientRealReputationRejectsTheRepeatRequestWithoutPartialTransfer()
        {
            f.CreateSelectedHost(new[] { "D01", "D05" }, initialReputation: 1); f.OpenSelectedWindow(cleanup: true);
            var offer = f.ExchangeOption; Assert.That(offer.IsEnabled, Is.False); int score = f.State.FindPlayer(1).Score;
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "token-r2.shortage", offer.CleanupRequest, out _, out _, out _), Is.False);
            Assert.That(f.Reputation, Is.EqualTo(1)); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score));
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(TiagoInvestmentTestFixture.IsTokenTransfer), Is.False);
            f.Restore("真实声望短缺不受理不部分扣除"); Assert.That(f.Reputation, Is.EqualTo(1));
        }
        [Test] public void FiveADiscountChangesOnlyExchangeAndDoesNotReduceQuickOrExtraCosts()
        {
            var faces = new[] { "D01", "D03", "D05" };
            var exchange = f.Invoke("entrepreneur.D01.exchange", faces); Assert.That(exchange.IsSuccess, Is.True, exchange.Diagnostic);
            TiagoInvestmentTestFixture.AssertTokenCost(exchange.Effects.Single().NestedEffectGroups[0].Single(), 2, "original_supply");
            var quick = f.Invoke("entrepreneur.D07.quick", faces); Assert.That(quick.IsSuccess, Is.True, quick.Diagnostic);
            TiagoInvestmentTestFixture.AssertTokenCost(quick.Effects.Single().NestedEffectGroups[0].Single(), 3, "retired");
            var main = f.Rolled(faces, new[] { 2, 5 }).Effects.Single();
            var reward = f.Invoke(faces: faces, continuation: main.ContinuationContext, result: NormalizedValue.CreateString("2"));
            TiagoInvestmentTestFixture.AssertTokenCost(reward.Effects[1].NestedEffectGroups[0][0], 2, "original_supply");
        }
    }
}
