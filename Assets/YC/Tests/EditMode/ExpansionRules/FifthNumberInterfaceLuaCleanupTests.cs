using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Domain.Items;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>
    /// TOKEN-CONTRACT-CONSUMPTION-R2：真实T09 Lua、api3 token事务、Condition与重复收尾。
    /// 复用正式Host隔离夹具，不计正常对局；源码待统一登记后由017运行。
    /// </summary>
    public sealed class FifthNumberInterfaceLuaCleanupTests
    {
        private IsolatedTestArtifacts artifacts;
        private FifthNumberContentTestFixture f;

        [SetUp] public void Begin() { artifacts = IsolatedTestArtifacts.BeginCase(); f = null; }
        [TearDown] public void End() { f?.Snapshot("通用转移清理用例最终状态"); artifacts?.Finish(); }

        [TestCase("place")]
        [TestCase("resource")]
        public void SixRealMarkersTransferThreeThenRequireOneRewardAndAllowTheNextInvocation(string firstReward)
        {
            f = new FifthNumberContentTestFixture(artifacts);
            for (int i = 0; i < 4; i++) f.CollectOne();
            Assert.That(f.Stored, Is.EqualTo(6));
            var original = f.State.ItemZones.Items.Where(i => i.ZoneId == f.Storage.ZoneId)
                .ToDictionary(i => i.ItemId, i => i.OriginalSupplyZoneId);
            int pure = f.State.FindPlayer(1).Resources.PureOriginium;
            int map = f.State.Map.Influences.Count(i => i.PlayerId == 1);
            int departures = f.State.EffectRuntime.InfluenceDepartures.Count;
            f.OpenCleanup();
            string optionId = f.Registry.GetSettlementOptions(f.State, 1).Single(o => o.SourceItemId == f.Card).OptionId;

            f.InvokeCleanup("qy012.interface.exchange.first");
            var confirmation = f.Open();
            Assert.That(confirmation.InteractionTypeId, Is.EqualTo("action.decline_effect"), "保留卡面Condition自己的执行/放弃。");
            f.Answer(confirmation, NormalizedValue.CreateString("continue"));
            Assert.That(f.Stored, Is.EqualTo(3));
            var invocation = CleanupSourceService.Invocations(f.State).Single(i => i.SourceRef.ItemId == f.Card);
            var transfers = f.State.EffectRuntime.EffectNodes.Where(n =>
                InfluenceDepartureProtocol.IsWithin(f.State, n.EffectId, invocation.TargetEffectId) &&
                n.PositionChanges.Any(c => original.ContainsKey(c.ItemId))).ToList();
            Assert.That(transfers.Count, Is.EqualTo(1), "三个实例须由同一通用物品事务完成，不能留专用付款或拆成可部分成功的三笔。");
            Assert.That(transfers[0].EffectTypeId, Is.EqualTo(ItemTransferEffectExecutor.TypeId));
            Assert.That(transfers[0].DefinitionVersion, Is.EqualTo("3.0.0"));
            Assert.That(ItemTransferEffectExecutor.Number(transfers[0].NormalizedArguments, "apiVersion"), Is.EqualTo(3));
            Assert.That(transfers[0].TransferBatchRequest.ProtocolVersion, Is.EqualTo(3));
            Assert.That(transfers[0].TransferBatchRequest.ExpectedOrders, Is.Empty, "token不使用设施层序证明。");
            Assert.That(transfers[0].TransferBatchRequest.Transfers.All(t => t.OperationProfileId == "core.player_marker_storage"), Is.True);
            Assert.That(transfers[0].PositionChanges.Select(c => c.ItemId).Distinct().Count(), Is.EqualTo(3));
            foreach (var change in transfers[0].PositionChanges)
                Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == change.ItemId).ZoneId, Is.EqualTo(original[change.ItemId]));
            var receipt = ItemTransferEffectExecutor.Value(transfers[0].ContinuationData, "transferBatchReceipt");
            Assert.That(ItemTransferEffectExecutor.Number(receipt, "protocolVersion"), Is.EqualTo(3));
            Assert.That(ItemTransferEffectExecutor.Text(receipt, "operationId"), Is.EqualTo(transfers[0].EffectId + ":move"));
            Assert.That(ItemTransferService.TryValidateTokenState(f.State, transfers[0], out string invalid), Is.True, invalid);

            var reward = f.Open();
            Assert.That(reward.PromptKey, Is.Not.EqualTo("effect.card.payment_items"));
            Assert.That(reward.CandidateIds, Is.EquivalentTo(new[] { "place", "resource" }));
            Assert.That(reward.AllowDecline, Is.False, "已付三枚后不能放弃收益。");
            Assert.That(f.Engine.TrySubmitInteraction(reward.GetStableInteractionId(), 1, reward.StateRevision,
                NormalizedValue.CreateBoolean(false), out _), Is.False);
            string requestId = reward.GetStableInteractionId();
            string transferId = transfers[0].EffectId;
            int positionChanges = f.State.EffectRuntime.EffectNodes.Sum(n => n.PositionChanges.Count);
            f.Restore("通用转移已提交收益待选");
            Assert.That(f.Stored, Is.EqualTo(3));
            Assert.That(f.State.EffectRuntime.EffectNodes.Sum(n => n.PositionChanges.Count), Is.EqualTo(positionChanges));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectId == transferId), Is.EqualTo(1));
            var restoredTransfer = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == transferId);
            Assert.That(ItemTransferService.TryValidateTokenState(f.State, restoredTransfer, out invalid), Is.True, invalid);
            reward = f.State.EffectRuntime.InteractionRequests.Single(q => q.GetStableInteractionId() == requestId);
            f.AnswerCandidate(reward, firstReward);
            if (firstReward == "place") f.AnswerCandidate(f.PlacementRequest(), f.PlacementRequest().CandidateIds.First());
            f.Restore("第一笔通用转移兑换完成"); f.Pump();
            Assert.That(f.Registry.GetSettlementOptions(f.State, 1).Single(o => o.SourceItemId == f.Card).OptionId, Is.EqualTo(optionId));

            f.InvokeCleanup("qy012.interface.exchange.second"); f.Answer("continue");
            string secondReward = firstReward == "place" ? "resource" : "place";
            f.Answer(secondReward);
            if (secondReward == "place") f.AnswerCandidate(f.PlacementRequest(), f.PlacementRequest().CandidateIds.First());
            Assert.That(f.Stored, Is.Zero);
            Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure + 1));
            Assert.That(f.State.Map.Influences.Count(i => i.PlayerId == 1), Is.EqualTo(map + 1));
            Assert.That(f.State.EffectRuntime.InfluenceDepartures.Count, Is.EqualTo(departures), "牌上转出不制造地图离场或再次收容。");
            Assert.That(CleanupSourceService.Invocations(f.State).Count(i => i.SourceRef.ItemId == f.Card), Is.EqualTo(2));
            // Cleanup按实际已提交费用节点保存证据；PaymentCommitted只属于AbilityActionInvocation。
            var paidIds = CleanupSourceService.Invocations(f.State).Where(i => i.SourceRef.ItemId == f.Card).SelectMany(i => {
                Assert.That(i.PaymentEffectIds.Count, Is.EqualTo(1)); return i.PaymentEffectIds;
            }).ToArray();
            Assert.That(paidIds.Distinct().Count(), Is.EqualTo(2), "两次独立收尾各自提交一笔费用。");
            foreach (var id in paidIds)
            {
                var paid = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
                Assert.That(AbilityActionExecutor.IsCommittedCost(f.State, paid), Is.True);
                Assert.That(ItemTransferService.TryValidateTokenState(f.State, paid, out var paidReason), Is.True, paidReason);
                Assert.That(ItemTransferEffectExecutor.Number(paid.NormalizedResult, "movedCount"), Is.EqualTo(3));
            }
            Assert.That(f.Registry.QuerySources(f.State, 1).Any(s => s.ItemId == f.Card && s.AbilityId == "entrepreneur.T09.departure"), Is.True);
        }

        [Test]
        public void TwoMarkersCannotStartAnExchangeOrCreateAReward()
        {
            f = new FifthNumberContentTestFixture(artifacts); f.OpenCleanup();
            var option = f.Registry.GetSettlementOptions(f.State, 1).Single(o => o.SourceItemId == f.Card);
            Assert.That(option.IsEnabled, Is.False);
            // 独立纯查询负例：兼容总数故意抬高，真实SourceFacts仍只有本牌2枚。
            // 不改状态、不补来源，验证资格不会把跨声明/兼容总数当本牌可转移库存。
            var window = SettlementEntryService.CreateQueryContext(f.State, f.CleanupWindow);
            var entry = f.CleanupWindow.Entries.Single(e => e.SourceItemId == f.Card && e.EntryKind == SettlementEntryService.CleanupOrder);
            string beforeQuery = JsonConvert.SerializeObject(f.State);
            var facts = CleanupSourceLuaCatalog.CreateFacts(f.State, f.CleanupWindow, entry);
            var legacyTotals = ItemTransferEffectExecutor.Value(facts, "source");
            ItemTransferEffectExecutor.Value(legacyTotals, "markerCount").IntegerValue = 99;
            ItemTransferEffectExecutor.Value(legacyTotals, "transferableTokenCount").IntegerValue = 99;
            var sourceFacts = SourceFactsLuaCatalog.ForSettlement(f.State, f.Registry, window, entry);
            var catalog = new SettlementLuaCatalog(f.Pack);
            for (int i = 0; i < 2; i++)
                Assert.That(catalog.QueryAvailability(window, entry, facts, true, sourceFacts).Enabled, Is.False);
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(beforeQuery), "纯资格查询不能转移token、生成请求或消耗次数。");
            var original = f.State.ItemZones.Items.Where(i => i.ZoneId == f.Storage.ZoneId).Select(i => i.ItemId).OrderBy(i => i).ToArray();
            int pure = f.State.FindPlayer(1).Resources.PureOriginium;
            int moves = f.State.EffectRuntime.EffectNodes.Sum(n => n.PositionChanges.Count);
            var command = CleanupSourceCommand.Create(1, "qy012.interface.insufficient", option.CleanupRequest, f.State.EffectRuntime.StateRevision);
            Assert.That(new InvokeSettlementOptionCommandHandler(f.Registry).Handle(f.State, command).Succeeded, Is.False);
            Assert.That(f.State.ItemZones.Items.Where(i => i.ZoneId == f.Storage.ZoneId).Select(i => i.ItemId).OrderBy(i => i), Is.EqualTo(original));
            Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure));
            Assert.That(f.State.EffectRuntime.EffectNodes.Sum(n => n.PositionChanges.Count), Is.EqualTo(moves));
            Assert.That(CleanupSourceService.Invocations(f.State).Any(i => i.SourceRef.ItemId == f.Card), Is.False);
        }
    }
}
