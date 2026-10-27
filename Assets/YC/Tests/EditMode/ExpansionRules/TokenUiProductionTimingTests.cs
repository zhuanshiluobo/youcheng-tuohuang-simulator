using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 复用当前正式Lua/Host隔离夹具和原HUD组件；不启动Player，不代表正常局、GPU帧时延或完整场景验收。
    public sealed class TokenUiProductionTimingTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value) ?? value.GetType().GetProperty(name, Flags)?.GetValue(value);
        private static object Call(object value, string name, params object[] args)
        {
            try { return value.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private static void Set(object value, string name, object data) => value.GetType().GetProperty(name, Flags).SetValue(value, data);
        private AgnesContentTestFixture fixture;
        private GameSession session;
        private GameObject hud, controllerRoot, eventRoot;
        private object controller, declaration, expansion, actionPanel, nextRenderer;
        private string phase;
        private readonly List<object> samples = new List<object>();
        private readonly List<object> artworkBuildLocations = new List<object>();
        private bool sampleWarm, sampleQuick;
        private bool sampleListeners;
        private int sampleOrdinal;
        private long clickAllocatedBytes;
        private Dictionary<string,long> targetExecutionBefore,targetExecutionAfter;
        private readonly List<CpuRow> cpuRows = new List<CpuRow>(256);
        private struct CpuRow { public string phase, handler; public long ticks; }
        private readonly List<GameCommand> commands = new List<GameCommand>();
        private readonly List<CommandResult> results = new List<CommandResult>();
        private GameState State => session.State;
        private void Record(UiRegressionMetrics.Sample sample)
        {
            samples.Add(new { phase, sample });
            if (sample.Phase == "artwork.definition_index_build" || sample.Phase == "artwork.decode_cold")
                artworkBuildLocations.Add(new { phase, ordinal = samples.Count, metric = sample.Phase });
        }
        private GameStateView Visible() => (GameStateView)Call(controller, "VisibleSaleState");
        private Button Button(string id) => hud.GetComponentsInChildren<MonoBehaviour>(true)
            .Where(c => c != null && c.gameObject.activeInHierarchy && c.GetType().FullName == "YC.PlayerJourney.PlayerAutomationId" &&
                (string)c.GetType().GetField("Id").GetValue(c) == id).Select(c => c.GetComponent<Button>() ?? c.GetComponentInChildren<Button>(true)).Single(b => b != null);
        private void Click(Button button)
        {
            Assert.That(button.IsActive() && button.IsInteractable(), Is.True);
            using (UiRegressionMetrics.Measure("input.native_button_total"))
            {
                var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
                ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
                using (UiRegressionMetrics.Measure("input.layout")) Canvas.ForceUpdateCanvases();
            }
        }
        private void RefreshRows()
        {
            CharacterCardPanelViewModel model;
            using (UiRegressionMetrics.Measure("quick.query_projection"))
                model = new CharacterCardPanelPresenter().BuildProjectedView(State, 1, Visible());
            using (UiRegressionMetrics.Measure("quick.render")) Call(actionPanel, "RenderCharacterQuickAction", model);
        }
        [TestCase(false)]
        [TestCase(true)]
        public void CurrentReadOnlyPagesRecordFirstAndRepeatedOpenCpuSegments(bool warm) => RunScenario(warm, false);
        [TestCase(false, 0)] [TestCase(false, 1)] [TestCase(false, 2)]
        [TestCase(true, 0)] [TestCase(true, 1)] [TestCase(true, 2)]
        public void CurrentLuaQuickEntryReachesItsRealBodyRequest(bool listeners, int ordinal)
        {
            sampleListeners = listeners; sampleOrdinal = ordinal;
            RunScenario(false, true);
        }
        private void RunScenario(bool warm, bool quick)
        {
            samples.Clear(); commands.Clear(); results.Clear(); artworkBuildLocations.Clear(); cpuRows.Clear();
            targetExecutionBefore=targetExecutionAfter=null;
            Action<string,string,long> luaListener=(name,handler,ticks)=>cpuRows.Add(new CpuRow {phase=name,handler=handler,ticks=ticks});
            Action<long> validationListener=ticks=>cpuRows.Add(new CpuRow {phase="item_zone.validate",handler="",ticks=ticks});
            sampleWarm = warm; sampleQuick = quick;
            fixture = new AgnesContentTestFixture(); fixture.Formal();
            if (quick) fixture.Choose(1, "A08", "2");
            var round = new RoundExecutionService(fixture.Registry);
            Assert.That(round.PrepareActionWindowForSmoke(fixture.State, 1).IsValid, Is.True);
            session = new GameSession(fixture.State); session.ExecutionTimings.Enabled = true;
            session.RegisterHandler(new InvokeSettlementOptionCommandHandler(fixture.Registry, round));
            session.RegisterHandler(new AnswerInteractionCommandHandler(fixture.Registry, round));
            controllerRoot = new GameObject("QY017 隔离生产读取"); controllerRoot.SetActive(false);
            controller = controllerRoot.AddComponent(T("MobileCityInteractionController"));
            controller.GetType().GetField("session", Flags).SetValue(controller, session);
            controller.GetType().GetField("localPlayerId", Flags).SetValue(controller, 1);
            phase = quick ? "快速入口初次权威投影" : "只读页初次权威投影";
            UiRegressionMetrics.Recorded += Record; UiRegressionMetrics.Enabled = true;
            var theme = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", T("UiThemeCatalog"));
            T("UiTheme").GetMethod("Initialize").Invoke(null, new[] { theme });
            hud = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            if (EventSystem.current == null) eventRoot = new GameObject("QY017 隔离原按钮事件", typeof(EventSystem), typeof(StandaloneInputModule));
            var uiRegistry = hud.GetComponentInChildren(T("GameplayDialogRegistry"), true);
            var hudView = hud.GetComponent(T("GameplayInteractionHudView")); var frame = Read(hudView, "Frame");
            // EditMode隔离组件显式接通原生命周期监听；仅操作实例，不重建或保存界面资产。
            if (!ReferenceEquals(Read(frame, "active"), frame)) Call(frame, "Awake");
            Call(hud.GetComponentInChildren(T("UiMainActionTabs"), true), "Awake");
            var canvas = (RectTransform)Read(frame, "ContentRect");
            Set(uiRegistry, "BeginInputRead", new Func<IDisposable>(() => (IDisposable)Call(controller, "BeginVisibleRead")));
            Call(frame, "ConfigureGameplayPresentation", fixture.Pack.Presentation,
                fixture.Pack.ActiveDefinitions.ToDictionary(d => d.DefinitionId, d => d.DisplayName));
            Call(frame, "BindGameplaySnapshot", Visible());
            var effectDialog = Activator.CreateInstance(T("CharacterCardEffectChoiceDialog"), Flags, null, new object[] { uiRegistry, canvas }, null);
            nextRenderer = Activator.CreateInstance(T("CharacterAbilityInteractionUiCoordinator"), Flags, null, new object[] {
                new Func<GameState>(() => State), new Func<int>(() => 1), effectDialog,
                new Action<IReadOnlyList<WorkflowHighlight>>(_ => { }), new Action(() => { }),
                new Action<GameCommand>(_ => Assert.Fail("本例只到快速行动后的正文请求，不代答正文")), new Action<string>(_ => { }) }, null);
            Set(nextRenderer, "GetVisibleState", new Func<GameStateView>(Visible));
            Set(nextRenderer, "GetVisibleRequests", new Func<IReadOnlyList<InteractionRequest>>(() => VisibleInteractionRequestSource.Read(null, Visible(), 1)));
            Set(nextRenderer, "CanSubmit", new Func<bool>(() => true));
            declaration = Activator.CreateInstance(T("CityStyleDeclarationPreviewDialog"), Flags, null,
                new object[] { uiRegistry, new Func<RectTransform>(() => canvas) }, null);
            var view = new View { Show = model => Call(declaration, "Show", model), Refresh = RefreshRows };
            var context = new Context { Current = () => State };
            var city = new CityStyleInteraction(context, new NoCommand(), view, new InteractionFlowCoordinator(),
                new MapQueryService(StaticMapDefinitions.Resolve(State.MapId)), () => true, () => true, () => "", _ => { }, () => { });
            city.GetVisibleState = Visible;
            Action noop = () => { };
            actionPanel = T("ActionPanelController").GetMethod("Bind", Flags).Invoke(null, new object[] {
                Read(hudView, "ActionPanelView"), Read(uiRegistry, "CardVisualCatalog"), new Action<string>(_ => Assert.Fail("声明动作不能走旧物品入口")),
                new Action(() => city.BeginDeclare()), noop, noop, noop, noop, noop, noop, noop });
            Set(actionPanel, "CanBeginAction", new Func<bool>(() => true)); Set(actionPanel, "CanSubmitCharacter", new Func<bool>(() => true));
            Call(actionPanel, "SetButtonStates", false, true, false, false, false, false, false, false, false);
            Set(actionPanel, "BeginInputRead", new Func<IDisposable>(() => (IDisposable)Call(controller, "BeginVisibleRead")));
            Set(actionPanel, "DeclaredActionRequested", new Action<AbilityActionOption>(option => {
                var command = AbilityActionCommand.Create(1, option); commands.Add(command);
                var oldLua=LuaEffectEventDispatcher.CpuTimingSample;var oldValidation=ItemZoneQuery.ValidationTimingSample;
                try
                {
                    LuaEffectEventDispatcher.CpuTimingSample=sampleListeners?luaListener:null;
                    ItemZoneQuery.ValidationTimingSample=sampleListeners?validationListener:null;
                    using (UiRegressionMetrics.Measure("quick.authoritative_submit")) results.Add(session.Submit(command));
                }
                finally {LuaEffectEventDispatcher.CpuTimingSample=oldLua;ItemZoneQuery.ValidationTimingSample=oldValidation;}
                fixture.State = session.State;
                using (UiRegressionMetrics.Measure("quick.next_request_projection"))
                {
                    var next = VisibleInteractionRequestSource.Read(null, Visible(), 1);
                    Assert.That(next.Count, Is.GreaterThan(0), "快速费用后必须出现真实正文请求");
                    Assert.That(next.All(r => r.PromptKey != "effect.card.payment_items"), Is.True);
                    var projection = InteractionRequestProjector.ProjectForPlayer(next[0], 1);
                    if ((bool)Call(nextRenderer, "CanRender", projection))
                        using (UiRegressionMetrics.Measure("quick.next_request_render")) Call(nextRenderer, "Render", projection);
                }
            }));
            expansion = hud.GetComponent(T("GameplayExpansionUi"));
            Call(expansion, "Configure", new Func<GameStateView>(Visible), new Func<int>(() => 1), fixture.Registry.EnterpriseDefinitions,
                fixture.Registry.CharacterAbilityContracts, new Action<GameCommand>(_ => Assert.Fail("只读详情不能提交")));
            Set(nextRenderer, "ExpansionUi", expansion);
            var resolve = Delegate.CreateDelegate(typeof(Func<GameplayTextRef, GameplayTextDocument>), expansion, "ResolveGameplayText");
            var render = Delegate.CreateDelegate(typeof(Action<Component, GameplayTextDocument, bool>), frame, "RenderGameplayText");
            Set(actionPanel, "ResolveActionText", resolve); Set(actionPanel, "RenderActionText", render);
            Call(effectDialog, "BindGameplayText", resolve, render);
            Call(expansion, "Refresh"); RefreshRows();
            var unchanged = JsonConvert.SerializeObject(State);
            object firstDeclarationPage = null;
            for (int i = 0; i < (warm ? 2 : 1); i++)
            {
                phase = "打开宣告前原页签"; Click(Button("action.tab.2"));
                phase = i == 0 ? "宣告首次打开" : "宣告再次打开";
                Click(Button("action.declare")); Assert.That((bool)Read(declaration, "IsShowing"), Is.True);
                var currentPage = Read(declaration, "view");
                if (i == 0) firstDeclarationPage = currentPage;
                else Assert.That(currentPage, Is.SameAs(firstDeclarationPage), "同一HUD重开应复用原页面实例，重新绑定当前数据");
                phase += "同帧节流轮询";
                for (int poll = 0; poll < 3; poll++)
                    using (UiRegressionMetrics.Measure("declare.throttled_poll_cpu")) Call(declaration, "RefreshProjection", false);
                phase = i == 0 ? "宣告首次打开强制刷新" : "宣告再次打开强制刷新";
                using (UiRegressionMetrics.Measure("declare.forced_refresh_cpu")) Call(declaration, "RefreshProjection", true);
                Call(declaration, "Hide");
                phase = i == 0 ? "主界面详情首次打开" : "主界面详情再次打开";
                var roots = (RectTransform[])Read(expansion, "entrepreneurSlotRoots");
                Click((Button)Read(roots[0].GetComponent(T("GameplayCardSlotView")), "button"));
                var viewer = Read(expansion, "entrepreneurViewer"); Assert.That((bool)Read(viewer, "IsShowing"), Is.True);
                Call(viewer, "Close");
            }
            Assert.That(JsonConvert.SerializeObject(State), Is.EqualTo(unchanged));
            if (!quick) return;
            phase = warm ? "页面再次打开后刷新快捷行" : "页面首次打开后刷新快捷行"; RefreshRows(); Click(Button("action.tab.1"));
            var target = Visible().AbilityActions.Single(a => a.ActionId == "entrepreneur.A08.tactic.quick");
            Assert.That(target.Enabled, Is.True, target.DisabledReason);
            phase = warm ? "页面再次打开后的首个快速命令" : "页面首次打开后的首个快速命令";
            var quickButton = Button("action.declared." + target.OptionId);
            // 此处为R12新现场，不倒填R10缺失的前Host。所有落盘均在原按钮/Submit计时外。
            var storedIds=State.ItemZones.Items.Where(i=>i.ZoneId==fixture.Storage.ZoneId).Select(i=>i.ItemId).OrderBy(i=>i,StringComparer.Ordinal).ToArray();
            fixture.Artifacts.WriteLabeledText("R12快速命令前正式Host",HostRecoveryService.SerializeArchive(session.CreateHostArchive(State.ContentPackHash)));
            targetExecutionBefore=session.ExecutionTimings.Snapshot();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            Click(quickButton);
            clickAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            targetExecutionAfter=session.ExecutionTimings.Snapshot();
            fixture.Artifacts.WriteLabeledText("R12快速实际命令",JsonConvert.SerializeObject(commands.Single()));
            Assert.That(results.Single().Succeeded, Is.True, results.Single().Validation?.Reason);
            Assert.That(fixture.Stored, Is.Zero);
            var invocation = State.EffectRuntime.AbilityActionInvocations.Single(i => i.CommandId == commands.Single().CommandId);
            Assert.That(invocation.PaymentCommitted, Is.True);
            var paid = AbilityActionExecutor.EntryPaymentNodes(State, invocation).Where(n => AbilityActionExecutor.HasCommittedTokenTransfer(State, n)).ToArray();
            Assert.That(paid.Length, Is.EqualTo(1));
            Assert.That(ItemTransferEffectExecutor.Number(paid[0].NormalizedResult, "movedCount"), Is.EqualTo(2));
            var movedIds=ItemTransferEffectExecutor.Value(paid[0].NormalizedResult,"movedItemIds").Items.Select(i=>i.StringValue).ToArray();
            Assert.That(movedIds.Distinct(StringComparer.Ordinal).Count(),Is.EqualTo(2));
            Assert.That(movedIds,Is.EquivalentTo(storedIds));
            Assert.That(State.EffectRuntime.InteractionRequests.Any(r=>r.Status=="open"&&r.InteractionTypeId=="effect.influence.place.target"),Is.True);
            Assert.That(State.EffectRuntime.InteractionRequests.All(r => r.PromptKey != "effect.card.payment_items"), Is.True);
        }
        [TearDown] public void Finish()
        {
            UiRegressionMetrics.Recorded -= Record; UiRegressionMetrics.Enabled = false;
            if (fixture != null)
            {
                if (session != null) fixture.State = session.State;
                // 计时监听解除后才创建并落盘Host归档；不将普通GameState JSON冒作正式恢复证据。
                if (session != null) fixture.Artifacts.WriteLabeledText("采样结束正式Host归档",
                    HostRecoveryService.SerializeArchive(session.CreateHostArchive(State.ContentPackHash)));
                fixture.Artifacts.WriteLabeledText("原组件输入与生产Lua命令分段", JsonConvert.SerializeObject(new {
                    scope = "隔离原按钮事件、当前Lua及Host；EditMode CPU时延，不含GPU/可见正常局/完整控制器刷新；快速行动均为该夹具首次命令，热样本仅指页面再次打开；同帧节流及强制刷新分别记录，不充正常空闲帧", phase, samples, commands, results,
                    sampleWarm, sampleQuick, sampleListeners, sampleOrdinal, clickAllocatedBytes, cpuRows,
                    targetExecutionBefore,targetExecutionAfter,
                    targetExecutionDelta=targetExecutionBefore==null||targetExecutionAfter==null?null:
                        targetExecutionAfter.ToDictionary(p=>p.Key,p=>p.Key=="clock.frequency"?p.Value:p.Value-targetExecutionBefore[p.Key]),
                    cpuClockFrequency = System.Diagnostics.Stopwatch.Frequency, artworkBuildLocations,
                    samplingBoundary = "全部样本保留；索引/解码位置按所属phase及样本序号记录；Host创建与证据I/O在计时监听解除后；未在性能用例内执行Host恢复；嵌套耗时不可相加",
                    device = new { SystemInfo.graphicsDeviceType, SystemInfo.graphicsDeviceName, SystemInfo.maxTextureSize, Screen.width, Screen.height },
                    state = fixture.State, execution = session?.ExecutionTimings.Snapshot(),
                    assemblies = new[] { typeof(GameSession).Assembly, typeof(EffectSpec).Assembly, T("ActionPanelController").Assembly }.Select(a => new { a.FullName, mvid = a.ManifestModule.ModuleVersionId }) }, Formatting.Indented));
            }
            if (declaration != null) Call(declaration, "Dispose");
            if (nextRenderer != null) Call(nextRenderer, "Dispose");
            if (expansion != null)
            {
                Call(expansion, "BeginShutdown");
                foreach (var name in new[] { "publicBoards", "publicDepartments", "cardViewer", "entrepreneurViewer" })
                    if (Read(expansion, name) is Component component && component != null) UnityEngine.Object.DestroyImmediate(component.gameObject);
            }
            if (hud != null) UnityEngine.Object.DestroyImmediate(hud);
            if (eventRoot != null) UnityEngine.Object.DestroyImmediate(eventRoot);
            if (controllerRoot != null) UnityEngine.Object.DestroyImmediate(controllerRoot);
            fixture?.Dispose(); fixture = null; session = null; hud = controllerRoot = eventRoot = null;
            controller = declaration = expansion = actionPanel = nextRenderer = null;
        }
        private sealed class Context : IWritableGameplayContext
        { public Func<GameState> Current; public GameState CurrentState => Current(); public int LocalPlayerId => 1; public bool ControlsCurrentPlayerLocally => true; public void SetLocalPlayerId(int id) { } }
        private sealed class NoCommand : IGameCommandPort
        { public WorkflowSubmissionResult Submit(GameCommand command) => throw new InvalidOperationException("宣告采样只打开原页，不提交宣告。"); }
        private sealed class View : ITurnActionView
        {
            public Action<CityStyleOptionsViewModel> Show; public Action Refresh;
            public void ShowCityStyleOptions(CityStyleOptionsViewModel model) => Show(model);
            public void RefreshActionPanel() => Refresh();
            public void ShowPrompt(string value) { } public void SetHighlights(IReadOnlyList<WorkflowHighlight> values) { }
            public void ClearHighlights() { } public void RefreshFromState() { } public void RefreshInformation() { }
            public void ShowPendingChoice() { } public void CompleteMainActionPresentation(string name) { }
            public void ShowBuildFacilityDraft(BuildFacilityDraftViewModel model) { } public void HideBuildFacilityDraft() { }
        }
    }
}
