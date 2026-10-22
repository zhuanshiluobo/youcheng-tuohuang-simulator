using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using YC.Presentation.Workflows;
using Object = UnityEngine.Object;

namespace YC.Tests.PlayMode.ExpansionRules
{
    /// <summary>
    /// 正常房间按钮到全员入场及首位实际盖牌。Host仅供只读断言/定位合法控件，
    /// 不注册自动答题器、不改种子/配方/状态、不调用任何命令或规则推进入口。
    /// </summary>
    [Timeout(3600000)] // 性能重复可超过框架默认180秒；每步仍有15/60秒等待保护。
    public sealed class HotseatOpeningUiTests
    {
        private Component menu, room, gameplay;
        private GameSession session;
        private HotseatUiInputDriver input;
        private HotseatProfilerSampler profiler;
        private string evidenceDirectory;
        private bool completed;
        private int iteration;
        private SkipSubmitProbe skipSubmitProbe;
        private string skipRequestId, skipFeedbackAfterClick;
        private bool Diagnostic => Environment.GetEnvironmentVariable("YC_HOTSEAT_STAGE") == "Diagnostic";
        private bool Performance => Diagnostic || Environment.GetEnvironmentVariable("YC_HOTSEAT_STAGE") == "Performance";
        private int WarmupIterations => int.TryParse(Environment.GetEnvironmentVariable("YC_HOTSEAT_WARMUP"), out var count) ? Math.Max(5, count) : 5;
        private int MeasuredIterations => int.TryParse(Environment.GetEnvironmentVariable("YC_HOTSEAT_SAMPLES"), out var count) ? Math.Max(30, count) : 30;
        private GameState State => session?.State;
        private InteractionRequest Open => State?.EffectRuntime?.InteractionRequests
            .FirstOrDefault(r => r.Status == "open" && !r.IsInternalMainlineInteraction());
        private int LocalPlayer => gameplay == null ? -1 : (int)Read(gameplay, "localPlayerId");
        private static object Read(object owner, string name) => HotseatUiInputDriver.Read(owner, name);
        private static Component[] Active(string name) => HotseatUiInputDriver.Active("YC.Presentation." + name);
        private static GameObject ButtonObject(object owner, string name) => ((Button)Read(owner, name))?.gameObject;
        private static bool Same(InteractionRequest a, InteractionRequest b) => a != null && b != null &&
            a.GetStableInteractionId() == b.GetStableInteractionId();

        // UnityTest不用TestCase参数化，14个方法分别进入XML，禁止零匹配冒充矩阵。
        [UnityTest] public IEnumerator CoreTwoPlayersThreePlayerMapPlacesInfluence() => Run(false, 2, false, true);
        [UnityTest] public IEnumerator CoreTwoPlayersThreePlayerMapSkipsInfluence() => Run(false, 2, false, false);
        [UnityTest] public IEnumerator CoreTwoPlayersFourPlayerMapPlacesInfluence() => Run(false, 2, true, true);
        [UnityTest] public IEnumerator CoreTwoPlayersFourPlayerMapSkipsInfluence() => Run(false, 2, true, false);
        [UnityTest] public IEnumerator EnterpriseTwoPlayersThreePlayerMapPlacesInfluence() => Run(true, 2, false, true);
        [UnityTest] public IEnumerator EnterpriseTwoPlayersThreePlayerMapSkipsInfluence() => Run(true, 2, false, false);
        [UnityTest] public IEnumerator EnterpriseTwoPlayersFourPlayerMapPlacesInfluence() => Run(true, 2, true, true);
        [UnityTest] public IEnumerator EnterpriseTwoPlayersFourPlayerMapSkipsInfluence() => Run(true, 2, true, false);
        [UnityTest] public IEnumerator CoreThreePlayersThreePlayerMap() => Run(false, 3, false, false);
        [UnityTest] public IEnumerator CoreThreePlayersFourPlayerMap() => Run(false, 3, true, false);
        [UnityTest] public IEnumerator EnterpriseThreePlayersThreePlayerMap() => Run(true, 3, false, false);
        [UnityTest] public IEnumerator EnterpriseThreePlayersFourPlayerMap() => Run(true, 3, true, false);
        [UnityTest] public IEnumerator CoreFourPlayersFourPlayerMap() => Run(false, 4, true, false);
        [UnityTest] public IEnumerator EnterpriseFourPlayersFourPlayerMap() => Run(true, 4, true, false);

        private IEnumerator Run(bool enterprise, int count, bool fourPlayerMap, bool placeInfluence)
        {
            var repetitions = Diagnostic ? 1 : Performance ? 1 + WarmupIterations + MeasuredIterations : 1;
            for (iteration = 0; iteration < repetitions; iteration++)
            {
                yield return RunOnce(enterprise, count, fourPlayerMap, placeInfluence);
                yield return Release();
            }
        }

