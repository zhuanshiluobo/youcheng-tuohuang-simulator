using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Presentation.Workflows;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;

namespace YC.Tests.PlayMode.ExpansionRules
{
    public sealed class RouteResourceSelectionUiTests
    {
        private GameplayPageAcceptanceFixture f;
        private RouteResourceSelectionPresenter RoutePresenter => (RouteResourceSelectionPresenter)Presenter;
        private EffectRegistry Registry => (EffectRegistry)Read(((IEnumerable)Read(f.Session, "commandHandlers"))
            .Cast<object>().OfType<AnswerInteractionCommandHandler>().Single(), "registry");
        private int Player => f.State.CurrentPlayerId;
        private const string EnterpriseEvent = "enterprise.event.cave_ruins";
        private const string TargetLocation = "D-02";

        [UnityTest]
        public IEnumerator LocalDeployOpenCancelDoNotSubmitAndConfirmationPlacesExactlyOnce()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, _ => Settle());
            int player = Player;
            var button = ButtonObject(Read(f.Hud, "ActionPanelView"), "DeployButton");
            var influence = Read(Read(f.Gameplay, "turnActionPresenter"), "influenceActionPresenter");
            var before = GameStateCloneService.DeepClone(f.State);
            var service = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(f.State.MapId)));
            var expected = service.GetLegalPlacementSlotIds(f.State, player).ToArray();
            Assert.That(expected, Is.Not.Empty);
            for (int i = 0; i < 2; i++)
            {
                using (var counter = new RuleSubmitCounter())
                {
                    yield return f.Click(button, "本地部署_打开_" + i);
                    Assert.That((bool)Read(influence, "IsSelectingDeployTarget"), Is.True);
                    Assert.That(f.Request, Is.Null, "打开本地部署不得预先创建 Host 选择请求");
                    var actual = Active("MapButtonView").Where(target => Read(target, "TargetKind").ToString() == "InfluenceSlot" &&
                        (bool)Read(target, "IsInteractive")).Select(target => (string)Read(target, "TargetId")).ToArray();
                    Assert.That(actual, Is.EquivalentTo(expected), "实际地图候选应遵守现有红区及空白航道规则");
                    Assert.That(actual.Any(id => id.StartsWith("route:", StringComparison.Ordinal)), Is.True);
                    Assert.That(actual.Any(id => HotseatUiInputDriver.TryPoint(MapTarget("InfluenceSlot", id).gameObject, out _, out _)), Is.True);
                    yield return f.Click(button, "本地部署_再次点击部署按钮取消_" + i);
                    yield return Settle();
                    Assert.That((bool)Read(influence, "IsSelectingDeployTarget"), Is.False);
                    Assert.That(counter.Calls, Is.Zero, "本地打开和取消均不能正式提交");
                }
                Assert.That(GameStateCloneService.AreEquivalent(f.State, before), Is.True);
            }
            yield return f.Click(button, "本地部署_再次打开并准备确认");
            var slot = expected.First(id => id.StartsWith("route:", StringComparison.Ordinal) &&
                HotseatUiInputDriver.TryPoint(MapTarget("InfluenceSlot", id).gameObject, out _, out _));
            var targetObject = MapTarget("InfluenceSlot", slot).gameObject;
            var oldIds = f.State.Map.Influences.Select(i => i.InfluenceId).ToArray();
            using (var counter = new RuleSubmitCounter())
            {
                yield return f.Click(targetObject, "本地部署_第一次选择实际航道位");
                Assert.That(counter.Calls, Is.Zero, "第一次点击只能设本地确认目标");
                yield return f.Click(targetObject, "本地部署_第二次点击确认");
                Assert.That(counter.Calls, Is.EqualTo(1), "确认时仅提交一次带 TargetId 的正式部署命令");
            }
            yield return Wait(() => f.State.Map.Influences.Any(i => !oldIds.Contains(i.InfluenceId) && i.SlotId == slot), "确认后实际落子未出现");
            Assert.That(f.State.Map.Influences.Count(i => !oldIds.Contains(i.InfluenceId) && i.PlayerId == player), Is.EqualTo(1));
            Assert.That(f.State.FindPlayer(player).CompletedMainActionsThisTurn, Is.EqualTo(before.FindPlayer(player).CompletedMainActionsThisTurn + 1));
            f.Record("本地部署实际输入结果", new { player, slot, candidates = expected, openCancelSubmits = 0, confirmSubmits = 1,
                note = "开/取消使用真实本地草稿；确认由两次地图点击发出；性能采样另行执行" });
        }

        [UnityTest]
        public IEnumerator MainExplorePrunesBranchRestoresHeldEnterpriseEventAndDeliversOnce()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, _ => Settle());
            int player = Player;
            int receiver = f.State.Players.First(p => p.PlayerId != player).PlayerId;
            yield return PrepareScenario(player, receiver, 4, false);
            int receiverBefore = f.State.FindPlayer(receiver).Resources.GoldVoucher;
            int actionsBefore = f.State.FindPlayer(player).CompletedMainActionsThisTurn;
            var influenceBefore = f.State.Map.Influences.Select(i => i.InfluenceId).ToArray();
            var eventItemsBefore = f.State.ItemZones.Items.Where(i => i.DefinitionId == EnterpriseEvent).Select(i => i.ItemId).ToArray();
            Assert.That(eventItemsBefore.Length, Is.EqualTo(1), "本局配方必须使用唯一企业替换事件实例");
            Assert.That(f.State.ItemZones.Items.Any(i => i.DefinitionId == "event_yellow_02"), Is.False);
            yield return OpenMainExplore();
            Assert.That(RoutePresenter.Session.StartGoldVoucherAmount, Is.EqualTo(4));
            Assert.That(RoutePresenter.Projection.RouteOptions.Single(r => r.RouteId == "A1").IsAutomaticallySelectedFreeRoute, Is.True);
            Assert.That((bool)Read(MapTarget("Route", "A1"), "IsSelected"), Is.True);
            yield return ChooseRoute("A2", "player:" + receiver);
            yield return ChooseRoute("D1", "player:" + receiver);
            yield return ChooseRoute("B1", "bank");
            Assert.That(RoutePresenter.Draft.SelectedLocationIds, Is.Empty);
            Assert.That(RoutePresenter.Draft.ConfirmedRouteIds, Is.EqualTo(new[] { "A2", "D1", "B1" }), "无目标前不可剪空草稿");
            yield return f.Click(MapTarget("Location", TargetLocation).gameObject, "探索_选择真实目标后自动剪枝");
            var preview = RoutePresenter.Projection.Preview;
            Assert.That(preview, Is.Not.Null);
            Assert.That(preview.PrunedRouteIds, Does.Contain("B1"));
            Assert.That(preview.ChargeRouteIds, Is.EquivalentTo(new[] { "A2", "D1" }));
            Assert.That(preview.EffectiveRouteIds, Is.EquivalentTo(new[] { "A1", "A2", "D1" }));
            Assert.That(preview.PaymentPlan.TotalGoldVoucherAmount, Is.EqualTo(4));
            Assert.That(preview.PaymentPlan.Lines.All(line => line.ReceiverSubjectKey == "player:" + receiver), Is.True);
            foreach (var route in new[] { "A1", "A2", "D1" })
                Assert.That((bool)Read(MapTarget("Route", route), "IsSelected"), Is.True);
            Assert.That((bool)Read(MapTarget("Route", "B1"), "IsSelected"), Is.False, "剔除支路不能残留最终高亮");
            Assert.That(RoutePresenter.TryBuildAnswer(f.Request.CandidateIds, false, out var selected), Is.True);
            var duplicateSelection = Answer(f.Request, selected);
            var selectionOwner = f.Request.OwnerEffectId;
            f.Record("实际探索最终草稿与费用", new { player, receiver, preview, input = "自选 A2、D1、B1；目标 D-02；保留免费 A1" });
            yield return f.Capture("真实探索_目标剪枝与最终四金券", (Component)f.Frame);
            yield return ConfirmRoute();
            yield return Wait(() => f.Request?.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId &&
                Active("EventChoiceDialogView").Length == 1, "一次确认后必须出现当前企业事件实际页面");
            AssertBalance(player, 0, 4);
            Assert.That(f.State.FindPlayer(receiver).Resources.GoldVoucher, Is.EqualTo(receiverBefore));
            Assert.That(f.State.FindPlayer(player).CompletedMainActionsThisTurn, Is.EqualTo(actionsBefore));
            Assert.That(f.Request.CandidateIds, Does.Contain(EnterpriseEvent + ":option:1"));
            Assert.That(new RouteSelectionSessionAuthority().TryResolveFinalized(f.State, selectionOwner, out _, out var commitment).IsValid, Is.True);
            Assert.That(commitment.AcceptedPreview.PlanDigest, Is.EqualTo(preview.PlanDigest));
            var explorer = f.State.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == selectionOwner && n.EffectTypeId == ExplorationEffectTypeIds.Explore);
            Assert.That(RouteTollEscrowService.TryReadReceipt(explorer, out var held, out var receiptReason), Is.True, receiptReason);
            Assert.That(held.State, Is.EqualTo("held"));
            Assert.That(held.OriginalAmount, Is.EqualTo(4));
            var eventRequestId = f.Request.GetStableInteractionId();
            var deckAfterReveal = EventDeckOrder();
            yield return RestoreArchive("held_企业事件待答");
            Assert.That(f.Request.GetStableInteractionId(), Is.EqualTo(eventRequestId));
            Assert.That(EventDeckOrder(), Is.EqualTo(deckAfterReveal), "held 恢复不能重抽牌");
            AssertBalance(player, 0, 4);
            Assert.That(Read(Read(f.Gameplay, "interactionRequestRouter"), "activeRenderer").GetType().Name,
                Is.EqualTo("EventCardInteractionUiCoordinator"));
            yield return f.Capture("held恢复_当前企业事件实际页", Active("EventChoiceDialogView").Single());
            AssertRejectedDuplicate(duplicateSelection, "已托管后重复最终选路回答");

            // 同一 held 存档的独立规则分支逐步观察真实奖励提交点；不改变 UI 主分支、不伪造 receipt。
            ObserveRewardBeforeSettlement(player, receiver, receiverBefore, 10);
            var duplicateEvent = Answer(f.Request, new[] { EnterpriseEvent + ":option:1" });
            var eventButton = HotseatUiInputDriver.Marker("event.option.1").GetComponent<Button>();
            var lateCallback = eventButton.onClick;
            using (var counter = new RuleSubmitCounter())
            {
                yield return f.Click(eventButton.gameObject, "企业洞穴遗迹_选择实际十金券奖励");
                Assert.That(counter.Calls, Is.EqualTo(1));
            }
            yield return CompleteRequiredInfluenceInputs();
            yield return Wait(() => f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == explorer.EffectId).Status == EffectNodeStatus.Completed,
                "奖励、影响力及其响应链必须结束后才能交付");
            AssertBalance(player, 10, 0);
            Assert.That(f.State.FindPlayer(receiver).Resources.GoldVoucher, Is.EqualTo(receiverBefore + 4));
            Assert.That(f.State.FindPlayer(player).CompletedMainActionsThisTurn, Is.EqualTo(actionsBefore + 1));
            Assert.That(f.State.Map.Influences.Count(i => !influenceBefore.Contains(i.InfluenceId) && i.PlayerId == player && i.LocationId == TargetLocation), Is.EqualTo(1),
                "本张事件的主要影响力沿正式 Lua 自动放置一次；若需要选择则必须经过上方实际地图输入");
            Assert.That(f.State.Map.ResourceTokens.Count(t => t.LocationId == TargetLocation), Is.EqualTo(1));
            Assert.That(RouteTollEscrowService.TryReadReceipt(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == explorer.EffectId), out var settled, out receiptReason), Is.True, receiptReason);
            Assert.That(settled.State, Is.EqualTo("settled"));
            Assert.That(settled.SettlementReceiptId, Is.Not.Empty);
            var business = BusinessSnapshot();
            using (var counter = new RuleSubmitCounter())
            {
                lateCallback.Invoke(); // 明确过期回调补证，不冒充隐藏控件真实点击。
                yield return Settle();
                Assert.That(counter.Calls, Is.Zero);
            }
            Assert.That(BusinessSnapshot(), Is.EqualTo(business));
            AssertRejectedDuplicate(duplicateEvent, "重复企业事件回答");
            yield return RestoreArchive("settled_完整探索结束");
            Assert.That(new EffectTreeExecutor(f.State, Registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(BusinessSnapshot(), Is.EqualTo(business), "settled 恢复及再次推进不得重复交付或扣行动");
            f.Record("完整探索与恢复最终结果", new { player, receiver, reward = 10, payer = RouteTollEscrowService.QueryBalance(f.State, player),
                receiverBefore, receiverAfter = f.State.FindPlayer(receiver).Resources.GoldVoucher, settled.SettlementReceiptId,
                actionsBefore, actionsAfter = f.State.FindPlayer(player).CompletedMainActionsThisTurn, eventItemsBefore,
                scope = "受控局面后从真实主要按钮开始，实际航道/收款人/目标/确认/事件页输入；主要影响力由当前正式事件自动放置" });
            yield return f.Capture("完整探索_交付结束", (Component)f.Frame);
        }

        [UnityTest]
        public IEnumerator InsufficientStartingBudgetCannotReachFutureEnterpriseReward()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, _ => Settle());
            int player = Player, receiver = f.State.Players.First(p => p.PlayerId != Player).PlayerId;
            yield return PrepareScenario(player, receiver, 2, false);
            var deck = EventDeckOrder();
            yield return OpenMainExplore();
            yield return ChooseRoute("A2", "player:" + receiver);
            yield return ChooseRoute("D1", "player:" + receiver);
            yield return f.Click(MapTarget("Location", TargetLocation).gameObject, "开始预算不足_选择四金券目标");
            Assert.That(RoutePresenter.Session.StartGoldVoucherAmount, Is.EqualTo(2));
            Assert.That(RoutePresenter.Projection.Preview.PaymentPlan.TotalGoldVoucherAmount, Is.EqualTo(4));
            Assert.That(RoutePresenter.Projection.Preview.FinalizationValidation.IsValid, Is.False);
            Assert.That(RoutePresenter.CanConfirm, Is.False);
            Assert.That(((Button)Read(f.Frame, "EndActionButton")).IsInteractable(), Is.False);
            AssertBalance(player, 2, 0);
            Assert.That(EventDeckOrder(), Is.EqualTo(deck));
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && r.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId), Is.False,
                "牌堆顶的十金券奖励不能垫付开始时缺少的路费");
            f.Record("正式入口开始预算不足", new { budget = 2, required = 4, futureReward = 10, RoutePresenter.Projection.Preview.FinalizationValidation });
            Assert.That((bool)Call(f.Gameplay, "TryHandleInteractionEscape"), Is.True);
            yield return Wait(() => f.Request == null, "不足预算草稿必须仍可确认前取消");
        }

        [UnityTest]
        public IEnumerator NormalHarvestUsesActualSharedMapInputsAfterBothActionRounds()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, _ => Settle());
            var map = new MapQueryService(StaticMapDefinitions.Resolve(f.State.MapId));
            var influence = new InfluenceService(map);
            int windows = 0;
            while (f.State.Phase == GamePhase.ActionRound1 || f.State.Phase == GamePhase.ActionRound2)
            {
                Assert.That(++windows, Is.LessThanOrEqualTo(8), "四人两轮行动应在 8 个窗口后进入采集");
                Assert.That(f.Request, Is.Null, "辅助行动前有必须处理的正式请求");
                int actor = Player;
                string target = influence.GetLegalPlacementSlotIds(f.State, actor).First();
                foreach (var command in new[] {
                    new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.DeployInfluence, PlayerId = actor, TargetId = target },
                    new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.EndAction, PlayerId = actor } })
                {
                    var result = f.Session.Submit(command);
                    Assert.That(result.Succeeded, Is.True, command.Kind + ": " + result.Validation?.Reason);
                    f.Record("采集前两轮行动正式辅助提交", new { command.Kind, actor, target, scope = "规则命令加速非待验行动；不冒充地图点击，不直接改阶段或行动完成标记" });
                    Call(f.Gameplay, "SynchronizeInteractionFromState");
                    yield return Settle();
                }
            }
            Assert.That(windows, Is.EqualTo(8));
            yield return Wait(() => f.Request?.InteractionTypeId == "route_resource.selection" && Coordinator != null && RoutePresenter.IsActive,
                "两轮行动后普通采集共用界面未出现");
            Assert.That(RoutePresenter.Session.Purpose, Is.EqualTo(RouteSelectionPurpose.NormalHarvest));
            var opened = OpenCollectionRequests();
            var players = f.State.Players.Select(p => p.PlayerId).ToArray();
            Assert.That(opened.Select(r => r.AnsweringPlayerId), Is.EquivalentTo(players), "必须同时开放全部玩家，不能只检查热座首个请求");
            var requestIds = opened.ToDictionary(r => r.AnsweringPlayerId, r => r.GetStableInteractionId());
            int first = f.Request.AnsweringPlayerId, round = f.State.Round;
            string mainId = f.State.EffectRuntime.ActiveMainNodeId;
            var main = f.State.EffectRuntime.MainNodes.Single(n => n.NodeId == mainId);
            var owner = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == main.ExecutionEffectId);
            Assert.That(owner.ChildSchedulingGroups.Single(g => g.StableGroupKey == "collection_players").ChildEffectIds,
                Is.EquivalentTo(opened.Select(r => r.OwnerEffectId)));
            int startEvents = f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "CollectionStarted");
            var order = new List<int>();
            f.Record("普通采集全员同时开放", new { players, requestIds, mainId, round, first,
                group = owner.ChildSchedulingGroups, budgets = f.State.Players.Select(p => new { p.PlayerId, p.ResourceCollectionStartGoldVoucher }).ToArray(),
                scope = "真实 StartScene 与正式两轮行动进入；未直接修改 CurrentPlayerId、完成标记或资源。热座 LocalPlayerResolver 固定显示首个开放请求，无自由切玩家入口" });
            yield return f.Capture("普通采集_全员开放但热座显示首玩家", (Component)f.Frame);

            // 正式候选协议允许非首玩家先回答。此处是 Session 命令证据，不冒充该玩家的 UI 点击。
            var nonFirst = opened.First(r => r.AnsweringPlayerId != first);
            int nonFirstPlayer = nonFirst.AnsweringPlayerId;
            string commit = nonFirst.CandidateIds.Single(id => id.EndsWith(".commit", StringComparison.Ordinal));
            var emptyResources = JsonConvert.SerializeObject(f.State.FindPlayer(nonFirstPlayer).Resources);
            var nonFirstCommand = Answer(nonFirst, new[] { commit });
            var submitted = f.Session.Submit(nonFirstCommand);
            Assert.That(submitted.Succeeded, Is.True, submitted.Validation?.Reason);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            yield return Settle();
            order.Add(nonFirstPlayer);
            Assert.That(f.State.FindPlayer(nonFirstPlayer).HasCollectedResourcesThisRound, Is.True);
            Assert.That(JsonConvert.SerializeObject(f.State.FindPlayer(nonFirstPlayer).Resources), Is.EqualTo(emptyResources), "正式空采集不应改变资源");
            Assert.That(f.Request.AnsweringPlayerId, Is.EqualTo(first), "正式协议提交不能伪造热座切视角");
            AssertCollectionStillWaiting(mainId, round, players.Except(order).ToArray(), requestIds);
            f.Record("非首玩家正式协议先提交", new { nonFirstPlayer, first, nonFirstCommand.CommandId, nonFirstCommand.OptionIds,
                submitted.Succeeded, input = "Session.Submit(EffectInteractionCommands.Answer)，合法空采集 commit；不是非首玩家 UI 操作", order = order.ToArray() });
            yield return f.Capture("普通采集_非首玩家协议提交后仍等待", (Component)f.Frame);

            while (order.Count < players.Length)
            {
                yield return Wait(() => RoutePresenter.IsActive && RoutePresenter.Session.Purpose == RouteSelectionPurpose.NormalHarvest,
                    "剩余玩家普通采集 UI 未接入");
                var request = f.Request;
                int player = request.AnsweringPlayerId;
                CollectionAssert.DoesNotContain(order, player);
                Assert.That(RoutePresenter.Session.PlayerId, Is.EqualTo(player));
                Assert.That((int)Read(f.Gameplay, "localPlayerId"), Is.EqualTo(player));
                var option = RoutePresenter.Projection.LocationOptions.FirstOrDefault(o => o.IsReachableInOriginalDraft);
                Assert.That(option, Is.Not.Null, "普通采集应有城市资源点");
                var location = option.LocationId;
                if (RoutePresenter.Draft.SelectedLocationIds.Contains(location))
                    yield return f.Click(MapTarget("Location", location).gameObject, "普通采集_取消本地点草稿_玩家" + player);
                yield return f.Click(MapTarget("Location", location).gameObject, "普通采集_选择实际资源点_玩家" + player);
                Assert.That(f.State.FindPlayer(player).HasCollectedResourcesThisRound, Is.False);
                var selectedLocations = RoutePresenter.Draft.SelectedLocationIds.ToArray();
                Assert.That(selectedLocations, Does.Contain(location));
                if (order.Count == players.Length - 1)
                    yield return f.Capture("普通采集_最后玩家确认前仍未推进", (Component)f.Frame);
                yield return ConfirmRoute();
                yield return Wait(() => f.State.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == requestIds[player]).Status == "answered",
                    "普通采集正式确认未完成");
                order.Add(player);
                Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(RouteTollEscrowService.QueryBalance(f.State, player).Reserved, Is.Zero);
                if (order.Count < players.Length)
                    AssertCollectionStillWaiting(mainId, round, players.Except(order).ToArray(), requestIds);
                f.Record("普通采集逐玩家实际UI确认", new { player, location, selectedLocations, request.OwnerEffectId,
                    order = order.ToArray(), phase = f.State.Phase.ToString(), activeMain = f.State.EffectRuntime.ActiveMainNodeId,
                    scope = "剩余玩家跟随正常热座视角，实际地图取消/重选及确认；不直接修改玩家或主链" });
            }
            yield return Wait(() => f.State.EffectRuntime.ActiveMainNodeId != mainId, "全员采集完成后主链没有推进");
            Assert.That(f.State.EffectRuntime.MainNodes.Single(n => n.NodeId == mainId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.EffectRuntime.MainNodes.Single(n => n.NodeId == mainId).CompletedPlayerIds, Is.EquivalentTo(players));
            Assert.That(OpenCollectionRequests(), Is.Empty);
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "CollectionStarted"), Is.EqualTo(startEvents));
            Assert.That(order.First(), Is.Not.EqualTo(first));
            f.Record("全员完成后主链推进", new { order = order.ToArray(), beforeMain = mainId,
                afterMain = f.State.EffectRuntime.ActiveMainNodeId, beforeRound = round, afterRound = f.State.Round, phase = f.State.Phase.ToString(),
                inputBoundary = "首笔为非首玩家正式协议回答，其余三笔为真实 UI；没有宣称热座可任意切换或真实多进程联机" });
            yield return f.Capture("普通采集_全员完成后正式推进", (Component)f.Frame);
        }

        private InteractionRequest[] OpenCollectionRequests() => f.State.EffectRuntime.InteractionRequests
            .Where(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection").ToArray();

        private void AssertCollectionStillWaiting(string mainId, int round, int[] pendingPlayers, Dictionary<int, string> requestIds)
        {
            Assert.That(f.State.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            Assert.That(f.State.Round, Is.EqualTo(round));
            Assert.That(f.State.EffectRuntime.ActiveMainNodeId, Is.EqualTo(mainId), "最后玩家结束前不能推进主链");
            Assert.That(f.State.EffectRuntime.MainNodes.Single(n => n.NodeId == mainId).CompletionRequested, Is.False);
            Assert.That(OpenCollectionRequests().Select(r => r.AnsweringPlayerId), Is.EquivalentTo(pendingPlayers));
            foreach (var request in OpenCollectionRequests())
            {
                Assert.That(request.GetStableInteractionId(), Is.EqualTo(requestIds[request.AnsweringPlayerId]), "其余已开放请求不能重发");
                Assert.That(f.State.FindPlayer(request.AnsweringPlayerId).HasCollectedResourcesThisRound, Is.False);
            }
        }

        [UnityTest]
        public IEnumerator ExtraHarvestUsesActualSharedMapInputsAndOriginalSettlementOrder()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, _ => Settle());
            int actor = Player, receiver = f.State.Players.First(p => p.PlayerId != actor).PlayerId;
            yield return PrepareScenario(actor, receiver, 4, true);
            var before = GameStateCloneService.DeepClone(f.State.FindPlayer(actor).Resources);
            int received = f.State.FindPlayer(receiver).Resources.GoldVoucher;
            int actions = f.State.FindPlayer(actor).CompletedMainActionsThisTurn;
            var args = ObjectValue("kind", NormalizedValue.CreateString("Collect"), "apiVersion", NormalizedValue.CreateInteger(1),
                "effectTypeId", NormalizedValue.CreateString(ResourceCollectionEffectExecutor.TypeId),
                "player", NormalizedValue.CreateString(actor.ToString()), "scope", NormalizedValue.CreateString("extra"));
            Assert.That(ResourceCollectionEffectExecutor.ValidateArguments(args), Is.Empty);
            var root = RunFormalRoot(new EffectSpec(ResourceCollectionEffectExecutor.TypeId, args) { PlayerId = actor, SourceId = "acceptance.extra_collection" });
            f.Record("额外采集入口边界", new { root, scope = "触发已注册 Effect.Collect extra 能力入口；不伪造其预算 owner 或选择请求，随后实际 UI 操作" });
            yield return Wait(() => f.Request?.InteractionTypeId == "route_resource.selection" && RoutePresenter.IsActive, "extra 未接入共用地图");
            Assert.That(RoutePresenter.Session.Purpose, Is.EqualTo(RouteSelectionPurpose.ExtraHarvest));
            Assert.That(RoutePresenter.Session.StartGoldVoucherAmount, Is.EqualTo(4));
            yield return ChooseRoute("A2", "player:" + receiver);
            yield return ChooseRoute("D1", "player:" + receiver);
            foreach (var id in RoutePresenter.Draft.SelectedLocationIds.Where(id => id != "A-03" && id != TargetLocation).ToArray())
                yield return f.Click(MapTarget("Location", id).gameObject, "extra_移除非本次目标");
            foreach (var id in new[] { "A-03", TargetLocation })
            {
                if (RoutePresenter.Draft.SelectedLocationIds.Contains(id)) yield return f.Click(MapTarget("Location", id).gameObject, "extra_取消资源点草稿");
                yield return f.Click(MapTarget("Location", id).gameObject, "extra_选择实际资源点");
            }
            Assert.That(RoutePresenter.Draft.SelectedLocationIds, Is.EquivalentTo(new[] { "A-03", TargetLocation }));
            Assert.That(RoutePresenter.Projection.Preview.PaymentPlan.TotalGoldVoucherAmount, Is.EqualTo(4));
            Assert.That(f.State.FindPlayer(actor).Resources.GoldVoucher, Is.EqualTo(before.GoldVoucher), "确认前不付款");
            yield return ConfirmRoute();
            yield return Wait(() => f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status == EffectNodeStatus.Completed, "extra 结算未完成");
            AssertBalance(actor, 0, 0);
            Assert.That(f.State.FindPlayer(actor).Resources.Iron, Is.EqualTo(before.Iron + 2));
            Assert.That(f.State.FindPlayer(receiver).Resources.GoldVoucher, Is.EqualTo(received + 4));
            Assert.That(f.State.FindPlayer(actor).CompletedMainActionsThisTurn, Is.EqualTo(actions));
            f.Record("额外采集完成", new { root, before, after = f.State.FindPlayer(actor).Resources,
                scope = "两个实际目标一次确认；沿原采集付款/奖励顺序，不采用探索 held 延付" });
        }

        [UnityTest]
        public IEnumerator RegisteredWarehouseLuaChooseExploreUsesSharedMapWithoutLegacyRouteMenu()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, _ => Settle());
            int player = Player, receiver = f.State.Players.First(p => p.PlayerId != Player).PlayerId;
            yield return PrepareScenario(player, receiver, 4, false);
            var state = GameStateCloneService.DeepClone(f.State);
            var card = state.ItemZones.Items.First(i => FacilityCardDatabase.Get(state, i.DefinitionId)?.EffectId == "remove_then_dispatch_or_explore" &&
                state.ItemZones.Zones.Single(z => z.ZoneId == i.ZoneId).LocalZoneId == "facility_deck");
            var cell = state.ItemZones.Zones.First(z => z.TemplateId == "core.zone.city_facility_cell" && z.OwnerPlayerId == player &&
                !state.ItemZones.Items.Any(i => i.ZoneId == z.ZoneId));
            Relocate(state, card, cell);
            Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.True, reason);
            f.Session.ReplaceState(state);
            var root = RunFormalRoot(FacilityEntryEffectSpecFactory.Activate(player, card.DefinitionId, card.ItemId,
                int.Parse(cell.LocalZoneId.Substring("city_cell_".Length)), "acceptance.warehouse_entry"));
            f.Record("正式设施 Lua 入口边界", new { root, card.ItemId, card.DefinitionId,
                scope = "实际目录载具仓库实例的正式 Activate 入口；设施位置为受控制备，不冒充本测试走过建造支付" });
            yield return Wait(() => f.Request?.CandidateIds.Contains("vehicle.explore") == true, "真实仓库能力选择页未出现");
            var index = f.Request.CandidateIds.IndexOf("vehicle.explore");
            var choice = Active("EffectDialogOptionRowView").Single(row => row.gameObject.name == "Option " + index);
            yield return f.Click(ButtonObject(choice, "Button"), "载具仓库_正式能力选择探索");
            var shell = Active("EffectDialogShellView").Single();
            var confirm = shell.GetComponentsInChildren<Button>().Single(button => button.gameObject.name == "Confirm Selection");
            yield return f.Click(confirm.gameObject, "载具仓库_确认正式能力分支");
            yield return Wait(() => f.Request?.InteractionTypeId == "route_resource.selection" && RoutePresenter.IsActive,
                "正式 Lua ChooseExplore 必须进入共用地图而非旧选路文字菜单");
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && r.CandidateIds.Any(id =>
                id.StartsWith("explore.step", StringComparison.Ordinal) || id.StartsWith("explore.pay", StringComparison.Ordinal))), Is.False);
            Assert.That(RoutePresenter.Session.Purpose, Is.EqualTo(RouteSelectionPurpose.Exploration));
            yield return ChooseRoute("A2", "player:" + receiver);
            Assert.That(f.State.FindPlayer(player).Resources.GoldVoucher, Is.EqualTo(4));
            Assert.That((bool)Call(f.Gameplay, "TryHandleInteractionEscape"), Is.True);
            yield return Wait(() => f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status == EffectNodeStatus.Completed,
                "正式 Lua 共用探索确认前取消应完成父能力，不残留旧菜单");
            AssertBalance(player, 4, 0);
        }

        private IEnumerator PrepareScenario(int player, int receiver, int gold, bool harvest)
        {
            var state = PrepareScenarioState(f.State, player, receiver, gold, harvest);
            f.Record("受控规则局面制备", new { player, receiver, gold, harvest, start = "A-01", free = new[] { "A1" },
                paid = new[] { "A2", "D1" }, prunedBranch = "B1", target = TargetLocation,
                scope = "从真实开局副本调整已有 raw 实体位置、钱包和未抽取牌堆次序；不是正常开局自然经历，不写 receipt，不改当前内容定义和哈希" });
            f.Session.ReplaceState(state);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            yield return Settle();
        }

        internal static GameState PrepareScenarioState(GameState source, int player, int receiver, int gold, bool harvest)
        {
            var state = GameStateCloneService.DeepClone(source);
            Assert.That(state.Map.RoadRouteIds, Is.Empty, "该四人新局夹具不应已有公路；不能静默改变免费路合同");
            var query = new ItemZoneQuery();
            foreach (var item in state.ItemZones.Items.Where(i => query.IsInfluence(state, i.ItemId)).ToArray())
            {
                Assert.That(item.OriginalSupplyZoneId, Is.Not.Empty);
                Relocate(state, item, state.ItemZones.Zones.Single(z => z.ZoneId == item.OriginalSupplyZoneId));
            }
            var orderedPlayers = new[] { player }.Concat(state.Players.Select(p => p.PlayerId).Where(id => id != player)).ToArray();
            var docks = new[] { "A-01", "B-01", "C-01", "G-01" };
            for (int i = 0; i < orderedPlayers.Length; i++)
            {
                var city = state.ItemZones.Items.Single(item => item.PhysicalKind == ItemPhysicalKind.MobileCity && item.OwnerSubject.PlayerId == orderedPlayers[i]);
                Relocate(state, city, MapZone(state, "core.zone.map_location", docks[i], "core.zone.city_dock"));
                if (!state.Map.OpenLocationIds.Contains(docks[i])) state.Map.OpenLocationIds.Add(docks[i]);
            }
            PlaceScenarioInfluence(state, player, "A1", true);
            PlaceScenarioInfluence(state, receiver, "A2", true);
            PlaceScenarioInfluence(state, receiver, "D1", true);
            state.FindPlayer(player).Resources.GoldVoucher = gold;
            // 只移动现有实体与牌序，不改目录、内容哈希、Effect receipt 或已注册 Lua。
            var tokenTargets = harvest ? new[] { "A-03", TargetLocation } : Array.Empty<string>();
            if (harvest)
            {
                var tokens = state.ItemZones.Items.Where(i => i.TypeId == "core.resource_point_token").Take(2).ToArray();
                Assert.That(tokens.Length, Is.EqualTo(2));
                for (int i = 0; i < tokenTargets.Length; i++)
                {
                    var zone = MapZone(state, "core.zone.map_location", tokenTargets[i], "core.zone.resource_point_markers");
                    Assert.That(state.ItemZones.Items.Any(item => item.ZoneId == zone.ZoneId), Is.False, "采集目标需原为空，不覆盖真实实体");
                    Relocate(state, tokens[i], zone);
                    tokens[i].LocalState = ObjectValue("resourceType", NormalizedValue.CreateInteger((int)ResourceType.Iron), "amount", NormalizedValue.CreateInteger(1));
                    if (!state.Map.OpenLocationIds.Contains(tokenTargets[i])) state.Map.OpenLocationIds.Add(tokenTargets[i]);
                    PlaceScenarioInfluence(state, player, tokenTargets[i], false);
                }
            }
            else
            {
                Assert.That(state.Map.ResourceTokens.Any(t => t.LocationId == TargetLocation), Is.False,
                    "D-02 是本测试明确未揭示目标；不能拿已有资源点代替探索");
                var eventCard = state.ItemZones.Items.Single(i => i.DefinitionId == EnterpriseEvent);
                var eventDeck = state.ItemZones.Zones.Single(z => z.LocalZoneId == "event_yellow");
                Assert.That(eventCard.ZoneId, Is.EqualTo(eventDeck.ZoneId));
                var order = state.ItemZones.Orders.Single(o => o.ZoneId == eventDeck.ZoneId);
                order.ItemIds.Remove(eventCard.ItemId); order.ItemIds.Insert(0, eventCard.ItemId); order.OrderRevision++;
            }
            Assert.That(query.TryValidate(state, out var reason), Is.True, reason);
            Assert.That(state.ContentPackHash, Is.EqualTo(source.ContentPackHash));
            return state;
        }

        private static ZoneInstanceState MapZone(GameState state, string parentTemplate, string id, string childTemplate)
        {
            var parent = state.ItemZones.Zones.Single(z => z.TemplateId == parentTemplate && z.LocalZoneId == id);
            return state.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == childTemplate);
        }

        private static void Relocate(GameState state, ItemInstanceState item, ZoneInstanceState zone)
        {
            foreach (var order in state.ItemZones.Orders)
                if (order.ItemIds.Remove(item.ItemId)) order.OrderRevision++;
            item.ZoneId = zone.ZoneId;
            item.SlotId = zone.LegalSlotIds.FirstOrDefault(id => !state.ItemZones.Items.Any(other => other != item && other.ZoneId == zone.ZoneId && other.SlotId == id)) ?? string.Empty;
            var destinationOrder = state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == zone.ZoneId);
            if (destinationOrder != null) { destinationOrder.ItemIds.Add(item.ItemId); destinationOrder.OrderRevision++; }
        }

        private static void PlaceScenarioInfluence(GameState state, int player, string target, bool route)
        {
            var supply = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.player_marker_supply" && z.OwnerPlayerId == player);
            var marker = state.ItemZones.Items.First(i => i.ZoneId == supply.ZoneId && i.OwnerSubject.PlayerId == player && i.PhysicalKind == ItemPhysicalKind.Cube);
            Relocate(state, marker, MapZone(state, route ? "core.zone.map_route" : "core.zone.map_location", target, "core.zone.influence_slots"));
        }

        private IEnumerator OpenMainExplore()
        {
            yield return f.Click(ButtonObject(Read(f.Hud, "ActionPanelView"), "ExploreButton"), "真实主要行动_探索");
            yield return Wait(() => f.Request?.InteractionTypeId == "route_resource.selection" && RoutePresenter.IsActive, "共用探索界面未绑定");
            Assert.That(RoutePresenter.Session.Purpose, Is.EqualTo(RouteSelectionPurpose.Exploration));
        }

        private IEnumerator ChooseRoute(string route, string recipient)
        {
            var before = GameStateCloneService.DeepClone(f.State);
            using (var counter = new RuleSubmitCounter())
            {
                var target = MapTarget("Route", route);
                Assert.That(target, Is.Not.Null, "实际航道不存在：" + route);
                yield return f.Click(target.gameObject, "自选航道_" + route);
                yield return Wait(() => Active("EventChoiceDialogView").Length == 1, "收费航道未打开既有收款页");
                var dialog = Active("EventChoiceDialogView").Single();
                var option = RoutePresenter.Projection.RouteOptions.Single(r => r.RouteId == route);
                Assert.That(option.EligibleRecipients.Any(r => r.SubjectKey == recipient), Is.True);
                var button = recipient == "bank" ? (Button)Read(dialog, "ResourcePaymentBankButton") :
                    ((RectTransform)Read(dialog, "ResourcePaymentRecipientHost")).GetComponentsInChildren<Button>().Single(b =>
                        b.gameObject.name == "Route Recipient " + recipient || b.transform.parent.name == "Route Recipient " + recipient);
                yield return f.Click(button.gameObject, "收款人_" + recipient);
                Assert.That(counter.Calls, Is.Zero);
            }
            Assert.That(RoutePresenter.Draft.ConfirmedRouteIds, Does.Contain(route));
            Assert.That(GameStateCloneService.AreEquivalent(before, f.State), Is.True, "路线编辑不写 Host");
        }

        private IEnumerator ConfirmRoute()
        {
            Assert.That(RoutePresenter.CanConfirm, Is.True, RoutePresenter.Projection?.Preview?.FinalizationValidation.Reason);
            using (var counter = new RuleSubmitCounter())
            {
                yield return f.Click(ButtonObject(f.Frame, "EndActionButton"), "一次确认共用选路");
                Assert.That(counter.Calls, Is.EqualTo(1), "最终选路只允许一次 Host 提交");
            }
        }

        private string RunFormalRoot(EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(f.State, Registry);
            var root = engine.CreateRoot(spec);
            var report = engine.RunUntilQuiescent();
            Assert.That(report.Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            return root;
        }

        private static NormalizedValue ObjectValue(params object[] fields)
        {
            var values = new List<NormalizedValueEntry>();
            for (int i = 0; i < fields.Length; i += 2) values.Add(new NormalizedValueEntry { Name = (string)fields[i], Value = (NormalizedValue)fields[i + 1] });
            return NormalizedValue.CreateObject(values);
        }

        private static GameCommand Answer(InteractionRequest request, IReadOnlyList<string> ids) => EffectInteractionCommands.Answer(
            InteractionRequestProjector.ProjectForPlayer(request, request.AnsweringPlayerId), request.AnsweringPlayerId, ids);

        private void AssertBalance(int player, int available, int reserved)
        {
            var balance = RouteTollEscrowService.QueryBalance(f.State, player);
            Assert.That(balance.Available, Is.EqualTo(available));
            Assert.That(balance.Reserved, Is.EqualTo(reserved));
            Assert.That(balance.Owned, Is.EqualTo(available + reserved));
            var view = GameStateViewProjector.ProjectForPlayer(f.State, player).Players.Single(p => p.PlayerId == player);
            Assert.That(view.AvailableGoldVoucher, Is.EqualTo(available));
            Assert.That(view.ReservedGoldVoucher, Is.EqualTo(reserved));
        }

        private string BusinessSnapshot() => JsonConvert.SerializeObject(new
        {
            f.State.Players, f.State.ItemZones, f.State.Map, f.State.Decks,
            receipts = f.State.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == ExplorationEffectTypeIds.Explore)
                .Select(n => new { n.EffectId, n.FlowStage, n.Status, n.ContinuationData, n.NormalizedResult })
        });

        private string EventDeckOrder() => JsonConvert.SerializeObject(f.State.ItemZones.Orders.Where(order =>
            f.State.ItemZones.Zones.Any(z => z.ZoneId == order.ZoneId && z.LocalZoneId.StartsWith("event_", StringComparison.Ordinal))));

        private void AssertRejectedDuplicate(GameCommand command, string label)
        {
            var before = BusinessSnapshot();
            var result = f.Session.Submit(command);
            Assert.That(result.Succeeded, Is.False, label);
            Assert.That(BusinessSnapshot(), Is.EqualTo(before), label + "改变了业务结果");
            f.Record(label, new { result.Succeeded, result.Validation });
        }

        private IEnumerator RestoreArchive(string label)
        {
            var archive = HostRecoveryService.CreateArchive(f.State);
            var json = HostRecoveryService.SerializeArchive(archive);
            Directory.CreateDirectory(f.DirectoryPath);
            File.WriteAllText(Path.Combine(f.DirectoryPath, label + ".archive.json"), json);
            var recovered = HostRecoveryService.RecoverJson(json, Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(recovered.State.ContentPackHash, Is.EqualTo(f.State.ContentPackHash));
            f.Session.ReplaceState(recovered.State);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            yield return Settle();
            f.Record("正式存档恢复", new { label, recovered.Succeeded, recovered.Diagnostic, f.State.ContentPackHash });
        }

        private void ObserveRewardBeforeSettlement(int player, int receiver, int receiverBefore, int reward)
        {
            var branch = GameStateCloneService.DeepClone(f.State);
            var request = branch.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId);
            var engine = new EffectTreeExecutor(branch, Registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), player, request.StateRevision,
                NormalizedValue.CreateString(EnterpriseEvent + ":option:1"), out var reason), Is.True, reason);
            var samples = new List<int[]>();
            var observationStates = new List<GameState>();
            for (int i = 0; i < 1000; i++)
            {
                var balance = RouteTollEscrowService.QueryBalance(branch, player);
                samples.Add(new[] { balance.Available, balance.Reserved, branch.FindPlayer(receiver).Resources.GoldVoucher });
                if (balance.Available == reward && balance.Reserved == 4)
                    observationStates.Add(GameStateCloneService.DeepClone(branch));
                if (balance.Reserved == 0) break;
                Assert.That(engine.Advance(), Is.True, "逐步规则观察未到交付前即停滞：" + branch.EffectRuntime.LastFaultMessage);
                Assert.That(branch.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), branch.EffectRuntime.LastFaultMessage);
            }
            Assert.That(samples.Any(s => s[0] == 0 && s[1] == 4 && s[2] == receiverBefore), Is.True);
            Assert.That(samples.Any(s => s[0] == reward && s[1] == 4 && s[2] == receiverBefore), Is.True, "奖励到账时路费仍在托管且收款方尚未入账");
            Assert.That(samples.Last(), Is.EqualTo(new[] { reward, 0, receiverBefore + 4 }));
            Directory.CreateDirectory(f.DirectoryPath);
            File.WriteAllText(Path.Combine(f.DirectoryPath, "奖励已到账_仍held.archive.json"), HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(observationStates.First())));
            f.Record("相同held状态独立规则分支逐节点观察", new { samples,
                scope = "真实 TrySubmitInteraction/Advance，不修改内容或 receipt；记录中间提交点，不冒充 UI 主分支逐帧显示" });
        }

        private IEnumerator CompleteRequiredInfluenceInputs()
        {
            for (int i = 0; i < 12 && f.Request != null; i++)
            {
                var request = f.Request;
                Assert.That(request.InteractionTypeId, Is.EqualTo(EventCardEffectExecutor.InfluenceInteractionTypeId),
                    "出现额外必需响应，必须按实际页面补接，不能自动跳过：" + request.InteractionTypeId);
                foreach (var id in request.CandidateIds.Take(request.MinSelections))
                {
                    var target = MapTarget("InfluenceSlot", id);
                    Assert.That(target, Is.Not.Null, "实际影响力目标未显示");
                    yield return f.Click(target.gameObject, "事件必需影响力_选择实际目标");
                }
                if (f.Request?.GetStableInteractionId() == request.GetStableInteractionId())
                    yield return f.Click(ButtonObject(f.Frame, "EndActionButton"), "事件必需影响力_确认");
                yield return Wait(() => f.Request == null || f.Request.GetStableInteractionId() != request.GetStableInteractionId(), "影响力请求未完成");
            }
        }

        private object Coordinator => Read(f.Gameplay, "routeResourceSelection");
        private object Presenter => Read(Coordinator, "Presenter");
        private static object[] Values(object owner, string field) => ((IEnumerable)Read(owner, field)).Cast<object>().ToArray();
        private Component MapTarget(string kind, string id) => Active("MapButtonView").SingleOrDefault(target =>
            Read(target, "TargetKind").ToString() == kind && (string)Read(target, "TargetId") == id);

        [UnityTest]
        public IEnumerator MainExploreUsesSharedRequestAndOneHundredLocalEditsDoNotSubmitHost()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, _ => Settle());
            var beforeOpening = GameStateCloneService.DeepClone(f.State);
            var actionView = Read(f.Hud, "ActionPanelView");
            yield return f.Click(ButtonObject(actionView, "ExploreButton"), "主要行动_探索");
            yield return Wait(() => f.Request?.InteractionTypeId == "route_resource.selection", "主要行动探索必须进入正式共用选路请求");
            yield return Wait(() => Coordinator != null && (bool)Read(Presenter, "IsActive"), "正式共用地图输入未绑定");
            var request = f.Request;
            Assert.That(Read(Read(Presenter, "Session"), "Purpose").ToString(), Is.EqualTo("Exploration"));
            Assert.That(request.CandidateIds.All(id => id.StartsWith("rs1.", StringComparison.Ordinal)), Is.True);
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" &&
                (r.InteractionTypeId == "lua.choice" || r.CandidateIds.Any(id => id.StartsWith("explore.step", StringComparison.Ordinal)))), Is.False,
                "正式入口不能退回旧泛 Choice 选路菜单");
            var selectionStart = GameStateCloneService.DeepClone(f.State);
            var revision = f.State.EffectRuntime.StateRevision;
            var draftVersion = (int)Read(Read(Presenter, "Draft"), "LocalDraftVersion");
            var projection = Read(Presenter, "Projection");
            var free = Values(projection, "RouteOptions").Where(option => (bool)Read(option, "IsAutomaticallySelectedFreeRoute")).ToArray();
            foreach (var option in free)
            {
                var target = MapTarget("Route", (string)Read(option, "RouteId"));
                Assert.That(target, Is.Not.Null);
                Assert.That((bool)Read(target, "IsSelected"), Is.True, "免费闭包必须在真实地图上默认高亮");
                Assert.That(((SpriteRenderer)Read(target, "Image")).enabled, Is.True);
            }
            var paid = Values(projection, "RouteOptions").FirstOrDefault(option =>
                Read(option, "FreeReason").ToString() == "None" && (bool)Read(option, "IsFrontier") &&
                Values(option, "EligibleRecipients").Length > 0 && MapTarget("Route", (string)Read(option, "RouteId")) != null &&
                HotseatUiInputDriver.TryPoint(MapTarget("Route", (string)Read(option, "RouteId")).gameObject, out _, out _));
            Assert.That(paid, Is.Not.Null, "本局必须提供实际可点的收费边界航道，不能以零次编辑通过");
            var routeId = (string)Read(paid, "RouteId");
            var recipient = Values(paid, "EligibleRecipients").First();
            var subjectKey = (string)Read(recipient, "SubjectKey");
            for (int i = 0; i < 50; i++)
            {
                // 每轮两个真实输入：航道→收款人。按小窗口分别计数避免 Recorder 容量溢出。
                using (var submits = new RuleSubmitCounter())
                {
                    yield return f.Click(MapTarget("Route", routeId).gameObject, "本地选路_" + i);
                    var dialog = Active("EventChoiceDialogView").Single();
                    Assert.That((string)Read(Read(Presenter, "Draft"), "EditingRouteId"), Is.EqualTo(routeId));
                    var button = subjectKey == "bank" ? (Button)Read(dialog, "ResourcePaymentBankButton") :
                        ((RectTransform)Read(dialog, "ResourcePaymentRecipientHost")).GetComponentsInChildren<Button>()
                            .Single(b => b.gameObject.name == "Route Recipient " + subjectKey ||
                                b.transform.parent.name == "Route Recipient " + subjectKey);
                    yield return f.Click(button.gameObject, "本地收款人_" + i);
                    Assert.That(submits.Calls, Is.Zero, "点击地图和收款人只能修改本地草稿");
                }
                Assert.That(f.Request.GetStableInteractionId(), Is.EqualTo(request.GetStableInteractionId()));
                Assert.That(f.State.EffectRuntime.StateRevision, Is.EqualTo(revision));
                Assert.That(GameStateCloneService.AreEquivalent(f.State, selectionStart), Is.True,
                    "本地编辑不得改变钱包、地图、牌堆、行动、审计或托管");
            }
            Assert.That((int)Read(Read(Presenter, "Draft"), "LocalDraftVersion") - draftVersion, Is.EqualTo(100));
            f.Record("正式探索本地百次编辑", new { routeId, subjectKey, revision, inputCount = 100,
                freeRoutes = free.Select(option => (string)Read(option, "RouteId")).ToArray(),
                requestId = request.GetStableInteractionId(), request.CandidateIds,
                scope = "真实主要行动入口、共用地图和收款页；本方法止于确认前取消，不覆盖事件与最终托管交付" });
            yield return f.Capture("正式探索百次本地编辑后", (Component)f.Frame);
            using (var submits = new RuleSubmitCounter())
            {
                Assert.That((bool)Call(f.Gameplay, "TryHandleInteractionEscape"), Is.True,
                    "调用生产 Esc 路由；不是 OS 键盘输入，也不直接调用 Presenter.Cancel");
                yield return Wait(() => f.Request == null, "确认前取消未关闭正式请求");
                yield return Settle();
                Assert.That(submits.Calls, Is.EqualTo(1), "整体取消仅有一次正式回答");
            }
            Assert.That(f.State.Players.Select(p => p.Resources.GoldVoucher),
                Is.EqualTo(beforeOpening.Players.Select(p => p.Resources.GoldVoucher)));
            Assert.That(f.State.Players.Select(p => p.CompletedMainActionsThisTurn),
                Is.EqualTo(beforeOpening.Players.Select(p => p.CompletedMainActionsThisTurn)));
            Assert.That(JsonConvert.SerializeObject(f.State.ItemZones), Is.EqualTo(JsonConvert.SerializeObject(beforeOpening.ItemZones)),
                "确认前取消不能改变真实地图实体或任何牌堆顺序");
        }

        [UnityTearDown]
        public IEnumerator RestoreLivePresentation()
        {
            if (f?.Gameplay != null)
            {
                Call(f.Frame, "SetInteractionMessage", string.Empty, null);
                Call(f.Gameplay, "SynchronizeInteractionFromState");
                yield return Settle();
            }
        }

        [UnityTest]
        public IEnumerator PublicReservationSummaryRemainsVisibleWithLongStatusAtThreeWindowSizes()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, _ => Settle());
            var original = GameStateCloneService.DeepClone(f.State);
            var displayState = GameStateCloneService.DeepClone(f.State);
            var local = (int)Read(f.Gameplay, "localPlayerId");
            var action = (ActionPanelViewModel)Call(Read(f.Gameplay, "turnActionPresenter"), "BuildActionPanelViewModel");
            var summary = (Text)Read(f.Frame, "summaryText");
            var shortSummary = (Text)Read(f.Frame, "shortSummaryText");
            var reservationFormat = (string)Read(f.Frame, "voucherReservationPlayerFormat");
            var separator = (string)Read(f.Frame, "voucherReservationPlayerSeparator");
            var samples = new[] { new[] { 0, 4 }, new[] { 2, 4 }, new[] { 2, 0 } };
            // 这是公开余额投影的显示压力测试。没有制造 Host escrow，不能代替托管/奖励/交付规则链。
            f.Record("预留摘要显示测试边界", new
            {
                scope = "真实场景与当前 HUD 资产；受控显示副本和公开 PlayerView 输入。未修改正式 Session.State，未称为完整探索链。",
                source = HotseatUiInputDriver.PathOf(((Component)f.Frame).gameObject),
                samples, resolutions = new[] { "1920x1080", "1366x768", "1024x768" }
            });
            foreach (var size in new[] { new Vector2Int(1920, 1080), new Vector2Int(1366, 768), new Vector2Int(1024, 768) })
            {
                yield return f.Resolution(size);
                foreach (var sample in samples)
                {
                    displayState.FindPlayer(local).Resources.GoldVoucher = sample[0];
                    var publicView = GameStateViewProjector.ProjectForPlayer(displayState, local);
                    foreach (var player in publicView.Players)
                    {
                        SetBalance(player, "AvailableGoldVoucher", player.PlayerId == local ? sample[0] : player.Resources.GoldVoucher);
                        SetBalance(player, "ReservedGoldVoucher", player.PlayerId == local ? sample[1] : 0);
                    }
                    var expected = string.Join(separator, publicView.Players.Select(player => string.Format(reservationFormat,
                        player.PlayerId, Read(player, "AvailableGoldVoucher"), Read(player, "ReservedGoldVoucher"))));
                    Call(f.Frame, "Refresh", displayState, action, action.StatusText, null, publicView);
                    const string longStatus = "目标状态发生变化，请检查已确认航道、收款人和所选资源点后重新确认本次操作。";
                    Call(f.Frame, "SetInteractionMessage", longStatus, null);
                    yield return Settle();
                    Assert.That(summary.text, Is.EqualTo(shortSummary.text), "长短摘要必须消费同一公开余额");
                    Assert.That(summary.text, Does.Contain(longStatus), "追加余额不得覆盖动态错误");
                    if (sample[1] > 0) Assert.That(summary.text, Does.Contain(expected));
                    else Assert.That((string)Read(f.Frame, "voucherReservationSummary"), Is.Null.Or.Empty,
                        "公开 held 清零后必须移除预留摘要");
                    var shown = new[] { summary, shortSummary }.Where(t => t.gameObject.activeInHierarchy).ToArray();
                    Assert.That(shown, Is.Not.Empty, "摘要不能只赋值给隐藏 Text");
                    foreach (var text in shown) AssertFullyRendered(text);
                    var values = (Text[])Read(f.Frame, "resourceValueTexts");
                    Assert.That(values[4].text, Is.EqualTo(sample[0].ToString()), "窄金券数字仍显示可用金额");
                    f.Record("公开余额摘要实际布局", new { available = sample[0], reserved = sample[1], expected,
                        localVoucher = values[4].text, visible = shown.Select(text => new
                        {
                            text.text, text.cachedTextGenerator.characterCountVisible, text.cachedTextGenerator.lineCount,
                            geometry = Geometry(text.rectTransform)
                        }).ToArray() });
                    yield return f.Capture("公开余额_" + sample[0] + "_" + sample[1], (Component)f.Frame);

                    if (sample[1] > 0)
                    {
                        // 使用生产消息过期入口，不操纵计时字段，也不把提醒设成永久。
                        yield return new WaitForSecondsRealtime(3.1f);
                        Call(f.Frame, "UpdateInteractionMessage", false);
                        yield return Settle();
                        Assert.That(summary.text, Does.Not.Contain(longStatus));
                        Assert.That(summary.text, Does.Contain(expected), "临时提示过期不能带走 held 摘要");
                    }
                    Assert.That(GameStateCloneService.AreEquivalent(f.State, original), Is.True,
                        "显示测试不应向 Host 写入钱包或托管数据");
                }
            }
        }

        private static void SetBalance(object player, string field, int value)
        {
            var target = player.GetType().GetField(field);
            Assert.That(target, Is.Not.Null, "公开余额字段尚未接入：" + field);
            target.SetValue(player, value);
        }

        private static void AssertFullyRendered(Text text)
        {
            Assert.That(text.color.a, Is.GreaterThan(0));
            Assert.That(text.GetComponentsInParent<CanvasGroup>().All(g => g.alpha > 0), Is.True);
            var generator = text.cachedTextGenerator;
            Assert.That(generator.vertexCount, Is.GreaterThan(4), "必须实际生成字形");
            // 输入不含富文本；空格/换行不要求生成四个顶点，但正文不能被 Truncate 裁掉。
            var required = text.text.Count(c => !char.IsControl(c));
            Assert.That(generator.characterCountVisible, Is.GreaterThanOrEqualTo(required), "实际生成器截断了摘要文字");
            var local = text.rectTransform.rect;
            var screen = new Rect(0, 0, Screen.width, Screen.height);
            for (int i = 0; i < generator.vertexCount - 4; i++)
            {
                var point = generator.verts[i].position / text.pixelsPerUnit;
                Assert.That(point.x, Is.InRange(local.xMin - 2, local.xMax + 2), "字形越出当前 Text 横向范围");
                Assert.That(point.y, Is.InRange(local.yMin - 2, local.yMax + 2), "字形越出当前 Text 纵向范围");
                var canvas = text.GetComponentInParent<Canvas>().rootCanvas;
                var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                var position = RectTransformUtility.WorldToScreenPoint(camera, text.rectTransform.TransformPoint(point));
                Assert.That(screen.Contains(position), Is.True, "实际字形越出屏幕");
                foreach (var mask in text.GetComponentsInParent<RectMask2D>().Where(m => m.isActiveAndEnabled))
                    Assert.That(ScreenRect(mask.rectTransform).Contains(position), Is.True, "实际字形被祖先遮罩裁切");
            }
        }
    }
}
