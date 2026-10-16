using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 最小诊断，不改正式状态或生产文件；首次/再次两个样本不能用来判P95。
    [Timeout(600000)]
    public sealed class MainActionResponseBaselineTests
    {
        private GameplayPageAcceptanceFixture f;
        private HotseatProfilerSampler profiler;
        private HotseatUiInputDriver input;
        private string directory;
        private bool completed;
        private object Presenter => Read(f.Gameplay, "turnActionPresenter");
        private object ActionView => Read(f.Hud, "ActionPanelView");
        private string StateKey => Read(Presenter, "Mode") + ":" + f.State.EffectRuntime.StateRevision + ":" +
            Read(f.Frame, "InteractionMessage") + ":" + Read(f.Gameplay, "selectedBuildFacilityId");

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            input?.FlushRecords();
            if (profiler != null) { profiler.Finish(completed); UnityEngine.Object.Destroy(profiler.gameObject); }
            yield return null;
        }

        [UnityTest]
        public IEnumerator SampleScene_MainActionsAndBuildListFirstAndRepeatResponses()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, _ => Noop());
            yield return f.Resolution(Sizes[0]);
            directory = Path.Combine(f.DirectoryPath, "response-baseline"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "真实采样起点状态.json"), JsonConvert.SerializeObject(f.State, Formatting.Indented));
            RecordQueries(); // 独立查询探针在点击采样前运行；按钮和窗口首次打开仍是本局首次。
            profiler = HotseatProfilerSampler.Create(directory, true, 0, 0);
            input = new HotseatUiInputDriver(directory, () => new { stateKey = StateKey,
                revision = f.State.EffectRuntime.StateRevision, phase = f.State.Phase.ToString() }, profiler);
            input.Record("诊断边界", new { note = "一个真实四人局，仅首次/再次响应。非OS鼠标，未改种子或状态；查询探针先执行，因此不声称进程冷启动。不可用按钮记录实际缺口，绝不强制interactable。" });
            var names = new[] { "DeployButton", "DispatchButton", "ExploreButton", "MoveCityButton", "BuildButton", "SpecialButton" };
            input.Record("六按钮初始状态", names.Select(name => new { name,
                path = HotseatUiInputDriver.PathOf(((Button)Read(ActionView, name)).gameObject),
                active = ((Button)Read(ActionView, name)).gameObject.activeInHierarchy,
                available = ((Button)Read(ActionView, name)).IsInteractable() }).ToArray());
            if (Environment.GetEnvironmentVariable("YC_MAIN_ACTION_BASELINE_SCOPE") == "Opening")
            {
                input.CaptureAllocationMetrics = true;
                yield return OpeningResponses();
                completed = true; yield break;
            }
            yield return BuildList();
            if (Environment.GetEnvironmentVariable("YC_MAIN_ACTION_BASELINE_SCOPE") == "Build")
            { completed = true; yield break; }
            foreach (var name in new[] { "MoveCityButton", "ExploreButton", "DispatchButton", "SpecialButton", "DeployButton" })
            {
                var button = (Button)Read(ActionView, name);
                if (name == "ExploreButton")
                {
                    yield return ExploreEnterCancelRepeat(button);
                    continue;
                }
                for (var repetition = 0; repetition < 2; repetition++)
                {
                    if (!button.gameObject.activeInHierarchy || !button.IsInteractable())
                    {
                        input.Record("按钮无合法样本", new { name, repetition, reason = "当前真实状态下不可用；不补零，不绕过规则", stateKey = StateKey });
                        break;
                    }
                    var before = StateKey;
                    yield return input.Click(() => button.gameObject, name + (repetition == 0 ? "首次点击" : "再次点击"),
                        () => MainReady(button), name + (repetition == 0 ? "首次点击" : "再次点击"));
                    input.Record("按钮响应状态", new { name, repetition, before, after = StateKey,
                        note = "再次点击可能是取消/切换选择，按实际前后状态分析，不冒充相同打开操作" });
                }
                if (f.Request != null)
                {
                    input.Record("正式待答请求边界", new { name, f.Request.InteractionTypeId,
                        note = "部署等按钮已进入正式请求，基线不完成或伪造后续规则以制造重复样本" });
                    break;
                }
            }
            completed = true;
        }

        private static IEnumerator Noop() { yield break; }

        private string OpeningBusinessState()
        {
            // 共用探索进入及取消是正式命令，审计日志/Effect节点/Revision会前进；不把它们回写或伪装相同。
            // 其余完整业务状态必须相同，包括玩家资源/额度、地图、牌序、实体区和既有待结算字段。
            var state = JObject.FromObject(f.State);
            state.Remove("EffectRuntime"); state.Remove("EffectRuntimeState"); state.Remove("Logs");
            return state.ToString(Formatting.None);
        }

        private static Dictionary<int, string> SceneObjects() => UnityEngine.Object.FindObjectsOfType<GameObject>(true)
            .Where(o => o.scene.IsValid()).ToDictionary(o => o.GetInstanceID(), o => o.name);

        private IEnumerator MeasureOpeningClick(Func<GameObject> button, string label,
            Func<HotseatProfilerSampler.ReadyTarget> ready)
        {
            var before = SceneObjects();
            yield return input.Click(button, label, ready, label);
            var after = SceneObjects();
            var newIds = after.Keys.Except(before.Keys).ToArray();
            var goneIds = before.Keys.Except(after.Keys).ToArray();
            input.Record("操作前后场景对象存活差额", new { label, beforeCount = before.Count, afterCount = after.Count,
                netChange = after.Count - before.Count, newlyObservedSurvivors = newIds.Length, noLongerObserved = goneIds.Length,
                survivorNames = newIds.GroupBy(id => after[id]).Select(g => new { name = g.Key, count = g.Count() }).OrderByDescending(g => g.count).Take(30).ToArray(),
                note = "对象枚举在响应计时窗外，包含非激活场景对象；这是实例ID存活差额，漏掉窗口内创建后销毁，不能当Instantiate调用次数。枚举自身分配可能影响后续GC，不强制GC或补造计数" });
        }

        private IEnumerator OpeningResponses(bool preparedDispatch = false)
        {
            var baseline = OpeningBusinessState();
            File.WriteAllText(Path.Combine(directory, preparedDispatch ? "正式部署后调度业务状态基准.json" : "打开取消业务状态基准.json"), baseline);
            input.Record("打开取消测量范围", new { buttons = new[] { "BuildButton", "ExploreButton", "DispatchButton", "DeployButton" },
                preparedDispatch,
                note = "真实开局同一有效业务状态，首次进入/取消/再次进入/取消分列；共用探索及部署使用生产Esc入口取消，并记录模式。主要地图行动进入须当前合法候选可交互且实际射线命中；比旧按钮或任意地图可命中更严格。点击采样起终点机制不变。未修改正式State。仅两个进入样本，不判P95；没有Instantiate调用计数接口。" });
            bool dispatchNeedsPreparation = false;
            foreach (var name in preparedDispatch ? new[] { "DispatchButton" } : new[] { "BuildButton", "ExploreButton", "DispatchButton", "DeployButton" })
            {
                if (name == "DispatchButton" && !preparedDispatch && !f.State.Map.Influences.Any(i => i.PlayerId == f.State.CurrentPlayerId &&
                    (bool)Call(Read(Presenter, "influenceActionPresenter"), "CanMoveFrom", i.SlotId)))
                {
                    dispatchNeedsPreparation = true;
                    input.Record("原起点缺少合法调度样本", new { player = f.State.CurrentPlayerId,
                        influenceCount = f.State.Map.Influences.Count, note = "无合法自己的调度来源；不打开空选择计时，不填零。原约74ms只代表空选择入口，后续合法样本状态不同。" });
                    continue;
                }
                var button = (Button)Read(ActionView, name);
                Assert.That(button.gameObject.activeInHierarchy && button.IsInteractable(), Is.True, name + "没有合法样本，不补零通过");
                Component BuildPage() => Active("FacilityBuildDialogView").SingleOrDefault();
                bool Selecting()
                {
                    if (name == "BuildButton") return BuildPage() != null;
                    if (name == "ExploreButton") return f.Request?.InteractionTypeId == "route_resource.selection" &&
                        (bool)Read(Read(Read(f.Gameplay, "routeResourceSelection"), "Presenter"), "IsActive");
                    if (name == "DispatchButton") return (bool)Read(Read(Presenter, "influenceActionPresenter"), "IsSelectingDispatchSource");
                    return f.Request?.InteractionTypeId == "effect.influence.place.target" ||
                        (bool)Read(Read(Presenter, "influenceActionPresenter"), "IsSelectingDeployTarget");
                }
                HotseatProfilerSampler.ReadyTarget OpenReady()
                {
                    if (!Selecting()) return null;
                    if (name != "BuildButton")
                    {
                        var target = Active("MapButtonView").FirstOrDefault(c => (bool)Read(c, "IsInteractive") &&
                            HotseatUiInputDriver.TryPoint(c.gameObject, out _, out _));
                        return target == null ? null : new HotseatProfilerSampler.ReadyTarget { Control = target.gameObject,
                            StateKey = StateKey + ":" + Read(target, "TargetKind") + ":" + Read(target, "TargetId"),
                            Criterion = "当前行动选择已激活，真实合法地图候选IsInteractive且实际射线可命中，随后观察到渲染帧" };
                    }
                    var page = BuildPage();
                    return new HotseatProfilerSampler.ReadyTarget { Control = ButtonObject(page, "cancel"),
                        StateKey = StateKey + ":" + page.GetInstanceID(), Criterion = "正式建设页激活、取消按钮可命中并观察到渲染帧" };
                }
                for (var repetition = 0; repetition < 2; repetition++)
                {
                    Assert.That(Selecting(), Is.False, name + "起点仍处于选择状态");
                    Assert.That(f.Request, Is.Null);
                    Assert.That(OpeningBusinessState(), Is.EqualTo(baseline), name + "重复进入前业务状态已变化");
                    var beforeRevision = f.State.EffectRuntime.StateRevision;
                    var label = name + (repetition == 0 ? "首次进入" : "同状态再次进入");
                    try { yield return MeasureOpeningClick(() => button.gameObject, label, OpenReady); }
                    finally { RecordMapReadiness(label, Selecting()); }
                    input.Record("进入后实际状态", new { name, repetition, beforeRevision,
                        afterRevision = f.State.EffectRuntime.StateRevision, stateKey = StateKey,
                        requestType = f.Request?.InteractionTypeId, requestId = f.Request?.GetStableInteractionId(),
                        candidates = f.Request?.CandidateIds, sourceObjects = name == "BuildButton" ? Read(BuildPage(), "SupplyItemIds") : null });
                    var cancelLabel = name + (repetition == 0 ? "首次取消" : "再次取消恢复");
                    if ((name == "DeployButton" && f.Request != null) || name == "ExploreButton")
                    {
                        var selectionMode = name == "ExploreButton" ? "正式共用探索请求" : f.Request == null ? "本地部署草稿" : "正式部署请求";
                        if (f.Request != null)
                        {
                            if (name == "DeployButton") Assert.That(f.Request.AllowDecline, Is.True, "正式部署请求不允许取消，停止同状态重复采样");
                            Assert.That(f.Request.CandidateIds, Is.Not.Empty);
                        }
                        else Assert.That((bool)Read(Read(Presenter, "influenceActionPresenter"), "IsSelectingDeployTarget"), Is.True,
                            "无 Host 请求时必须有真实活跃本地部署草稿，不能以空请求代替已进入");
                        var watch = Stopwatch.StartNew();
                        Assert.That((bool)Call(f.Gameplay, "TryHandleInteractionEscape"), Is.True);
                        watch.Stop();
                        input.Record("生产Esc取消派发", new { label = cancelLabel, selectionMode, synchronousMilliseconds = watch.Elapsed.TotalMilliseconds,
                            note = "调用生产键盘Esc入口，未伪造按钮或改状态；只记同步调用耗时，不冒充真实OS键盘端到端样本" });
                        yield return Wait(() => f.Request == null && !Selecting() && button.IsInteractable(), "生产取消未恢复可用入口：" + name);
                    }
                    else
                    {
                        Func<GameObject> cancel = name == "BuildButton" ? (Func<GameObject>)(() => ButtonObject(BuildPage(), "cancel")) : () => button.gameObject;
                        yield return MeasureOpeningClick(cancel, cancelLabel, () => !Selecting() && f.Request == null && button.IsInteractable() ? MainReady(button) : null);
                    }
                    yield return Settle();
                    var after = OpeningBusinessState();
                    var equal = after == baseline;
                    if (!equal) File.WriteAllText(Path.Combine(directory, name + "-取消后业务状态.json"), after);
                    input.Record("取消恢复同业务起点", new { name, repetition, equal, beforeRevision,
                        afterRevision = f.State.EffectRuntime.StateRevision,
                        note = "比较完整业务JSON只排除EffectRuntime及其别名和日志；所有审计增量保留于正式State。比较与序列化均在计时窗外" });
                    Assert.That(equal, Is.True, name + "取消未恢复同一有效业务状态，不能采伪重复样本");
                }
            }
            if (dispatchNeedsPreparation)
            {
                // 通过正式主行动和结束窗口产生可调度来源，不注入实体或修改行动额度。
                int originalPlayer = f.State.CurrentPlayerId;
                var influence = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(f.State.MapId)));
                for (int window = 0; window < f.State.Players.Count; window++)
                {
                    int player = f.State.CurrentPlayerId;
                    string slot = influence.GetLegalPlacementSlotIds(f.State, player).First();
                    foreach (var command in new[] {
                        new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.DeployInfluence, PlayerId = player, TargetId = slot },
                        new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.EndAction, PlayerId = player } })
                    {
                        var result = f.Session.Submit(command);
                        Assert.That(result.Succeeded, Is.True, "准备合法调度来源：" + command.Kind + " " + result.Validation?.Reason);
                        Call(f.Gameplay, "SynchronizeInteractionFromState");
                        yield return Settle();
                        input.Record("计时窗外正式准备调度来源", new { player, slot, command.Kind, phase = f.State.Phase.ToString() });
                    }
                }
                Assert.That(f.State.CurrentPlayerId, Is.EqualTo(originalPlayer));
                Assert.That(f.State.Map.Influences.Any(i => i.PlayerId == originalPlayer &&
                    (bool)Call(Read(Presenter, "influenceActionPresenter"), "CanMoveFrom", i.SlotId)), Is.True);
                File.WriteAllText(Path.Combine(directory, "正式部署后调度采样起点状态.json"), JsonConvert.SerializeObject(f.State, Formatting.Indented));
                yield return OpeningResponses(true);
            }
        }

        private string FormalStateHash()
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(f.State)))).Replace("-", "");
        }

        private void RecordMapReadiness(string label, bool selecting)
        {
            // 已在就绪计时完成或失败之后；只读诊断不得混入端到端窗口。
            var influence = Read(Presenter, "influenceActionPresenter");
            var localPlayer = (int)Read(f.Gameplay, "localPlayerId");
            input.Record("计时窗外地图就绪诊断", new { label, selecting, localPlayer, mode = Read(Presenter, "Mode").ToString(),
                dispatchSources = f.State.Map.Influences.Where(i => i.PlayerId == localPlayer).Select(i => new {
                    i.InfluenceId, i.SlotId, canMoveFrom = (bool)Call(influence, "CanMoveFrom", i.SlotId) }).ToArray(),
                targets = Active("MapButtonView").Select(c => {
                    var area = (Collider2D)Read(c, "HitArea"); var sprite = (SpriteRenderer)Read(c, "Image");
                    return new { kind = Read(c, "TargetKind").ToString(), id = Read(c, "TargetId"),
                        interactive = Read(c, "IsInteractive"), areaEnabled = area != null && area.enabled,
                        imageEnabled = sprite != null && sprite.enabled, alpha = sprite == null ? 0 : sprite.color.a,
                        tryPoint = HotseatUiInputDriver.TryPoint(c.gameObject, out _, out _),
                        rays = area == null ? null : RaycasterManager.GetRaycasters().OfType<PhysicsRaycaster>()
                            .Where(r => r.isActiveAndEnabled && r.eventCamera != null).Select(r => {
                                var screen = r.eventCamera.WorldToScreenPoint(area.bounds.center);
                                var hits = new List<RaycastResult>();
                                EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = screen }, hits);
                                return new { camera = r.eventCamera.name, screen = new[] { screen.x, screen.y, screen.z },
                                    centerInside = area.OverlapPoint(area.bounds.center), hits = hits.Take(4).Select(h => new {
                                        path = HotseatUiInputDriver.PathOf(h.gameObject),
                                        handler = HotseatUiInputDriver.PathOf(ExecuteEvents.GetEventHandler<IPointerClickHandler>(h.gameObject)) }).ToArray() };
                            }).ToArray() };
                }).ToArray() });
        }

        private IEnumerator ExploreEnterCancelRepeat(Button button)
        {
            if (!button.gameObject.activeInHierarchy || !button.IsInteractable())
            {
                input.Record("按钮无合法样本", new { name = "ExploreButton", reason = "当前真实状态下不可用，不强制启用" });
                yield break;
            }
            var explore = Read(Presenter, "explorationEventPresenter");
            Assert.That((bool)Read(explore, "IsActive"), Is.False);
            var originalHash = FormalStateHash();
            for (var operation = 0; operation < 4; operation++)
            {
                var entering = operation % 2 == 0;
                var label = new[] { "探索首次进入", "探索首次取消", "探索同状态再次进入", "探索再次取消恢复" }[operation];
                var beforeHash = FormalStateHash(); var beforeKey = StateKey;
                var beforeStage = Read(explore, "stage").ToString();
                Assert.That(beforeHash, Is.EqualTo(originalHash), "探索重复进入前正式状态已变化，不得冒充同状态");
                yield return input.Click(() => button.gameObject, label,
                    () => (bool)Read(explore, entering ? "IsSelectingExploreTarget" : "IsActive") == entering ? MainReady(button) : null, label);
                var afterHash = FormalStateHash();
                input.Record("探索同状态进入取消分离", new { operation, label, entering, beforeHash, afterHash,
                    beforeKey, afterKey = StateKey, beforeStage, afterStage = Read(explore, "stage").ToString(),
                    note = "状态哈希在点击计时窗口外计算；两次进入相互比较，取消单列；第二次取消仅恢复其余按钮采样起点" });
                Assert.That(afterHash, Is.EqualTo(originalHash));
                Assert.That(f.Request, Is.Null, "只进入/取消探索不得制造正式请求");
            }
        }

        private HotseatProfilerSampler.ReadyTarget MainReady(Button clicked)
        {
            var map = Active("MapButtonView").Select(c => c.gameObject)
                .FirstOrDefault(g => HotseatUiInputDriver.TryPoint(g, out _, out _));
            var control = clicked.IsInteractable() ? clicked.gameObject : map;
            return control == null ? null : new HotseatProfilerSampler.ReadyTarget { Control = control,
                StateKey = StateKey, Criterion = "实际行动状态/反馈已读取，按钮或地图控件可射线命中，随后观察到渲染帧；不代表执行行动完毕" };
        }

        private IEnumerator BuildList()
        {
            var build = (Button)Read(ActionView, "BuildButton");
            Assert.That(build.IsInteractable(), Is.True);
            for (var repetition = 0; repetition < 2; repetition++)
            {
                Component Page() => Active("FacilityBuildDialogView").SingleOrDefault();
                HotseatProfilerSampler.ReadyTarget Ready() => Page() == null ? null : new HotseatProfilerSampler.ReadyTarget {
                    Control = ButtonObject(Page(), "cancel"), StateKey = StateKey + ":" + Page().GetInstanceID(),
                    Criterion = "实际Registry建设页激活，取消按钮可首命中，随后观察到渲染帧" };
                yield return input.Click(() => build.gameObject, repetition == 0 ? "建设列表首次打开" : "建设列表再次打开", Ready);
                var page = Page(); var host = (RectTransform)Read(page, "candidates");
                var scroll = host.GetComponentInParent<ScrollRect>();
                input.Record("建设列表实际规模", new { repetition, supplyIds = Read(page, "SupplyItemIds"),
                    generatedCount = ((IList)Read(page, "generated")).Count,
                    content = scroll == null ? null : Geometry(scroll.content), viewport = scroll == null ? null : Geometry(scroll.viewport) });
                if (scroll != null)
                {
                    for (var wheel = 0; wheel < 2; wheel++)
                    {
                        var point = ScreenRect(scroll.viewport).center; var hit = Hit(point);
                        var handler = ExecuteEvents.GetEventHandler<IScrollHandler>(hit.gameObject);
                        Assert.That(handler, Is.SameAs(scroll.gameObject));
                        var before = scroll.content.anchoredPosition;
                        profiler.Point("建设滚轮同步派发开始", new { repetition, wheel });
                        var timer = Stopwatch.StartNew();
                        ExecuteEvents.Execute(handler, new PointerEventData(EventSystem.current) { position = point,
                            pointerCurrentRaycast = hit, scrollDelta = scroll.horizontal ? new Vector2(wheel == 0 ? -1 : 1, 0) : new Vector2(0, wheel == 0 ? -1 : 1) }, ExecuteEvents.scrollHandler);
                        timer.Stop(); var dispatchMs = timer.Elapsed.TotalMilliseconds;
                        yield return null; yield return null;
                        profiler.Point("建设滚轮跨帧观察结束");
                        input.Record("建设列表滚轮", new { repetition, wheel, dispatchMs, before,
                            after = scroll.content.anchoredPosition, note = "派发CPU时间单列；两帧观察不冒充精确可见首帧或P95" });
                    }
                }
                var cards = page.GetComponentsInChildren(T("FacilityEffectCardView"), false).Cast<Component>().ToArray();
                var card = cards.First(c => HotseatUiInputDriver.TryPoint(ButtonObject(c, "Button"), out _, out _));
                var target = ButtonObject(card, "Button");
                yield return input.Click(() => target, "建设列表选择设施_" + repetition,
                    () => string.IsNullOrEmpty((string)Read(f.Gameplay, "selectedBuildFacilityId")) ? null : Ready());
                input.Record("设施选择刷新后规模", new { repetition, selected = Read(f.Gameplay, "selectedBuildFacilityId"),
                    generatedCount = ((IList)Read(page, "generated")).Count });
                yield return input.Click(() => ButtonObject(Page(), "cancel"), "关闭建设列表_" + repetition,
                    () => !build.IsInteractable() || Page() != null ? null : new HotseatProfilerSampler.ReadyTarget {
                        Control = build.gameObject, StateKey = StateKey, Criterion = "实际建设页关闭，建设按钮可再次首命中并已渲染" });
            }
        }

        private void RecordQueries()
        {
            var clone = GameStateCloneService.DeepClone(f.State);
            var player = (int)Read(f.Gameplay, "localPlayerId");
            var calls = new Dictionary<string, long>();
            Probe(clone.Decks.FacilitySupply, "设施供应完整投影", calls);
            Probe(clone.Map.Facilities, "全体城市设施完整投影", calls);
            foreach (var p in clone.Players) Probe(p.BuiltFacilityIds, "玩家" + p.PlayerId + "已建设设施完整投影", calls);
            var service = new BuildFacilityOptionQueryService();
            var id = clone.Decks.FacilitySupply.First(s => !string.IsNullOrEmpty(s));
            var rows = new List<object>();
            void Measure(string name, Func<object> query)
            {
                for (var repetition = 0; repetition < 2; repetition++)
                {
                    foreach (var key in calls.Keys.ToArray()) calls[key] = 0;
                    var clock = Stopwatch.StartNew(); var result = query(); clock.Stop();
                    rows.Add(new { name, repetition, milliseconds = clock.Elapsed.TotalMilliseconds,
                        projectionInvocations = new Dictionary<string, long>(calls), resultType = result?.GetType().FullName });
                }
            }
            Measure("全部设施建造选项独立查询", () => service.Query(clone, player));
            Measure("单张设施含12槽及支付选项独立查询", () => service.Query(clone, player, id));
            Measure("行动面板ViewModel独立查询", () => Call(Presenter, "BuildActionPanelViewModel"));
            Measure("角色手牌投影独立查询", () => Call(Read(f.Gameplay, "characterCardPresenter"), "BuildView", clone, player));
            Measure("样式特殊行动独立查询", () => Call(Read(Presenter, "CityStyleInteraction"), "QuerySpecialActions"));
            File.WriteAllText(Path.Combine(directory, "independent-query-probes.json"), JsonConvert.SerializeObject(new {
                note = "实例与数据来自当前正式局。设施/角色查询用深克隆，计数只包装克隆的LegacyPositionList投影委托；不改正式State。行动/样式为只读现有查询。独立耗时不能从点击总时延相减或当作精确调用分摊；0代表此克隆包装器未调用，并不代表实时查询次数0。",
                player, clone.EntitySchemaVersion, rows }, Formatting.Indented));
        }

        private static void Probe<T>(LegacyPositionList<T> list, string name, IDictionary<string, long> calls)
        {
            var original = (Func<List<T>>)typeof(LegacyPositionList<T>).GetField("projection", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(list);
            Assert.That(original, Is.Not.Null, "必须使用实际实体投影列表");
            calls[name] = 0;
            list.Bind(() => { calls[name]++; return original(); });
        }
    }
}