        private IEnumerator RunOnce(bool enterprise, int count, bool fourPlayerMap, bool placeInfluence)
        {
            completed = false; session = null; gameplay = null;
            var root = Environment.GetEnvironmentVariable("YC_HOTSEAT_OPENING_EVIDENCE") ??
                "Logs/ExpansionValidation/GameplayIssueAcceptance/hotseat-opening";
            evidenceDirectory = Path.GetFullPath(Path.Combine(root,
                TestContext.CurrentContext.Test.MethodName + "-" + iteration.ToString("D3") + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")));
            profiler = HotseatProfilerSampler.Create(evidenceDirectory, Performance, iteration, WarmupIterations);
            input = new HotseatUiInputDriver(evidenceDirectory, Observe, profiler);
            if (Diagnostic) input.Record("单样本诊断", new { stage = "Diagnostic", repetitions = 1,
                note = "每条真实开局仅一次；无预热，不用于P95或独立冷启动达标判定。复用采样器的Performance字段仅表示启用采样。" });
            input.Record("开始", new { enterprise, count, fourPlayerMap, placeInfluence,
                evidenceKind = "正常房间＋EventSystem指针；非操作系统原生鼠标；无规则写入",
                unity = UnityEngine.Application.unityVersion });
            yield return SceneManager.LoadSceneAsync("StartScene");
            yield return null;
            yield return PrepareGameView();
            menu = Active("StartMenuController").Single();
            var view = Read(menu, "view");
            room = (Component)Read(view, "RoomPanel");
            yield return RoomClick(() => ButtonObject(view, "StartGameButton"), "创建真实本地房间");
            yield return RoomIdle();
            Assert.That(room.gameObject.activeInHierarchy, Is.True);
            var service = Read(menu, "roomService");
            Assert.That(service, Is.InstanceOf<RoomSessionService>(), "主矩阵不可注入房间测试替身");
            Assert.That(((IRoomNetworkingControl)service).IsOnline, Is.False);
            for (var seat = 1; seat < count; seat++)
            {
                var index = seat;
                yield return RoomClick(() => SeatButton(index), "添加热座玩家" + (index + 1));
                yield return RoomIdle();
            }
            var mapId = fourPlayerMap ? "map-four-players" : "map-three-players";
            if (DisplayedRoom().Configuration.MapId != mapId)
            {
                yield return RoomClick(() => ContentButton("maps", mapId), "选择真实地图");
                yield return RoomIdle();
            }
            Assert.That(DisplayedRoom().Configuration.RuleModeId, Is.EqualTo("standard"));
            if (DisplayedRoom().Configuration.EnabledExpansionIds.Contains("enterprise") != enterprise)
            {
                yield return RoomClick(() => ContentButton("expansions", "enterprise"), "切换企业扩展");
                yield return RoomIdle();
            }
            var frozen = DisplayedRoom();
            Assert.That(frozen.PlayerCount, Is.EqualTo(count));
            Assert.That(frozen.Configuration.MapId, Is.EqualTo(mapId));
            Assert.That(frozen.Configuration.EnabledExpansionIds,
                Is.EquivalentTo(enterprise ? new[] { "core", "enterprise" } : new[] { "core" }));
            yield return input.Capture("房间配置", 0);
            yield return input.Click(() => ButtonObject(room, "StartButton"), "开始正常热座对局", ReadyAfterSceneLoad, "场景加载（冷态）");
            var scene = fourPlayerMap ? "SampleScene" : "ThreePlayerScene";
            yield return input.Until(() => SceneManager.GetActiveScene().name == scene && !Transitioning(), "正式转场未完成", 60);
            yield return input.Until(() => Active("MobileCityInteractionController").Length == 1, "未创建真实游戏控制器");
            gameplay = Active("MobileCityInteractionController").Single();
            session = (GameSession)Read(gameplay, "session");
            Assert.That(session, Is.Not.Null);
            var launch = HotseatUiInputDriver.RuntimeType("YC.Presentation.GameLaunchContext").GetProperty("Instance").GetValue(null);
            Assert.That(Read(launch, "Mode"), Is.EqualTo(LaunchMode.Local), "本地Submit计数不得套用网络转发");
            profiler.Discover();
            Assert.That(State.Players.Count, Is.EqualTo(count));
            Assert.That(State.RuleConfiguration.MapId, Is.EqualTo(mapId));
            Assert.That(State.RuleConfiguration.ConfigurationRevision, Is.EqualTo(frozen.Configuration.ConfigurationRevision));
            Assert.That(State.RuleConfiguration.ContentPackHash, Is.EqualTo(frozen.Configuration.ContentPackHash));
            input.Record("实际资产来源", ActualAssetSources());
            var order = State.EffectRuntime.PlayerOrderSnapshot.ToArray();
            Assert.That(order.Length, Is.EqualTo(count));
            var entrancePlayers = new List<int>();
            var discardPlayers = new List<int>();
            var optionalPlayers = new List<int>();
            var supplyPlayers = new List<int>();
            var step = 0;
            while (step++ < 80)
            {
                yield return input.Until(() => Open != null, "开局既未到盖牌也无可回答请求");
                var request = Open;
                yield return input.Until(() => LocalPlayer == request.AnsweringPlayerId, "热座没有切换到真实请求回答玩家");
                yield return WaitForHudGold();
                CheckNoForeignPrivateCards(request);
                if (request.InteractionTypeId == "character.cover.card") break;
                Assert.That(request.InteractionTypeId, Is.Not.EqualTo("setup.basic_character.keep"),
                    "正常新局仍产生选保留请求；不能按弃牌界面反向作答");
                profiler.SetRequest(request.GetStableInteractionId(), request.InteractionTypeId);
                var submitBefore = profiler.LocalSubmitCount();
                var goldBefore = InitialGoldAmounts();
                yield return input.Capture("开局-" + request.InteractionTypeId, request.AnsweringPlayerId);
                switch (request.InteractionTypeId)
                {
                    case "setup.enterprise.choose":
                    case "setup.department.choose":
                    case "enterprise.department":
                        yield return ChooseEnterprise(request);
                        break;
                    case "entrance.location":
                        entrancePlayers.Add(request.AnsweringPlayerId);
                        yield return ChooseMap(request, "Location", 1);
                        break;
                    case "event_card.option":
                        yield return input.Click(() => EventOption(), "真实事件卡可见选项", () => NextGameplayReady(request), request.InteractionTypeId);
                        break;
                    case "event_card.influence_targets":
                        yield return ChooseMap(request, "InfluenceSlot", request.MinSelections);
                        break;
                    case "setup.influence.optional":
                        optionalPlayers.Add(request.AnsweringPlayerId);
                        var influenceBefore = State.Map.Influences.Count;
                        var supplyBefore = State.FindPlayer(request.AnsweringPlayerId).InfluenceSupply;
                        Assert.That(count, Is.EqualTo(2));
                        Assert.That(request.AnsweringPlayerId, Is.EqualTo(order[1]));
                        yield return input.Until(() => SkipOptional(request) != null, "可选影响力底部入口尚未绑定当前请求");
                        AssertOptionalMapVisible(request);
                        if (placeInfluence) yield return ChooseMap(request, "InfluenceSlot", 1);
                        else yield return SkipOptionalAndVerify(request);
                        yield return Answered(request);
                        Assert.That(State.Map.Influences.Count - influenceBefore, Is.EqualTo(placeInfluence ? 1 : 0));
                        Assert.That(supplyBefore - State.FindPlayer(request.AnsweringPlayerId).InfluenceSupply, Is.EqualTo(placeInfluence ? 1 : 0));
                        break;
                    case "setup.basic_character.discard":
                        Assert.That(enterprise, Is.True, "基础5张不应出现弃牌窗口");
                        discardPlayers.Add(request.AnsweringPlayerId);
                        Assert.That(request.CandidateIds.Count, Is.EqualTo(7));
                        Assert.That(request.MinSelections, Is.EqualTo(2));
                        Assert.That(request.MaxSelections, Is.EqualTo(2));
                        yield return ChooseCards(request, false);
                        break;
                    case "round.supply.retire":
                        supplyPlayers.Add(request.AnsweringPlayerId);
                        yield return ChooseCards(request, false);
                        break;
                    default:
                        input.Record("未覆盖的实际请求", new { request.InteractionTypeId, request.PromptKey });
                        Assert.Fail("实际正常开局出现未接入的可见UI请求：" + request.InteractionTypeId +
                            "；须补明确控件映射，禁止内部提交或自动忽略。");
                        break;
                }
                yield return Answered(request);
                RecordAnsweredRequest(request, submitBefore);
                CheckGoldNeverRepeated(goldBefore);
                input.Record("真实请求完成", new { request.InteractionTypeId, request.AnsweringPlayerId,
                    requestId = request.GetStableInteractionId(), releaseToObservedStateMs = input.ElapsedSinceRelease(),
                    note = "含输入后等待及断言开销；各Profiler区间及原始调用数另存，不嵌套相加" });
                yield return input.Capture("完成-" + request.InteractionTypeId, request.AnsweringPlayerId);
            }
            Assert.That(step, Is.LessThan(81), "开局请求超过保护上限");
            Assert.That(entrancePlayers, Is.EqualTo(order), "必须全员实际入场，不能首次交接就返回");
            Assert.That(discardPlayers, Is.EqualTo(enterprise ? order : Array.Empty<int>()));
            Assert.That(optionalPlayers, Is.EqualTo(count == 2 ? new[] { order[1] } : Array.Empty<int>()),
                "二人必须真实覆盖一次指定执行/跳过分支，缺请求不能当作已覆盖");
            Assert.That(supplyPlayers, Is.EqualTo(count == 2 ? order : Array.Empty<int>()));
            Assert.That(State.Players.All(p => !string.IsNullOrEmpty(p.CityLocationId)), Is.True);
            Assert.That(State.Round, Is.EqualTo(1));
            Assert.That(State.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(Open.AnsweringPlayerId, Is.EqualTo(order[0]));
            Assert.That(LocalPlayer, Is.EqualTo(order[0]));
            var expectedGold = count == 2 ? new[] { 10, 18 } : count == 3 ? new[] { 10, 12, 18 } : new[] { 10, 12, 14, 18 };
            Assert.That(InitialGoldAmounts(), Is.EqualTo(expectedGold), "仅核对setup.initial_gold本金，不吞掉事件增减");
            foreach (var player in order)
                Assert.That(State.EffectRuntime.EffectNodes.Count(n => n.SourceId == "setup.initial_gold" &&
                    n.PlayerId == player && n.Status == EffectNodeStatus.Completed), Is.EqualTo(1));
            yield return WaitForHudGold();
            yield return input.Capture("首位第一回合盖牌前", order[0]);
            var cover = Open;
            profiler.SetRequest(cover.GetStableInteractionId(), cover.InteractionTypeId);
            var beforeCover = profiler.LocalSubmitCount();
            yield return ChooseCards(cover, true);
            yield return Answered(cover);
            RecordAnsweredRequest(cover, beforeCover);
            Assert.That(InitialGoldAmounts(), Is.EqualTo(expectedGold));
            yield return input.Until(() => Open != null && Open.AnsweringPlayerId == order[1] && LocalPlayer == order[1],
                "首位盖牌后没有把盖牌控制权交给下一位");
            yield return WaitForHudGold();
            yield return input.Capture("首位盖牌已提交", order[1]);
            // 仅等真实刷新，不主动调用刷新方法或规则推进；验证没有无输入重复发款/命令。
            profiler.Point("无输入稳定性观察");
            var settledSubmissions = profiler.LocalSubmitCount();
            for (var frame = 0; frame < 12; frame++) yield return null;
            profiler.AssertLocalSubmissions(settledSubmissions, 0, "连续12个无输入帧");
            Assert.That(InitialGoldAmounts(), Is.EqualTo(expectedGold));
            profiler.ValidateCoverage();
            completed = true;
            input.Record("完整按钮路径通过", new { entrancePlayers, discardPlayers, optionalPlayers, supplyPlayers, expectedGold });
        }

        private IEnumerator ChooseEnterprise(InteractionRequest request)
        {
            Component Dialog() => Active("EnterpriseSelectionDialogView").SingleOrDefault(d =>
                ((EnterpriseSelectionDraft)Read(d, "Draft")).Projection?.RequestId == request.GetStableInteractionId());
            yield return input.Until(() => Dialog() != null, "没有绑定当前真实请求的企业/科室窗口");
            foreach (var id in request.CandidateIds.Take(request.MinSelections).ToArray())
            {
                yield return input.Click(() =>
                {
                    var dialog = Dialog();
                    if (dialog == null) return null;
                    foreach (DictionaryEntry board in (IDictionary)Read(dialog, "items"))
                    {
                        var targets = (IDictionary)Read(board.Value, "targets");
                        if (targets.Contains(id)) return ButtonObject(targets[id], "button");
                    }
                    return null;
                }, "选择真实企业候选");
            }
            Assert.That(((EnterpriseSelectionDraft)Read(Dialog(), "Draft")).SelectedIds.Count, Is.EqualTo(request.MinSelections));
            yield return input.Click(() => ButtonObject(Dialog(), "confirm"), "确认企业/科室", () => NextGameplayReady(request), request.InteractionTypeId);
        }

        private IEnumerator ChooseCards(InteractionRequest request, bool cover)
        {
            var chosen = request.CandidateIds.Take(request.MinSelections).ToArray();
            var before = request.CandidateIds.ToArray();
            yield return input.Until(() => CurrentShell(cover) != null, "缺少真实选牌窗口");
            if (!cover)
                Assert.That((string)Read(Read(gameplay, "characterAbilityInteraction"), "renderedInteractionId"),
                    Is.EqualTo(request.GetStableInteractionId()), "选牌窗口仍绑定前一玩家请求");
            var confirm = NamedAction(CurrentShell(cover), "Confirm Selection");
            Assert.That(confirm, Is.Not.Null, "真实选牌确认入口缺失");
            Assert.That(confirm.GetComponent<Button>().IsInteractable(), Is.False, "尚未选牌不能确认");
            foreach (var id in chosen)
                yield return input.Click(() => CardTarget(id, cover), "选择将提交的真实卡牌");
            yield return input.Click(() => NamedAction(CurrentShell(cover), "Confirm Selection"), "确认所选卡牌", () => NextGameplayReady(request), request.InteractionTypeId);
            yield return Answered(request);
            profiler.Point("测试只读断言/投影");
            var accepted = State.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == request.GetStableInteractionId());
            Assert.That(AnswerIds(accepted.NormalizedAnswer), Is.EquivalentTo(chosen), "实际UI答案与所点卡牌相反或不同");
            if (request.InteractionTypeId == "setup.basic_character.discard")
            {
                foreach (var id in before)
                {
                    var item = State.ItemZones.Items.Single(i => i.ItemId == id);
                    var zone = State.ItemZones.Zones.Single(z => z.ZoneId == item.ZoneId);
                    Assert.That(zone.LocalZoneId, Is.EqualTo(chosen.Contains(id) ? "secret_retired" : "hand"));
                }
                foreach (var player in State.Players)
                {
                    var json = JsonConvert.SerializeObject(GameStateViewProjector.ProjectForPlayer(State, player.PlayerId));
                    foreach (var secret in chosen) Assert.That(json, Does.Not.Contain(secret), "秘密回盒实例泄漏");
                }
            }
            if (cover)
            {
                var item = State.ItemZones.Items.Single(i => i.ItemId == chosen.Single());
                Assert.That(State.ItemZones.Zones.Single(z => z.ZoneId == item.ZoneId).LocalZoneId, Is.EqualTo("covered"));
            }
        }

        private IEnumerator ChooseMap(InteractionRequest request, string kind, int required)
        {
            var selected = new HashSet<string>();
            for (var index = 0; index < Math.Max(1, required) && Same(Open, request); index++)
            {
                string VisibleCandidate() => request.CandidateIds.FirstOrDefault(candidate =>
                    !selected.Contains(candidate) && HotseatUiInputDriver.TryPoint(MapTarget(kind, candidate), out _, out _));
                yield return input.Until(() => VisibleCandidate() != null, "当前地图没有可见且射线可达的合法目标：" + kind);
                var id = VisibleCandidate();
                Assert.That(id, Is.Not.Null);
                selected.Add(id);
                yield return input.Click(() => MapTarget(kind, id), "地图实际目标：" + kind,
                    () => MapReadyAfterClick(request, kind, id, selected), request.InteractionTypeId);
                // 正常地图入口有二次确认；只在实际目标仍显示选中时点击同一控件确认。
                yield return null;
                var map = MapComponent(kind, id);
                if (Same(Open, request) && map != null && (bool)Read(map, "IsSelected"))
                    yield return input.Click(() => MapTarget(kind, id), "确认选中的地图目标",
                        () => MapReadyAfterClick(request, kind, id, selected), request.InteractionTypeId);
            }
        }

        private GameObject SkipOptional(InteractionRequest request)
        {
            var renderer = Read(gameplay, "influenceEffectInteraction");
            if ((string)Read(renderer, "rendered") != request.GetStableInteractionId() ||
                (int)Read(renderer, "revision") != request.StateRevision ||
                (int)Read(renderer, "candidateVersion") != request.CandidateSetVersion) return null;
            var expansion = Read(Read(gameplay, "gameplayInteractionHud"), "ExpansionUi");
            var bound = (InteractionRequestProjection)Read(expansion, "optionalInfluenceRequest");
            if (bound == null || bound.InteractionId != request.GetStableInteractionId() ||
                bound.StateRevision != request.StateRevision || bound.CandidateSetId != request.CandidateSetId ||
                bound.CandidateSetVersion != request.CandidateSetVersion || bound.AnsweringPlayerId != request.AnsweringPlayerId) return null;
            // 只从本局HUD实际绑定的底部宿主查找，不借用整页弹窗或按文案猜动作。
            var host = (RectTransform)Read(expansion, "settlementHost");
            if (host == null || !host.gameObject.activeInHierarchy) return null;
            var row = host.GetComponentsInChildren(HotseatUiInputDriver.RuntimeType("YC.Presentation.UiEffectRowView"), false)
                .Cast<Component>().SingleOrDefault(r => (string)Read(r, "ItemId") == "setup.influence.skip" &&
                    (string)Read(r, "requestId") == request.GetStableInteractionId() &&
                    (int)Read(r, "revision") == request.StateRevision);
            return row != null ? ButtonObject(row, "StatusButton") : null;
        }

        private object GameplayFrame => Read(Read(gameplay, "gameplayInteractionHud"), "Frame");
        private string GameplayFeedback => (string)Read(GameplayFrame, "InteractionMessage");

        private void AssertOptionalMapVisible(InteractionRequest request)
        {
            var frame = GameplayFrame;
            var page = (GameObject)Read(frame, "visiblePage");
            var suspended = (bool)HotseatUiInputDriver.RuntimeType("YC.Presentation.GameplayHudFrame")
                .GetProperty("EffectInputSuspended").GetValue(null);
            var reachable = request.CandidateIds.Where(id =>
                HotseatUiInputDriver.TryPoint(MapTarget("InfluenceSlot", id), out _, out _)).ToArray();
            input.Record("可选影响力地图与底部入口", new { requestId = request.GetStableInteractionId(),
                request.StateRevision, request.CandidateSetId, request.CandidateSetVersion,
                page = HotseatUiInputDriver.PathOf(page), pageActive = page != null && page.activeInHierarchy,
                suspended, reachable, skip = HotseatUiInputDriver.PathOf(SkipOptional(request)) });
            Assert.That(frame, Is.Not.Null, "实际HUD缺少页面框架");
            Assert.That(page == null || !page.activeInHierarchy, Is.True, "可选影响力阶段仍有活动大页面覆盖地图");
            Assert.That(suspended, Is.False, "可选影响力地图输入仍被页面挂起");
            Assert.That(reachable, Is.Not.Empty, "可选影响力的合法地图目标没有实际首命中点");
        }

        private IEnumerator SkipOptionalAndVerify(InteractionRequest request)
        {
            yield return input.Until(() => SkipOptional(request) != null, "底部跳过条目未绑定当前请求及版本");
            skipRequestId = request.GetStableInteractionId();
            var requestedRevision = request.StateRevision;
            skipFeedbackAfterClick = null;
            skipSubmitProbe = new SkipSubmitProbe();
            try
            {
                input.Record("跳过提交观察开始", new { requestId = skipRequestId,
                    feedbackBefore = GameplayFeedback, submit = skipSubmitProbe.Snapshot() });
                yield return input.Click(() => SkipOptional(request), "跳过可选初始影响力",
                    () => NextGameplayReady(request), "跳过可选初始影响力");
                skipFeedbackAfterClick = GameplayFeedback;
                RecordSkipOutcome("跳过点击后规则反馈");
                yield return Answered(request);
                var accepted = State.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == skipRequestId);
                Assert.That(accepted.Status, Is.EqualTo("answered"), "跳过未被正式回答入口接受");
                Assert.That(accepted.NormalizedAnswer.Kind, Is.EqualTo(NormalizedValueKind.Boolean), "跳过必须具有明确的布尔答案");
                Assert.That(accepted.NormalizedAnswer.BooleanValue, Is.False, "实际Host跳过答案必须为false");
                Assert.That(accepted.AnsweredAtRevision, Is.GreaterThan(requestedRevision), "原请求未形成新的已答修订");
                Assert.That(skipSubmitProbe.Calls, Is.EqualTo(1L), "跳过必须恰有一次实际YC.RuleSubmit，回调或缺失采样不能代替提交");
                yield return input.Until(() => NextGameplayReady(request) != null, "跳过后下一真实请求尚未绑定可操作控件");
            }
            finally { FinishSkipObservation(); }
        }

        private void RecordSkipOutcome(string kind)
        {
            var answer = State?.EffectRuntime?.InteractionRequests.SingleOrDefault(r => r.GetStableInteractionId() == skipRequestId);
            input.Record(kind, new { requestId = skipRequestId, submit = skipSubmitProbe?.Snapshot(),
                answer = answer == null ? null : new { answer.Status, answer.StateRevision, answer.AnsweredAtRevision,
                    answer.NormalizedAnswer }, feedbackAfterClick = skipFeedbackAfterClick, feedbackNow = GameplayFeedback,
                faultCode = State?.EffectRuntime?.LastFaultCode, faultMessage = State?.EffectRuntime?.LastFaultMessage,
                note = "仅当前跳过输入窗口的现有标记计数；HUD提示原样保存，不以按钮回调、日志条数或耗时推断规则提交" });
        }

        private void FinishSkipObservation()
        {
            if (skipSubmitProbe == null) return;
            try { RecordSkipOutcome("跳过规则观察收尾"); }
            finally { skipSubmitProbe.Dispose(); skipSubmitProbe = null; }
        }

        private sealed class SkipSubmitProbe : IDisposable
        {
            private ProfilerRecorder recorder;
            internal SkipSubmitProbe()
            {
                var available = new List<ProfilerRecorderHandle>();
                ProfilerRecorderHandle.GetAvailable(available);
                var handle = available.Single(h => ProfilerRecorderHandle.GetDescription(h).Name == "YC.RuleSubmit");
                recorder = new ProfilerRecorder(handle, 64, ProfilerRecorderOptions.StartImmediately |
                    ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                // 不启用全局Profiler或Raw采集，不读取耗时；采样缺失/溢出一律不补零通过。
            }
            internal long? Calls => !recorder.Valid || !recorder.IsRunning || recorder.WrappedAround || recorder.Count >= recorder.Capacity
                ? (long?)null : Enumerable.Range(0, recorder.Count).Any(i => recorder.GetSample(i).Count != 1)
                    ? (long?)null : Enumerable.Range(0, recorder.Count).Sum(i => recorder.GetSample(i).Count);
            internal object Snapshot() => new { marker = "YC.RuleSubmit", valid = recorder.Valid,
                running = recorder.Valid && recorder.IsRunning, calls = Calls,
                sampleCount = recorder.Valid ? (int?)recorder.Count : null,
                wrapped = recorder.Valid && recorder.WrappedAround, capacity = 64 };
            public void Dispose() => recorder.Dispose();
        }

        private Component CurrentShell(bool cover)
        {
            var dialog = Read(gameplay, cover ? "characterCoverChoiceDialog" : "characterCardEffectChoiceDialog");
            var shell = Read(dialog, "shell");
            var view = (Component)Read(shell, "view");
            return view != null && view.gameObject.activeInHierarchy ? view : null;
        }

        private GameObject CardTarget(string id, bool cover)
        {
            var target = HotseatUiInputDriver.Marker("selection.card." + id);
            var shell = CurrentShell(cover);
            return target != null && shell != null && target.transform.IsChildOf(shell.transform) ? target : null;
        }

        private static GameObject NamedAction(Component shell, string name)
        {
            if (shell == null) return null;
            var matches = shell.GetComponentsInChildren(HotseatUiInputDriver.RuntimeType("YC.Presentation.EffectDialogActionButtonView"), false)
                .Cast<Component>().Where(c => c.name == name).ToArray();
            Assert.That(matches.Length, Is.LessThanOrEqualTo(1));
            return matches.Length == 1 ? ButtonObject(matches[0], "Button") : null;
        }

        private static Component MapComponent(string kind, string id) => Active("MapButtonView")
            .SingleOrDefault(c => Read(c, "TargetKind").ToString() == kind && (string)Read(c, "TargetId") == id);
        private static GameObject MapTarget(string kind, string id)
        {
            var target = MapComponent(kind, id);
            return target != null && (bool)Read(target, "IsInteractive") ? target.gameObject : null;
        }

        private static GameObject EventOption()
        {
            // 选择画面上一个真实事件热点；原图序号映射仍由生产回调处理。
            for (var index = 0; index < 10; index++)
            {
                var target = HotseatUiInputDriver.Marker("event.option." + index);
                if (target != null && target.GetComponent<Button>()?.IsInteractable() == true) return target;
            }
            return null;
        }

        private IEnumerator Answered(InteractionRequest request) => input.Until(() => !Same(Open, request),
            "真实界面操作没有完成请求：" + request.InteractionTypeId);

        private IEnumerator RoomClick(Func<GameObject> resolve, string label)
        {
            var before = DisplayedRoom();
            var roomId = before?.RoomId;
            var revision = before?.StateRevision ?? -1;
            yield return input.Click(resolve, label, () =>
            {
                var shown = DisplayedRoom();
                if (shown == null || (shown.RoomId == roomId && shown.StateRevision <= revision) ||
                    (bool)Read(menu, "roomRequestPending") || (bool)Read(menu, "joiningRoom")) return null;
                var authority = ((RoomSessionService)Read(menu, "roomService")).GetCurrentRoom();
                if (authority == null || authority.RoomId != shown.RoomId || authority.StateRevision != shown.StateRevision ||
                    authority.Configuration.ContentPackHash != shown.Configuration.ContentPackHash ||
                    (int)Read(room, "viewerId") != authority.LocalPlayerId) return null;
                var controls = new List<GameObject> { ButtonObject(room, "StartButton") };
                controls.AddRange(Enumerable.Range(0, 4).Select(SeatButton));
                var control = controls.FirstOrDefault(c => HotseatUiInputDriver.TryPoint(c, out _, out _));
                return control == null ? null : new HotseatProfilerSampler.ReadyTarget { Control = control,
                    StateKey = shown.RoomId + ":" + shown.StateRevision + ":" + authority.LocalPlayerId,
                    Criterion = "新权威房间修订已绑定、身份一致、无pending、下一房间按钮首命中" };
            }, label, () => (int)Read(Read(Read(menu, "roomService"), "content"), "CatalogBuildCount"));
        }

        private HotseatProfilerSampler.ReadyTarget ReadyAfterSceneLoad()
        {
            if (Transitioning() || SceneManager.GetActiveScene().name == "StartScene") return null;
            var controller = Active("MobileCityInteractionController").SingleOrDefault();
            if (controller == null) return null;
            gameplay = controller; session = (GameSession)Read(controller, "session");
            var result = NextGameplayReady(null);
            if (result != null) result.Operation = "场景加载（冷态）";
            return result;
        }

        private HotseatProfilerSampler.ReadyTarget NextGameplayReady(InteractionRequest previous)
        {
            var request = Open;
            if (request == null || Same(request, previous) || LocalPlayer != request.AnsweringPlayerId) return null;
            var candidates = new List<GameObject>();
            switch (request.InteractionTypeId)
            {
                case "setup.enterprise.choose":
                case "setup.department.choose":
                case "enterprise.department":
                    var dialog = Active("EnterpriseSelectionDialogView").SingleOrDefault(d =>
                        ((EnterpriseSelectionDraft)Read(d, "Draft")).Projection?.RequestId == request.GetStableInteractionId());
                    if (dialog == null) return null;
                    foreach (DictionaryEntry board in (IDictionary)Read(dialog, "items"))
                    {
                        var targets = (IDictionary)Read(board.Value, "targets");
                        foreach (var id in request.CandidateIds)
                            if (targets.Contains(id)) candidates.Add(ButtonObject(targets[id], "button"));
                    }
                    break;
                case "entrance.location":
                    if ((string)Read(Read(gameplay, "influenceEffectInteraction"), "rendered") != request.GetStableInteractionId()) return null;
                    candidates.AddRange(request.CandidateIds.Select(id => MapTarget("Location", id)));
                    break;
                case "setup.influence.optional":
                    candidates.Add(SkipOptional(request));
                    candidates.AddRange(request.CandidateIds.Select(id => MapTarget("InfluenceSlot", id)));
                    break;
                case "event_card.influence_targets":
                case "event_card.option":
                    if ((string)Read(Read(gameplay, "eventCardInteraction"), "renderedInteractionId") != request.GetStableInteractionId()) return null;
                    if (request.InteractionTypeId == "event_card.option") candidates.Add(EventOption());
                    else candidates.AddRange(request.CandidateIds.Select(id => MapTarget("InfluenceSlot", id)));
                    break;
                case "character.cover.card":
                    if (State.Phase != GamePhase.CharacterCover || State.CurrentPlayerId != LocalPlayer) return null;
                    candidates.AddRange(request.CandidateIds.Select(id => CardTarget(id, true)));
                    break;
                case "setup.basic_character.discard":
                case "round.supply.retire":
                    if ((string)Read(Read(gameplay, "characterAbilityInteraction"), "renderedInteractionId") != request.GetStableInteractionId()) return null;
                    candidates.AddRange(request.CandidateIds.Select(id => CardTarget(id, false)));
                    break;
                default: return null; // 不假定未知请求有合法控件，不点击规则兜底。
            }
            var target = candidates.FirstOrDefault(c => HotseatUiInputDriver.TryPoint(c, out _, out _));
            return target == null ? null : new HotseatProfilerSampler.ReadyTarget { Control = target,
                StateKey = request.GetStableInteractionId() + ":" + request.StateRevision + ":" + LocalPlayer + ":" + State.Phase,
                Criterion = "下一开放请求/阶段已发布、回答玩家与本地身份一致、实际绑定控件首命中" };
        }

        private HotseatProfilerSampler.ReadyTarget MapReadyAfterClick(InteractionRequest request, string kind,
            string id, HashSet<string> selected)
        {
            if (!Same(Open, request)) return NextGameplayReady(request);
            if (LocalPlayer != request.AnsweringPlayerId) return null;
            if (request.InteractionTypeId == "event_card.influence_targets")
            {
                var accepted = (IEnumerable<string>)Read(Read(gameplay, "eventCardInteraction"), "selectedInfluenceSlots");
                if (accepted.Contains(id))
                {
                    var remaining = request.CandidateIds.Where(candidate => !selected.Contains(candidate))
                        .Select(candidate => MapTarget(kind, candidate)).FirstOrDefault(c => HotseatUiInputDriver.TryPoint(c, out _, out _));
                    if (remaining != null) return new HotseatProfilerSampler.ReadyTarget { Control = remaining,
                        StateKey = request.GetStableInteractionId() + ":selected:" + string.Join("|", accepted),
                        Criterion = "同请求多目标选择状态已更新，下一合法地图目标首命中", Operation = "地图多目标选择（非提交）" };
                }
            }
            var map = MapComponent(kind, id);
            return map != null && (bool)Read(map, "IsSelected") ? new HotseatProfilerSampler.ReadyTarget {
                Control = map.gameObject, StateKey = request.GetStableInteractionId() + ":selected:" + id,
                Criterion = "当前地图目标进入二次确认阶段且首命中", Operation = "地图预选（非提交）" } : null;
        }

        private RoomState DisplayedRoom() => (RoomState)Read(room, "DisplayedRoom");
        private IEnumerator RoomIdle() => input.Until(() => !(bool)Read(menu, "roomRequestPending") &&
            !(bool)Read(menu, "joiningRoom"), "房间按钮请求没有完成");
        private GameObject SeatButton(int index) => ButtonObject(((Array)Read(room, "seats")).GetValue(index), "HotSeatButton");
        private GameObject ContentButton(string group, string id)
        {
            var option = ((IEnumerable)Read(room, group)).Cast<object>().Single(o => (string)Read(o, "Id") == id);
            return ButtonObject(option, "Button");
        }

        private static IEnumerable<string> AnswerIds(NormalizedValue value)
        {
            if (value == null) return Array.Empty<string>();
            if (value.Kind == NormalizedValueKind.String) return new[] { value.StringValue };
            if (value.Kind == NormalizedValueKind.StableReference) return new[] { value.ReferenceId };
            if (value.Kind == NormalizedValueKind.Array) return value.Items.SelectMany(AnswerIds).ToArray();
            return Array.Empty<string>();
        }

        private void RecordAnsweredRequest(InteractionRequest request, long before)
        {
            var accepted = State.EffectRuntime.InteractionRequests.Single(r =>
                r.GetStableInteractionId() == request.GetStableInteractionId());
            Assert.That(accepted.Status, Is.EqualTo("answered"), "控件操作没有产生已回答请求");
            profiler.AssertLocalSubmissions(before, 1, request.InteractionTypeId);
            input.Record("已回答请求证据", new { requestId = accepted.GetStableInteractionId(),
                accepted.AnsweredAtRevision, source = Performance ? "YC.RuleSubmit逐调用样本，包含规则拒绝" : "Host实际已答状态；跳过独立计数另记",
                expectedSubmissions = Performance ? (int?)1 : null });
        }

        private int[] InitialGoldAmounts() => State.EffectRuntime.PlayerOrderSnapshot.Select(player =>
            State.EffectRuntime.EffectNodes.Where(n => n.PlayerId == player && n.SourceId == "setup.initial_gold" &&
                n.Status == EffectNodeStatus.Completed).Sum(n => checked((int)
                    n.NormalizedArguments.Properties.Single(f => f.Name == "amount").Value.IntegerValue))).ToArray();

        private void CheckGoldNeverRepeated(int[] before)
        {
            var after = InitialGoldAmounts();
            for (var i = 0; i < before.Length; i++)
                if (before[i] != 0) Assert.That(after[i], Is.EqualTo(before[i]), "重复发放初始本金");
        }

        private IEnumerator WaitForHudGold()
        {
            yield return input.Until(() =>
            {
                var modules = Active("GameplayMainModules").SingleOrDefault();
                if (modules == null || (int)Read(modules, "currentLocalPlayerId") != LocalPlayer) return false;
                var text = (Text)Read(modules, "localVoucherValue");
                return text != null && text.gameObject.activeInHierarchy &&
                    text.text == State.FindPlayer(LocalPlayer).Resources.GoldVoucher.ToString();
            }, "HUD金券仍属于旧玩家或未刷新");
        }

        private void CheckNoForeignPrivateCards(InteractionRequest request)
        {
            var visibleIds = HotseatUiInputDriver.Active("YC.PlayerJourney.PlayerAutomationId")
                .Select(c => (string)Read(c, "Id")).Where(id => id != null && id.StartsWith("selection.card.", StringComparison.Ordinal))
                .Select(id => id.Substring("selection.card.".Length)).ToArray();
            foreach (var id in visibleIds)
            {
                var item = State.ItemZones.Items.FirstOrDefault(i => i.ItemId == id);
                if (item == null) continue;
                var zone = State.ItemZones.Zones.Single(z => z.ZoneId == item.ZoneId);
                if (zone.LocalZoneId == "hand") Assert.That(item.OwnerSubject.PlayerId, Is.EqualTo(request.AnsweringPlayerId),
                    "上一玩家私密选牌页面残留");
                Assert.That(zone.LocalZoneId, Is.Not.EqualTo("secret_retired"), "秘密回盒牌仍显示在页面");
            }
        }

        private object Observe() => new { scene = SceneManager.GetActiveScene().name, localPlayer = LocalPlayer,
            phase = State?.Phase.ToString(), round = State?.Round,
            currentPlayer = State?.CurrentPlayerId, gameId = State?.GameId,
            request = Open == null ? null : new { id = Open.GetStableInteractionId(), type = Open.InteractionTypeId,
                player = Open.AnsweringPlayerId, revision = Open.StateRevision, min = Open.MinSelections, max = Open.MaxSelections },
            gold = State?.Players.Select(p => new { player = p.PlayerId, amount = p.Resources.GoldVoucher }).ToArray() };

        private static bool Transitioning() => (bool)HotseatUiInputDriver.RuntimeType("YC.Presentation.SceneTransitionContext")
            .GetProperty("IsTransitionInProgress").GetValue(null);

        private IEnumerator PrepareGameView()
        {
#if UNITY_EDITOR
            var capture = HotseatUiInputDriver.RuntimeType("YC.Presentation.Editor.GameplaySupplementalPageCapture");
            capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new Vector2Int(1920, 1080) });
#endif
            yield return input.Until(() => Screen.width == 1920 && Screen.height == 1080, "热座基准画面尺寸未生效");
        }

