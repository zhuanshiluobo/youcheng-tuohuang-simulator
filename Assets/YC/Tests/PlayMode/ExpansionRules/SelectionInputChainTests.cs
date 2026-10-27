using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 隔离合同检查：正式预制体、原指针事件、真实请求和权威提交；不计正常对局验收。
    public sealed class SelectionInputChainTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value) ?? value.GetType().GetProperty(name, Flags)?.GetValue(value);
        private static object Call(object value, string name, params object[] args) => value.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args);
        private GameObject hud, eventHost, controllerHost;
        private object controller, coordinator, dialog;
        private GameSession session;
        private EffectRegistry registry;
        private string phase, rootId;
        private bool canSubmit = true;
        private readonly List<object> samples = new List<object>();
        private readonly List<GameCommand> commands = new List<GameCommand>();
        private readonly List<CommandResult> results = new List<CommandResult>();
        private readonly Dictionary<string, int> metricCounts = new Dictionary<string, int>();
        private GameState State => session.State;
        private GameStateView Visible() => (GameStateView)Call(controller, "VisibleSaleState");
        private IReadOnlyList<InteractionRequest> Requests() => VisibleInteractionRequestSource.Read(null, Visible(), 1)
            .Where(r => r.OwnerEffectId == rootId).ToArray(); // 本组件消费自己的真实请求；主链操作窗口请求仍保留并接受权威校验。
        private void Record(UiRegressionMetrics.Sample value)
        {
            samples.Add(new { Phase = phase, Frame = Time.frameCount, Metric = value });
            var key = phase + ":" + value.Phase;
            metricCounts[key] = metricCounts.TryGetValue(key, out var count) ? count + 1 : 1;
        }
        private int Count(string scenario, string metric) => metricCounts.TryGetValue(scenario + ":" + metric, out var count) ? count : 0;
        private HashSet<string> Selected => (HashSet<string>)Read(coordinator, "selectedCandidates");
        private Button Button(string name) => hud.GetComponentsInChildren<Transform>(true)
            .Single(t => t.name == name && t.gameObject.activeInHierarchy).GetComponentInChildren<Button>();
        private static void Click(Button button)
        {
            Canvas.ForceUpdateCanvases();
            var driver = T("Validation.HotseatUiValidationDriver");
            var point = (Vector2)driver.GetMethod("UiPointerPoint", Flags).Invoke(null, new object[] { button.gameObject });
            Assert.That(driver.GetMethod("FirstUiClickTarget", Flags).Invoke(null, new object[] { point }), Is.EqualTo(button.gameObject));
            var data = new PointerEventData(EventSystem.current) { position = point, button = PointerEventData.InputButton.Left };
            using (UiRegressionMetrics.Measure("input.pointer"))
            {
                ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.pointerClickHandler);
            }
        }

        [UnitySetUp] public IEnumerator CreateIsolatedNativeFlow()
        {
            samples.Clear(); commands.Clear(); results.Clear(); metricCounts.Clear(); canSubmit = true;
            var state = new GameState { GameId = Guid.NewGuid().ToString("N"), Phase = GamePhase.ActionRound1, CurrentPlayerId = 1, StartPlayerId = 1 };
            state.Players.Add(new PlayerState { PlayerId = 1 });
            registry = new EffectRegistry(); ResourceEffectExecutor.Register(registry);
            // 真实提交需要主链操作窗口；仅改Phase会重演旧付款夹具的NotCurrentPlayer拒绝。
            var actionWindow = new RoundExecutionService(registry).PrepareActionWindowForSmoke(state, 1);
            Assert.That(actionWindow.IsValid, Is.True, actionWindow.Reason);
            Assert.That(OperationWindowService.ReadOpenWindows(state).Any(w =>
                w.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow && w.AvailablePlayerIds.Contains(1)), Is.True);
            registry.Register(new EffectRegistration("ui.selection.contract", context =>
            {
                if (context.Node.FlowStage == "selected")
                    return EffectStepResult.Continue("followup").AddChild(ResourceEffectSpecFactory.Gain(1, ResourceType.GoldVoucher, 1));
                if (context.Node.FlowStage == "followup")
                {
                    var next = new EffectInteractionSpec { InteractionTypeId = "lua.choice", AnsweringPlayerId = 1,
                        Visibility = "owner", PromptKey = "ui.selection.followup", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1 };
                    next.CandidateIds.Add("candidate-final");
                    return EffectStepResult.Continue("finished").AddInteraction(next);
                }
                if (context.Node.FlowStage == "finished") return EffectStepResult.Completed();
                var request = new EffectInteractionSpec { InteractionTypeId = "lua.choice", AnsweringPlayerId = 1,
                    Visibility = "owner", PromptKey = "ui.selection.contract", AnswerSchema = "candidate_id_array", MinSelections = 2, MaxSelections = 2 };
                request.CandidateIds.AddRange(new[] { "candidate-a", "candidate-b", "candidate-c" });
                return EffectStepResult.Continue("selected").AddInteraction(request);
            }, EffectExecutorKind.IntrinsicFlow));
            var engine = new EffectTreeExecutor(state, registry); rootId = engine.CreateRoot(EffectSpec.Create("ui.selection.contract", playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
            session = new GameSession(state); session.RegisterHandler(new AnswerInteractionCommandHandler(registry)); session.ExecutionTimings.Enabled = true;
            controllerHost = new GameObject("隔离读取控制器"); controllerHost.SetActive(false);
            controller = controllerHost.AddComponent(T("MobileCityInteractionController"));
            controller.GetType().GetField("session", Flags).SetValue(controller, session);
            controller.GetType().GetField("localPlayerId", Flags).SetValue(controller, 1);
            var theme = UnityEditor.AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", T("UiThemeCatalog"));
            T("UiTheme").GetMethod("Initialize").Invoke(null, new[] { theme });
            hud = UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            if (EventSystem.current == null) eventHost = new GameObject("隔离选择事件系统", typeof(EventSystem), typeof(StandaloneInputModule));
            yield return null;
            var dialogs = hud.GetComponentInChildren(T("GameplayDialogRegistry"), true);
            // 补正前属性不存在；同一测试可记录前后真实调用次数，不模拟投影耗时。
            dialogs.GetType().GetProperty("BeginInputRead")?.SetValue(dialogs, new Func<IDisposable>(() => (IDisposable)Call(controller, "BeginVisibleRead")));
            var frame = Read(hud.GetComponent(T("GameplayInteractionHudView")), "Frame");
            dialog = Activator.CreateInstance(T("CharacterCardEffectChoiceDialog"), Flags, null, new[] { dialogs, Read(frame, "ContentRect") }, null);
            coordinator = Activator.CreateInstance(T("CharacterAbilityInteractionUiCoordinator"), Flags, null, new object[] {
                new Func<GameState>(() => State), new Func<int>(() => 1), dialog,
                new Action<IReadOnlyList<WorkflowHighlight>>(_ => { }), new Action(() => { }),
                new Action<GameCommand>(command => {
                    commands.Add(command);
                    CommandResult result;
                    using (UiRegressionMetrics.Measure("command.authoritative_submit")) result = session.Submit(command);
                    results.Add(result);
                    Call(coordinator, "NotifyInteractionCommandResolved", State.GameId, command.CommandId, result.Succeeded, result.Validation.ErrorCode);
                    using (UiRegressionMetrics.Measure("command.post_projection"))
                    {
                        var next = Requests().SingleOrDefault();
                        if (next == null) Call(coordinator, "Clear");
                        else Call(coordinator, "Render", InteractionRequestProjector.ProjectForPlayer(next, 1));
                    }
                }), new Action<string>(_ => { }) }, null);
            coordinator.GetType().GetProperty("GetVisibleState").SetValue(coordinator, new Func<GameStateView>(Visible));
            coordinator.GetType().GetProperty("GetVisibleRequests").SetValue(coordinator, new Func<IReadOnlyList<InteractionRequest>>(Requests));
            coordinator.GetType().GetProperty("CanSubmit").SetValue(coordinator, new Func<bool>(() => canSubmit));
            UiRegressionMetrics.Recorded += Record; UiRegressionMetrics.Enabled = true;
            phase = "首次展示"; WriteEvidence(new { AllVisibleRequestsBeforeRender = VisibleInteractionRequestSource.Read(null, Visible(), 1), rootId, CurrentState = State }); Render();
        }

        private void Render()
        {
            using (UiRegressionMetrics.Measure("input.render_total"))
                Call(coordinator, "Render", InteractionRequestProjector.ProjectForPlayer(Requests().Single(), 1));
            using (UiRegressionMetrics.Measure("input.layout")) Canvas.ForceUpdateCanvases();
        }

        [UnityTest] public IEnumerator MultiSelectConfirmsRealRequestAndContinuesExactlyOnce()
        {
            var firstInteractionId = Requests().Single().GetStableInteractionId();
            phase = "同请求热刷新"; Render();
            phase = "空闲三帧"; yield return null; yield return null; yield return null;
            var beforeRow = Button("Character Effect Option 0").GetInstanceID();
            phase = "选择第一项"; Click(Button("Character Effect Option 1"));
            Assert.That(Selected, Is.EquivalentTo(new[] { "candidate-b" }));
            Assert.That(Button("Confirm Selection").interactable, Is.False);
            phase = "选择第二项"; Click(Button("Character Effect Option 0"));
            Assert.That(Selected, Is.EquivalentTo(new[] { "candidate-a", "candidate-b" }));
            phase = "超过上限"; Click(Button("Character Effect Option 2"));
            Assert.That(Selected.Count, Is.EqualTo(2));
            var retainedRow = Button("Character Effect Option 0").GetInstanceID() == beforeRow;
            Assert.That(retainedRow, Is.True, "同请求多选更新应保留原条目");
            Assert.That(Count("选择第一项", "selection.build"), Is.Zero);
            Assert.That(Count("选择第一项", "hud.projection"), Is.EqualTo(1));
            phase = "确认并权威提交"; var confirm = Button("Confirm Selection"); Click(confirm);
            Assert.That(commands.Count, Is.EqualTo(1)); Assert.That(results.Single().Succeeded, Is.True, results.Single().LogMessage);
            Assert.That(State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(1));
            var normalized = State.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == firstInteractionId).NormalizedAnswer.Items;
            Assert.That(normalized.All(v => v.Kind == NormalizedValueKind.StableReference && v.ReferenceType == "candidate"), Is.True);
            Assert.That(normalized.Select(v => v.ReferenceId), Is.EqualTo(new[] { "candidate-a", "candidate-b" }));
            var next = Requests().Single();
            Assert.That(next.GetStableInteractionId(), Is.Not.EqualTo(firstInteractionId));
            Assert.That(next.PromptKey, Is.EqualTo("ui.selection.followup"));
            Assert.That(next.CandidateIds, Is.EqualTo(new[] { "candidate-final" }));
            Assert.That(Selected, Is.Empty, "新请求不能沿用前一页的两个选中项");
            Assert.That(Button("Confirm Selection").interactable, Is.False);
            ExecuteEvents.Execute(confirm.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
            Assert.That(commands.Count, Is.EqualTo(1));
            // 用户点击后继发生在下一可见帧；等待Unity提交本帧销毁/布局，仍保留原射线命中校验。
            yield return null;
            phase = "回答真实后继请求"; Click(Button("Character Effect Option 0")); Click(Button("Confirm Selection"));
            Assert.That(commands.Count, Is.EqualTo(2)); Assert.That(results.All(r => r.Succeeded), Is.True);
            Assert.That(Requests(), Is.Empty);
            Assert.That(State.EffectRuntime.EffectNodes.Single(n => n.EffectId == rootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(1), "回答后继不能重复前一效果");
            WriteEvidence(new { RetainedRow = retainedRow, ActualCommands = commands.Count,
                FollowupInteractionId = next.GetStableInteractionId(), FollowupCompleted = true,
                FollowupGold = State.FindPlayer(1).Resources.GoldVoucher });
            yield return null;
        }

        [UnityTest] public IEnumerator StaleCandidateVersionAndLostPermissionCannotChangeSelectionOrSubmit()
        {
            canSubmit = false; phase = "失去提交权限"; Click(Button("Character Effect Option 0")); Assert.That(Selected, Is.Empty);
            canSubmit = true;
            State.EffectRuntime.InteractionRequests.Single(r => r.OwnerEffectId == rootId && r.Status == "open").CandidateSetVersion++;
            phase = "候选版本过期"; Click(Button("Character Effect Option 0")); Assert.That(Selected, Is.Empty);
            Assert.That(commands, Is.Empty); Assert.That(State.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            WriteEvidence(new { StaleRejected = true, PermissionRejected = true });
            yield return null;
        }

        [UnityTest] public IEnumerator IdleEffectRowDoesNotQueryAuthorityAndActualClickStillRejectsStaleRequest()
        {
            var native = UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Effects/Prefabs/EffectRow.prefab"), hud.transform);
            try
            {
                var row = native.GetComponent(T("UiEffectRowView"));
                var rowType = row.GetType(); var bindingType = rowType.GetNestedType("Binding"); var data = Activator.CreateInstance(bindingType);
                foreach (var pair in new Dictionary<string, object> { ["StableItemId"] = "isolated-row", ["RequestId"] = "isolated-request", ["Revision"] = 1,
                    ["RowMode"] = Enum.Parse(rowType.GetNestedType("Mode"), "ChoiceExecute"), ["RightStatus"] = Enum.Parse(rowType.GetNestedType("Status"), "Use"),
                    ["Enabled"] = true, ["Title"] = "隔离效果", ["Description"] = "计数检查" }) bindingType.GetField(pair.Key).SetValue(data, pair.Value);
                int queries = 0, invoked = 0; bool current = true;
                rowType.GetMethods().Single(m => m.Name == "Bind" && m.GetParameters().Length == 4).Invoke(row, new object[] {
                    data, new Func<string, int, bool>((id, revision) => { queries++; return current && id == "isolated-request" && revision == 1; }),
                    null, new Action<string>(_ => invoked++) });
                queries = 0; phase = "效果行空闲三帧";
                yield return null; yield return null; yield return null;
                Assert.That(queries, Is.Zero, "逐帧外观刷新不得重新查询权威状态");
                var status = (Button)Read(row, "StatusButton");
                // 此处只检查单组件的事件与防重边界；遮挡/命中由上面的完整原页选择测试负责。
                ExecuteEvents.Execute(status.gameObject, new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
                Assert.That(queries, Is.EqualTo(1)); Assert.That(invoked, Is.EqualTo(1));
                current = false;
                ExecuteEvents.Execute(status.gameObject, new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
                Assert.That(queries, Is.EqualTo(2)); Assert.That(invoked, Is.EqualTo(1));
                WriteEvidence(new { IdleAuthorityQueries = 0, PerClickAuthorityQueries = 1, StaleRejected = true });
            }
            finally { UnityEngine.Object.Destroy(native); }
        }

        [UnityTest] public IEnumerator FormalAssemblyProfilesColdHotAndOneSynchronousReadScopeWithoutMutatingRules()
        {
            // 真实装配的初始状态读链；不冒充动作阶段、付款页或全局正常流程的实测。
            var launchHost = new GameObject("隔离正式装配上下文");
            try
            {
                var launch = launchHost.AddComponent(T("GameLaunchContext"));
                var configuration = new GameRuleConfiguration { PlayerCount = 2, MapId = "map-three-players", RuleModeId = "entrepreneur",
                    EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
                T("GameLaunchContext").GetMethod("ConfigureNewLocal").Invoke(launch, new object[] { 1, "", new List<PlayerSeat> {
                    new PlayerSeat { PlayerId = 1, PlayerName = "隔离甲" }, new PlayerSeat { PlayerId = 2, PlayerName = "隔离乙" } }, configuration, null, null });
                object assembled;
                phase = "正式装配";
                using (UiRegressionMetrics.Measure("assembly.build"))
                    assembled = T("GameSessionBootstrapper").GetMethod("Build", new[] { T("GameLaunchContext"), typeof(bool), typeof(bool) }).Invoke(null, new[] { launch, (object)false, false });
                session = (GameSession)Read(assembled, "Session"); session.ExecutionTimings.Enabled = true;
                controller.GetType().GetField("session", Flags).SetValue(controller, session);
                var before = JsonConvert.SerializeObject(State);
                phase = "正式装配冷投影"; Visible();
                phase = "正式装配热连续读取";
                for (int i = 0; i < 6; i++) Visible();
                phase = "同输入范围六次读取";
                using ((IDisposable)Call(controller, "BeginVisibleRead"))
                {
                    var first = Visible();
                    for (int i = 1; i < 6; i++) Assert.That(Visible(), Is.SameAs(first));
                }
                Assert.That(Count("正式装配热连续读取", "hud.projection"), Is.EqualTo(6));
                Assert.That(Count("同输入范围六次读取", "hud.projection"), Is.EqualTo(1));
                Assert.That(JsonConvert.SerializeObject(State), Is.EqualTo(before));
                var installed = YC.Infrastructure.Lua.ExternalContentPack.Current;
                var actualRegistry = (EffectRegistry)Read(assembled, "EffectRegistry");
                WriteEvidence(new { ActualPhase = State.Phase, UnscopedProjections = 6, ScopedProjections = 1, RulesUnchanged = true,
                    FormalIdentity = new {
                        installedContentHash = installed.ContentHash, activeContentHash = State.ContentPackHash,
                        SourceFactsVersion = YC.Infrastructure.Lua.SourceFactsLuaCatalog.Version,
                        GameProtocolVersions.Identity, host = HostSnapshotDto.CurrentSchemaVersion,
                        journal = HostJournalDto.CurrentSchemaVersion, runtime = EffectRuntimeState.CurrentSchemaVersion,
                        entity = GameProtocolVersions.Entity, room = GameProtocolVersions.Room,
                        modules = installed.Manifest["luaModules"], installedScriptCount = installed.Scripts.Count,
                        activeSourceCount = actualRegistry.AbilityDeclarations.Count,
                        activeHandlerCount = ((IDictionary)Read(actualRegistry, "abilityHandlerHashes")).Count,
                        activeEntrepreneurs = actualRegistry.ContentDefinitions.Entrepreneurs.Count,
                        source = "真实Bootstrapper结果和同次已加载安装包；未使用旧DLL或私有Lua配方"
                    } });
                yield return null;
            }
            finally { UnityEngine.Object.Destroy(launchHost); }
        }

        private void WriteEvidence(object outcome)
        {
            var args = Environment.GetCommandLineArgs(); var index = Array.IndexOf(args, "-ycSelectionEvidence");
            var json = JsonConvert.SerializeObject(new { Isolated = true, NormalAcceptance = false,
                Assemblies = new[] { typeof(GameSession).Assembly, typeof(EffectSpec).Assembly, coordinator.GetType().Assembly }.Select(a => new { a.FullName, Mvid = a.ManifestModule.ModuleVersionId }),
                State.GameId, State.ContentPackHash, Outcome = outcome, Samples = samples, Execution = session.ExecutionTimings.Snapshot() }, Formatting.Indented);
            if (index >= 0 && index + 1 < args.Length)
                using (var stream = new FileStream(Path.Combine(args[index + 1], TestContext.CurrentContext.Test.Name + "-" + phase + ".json"), FileMode.CreateNew))
                using (var writer = new StreamWriter(stream)) writer.Write(json);
            else Debug.Log(json);
        }

        [UnityTearDown] public IEnumerator DisposeIsolatedFlow()
        {
            UiRegressionMetrics.Recorded -= Record; UiRegressionMetrics.Enabled = false;
            if (session != null && coordinator != null)
            {
                phase = "结束状态";
                WriteEvidence(new { CurrentState = State, Commands = commands, Results = results,
                    TestResult = TestContext.CurrentContext.Result.Outcome.ToString() });
            }
            if (coordinator != null) Call(coordinator, "Dispose");
            if (hud != null) UnityEngine.Object.Destroy(hud);
            if (eventHost != null) UnityEngine.Object.Destroy(eventHost);
            if (controllerHost != null) UnityEngine.Object.Destroy(controllerHost);
            coordinator = dialog = controller = null;
            yield return null;
        }
    }
}
