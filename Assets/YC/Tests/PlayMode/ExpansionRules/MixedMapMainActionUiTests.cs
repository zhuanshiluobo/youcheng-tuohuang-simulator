using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Multiplayer;
using YC.Presentation.Workflows;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 只走房间、Bootstrapper、正式主链和实际地图输入；诊断只写测试日志，不修改生产/资产。
    [Timeout(300000)]
    public sealed class MixedMapMainActionUiTests
    {
        private GameplayPageAcceptanceFixture f;
        private MapQueryService map;
        private int actor, blue, yellow, snapshotIndex, eventPages;
        private string stage;
        private bool completed, poolsChecked;
        private RouteResourceSelectionPresenter Routes => (RouteResourceSelectionPresenter)Read(Read(f.Gameplay, "routeResourceSelection"), "Presenter");
        private EffectRegistry Registry => (EffectRegistry)Read(((IEnumerable)Read(f.Session, "commandHandlers"))
            .Cast<object>().OfType<AnswerInteractionCommandHandler>().Single(), "registry");
        private int Viewer => (int)Read(f.Gameplay, "localPlayerId");
        private PlayerState Player => f.State.FindPlayer(actor);
        private int MoveCost => new TravelCostService(map).GetCityMoveBaseCost().OriginiumShard;

        [UnityTearDown]
        public IEnumerator KeepFailureEvidence()
        {
            if (f?.Session == null || f.Gameplay == null) yield break;
            Snapshot(completed ? "限定方法完成" : "首次失败终态");
            f.Record("限定方法状态", new { stage, completed, poolsChecked, eventPages });
            if (!completed) yield return f.Capture("失败现场", (Component)f.Hud);
        }

        [UnityTest]
        public IEnumerator CityMoveOnFourPlayerMapCommitsPositionCostAndAction()
        {
            yield return StartMixedMap();
            actor = blue;
            if (ControlledResourceSetup)
                yield return RestoreCheckpoint(Environment.GetEnvironmentVariable("YC_MIXED_MAP_OPENING_ARCHIVE"));
            yield return AdvanceToCheckpoint();
            AssertCheckpoint("C-02");
            yield return EnsureMovementResourcesForPerformance();
            Assert.That(Player.Resources.OriginiumShard, Is.GreaterThanOrEqualTo(MoveCost), "正式前置碎片不足，禁止补钱");
            string checkpoint = Snapshot("蓝色第二回合第二行动轮");
            // 用户未确认已揭示目标失败；这里只做与未揭示目标的小对照。
            Assert.That(f.State.Map.ResourceTokens.Any(t => t.LocationId == "C-01"), Is.True);
            yield return MoveThroughUi("C-01", true, "已揭示相邻点对照");
            yield return RestoreCheckpoint(checkpoint);
            AssertCheckpoint("C-02");
            Assert.That(f.State.Map.ResourceTokens.Any(t => t.LocationId == "C-03"), Is.False);
            yield return MoveThroughUi("C-03", true, "未揭示相邻点");
            completed = true;
        }

        [UnityTest]
        public IEnumerator ExplorationOnFourPlayerMapUsesOwnFreeRouteAndCompletesEvent()
        {
            yield return StartMixedMap();
            actor = yellow;
            // 本轮已真实产生的开局存档固定前置资源；通过正式恢复，不改任何状态字段。
            string opening = Environment.GetEnvironmentVariable("YC_MIXED_MAP_OPENING_ARCHIVE");
            Assert.That(!string.IsNullOrEmpty(opening) && File.Exists(opening), Is.True, "须提供本轮真实房间开局archive作为明确复现输入");
            var identities = f.State.Players.Select(p => p.PlayerId + ":" + p.Color).OrderBy(id => id).ToArray();
            yield return RestoreCheckpoint(opening);
            Assert.That(f.State.Players.Select(p => p.PlayerId + ":" + p.Color).OrderBy(id => id), Is.EqualTo(identities));
            CheckConfiguration();
            Assert.That(f.State.Round, Is.EqualTo(1));
            Assert.That(f.State.Phase, Is.EqualTo(GamePhase.ActionRound1));
            Assert.That(Player.CityLocationId, Is.EqualTo("A-02"));
            yield return EnsureMovementResourcesForPerformance();
            Assert.That(Player.Resources.OriginiumShard, Is.GreaterThanOrEqualTo(MoveCost));
            f.Record("真实开局archive作为前置", new { opening, f.State.GameId, f.State.EventDeckSeed,
                boundary = "恢复同版本本轮实际房间开局；从第一行动轮继续正式主链，不冒称本次新房间随机结果或原用户存档" });
            yield return AdvanceToCheckpoint();
            AssertCheckpoint("D-02");
            Assert.That(f.State.Map.Influences.Any(i => i.PlayerId == actor && i.RouteId == "D1"), Is.True, "D1须来自正式初放/部署");
            Assert.That(f.State.Map.ResourceTokens.Any(t => t.LocationId == "D-01"), Is.False);
            Stage("正常探索入口");
            Snapshot("探索入口前");
            var beforeNodes = NodeIds();
            int actions = Player.CompletedMainActionsThisTurn, remaining = Player.RemainingMainActionsThisTurn, events = eventPages;
            using (var count = new RuleSubmitCounter())
            {
                yield return MeasuredClick(ActionButton("ExploreButton"), "探索开始选点");
                Assert.That(count.Calls, Is.EqualTo(1));
            }
            Snapshot("探索入口返回");
            Assert.That(f.Request?.InteractionTypeId, Is.EqualTo("route_resource.selection"), "入口拒绝或未生成选路请求");
            yield return Wait(() => Routes.IsActive, "正式探索地图没有绑定");
            Assert.That(Routes.Session.Purpose, Is.EqualTo(RouteSelectionPurpose.Exploration));
            string candidate = RouteSelectionAnswerCodec.Encode(Routes.RequestBinding.ScopeKey, RouteSelectionCandidateKind.Location, "D-01");
            Assert.That(f.Request.CandidateIds, Does.Contain(candidate));
            var free = Routes.Projection.RouteOptions.Single(r => r.RouteId == "D1");
            Assert.That(free.FreeReason, Is.EqualTo(RouteFreeReason.OwnInfluence));
            Assert.That(free.IsAutomaticallySelectedFreeRoute, Is.True);
            Assert.That(Routes.Projection.LocationOptions.Single(o => o.LocationId == "D-01").IsReachableInOriginalDraft, Is.True);
            Assert.That((bool)Read(MapTarget("Route", "D1"), "IsSelected"), Is.True);
            using (var count = new RuleSubmitCounter())
            {
                yield return MeasuredClick(MapTarget("Location", "D-01").gameObject, "探索第一次点目标");
                Assert.That(count.Calls, Is.Zero);
            }
            Assert.That(Routes.Draft.SelectedLocationIds, Is.EqualTo(new[] { "D-01" }));
            Assert.That(Routes.Draft.ConfirmedRouteIds, Is.Empty);
            Assert.That(Routes.Projection.Preview.PaymentPlan.TotalGoldVoucherAmount, Is.Zero);
            Assert.That(Routes.CanConfirm, Is.True, Routes.Projection.Preview.FinalizationValidation.Reason);
            Snapshot("免费D1目标确认前");
            yield return f.Capture("己方D1免费可选D01", (Component)f.Hud);
            string owner = f.Request.OwnerEffectId;
            using (var count = new RuleSubmitCounter())
            {
                yield return MeasuredClick(ButtonObject(f.Frame, "EndActionButton"), "探索确认提交");
                Assert.That(count.Calls, Is.EqualTo(1));
            }
            Snapshot("探索确认后事件断点");
            yield return AnswerVisibleEventChain();
            var explorer = f.State.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == owner && n.EffectTypeId == ExplorationEffectTypeIds.Explore);
            AssertCompleted(explorer.EffectId);
            Assert.That(eventPages, Is.GreaterThan(events), "必须实际看到并回答事件页");
            Assert.That(Player.CompletedMainActionsThisTurn, Is.EqualTo(actions + 1));
            Assert.That(Player.RemainingMainActionsThisTurn, Is.EqualTo(remaining - 1));
            Assert.That(f.State.Map.ResourceTokens.Count(t => t.LocationId == "D-01"), Is.EqualTo(1));
            Assert.That(f.State.Map.Influences.Any(i => i.PlayerId == actor && i.LocationId == "D-01"), Is.True);
            Assert.That(RouteTollEscrowService.QueryBalance(f.State, actor).Reserved, Is.Zero);
            Assert.That(RouteTollEscrowService.TryReadReceipt(explorer, out var receipt, out var reason), Is.True, reason);
            Assert.That(receipt.OriginalAmount, Is.Zero);
            Assert.That(receipt.State, Is.EqualTo("settled"));
            Assert.That(f.Request, Is.Null);
            f.Record("免费探索完成", new { owner, explorer.EffectId, receipt, newlyCreated = NewNodes(beforeNodes), Player.Resources });
            yield return f.Capture("免费探索事件完成", (Component)f.Hud);
            completed = true;
        }

        private IEnumerator StartMixedMap()
        {
            f = new GameplayPageAcceptanceFixture(); completed = poolsChecked = false;
            snapshotIndex = eventPages = actor = 0;
            Stage("三名玩家四人地图正式开局");
            yield return f.Start(3, _ => Settle(), firstEnterpriseCheck: CheckInitialPools,
                openingRequestCheck: AnswerOpeningChoice, mapId: "map-four-players");
            Assert.That(poolsChecked, Is.True);
            map = (MapQueryService)Read(f.Gameplay, "mapQuery");
            blue = f.State.Players.Single(p => p.Color == PlayerColor.Blue).PlayerId;
            yellow = f.State.Players.Single(p => p.Color == PlayerColor.Yellow).PlayerId;
            CheckConfiguration();
            Snapshot("正式开局进入第一行动轮");
        }

        private IEnumerator CheckInitialPools(Component page)
        {
            CheckConfiguration();
            foreach (var pair in new[] { (EventColor.Green, 6), (EventColor.Yellow, 10), (EventColor.Red, 6) })
            {
                string role = "event_" + pair.Item1.ToString().ToLowerInvariant();
                var entries = f.State.SetupRecipe.Entries.Where(e => e.InitialZoneRole == role).ToArray();
                Assert.That(entries.Sum(e => e.Quantity), Is.EqualTo(pair.Item2), "四人地图事件配方不得按三名玩家裁减：" + role);
                var definitions = new HashSet<string>(entries.Select(e => e.DefinitionId));
                Assert.That(f.State.ItemZones.Items.Count(i => definitions.Contains(i.DefinitionId)), Is.EqualTo(pair.Item2));
                Assert.That(new EventDeckService().RemainingCount(f.State.Decks, pair.Item1), Is.EqualTo(pair.Item2));
            }
            poolsChecked = true;
            Snapshot("首次企业选择前完整事件池6_10_6");
            yield return null;
        }

        private void CheckConfiguration()
        {
            var context = T("GameLaunchContext").GetProperty("Instance").GetValue(null);
            var provider = HotseatUiInputDriver.RuntimeType("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider");
            var room = (RoomState)Call(provider.GetMethod("GetActive").Invoke(null, null), "GetCurrentRoom");
            var query = (MapQueryService)Read(f.Gameplay, "mapQuery");
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("SampleScene"));
            Assert.That((LaunchMode)Read(context, "Mode"), Is.EqualTo(LaunchMode.Local));
            Assert.That(room.HasStarted && room.PlayerCount == 3, Is.True);
            foreach (var config in new[] { room.Configuration, (GameRuleConfiguration)Read(context, "RuleConfiguration"), f.State.RuleConfiguration })
            {
                Assert.That(config.PlayerCount, Is.EqualTo(3));
                Assert.That(config.MapId, Is.EqualTo("map-four-players"));
                Assert.That(config.EnabledExpansionIds, Is.EquivalentTo(new[] { "core", "enterprise" }));
            }
            Assert.That(f.State.MapId, Is.EqualTo(query.Map.MapId));
            Assert.That(query.Map.MapId, Is.EqualTo("map-four-players"));
            Assert.That(query.Map.Locations.Any(l => l.LocationId == "G-03"), Is.True);
            f.Record("人数地图与实际注入", new { room.Configuration, mapQuery = query.Map.MapId,
                f.State.LaunchPlayerOrder, f.State.EventDeckSeed, f.State.ContentPackHash });
        }

        private IEnumerator AnswerOpeningChoice(InteractionRequest request)
        {
            if (request.InteractionTypeId == "entrance.location")
            {
                var color = f.State.FindPlayer(request.AnsweringPlayerId).Color;
                string target = color == PlayerColor.Blue ? "C-01" : color == PlayerColor.Yellow ? "A-02" : "G-01";
                Assert.That(request.CandidateIds, Does.Contain(target));
                yield return SubmitAuxiliary(Answer(request, new[] { target }), "合法初放_" + color);
            }
            else if (request.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId ||
                request.InteractionTypeId == EventCardEffectExecutor.InfluenceInteractionTypeId || request.InteractionTypeId == "setup.influence.optional")
                yield return SubmitAuxiliary(Answer(request, ChooseCandidates(request)), "非待验开局合法选择");
        }

        private IEnumerator AdvanceToCheckpoint()
        {
            Stage("正式主链推进至第二回合第二行动轮");
            var influence = new InfluenceService(map);
            for (int guard = 0; guard < 140; guard++)
            {
                if (f.State.Round == 2 && f.State.Phase == GamePhase.ActionRound2 && f.State.CurrentPlayerId == actor && f.Request == null)
                    yield break;
                Assert.That(f.State.Round, Is.LessThanOrEqualTo(2), "错过目标窗口，禁止改回合回退");
                if (f.Request != null)
                {
                    var request = f.Request;
                    if (request.InteractionTypeId == "route_resource.selection")
                    {
                        yield return Wait(() => Routes.IsActive && Routes.Session.PlayerId == request.AnsweringPlayerId, "采集辅助须使用当前请求");
                        Assert.That(Routes.Session.Purpose, Is.EqualTo(RouteSelectionPurpose.NormalHarvest));
                        Assert.That(Routes.TryBuildAnswer(request.CandidateIds, false, out var choices), Is.True, "正式采集草稿不能确认");
                        yield return SubmitAuxiliary(Answer(request, choices), "第一回合普通采集合法回答");
                    }
                    else yield return SubmitAuxiliary(Answer(request, ChooseCandidates(request)), "非待验主链请求合法回答");
                    continue;
                }
                int current = f.State.CurrentPlayerId;
                var player = f.State.FindPlayer(current);
                Assert.That(player, Is.Not.Null);
                bool hasAction = (f.State.Phase == GamePhase.ActionRound1 || f.State.Phase == GamePhase.ActionRound2) && player.RemainingMainActionsThisTurn > 0;
                if (!hasAction)
                {
                    yield return SubmitAuxiliary(new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = current }, "正常结束窗口");
                    continue;
                }
                // 仅制备当前目标玩家；其他玩家正常部署/结束，所有位置存证，不冒称原截图逐位重演。
                string destination = current != actor ? null : actor == blue && player.CityLocationId == "C-01" ? "C-02" :
                    actor == yellow && player.CityLocationId == "A-02" ? "A-03" : actor == yellow && player.CityLocationId == "A-03" ? "D-02" : null;
                if (destination != null) yield return EnsureMovementResourcesForPerformance();
                if (destination != null && player.Resources.OriginiumShard >= MoveCost)
                    yield return MoveThroughUi(destination, false, "前置自然移动");
                else
                {
                    string slot = SafeInfluenceCandidates(current, influence.GetLegalPlacementSlotIds(f.State, current)).FirstOrDefault();
                    Assert.That(slot, Is.Not.Null, "辅助部署没有不占用待验目标的合法槽位");
                    yield return SubmitAuxiliary(new GameCommand { Kind = GameCommandKind.DeployInfluence, PlayerId = current, TargetId = slot }, "非待验合法部署");
                }
            }
            Assert.Fail("正式主链超过有限前置步骤，未到第二回合第二行动轮");
        }

        private void AssertCheckpoint(string location)
        {
            Snapshot("目标窗口前置核对");
            Assert.That(f.State.Round, Is.EqualTo(2));
            Assert.That(f.State.ActionRound, Is.EqualTo(2));
            Assert.That(f.State.Phase, Is.EqualTo(GamePhase.ActionRound2));
            Assert.That(f.State.CurrentPlayerId, Is.EqualTo(actor));
            Assert.That(Viewer, Is.EqualTo(actor));
            Assert.That(Player.CityLocationId, Is.EqualTo(location), "自然主链未到目标位置，禁止写位置修补");
            Assert.That(Player.RemainingMainActionsThisTurn, Is.GreaterThan(0));
            Assert.That(f.Request, Is.Null);
        }

        private bool ControlledResourceSetup => Environment.GetEnvironmentVariable("YC_MIXED_MAP_CONTROLLED_RESOURCE_SETUP") == "1";
        private IEnumerator EnsureMovementResourcesForPerformance()
        {
            if (!ControlledResourceSetup || Player.Resources.OriginiumShard >= MoveCost) yield break;
            Assert.That(f.Request, Is.Null);
            int before = Player.Resources.OriginiumShard, added = MoveCost - before;
            var engine = new EffectTreeExecutor(f.State, Registry);
            string effect = engine.CreateRoot(ResourceEffectSpecFactory.Gain(actor, ResourceType.OriginiumShard, added,
                "acceptance.performance_movement_setup"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(effect).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Player.Resources.OriginiumShard, Is.EqualTo(MoveCost));
            f.Record("性能复测受控资源起点", new { actor, before, added, effect, position = Player.CityLocationId,
                boundary = "仅显式环境开关启用；由正式Gain Effect补足下一次移动费用，未直写资源、位置、主链或历史。不代表原截图现场或自然资源可达性复现。" });
            Snapshot("合法Effect建立性能资源起点");
            Call(f.Gameplay, "SynchronizeInteractionFromState"); yield return Settle();
        }

        private IEnumerator MoveThroughUi(string target, bool checkpointBranch, string label)
        {
            Stage(label);
            int mover = f.State.CurrentPlayerId;
            Assert.That(Viewer, Is.EqualTo(mover));
            var player = f.State.FindPlayer(mover);
            var oldNodes = NodeIds();
            int beforeShard = player.Resources.OriginiumShard, actions = player.CompletedMainActionsThisTurn;
            int remaining = player.RemainingMainActionsThisTurn, events = eventPages;
            string source = player.CityLocationId;
            bool revealed = f.State.Map.ResourceTokens.Any(t => t.LocationId == target);
            Assert.That(map.GetAdjacentLocations(source).Any(l => l.LocationId == target), Is.True);
            Assert.That(beforeShard, Is.GreaterThanOrEqualTo(MoveCost));
            Snapshot(label + "_入口前");
            using (var count = new RuleSubmitCounter())
            {
                yield return MeasuredClick(ActionButton("MoveCityButton"), label + "_开始选点");
                Assert.That(count.Calls, Is.Zero);
                yield return MeasuredClick(MapTarget("Location", target).gameObject, label + "_第一次点目标");
                Assert.That(count.Calls, Is.Zero, "地图首击只能预选");
                Assert.That(f.State.FindPlayer(mover).CityLocationId, Is.EqualTo(source));
                yield return MeasuredClick(MapTarget("Location", target).gameObject, label + "_确认提交");
                Assert.That(count.Calls, Is.EqualTo(1));
            }
            Snapshot(label + "_确认返回");
            var move = NewNodes(oldNodes).SingleOrDefault(n => n.EffectTypeId == CityMoveEffectTypeIds.Move && n.PlayerId == mover);
            Assert.That(move, Is.Not.Null, "命令拒绝/回滚或未生成本次节点；现场已保存，不能以完成提示判通过");
            yield return AnswerVisibleEventChain();
            AssertCompleted(move.EffectId);
            var currentMove = Node(move.EffectId);
            Assert.That(Text(currentMove.NormalizedResult, "outcome"), Is.EqualTo("completed"));
            Assert.That(Text(currentMove.NormalizedResult, "postMoveContinuationOutcome"), Is.EqualTo("completed"));
            var nodes = NewNodes(oldNodes);
            var pay = nodes.Single(n => n.ParentEffectId == move.EffectId && n.EffectTypeId == ResourceEffectTypeIds.Pay && n.SourceId == "city_move.base_cost");
            Assert.That(pay.Status, Is.EqualTo(EffectNodeStatus.Completed), pay.FailureReason);
            Assert.That(Number(pay.NormalizedArguments, "amount"), Is.EqualTo(MoveCost));
            Assert.That(nodes.Count(n => n.ParentEffectId == move.EffectId && n.EffectTypeId == CityMoveEffectTypeIds.CommitPosition && n.Status == EffectNodeStatus.Completed), Is.EqualTo(1));
            var after = f.State.FindPlayer(mover);
            var city = f.State.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == mover);
            var dock = f.State.ItemZones.Zones.Single(z => z.ZoneId == city.ZoneId);
            Assert.That(dock.TemplateId, Is.EqualTo("core.zone.city_dock"));
            Assert.That(f.State.ItemZones.Zones.Single(z => z.ZoneId == dock.ParentId).LocalZoneId, Is.EqualTo(target));
            Assert.That(after.CityLocationId, Is.EqualTo(target));
            Assert.That(after.HasMovedCityThisRound, Is.True);
            Assert.That(after.CompletedMainActionsThisTurn, Is.EqualTo(actions + 1));
            Assert.That(after.RemainingMainActionsThisTurn, Is.EqualTo(remaining - 1));
            int delta = nodes.Where(n => n.Status == EffectNodeStatus.Completed && Text(n.NormalizedArguments, "resourceType") == ResourceType.OriginiumShard.ToString())
                .Sum(n => ResourceDelta(n, mover));
            Assert.That(after.Resources.OriginiumShard - beforeShard, Is.EqualTo(delta), "实际钱包须符合本次扣费和奖励结果");
            Assert.That(f.Request, Is.Null);
            if (checkpointBranch && !revealed) Assert.That(eventPages, Is.GreaterThan(events), "未揭示分支须记录实际事件页");
            f.Record("真实移动结算", new { label, mover, source, target, revealed, beforeShard, afterShard = after.Resources.OriginiumShard,
                delta, pay.EffectId, cost = MoveCost, action = currentMove, item = city, dock, checkpointBranch,
                boundary = "用户未确认已揭示目标失败；该分支仅为对照" });
            if (checkpointBranch) yield return f.Capture(label + "_实际位移完成", (Component)f.Hud);
        }

        private IEnumerator AnswerVisibleEventChain()
        {
            for (int guard = 0; f.Request != null && guard < 16; guard++)
            {
                var request = f.Request;
                string id = request.GetStableInteractionId();
                yield return Wait(() => Viewer == request.AnsweringPlayerId, "事件视角没有正常切换");
                Snapshot("事件或必需响应待答");
                if (request.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId)
                {
                    yield return Wait(() => Active("EventChoiceDialogView").Length == 1, "真实事件页未出现");
                    Assert.That(Read(Read(f.Gameplay, "interactionRequestRouter"), "activeRenderer").GetType().Name, Is.EqualTo("EventCardInteractionUiCoordinator"));
                    string choice = ChooseCandidates(request).Single();
                    int option = int.Parse(choice.Substring(choice.LastIndexOf(':') + 1));
                    var button = HotseatUiInputDriver.Marker("event.option." + option);
                    Assert.That(button, Is.Not.Null);
                    yield return f.Capture("实际事件待选_" + eventPages, Active("EventChoiceDialogView").Single());
                    using (var count = new RuleSubmitCounter())
                    {
                        yield return MeasuredClick(button, "实际事件选项");
                        Assert.That(count.Calls, Is.EqualTo(1));
                    }
                    eventPages++;
                }
                else if (request.InteractionTypeId == EventCardEffectExecutor.InfluenceInteractionTypeId)
                {
                    foreach (string candidate in ChooseCandidates(request))
                    {
                        using (var count = new RuleSubmitCounter())
                        {
                            yield return MeasuredClick(MapTarget("InfluenceSlot", candidate).gameObject, "事件影响力首次选点");
                            if (count.Calls == 0 && f.Request?.GetStableInteractionId() == id)
                                yield return MeasuredClick(MapTarget("InfluenceSlot", candidate).gameObject, "事件影响力确认");
                        }
                        if (f.Request?.GetStableInteractionId() != id) break;
                    }
                }
                else Assert.Fail("待验动作产生未接入的真实响应，保留断点而不跳过：" + request.InteractionTypeId);
                yield return Wait(() => f.Request == null || f.Request.GetStableInteractionId() != id, "事件请求未完成");
            }
            Assert.That(f.Request, Is.Null, "必需响应超过有限步骤");
        }

        private IReadOnlyList<string> ChooseCandidates(InteractionRequest request)
        {
            if (request.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId)
            {
                var card = EventCardDatabase.Get(f.State, Text(Node(request.OwnerEffectId).NormalizedResult, "cardId"));
                Assert.That(card, Is.Not.Null);
                return request.CandidateIds.OrderByDescending(id =>
                {
                    int index = int.Parse(id.Substring(id.LastIndexOf(':') + 1));
                    var reward = card.ChoiceRewards[index];
                    // 只在合法候选中优先碎片，不改牌序或奖励来制备局面。
                    return reward.OriginiumShard * 1000 + reward.GoldVoucher - card.ChoicePendingEffects[index].Count * 100;
                }).Take(1).ToArray();
            }
            if (request.InteractionTypeId == EventCardEffectExecutor.InfluenceInteractionTypeId || request.InteractionTypeId == "setup.influence.optional")
                return SafeInfluenceCandidates(request.AnsweringPlayerId, request.CandidateIds).Take(Math.Max(1, request.MinSelections)).ToArray();
            return request.CandidateIds.Take(request.MinSelections).ToArray();
        }

        private IEnumerable<string> SafeInfluenceCandidates(int player, IEnumerable<string> candidates)
        {
            bool ownYellow = f.State.FindPlayer(player).Color == PlayerColor.Yellow;
            var protectedSlots = new HashSet<string>(new[] { "D-01", "C-03" }.SelectMany(id => Enumerable.Range(0, 2).Select(i => InfluenceService.GetLocationSlotId(id, i))));
            var d1 = new HashSet<string>(Enumerable.Range(0, 2).Select(i => InfluenceService.GetRouteSlotId("D1", i)));
            return candidates.Where(id => !protectedSlots.Contains(id) && (ownYellow || !d1.Contains(id)))
                .OrderByDescending(id => ownYellow && d1.Contains(id)).ThenBy(id => id, StringComparer.Ordinal);
        }

        private IEnumerator SubmitAuxiliary(GameCommand command, string label)
        {
            var before = new { f.State.Round, f.State.ActionRound, f.State.Phase, f.State.CurrentPlayerId };
            var result = f.Session.Submit(command);
            f.Record("正式协议辅助推进", new { label, before, command, result.Succeeded, result.Validation,
                boundary = "非待验输入使用Session提交，不修改主链/资源/城市/牌序；不冒称鼠标全流程" });
            if (!result.Succeeded) Snapshot("辅助命令失败");
            Assert.That(result.Succeeded, Is.True, label + ":" + result.Validation?.Reason);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            yield return Settle();
        }

        private static GameCommand Answer(InteractionRequest request, IReadOnlyList<string> candidates) => EffectInteractionCommands.Answer(
            InteractionRequestProjector.ProjectForPlayer(request, request.AnsweringPlayerId), request.AnsweringPlayerId, candidates);
        private GameObject ActionButton(string name) => ButtonObject(Read(f.Hud, "ActionPanelView"), name);
        private static Component MapTarget(string kind, string id) => Active("MapButtonView").Single(t =>
            Read(t, "TargetKind").ToString() == kind && (string)Read(t, "TargetId") == id);

        // 与共享夹具相同的顶层射线 Down/Up/Click；计时不含快照、截图和只读字段采集。
        private IEnumerator MeasuredClick(GameObject target, string label)
        {
            Assert.That(target, Is.Not.Null, label);
            Assert.That(HotseatUiInputDriver.TryPoint(target, out var point, out _), Is.True, label + "没有可见输入点");
            var hit = Hit(point);
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            Assert.That(handler, Is.EqualTo(ExecuteEvents.GetEventHandler<IPointerClickHandler>(target)), label + "被遮挡");
            var before = new { f.State.Round, f.State.Phase, f.State.ActionRound, f.State.CurrentPlayerId,
                effectNodes = f.State.EffectRuntime.EffectNodes.Count, logs = f.State.Logs.Count };
            var data = new PointerEventData(EventSystem.current) { pointerId = -1, button = PointerEventData.InputButton.Left,
                position = point, pressPosition = point, eligibleForClick = true, clickCount = 1, clickTime = Time.unscaledTime,
                pointerCurrentRaycast = hit, pointerPressRaycast = hit };
            ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerEnterHandler);
            data.pointerPress = ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerDownHandler) ?? handler;
            yield return null;
            Assert.That(ExecuteEvents.GetEventHandler<IPointerClickHandler>(Hit(point).gameObject), Is.EqualTo(handler));
            long started = Stopwatch.GetTimestamp();
            ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler);
            long dispatched = Stopwatch.GetTimestamp();
            yield return Settle();
            long settled = Stopwatch.GetTimestamp();
            f.Record("实际入口耗时样本", new { label, before, dispatchMs = (dispatched - started) * 1000.0 / Stopwatch.Frequency,
                throughEightFramesMs = (settled - started) * 1000.0 / Stopwatch.Frequency,
                afterEffectNodes = f.State.EffectRuntime.EffectNodes.Count, afterLogs = f.State.Logs.Count,
                target = HotseatUiInputDriver.PathOf(target), hit = HotseatUiInputDriver.PathOf(hit.gameObject),
                boundary = "同步Up/Click派发与随后8个自然帧分别计时；单次样本，无P95/GC/全局采样，不称首次可交互帧" });
        }

        private void Stage(string value) { stage = value; f?.Record("阶段", new { stage }); }
        private HashSet<string> NodeIds() => new HashSet<string>(f.State.EffectRuntime.EffectNodes.Select(n => n.EffectId));
        private EffectNodeRuntimeState Node(string id) => f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
        private EffectNodeRuntimeState[] NewNodes(HashSet<string> before) => f.State.EffectRuntime.EffectNodes.Where(n => !before.Contains(n.EffectId)).ToArray();
        private void AssertCompleted(string id)
        {
            var node = Node(id);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed), node.EffectTypeId + ":" + node.FailureReason);
        }
        private static NormalizedValue Value(NormalizedValue value, string name) => value?.Properties?.FirstOrDefault(p => p.Name == name)?.Value;
        private static string Text(NormalizedValue value, string name) => Value(value, name)?.StringValue ?? string.Empty;
        private static int Number(NormalizedValue value, string name) => checked((int)(Value(value, name)?.IntegerValue ?? 0));
        private static int ResourceDelta(EffectNodeRuntimeState node, int player)
        {
            int amount = Number(node.NormalizedArguments, "amount");
            if (node.EffectTypeId == ResourceEffectTypeIds.Pay && node.PlayerId == player) return -amount;
            if (node.EffectTypeId == ResourceEffectTypeIds.Gain && node.PlayerId == player ||
                node.EffectTypeId == ResourceEffectTypeIds.GainOpponents && node.PlayerId != player) return amount;
            return 0;
        }

        private string Snapshot(string label)
        {
            string prefix = (++snapshotIndex).ToString("D3") + "-" + label;
            Directory.CreateDirectory(f.DirectoryPath);
            string path = Path.Combine(f.DirectoryPath, prefix + ".archive.json");
            File.WriteAllText(path, HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State)));
            var visible = GameStateViewProjector.ProjectForPlayer(f.State, Viewer);
            File.WriteAllText(Path.Combine(f.DirectoryPath, prefix + ".viewer.json"), JsonConvert.SerializeObject(visible, Formatting.Indented));
            var deck = new EventDeckService();
            f.Record("关键状态快照", new { label, path, stage, actor, f.State.Round, f.State.ActionRound, f.State.Phase,
                f.State.CurrentPlayerId, viewer = visible.ViewerPlayerId, localPlayer = Viewer, visible.ViewerRole,
                f.State.ContentPackHash, f.State.EventDeckSeed,
                main = f.State.EffectRuntime.MainNodes.FirstOrDefault(n => n.NodeId == f.State.EffectRuntime.ActiveMainNodeId),
                players = f.State.Players.Select(p => new { p.PlayerId, p.Color, p.CityLocationId, p.Resources, p.RemainingMainActionsThisTurn, p.CompletedMainActionsThisTurn }).ToArray(),
                requests = f.State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open").ToArray(),
                hostDecks = new[] { EventColor.Green, EventColor.Yellow, EventColor.Red }.Select(c => new { color = c.ToString(),
                    count = deck.RemainingCount(f.State.Decks, c), top = deck.Peek(f.State.Decks, c), definition = EventCardDatabase.Get(f.State, deck.Peek(f.State.Decks, c)) }).ToArray(),
                viewerDecks = visible.Decks, routeSession = Routes?.Session, routeDraft = Routes?.Draft, routeProjection = Routes?.Projection,
                boundary = "完整Host仅留本地archive；Viewer不补隐藏牌序；快照在输入计时之外" });
            return path;
        }

        private IEnumerator RestoreCheckpoint(string path)
        {
            Stage("恢复同一正式主链断点验证另一移动分支");
            var recovered = HostRecoveryService.RecoverJson(File.ReadAllText(path), Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(recovered.State.ContentPackHash, Is.EqualTo(f.State.ContentPackHash));
            f.Session.ReplaceState(recovered.State);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            yield return Settle();
            Snapshot("未修改checkpoint恢复后");
        }
    }
}