        private object ActualAssetSources()
        {
            var hud = Active("GameplayInteractionHudView").Single();
#if UNITY_EDITOR
            var prefab = HotseatUiInputDriver.RuntimeType("UnityEditor.PrefabUtility");
            var method = prefab.GetMethod("GetPrefabAssetPathOfNearestInstanceRoot", new[] { typeof(Object) });
            return new { scene = SceneManager.GetActiveScene().path, hud = HotseatUiInputDriver.PathOf(hud.gameObject),
                prefab = method?.Invoke(null, new object[] { hud.gameObject }),
                note = "加载实际场景实例；覆盖字节哈希由统一运行前后审计记录" };
#else
            return new { scene = SceneManager.GetActiveScene().path, hud = HotseatUiInputDriver.PathOf(hud.gameObject) };
#endif
        }

        [UnityTearDown]
        public IEnumerator Release()
        {
            if (profiler == null && input == null && menu == null && gameplay == null) yield break;
            FinishSkipObservation();
            // 先保存失败/缺采样信息并释放Recorder，失败截图不再触发采样断言。
            if (profiler != null) profiler.Finish(completed);
            if (input != null)
            {
                input.Record("测试结束", new { completed, result = TestContext.CurrentContext.Result.Outcome.ToString(),
                    performance = "Profiler逐调用采样与输入时序；未证明预热/样本数/可交互首帧前不宣告P95达标" });
                if (!completed) yield return input.Capture("失败时实际画面", LocalPlayer);
                input.FlushRecords();
            }
            if (profiler != null)
            {
                Object.Destroy(profiler.gameObject);
                profiler = null;
            }
            // 仅隔离下个用例的正常场景收尾，不用此调用推进任何受测开局步骤。
            var launch = HotseatUiInputDriver.RuntimeType("YC.Presentation.GameLaunchContext");
            launch.GetMethod("ReturnToStartScene", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { "StartScene" });
            var deadline = Time.realtimeSinceStartup + 30;
            while ((Transitioning() || SceneManager.GetActiveScene().name != "StartScene") && Time.realtimeSinceStartup < deadline)
                yield return null;
            session = null; gameplay = null; input = null; menu = null; room = null;
        }
    }
}
