using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Multiplayer;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class DeterministicSimulationUiConsumptionTests
    {
        private static Type PresentationType(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static GameState ReadState(GameState simulation, GameStateView view, int viewer) =>
            (GameState)PresentationType("PlayerUiReadStateFactory").GetMethod("Create",
                BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { simulation, view, viewer });

        [Test]
        public void LocalSubmissionKeepsSynchronousCommitAndSecondPlayerReceipt()
        {
            var state = new GameState { GameId = "热座同步保护局", CurrentPlayerId = 1,
                RuleConfiguration = new GameRuleConfiguration { PlayerStageSchedulingMode = PlayerStageSchedulingMode.Sequential } };
            state.Players.Add(new PlayerState { PlayerId = 1 });
            state.Players.Add(new PlayerState { PlayerId = 2 });
            var session = new GameSession(state);
            var handler = new LocalSynchronousHandler(); session.RegisterHandler(handler);
            var events = new List<string>(); session.StateCommitted += () => events.Add("commit");
            var type = PresentationType("CommandSubmissionController");
            var controller = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { session, null, 1, null, (Action)(() => { }), (Action<string>)(_ => { }),
                    (Action<string>)(_ => { }), null,
                    (Action<string, string, bool, CommandErrorCode>)((game, id, accepted, error) =>
                    { Assert.That(game, Is.EqualTo(state.GameId)); Assert.That(accepted, Is.True); events.Add(id); }) }, null);
            try
            {
                type.GetMethod("Initialize", new[] { typeof(EffectRegistry) }).Invoke(controller, new object[] { new EffectRegistry() });
                Assert.That(type.GetProperty("UsesDeterministicSimulation").GetValue(controller), Is.False);
                for (int player = 1; player <= 2; player++)
                {
                    var command = new GameCommand { CommandId = "热座原回执" + player, Kind = GameCommandKind.EndAction, PlayerId = player };
                    var args = new object[] { command, false };
                    var result = (CommandResult)type.GetMethod("Submit").Invoke(controller, args);
                    events.Add("return");
                    Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
                    Assert.That(args[1], Is.EqualTo(true));
                    Assert.That(session.State.CurrentPlayerId, Is.EqualTo(player + 1));
                    Assert.That(handler.ThreadId, Is.EqualTo(System.Threading.Thread.CurrentThread.ManagedThreadId));
                }
                Assert.That(events, Is.EqualTo(new[] { "commit", "热座原回执1", "return", "commit", "热座原回执2", "return" }));
                Assert.That(session.State.RuleConfiguration.PlayerStageSchedulingMode, Is.EqualTo(PlayerStageSchedulingMode.Sequential));
            }
            finally { ((IDisposable)controller).Dispose(); }
        }

        private sealed class LocalSynchronousHandler : IGameCommandHandler
        {
            internal int ThreadId;
            public bool CanHandle(GameCommand command) => command.Kind == GameCommandKind.EndAction;
            public CommandResult Handle(GameState state, GameCommand command)
            {
                ThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                state.CurrentPlayerId = command.PlayerId + 1;
                return CommandResult.SuccessResult(new List<YC.Domain.Events.GameEvent>(), "热座同步夹具");
            }
        }

        [Test]
        public void PlayerUiReadStateKeepsCompleteSimulationAndHidesOtherHandsAndSecretReturns()
        {
            var state = new GameState { GameId = "本人展示局", ContentPackHash = "本人展示内容包",
                MapId = StaticMapDefinitions.ThreePlayerMapId, CurrentPlayerId = 2 };
            state.Players.Add(new PlayerState { PlayerId = 1, HandCardIds = { "本人手牌" } });
            state.Players.Add(new PlayerState { PlayerId = 2, HandCardIds = { "他人秘密手牌" } });
            state.Decks.CharacterDeck.Add("秘密牌堆顺序");
            state.RandomRuntime = GameplayRandomRuntime.Create(state.GameId);
            state.GameBox.Cards.Add(new GameBoxCardState { InstanceId = "本人秘密回盒", CardId = "本人牌", PrivateForPlayerId = 1 });
            state.GameBox.Cards.Add(new GameBoxCardState { InstanceId = "他人秘密回盒", CardId = "他人牌", PrivateForPlayerId = 2 });
            state.GameBox.Cards.Add(new GameBoxCardState { InstanceId = "公开回盒", CardId = "公开牌" });
            foreach (int player in new[] { 1, 2 })
                state.EffectRuntime.InteractionRequests.Add(new InteractionRequest { InteractionId = "选择" + player,
                    AnsweringPlayerId = player, Visibility = "owner", InteractionTypeId = "character.use.mode",
                    AnswerSchema = "candidate_id", CandidateIds = new List<string> { "strategy", "tactic" } });
            string original = JsonConvert.SerializeObject(state);
            var view = GameStateViewProjector.ProjectForPlayer(state, 1);
            var displayed = ReadState(state, view, 1);
            Assert.That(displayed, Is.Not.Null);
            Assert.That(displayed, Is.Not.SameAs(state));
            Assert.That(displayed.FindPlayer(1).HandCardIds, Is.EqualTo(new[] { "本人手牌" }));
            Assert.That(displayed.FindPlayer(2).HandCardIds, Is.Empty);
            Assert.That(displayed.GameBox.Cards.Select(card => card.InstanceId), Is.EquivalentTo(new[] { "本人秘密回盒", "公开回盒" }));
            Assert.That(displayed.Decks.CharacterDeck, Is.Empty);
            Assert.That(displayed.RandomRuntime, Is.Null);
            Assert.That(displayed.EffectRuntime.InteractionRequests.Select(request => request.AnsweringPlayerId), Is.EqualTo(new[] { 1 }));
            displayed.FindPlayer(1).Resources.GoldVoucher = 99;
            displayed.GameBox.Cards.Clear();
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(original), "展示副本的读取和修改不得裁剪或回写模拟状态");
        }

        [Test]
        public void PlayerUiReadStateRejectsHostForeignViewerAndStaleProjection()
        {
            var state = new GameState { GameId = "投影身份局", ContentPackHash = "投影内容包",
                Players = { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } };
            Assert.That(ReadState(state, GameStateViewProjector.ProjectForHost(state), 1), Is.Null);
            Assert.That(ReadState(state, GameStateViewProjector.ProjectForPlayer(state, 2), 1), Is.Null);
            Assert.That(ReadState(state, GameStateViewProjector.ProjectForSpectator(state), 1), Is.Null);
            var foreignGame = GameStateViewProjector.ProjectForPlayer(state, 1); foreignGame.GameId = "旧局";
            Assert.That(ReadState(state, foreignGame, 1), Is.Null);
            var foreignPack = GameStateViewProjector.ProjectForPlayer(state, 1); foreignPack.ContentPackHash = "旧内容包";
            Assert.That(ReadState(state, foreignPack, 1), Is.Null);
            var stale = GameStateViewProjector.ProjectForPlayer(state, 1); stale.StateRevision++;
            Assert.That(ReadState(state, stale, 1), Is.Null);
            Assert.That(ReadState(state, null, 1), Is.Null);
        }

        [Test, Category("原生界面模拟消费")]
        public void NativeSubmissionLocalApplicationReleasesOriginalCoordinatorAndShowsNextRequest()
        {
            using (var fixture = new NativeConsumerFixture())
            {
                Assert.That(fixture.CanSubmit, Is.False, "初始窗口尚未汇合，不能提交");
                Assert.That(fixture.SubmitCurrent(), Is.False);
                fixture.PermitInitialWindow();
                Assert.That(fixture.CanSubmit, Is.True);
                Assert.That(fixture.Adapter.LocalPlayerId, Is.EqualTo(2));
                Assert.That(fixture.Session.State.CurrentPlayerId, Is.EqualTo(1), "回答他人效果请求不能改写全局当前玩家");
                string firstRequest = fixture.RequestRouter.CurrentRequest.InteractionId;
                Assert.That(fixture.SubmitCurrent(), Is.True);
                Assert.That(fixture.InFlight, Is.Not.Empty);
                Assert.That(fixture.CanSubmit, Is.False, "编序接纳尚未本地执行，不得解除等待");
                string submitted = fixture.InFlight;
                Assert.That(fixture.ResolveForeign(submitted, 1, fixture.Session.State.GameId), Is.False);
                Assert.That(fixture.ResolveForeign(submitted, 2, "旧局"), Is.False);
                Assert.That(fixture.InFlight, Is.EqualTo(submitted));
                fixture.AwaitLocalApplication(1);
                Assert.That(fixture.ResultNotifications, Is.EqualTo(1));
                Assert.That(fixture.InFlight, Is.Empty);
                Assert.That(fixture.RequestRouter.CurrentRequest, Is.Not.Null);
                Assert.That(fixture.RequestRouter.CurrentRequest.InteractionId, Is.Not.EqualTo(firstRequest));
                Assert.That(fixture.DialogShowing, Is.True, "本地结果必须经原路由刷新真实下一请求页面");
                Assert.That(fixture.ResolveForeign(submitted, 2, fixture.Session.State.GameId), Is.False);
                Assert.That(fixture.ResultNotifications, Is.EqualTo(1), "重复和Host核验不能重复解锁");
                fixture.StopSubmission();
                Assert.That(fixture.CanSubmit, Is.False);
                Assert.That(fixture.SubmitCurrent(), Is.False);
            }
        }

        [TestCase("character"), TestCase("enterprise"), TestCase("route"), Category("原生界面模拟消费")]
        public void NativeRefreshRejectionDoesNotResubmitOriginalChoice(string receiver)
        {
            using (var fixture = new NativeConsumerFixture(receiver == "character" ? "setup.basic_character.discard" :
                receiver == "enterprise" ? "setup.enterprise.choose" : "route_resource.selection", false))
            {
                fixture.PermitInitialWindow();
                var request = InteractionRequestProjector.ProjectForPlayer(fixture.VisibleRequests().Single(), 2);
                request.StateRevision--;
                var command = EffectInteractionCommands.Answer(request, 2, new[] { "strategy" });
                var target = fixture.CreateResultReceiver(receiver);
                var type = target.GetType();
                var intent = (InteractionCommandIntent)type.GetField("confirmedIntent", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
                Assert.That(intent.Begin(fixture.Session.State.GameId, request, command, true), Is.True);
                fixture.SetField(target, receiver == "character" ? "inFlightCommandId" : "inFlight", command.CommandId);
                if (receiver == "character") fixture.SetField(target, "inFlightGameId", fixture.Session.State.GameId);
                var selected = type.GetField("selectedCandidates", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target) as HashSet<string>;
                selected?.Add("strategy");
                var candidates = request.CandidateIds.ToArray();
                var result = fixture.SubmitDirect(command);
                Assert.That(result.CommandResult.Succeeded, Is.False);
                Assert.That(result.CommandResult.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
                Assert.That(fixture.ResultNotifications, Is.EqualTo(1));
                Assert.That(intent.IsBusy, Is.False);
                Assert.That(intent.WaitingForRefresh, Is.False);
                request.StateRevision++;
                Assert.That(intent.TryRebuild(fixture.Session.State.GameId, request, string.Empty, out _), Is.False);
                Assert.That(request.CandidateIds, Is.EqualTo(candidates));
                if (selected != null) Assert.That(selected, Does.Contain("strategy"));
                Assert.That(fixture.DirectSubmissions, Is.EqualTo(1));
                Assert.That(fixture.Host.LastSequencedSequence, Is.EqualTo(0));
                fixture.DriveTransport();
                Assert.That(fixture.Host.LastAppliedSequence, Is.EqualTo(0), "被拒提案不得执行或重签入队");
            }
        }

        [Test, Category("原生界面模拟消费")]
        public void NativeEnterprisePermitRefreshKeepsDraftAndCollapsedState()
        {
            using (var fixture = new NativeConsumerFixture("setup.enterprise.choose"))
            {
                var request = InteractionRequestProjector.ProjectForPlayer(fixture.VisibleRequests().Single(), 2);
                bool available = false;
                var target = fixture.CreateEnterpriseReceiver(() => available);
                var registry = fixture.Hud.GetType().GetProperty("DialogRegistry").GetValue(fixture.Hud);
                var view = (Component)registry.GetType().GetMethod("InstantiateEnterpriseSelection").Invoke(registry, new object[] { fixture.PageHost, false });
                view.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, null);
                var draft = (EnterpriseSelectionDraft)view.GetType().GetProperty("Draft").GetValue(view);
                var projection = new EnterpriseSelectionProjection { GameId = fixture.Session.State.GameId, ViewerPlayerId = 2,
                    RequestId = request.InteractionId, Revision = request.StateRevision, CandidateSetId = request.CandidateSetId,
                    CandidateSetVersion = request.CandidateSetVersion, OwnerEffectId = request.OwnerEffectId,
                    RequestContract = InteractionCommandIntent.ContractKey(request), Mode = EnterpriseSelectionMode.Company,
                    MinSelections = 1, MaxSelections = 1, SubmissionAvailable = false,
                    Boards = request.CandidateIds.Select(id => new EnterpriseBoardProjection { Id = id, Label = id }).ToArray(),
                    Targets = request.CandidateIds.Select(id => new EnterpriseSelectionTarget { Id = id, BoardId = id, CompanyId = id, Available = true, Label = id }).ToArray() };
                view.GetType().GetMethod("BindSubmission").Invoke(view, new object[] {
                    (Func<EnterpriseSelectionProjection>)(() => projection), (Action<EnterpriseSelectionIntent>)(_ => Assert.Fail("许可刷新不得提交")), null });
                fixture.Frame.GetType().GetMethod("SetRequest", new[] { typeof(string), typeof(int) }).Invoke(fixture.Frame, new object[] { request.InteractionId, request.StateRevision });
                view.GetType().GetMethod("Present").Invoke(view, new object[] { projection, true });
                Assert.That(draft.Select("strategy"), Is.True);
                fixture.SetField(target, "enterprise", view);
                fixture.SetField(target, "renderedGame", fixture.Session.State.GameId);
                fixture.SetField(target, "renderedRequest", request);
                fixture.SetField(target, "renderedId", request.InteractionId);
                fixture.SetField(target, "renderedRevision", request.StateRevision);
                fixture.SetField(target, "renderedCandidateVersion", request.CandidateSetVersion);
                fixture.SetField(target, "renderedPlayer", 2);
                var confirm = (Button)view.GetType().GetField("confirm", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);
                var scroll = (ScrollRect)view.GetType().GetProperty("CandidateScroll").GetValue(view);
                fixture.Frame.GetType().GetMethod("ToggleEffectPage", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(fixture.Frame, null);
                Assert.That(view.gameObject.activeSelf, Is.False, "夹具使用原HUD收起流程");
                var position = scroll.content.anchoredPosition;
                var selected = draft.SelectedIds.ToArray();
                foreach (bool permitted in new[] { true, false })
                {
                    available = permitted;
                    ((IInteractionRequestRenderer)target).Render(request);
                    Assert.That(confirm.interactable, Is.EqualTo(permitted));
                    Assert.That(draft.Projection.SubmissionAvailable, Is.EqualTo(permitted));
                    Assert.That(draft.SelectedIds, Is.EqualTo(selected));
                    Assert.That(scroll.content.anchoredPosition, Is.EqualTo(position));
                    Assert.That(view.gameObject.activeSelf, Is.False);
                    Assert.That(draft.Projection.Revision, Is.EqualTo(request.StateRevision));
                }
            }
        }

        // 只替代跨进程运输：真实对端Coordinator从完整状态克隆生成Ready。
        // 提交控制器、Mirror主机本地入口、规则执行、两个原路由及预制体实例均使用实际代码。
        private sealed class NativeConsumerFixture : IDisposable
        {
            private readonly Type submissionType = PresentationType("CommandSubmissionController");
            private readonly Type coordinatorType = PresentationType("CharacterAbilityInteractionUiCoordinator");
            private readonly Type adapterType = PresentationType("MobileCityGameplayAdapter");
            private readonly Type contextType = PresentationType("GameLaunchContext");
            private readonly Type transportType = Type.GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport, Assembly-CSharp", true);
            private readonly PropertyInfo launchInstance;
            private readonly object previousLaunch;
            private readonly GameObject root, contextObject;
            private readonly object submission, consumer, dialog;
            private readonly object transport;
            private readonly DeterministicSimulationCoordinator peer;
            private readonly InteractionRouter interactionRouter = new InteractionRouter(_ => { });
            private readonly EffectRegistry registry = new EffectRegistry();
            private bool submissionStopped;
            private readonly bool renderRequests;
            private readonly List<IDisposable> additionalConsumers = new List<IDisposable>();
            internal readonly GameSession Session;
            internal readonly IGameplayContext Adapter;
            internal readonly InteractionRequestRouter RequestRouter = new InteractionRequestRouter();
            internal int ResultNotifications;
            internal int DirectSubmissions;
            internal object Hud, Frame, PageHost;
            internal DeterministicSimulationCoordinator Host =>
                (DeterministicSimulationCoordinator)transportType.GetProperty("SimulationCoordinator").GetValue(transport);
            internal bool CanSubmit => (bool)adapterType.GetProperty("CanSubmitCommands").GetValue(Adapter);
            internal string InFlight => (string)coordinatorType.GetField("inFlightCommandId", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(consumer);
            internal bool DialogShowing => (bool)dialog.GetType().GetProperty("IsShowing").GetValue(dialog);

            internal NativeConsumerFixture(string interactionType = CharacterLifecycleService.ModeInteractionTypeId, bool renderRequests = true)
            {
                this.renderRequests = renderRequests;
                Assert.That(transportType.GetProperty("Instance").GetValue(null), Is.Null, "测试不能接管既有运输实例");
                launchInstance = contextType.GetProperty("Instance"); previousLaunch = launchInstance.GetValue(null);
                launchInstance.GetSetMethod(true).Invoke(null, new object[] { null });
                root = new GameObject("真实界面模拟消费检查", typeof(RectTransform)); root.SetActive(false);
                contextObject = new GameObject("模拟消费检查启动上下文");
                try
                {
                    var state = new GameState { GameId = "真实界面模拟消费局", ContentPackHash = "模拟消费检查内容包",
                        Phase = GamePhase.ActionRound1, Round = 1, CurrentPlayerId = 1, StartPlayerId = 1,
                        UseSeatTurnOrder = true, LaunchPlayerOrder = new List<int> { 1, 2 }, MapId = StaticMapDefinitions.ThreePlayerMapId };
                    foreach (int player in new[] { 1, 2 }) state.Players.Add(new PlayerState { PlayerId = player, CityLocationId = "B-01" });
                    state.RandomRuntime = GameplayRandomRuntime.Create(state.GameId);
                    var round = new RoundExecutionService(registry);
                    Assert.That(round.PrepareActionWindowForSmoke(state, 1).IsValid, Is.True);
                    round.MarkMainActionComplete(state, 1);
                    registry.Register(new EffectRegistration("test.ui.consumer.input", ctx =>
                    {
                        if (ctx.GetLatestInteractionAnswer() != null && ctx.Node.FlowStage == "second") return EffectStepResult.Completed();
                        return EffectStepResult.Continue(ctx.Node.FlowStage == "" ? "first" : "second").AddInteraction(new EffectInteractionSpec
                        {
                            InteractionTypeId = interactionType, AnsweringPlayerId = 2,
                            Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1,
                            CandidateIds = { "strategy", "tactic" }
                        });
                    }, EffectExecutorKind.IntrinsicFlow));
                    var engine = round.CreateCommandExecutor(state);
                    Assert.That(engine.TryCreatePlayerActionEffect(1, EffectSpec.Create("test.ui.consumer.input", playerId: 1), "1", "test.ui.consumer", out _), Is.True);
                    Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
                    Session = new GameSession(state); Session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
                    var peerSession = new GameSession(GameStateCloneService.DeepClone(state));
                    peerSession.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
                    peer = new DeterministicSimulationCoordinator(peerSession,
                        DeterministicSimulationSessionOptions.CreateInitial(false, 1, peerSession, new[] { 1, 2 }));
                    peer.Begin();
                    var context = contextObject.AddComponent(contextType);
                    contextType.GetMethods().Single(method => method.Name == "Configure" && method.GetParameters().Length == 9).Invoke(context,
                        new object[] { LaunchMode.Host, 2, "模拟消费检查房间", new List<PlayerSeat> { new PlayerSeat { PlayerId = 1 }, new PlayerSeat { PlayerId = 2 } },
                            new GameRuleConfiguration { MapId = state.MapId, PlayerCount = 2 }, new[] { 1, 2 }, state.GameId, 42, state.RandomRuntime });
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
                    Assert.That(prefab, Is.Not.Null);
                    var hudObject = UnityEngine.Object.Instantiate(prefab, root.transform, false);
                    var cityController = hudObject.GetComponentInChildren(PresentationType("MobileCityInteractionController"), true);
                    if (cityController != null) UnityEngine.Object.DestroyImmediate(cityController);
                    root.SetActive(true);
                    var hud = hudObject.GetComponent(PresentationType("GameplayInteractionHudView"));
                    Hud = hud;
                    var dialogRegistry = hud.GetType().GetProperty("DialogRegistry").GetValue(hud);
                    var frame = hud.GetType().GetProperty("Frame").GetValue(hud);
                    frame.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(frame, null);
                    var pageHost = frame.GetType().GetProperty("ContentRect").GetValue(frame);
                    Frame = frame; PageHost = pageHost;
                    dialog = Activator.CreateInstance(PresentationType("CharacterCardEffectChoiceDialog"),
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new[] { dialogRegistry, pageHost }, null);
                    object adapter = null;
                    submission = Activator.CreateInstance(submissionType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new object[] { Session, context, 2, null, (Action)Refresh, (Action<string>)(_ => { }), (Action<string>)(_ => { }), null,
                            (Action<string, string, bool, CommandErrorCode>)((game, id, accepted, error) =>
                            { ResultNotifications++; interactionRouter.NotifyInteractionCommandResolved(game, id, accepted, error); }) }, null);
                    var getSubmission = Expression.Lambda(typeof(Func<>).MakeGenericType(submissionType), Expression.Constant(submission, submissionType)).Compile();
                    adapter = Activator.CreateInstance(adapterType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new object[] { (Func<GameState>)(() => ReadState(Session.State, Session.View, 2)), (Func<int>)(() => 2), (Func<bool>)(() => false),
                            (Action<int>)(_ => Assert.Fail("联机不得切换本人身份")), getSubmission, (Action)(() => { }) }, null);
                    Adapter = (IGameplayContext)adapter;
                    consumer = Activator.CreateInstance(coordinatorType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new object[] { (Func<GameState>)(() => Adapter.CurrentState), (Func<int>)(() => Adapter.LocalPlayerId), dialog,
                            (Action<IReadOnlyList<WorkflowHighlight>>)(_ => { }), (Action)(() => { }),
                            (Action<GameCommand>)(command => ((IGameCommandPort)Adapter).Submit(command)), (Action<string>)(_ => { }) }, null);
                    coordinatorType.GetProperty("CanSubmit").SetValue(consumer, (Func<bool>)(() => CanSubmit));
                    BindRefreshFlag(consumer);
                    coordinatorType.GetProperty("GetVisibleState").SetValue(consumer, (Func<GameStateView>)(() => Session.View));
                    coordinatorType.GetProperty("GetVisibleRequests").SetValue(consumer,
                        (Func<IReadOnlyList<InteractionRequest>>)(() => VisibleInteractionRequestSource.Read(null, Session.View, 2)));
                    interactionRouter.Register((IInteraction)consumer); RequestRouter.Register((IInteractionRequestRenderer)consumer);
                    var transportObject = new GameObject("模拟消费检查运输组件");
                    transport = transportObject.AddComponent(transportType);
                    transportType.GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(transport, null);
                    Assert.That(transportType.GetProperty("Instance").GetValue(null), Is.SameAs(transport));
                    submissionType.GetMethod("Initialize", new[] { typeof(EffectRegistry) }).Invoke(submission, new object[] { registry });
                    Assert.That(transport, Is.Not.Null);
                    AwaitPublished(() => Host.ReadBoundary(0) != null);
                    Assert.That(Host.IsBackgroundExecutionEnabled, Is.True, "正式运输入口必须显式启用执行owner");
                    Assert.That(Host.IsStopped, Is.False, Host.LastFault?.Diagnostic);
                    Refresh();
                }
                catch { Dispose(); throw; }
            }

            private void Refresh()
            {
                if (renderRequests && consumer != null) RequestRouter.RouteOpen(VisibleInteractionRequestSource.Read(null, Session.View, 2), 2);
            }
            internal IReadOnlyList<InteractionRequest> VisibleRequests() => VisibleInteractionRequestSource.Read(null, Session.View, 2);
            internal void SetField(object target, string name, object value) => target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
            private void BindRefreshFlag(object target) => target.GetType().GetProperty("RequiresExplicitRefresh").SetValue(target,
                (Func<bool>)(() => (bool)submissionType.GetProperty("RequiresExplicitRefreshForCurrentResult").GetValue(submission)));
            internal object CreateEnterpriseReceiver(Func<bool> permitted)
            {
                var expansion = Hud.GetType().GetProperty("ExpansionUi").GetValue(Hud);
                expansion.GetType().GetMethod("Configure").Invoke(expansion, new object[] {
                    (Func<GameStateView>)(() => Session.View), (Func<int>)(() => 2), new EnterpriseDefinitions(),
                    new Dictionary<string, CharacterModeContract>(), (Action<GameCommand>)(_ => DirectSubmissions++) });
                var target = Activator.CreateInstance(PresentationType("EnterpriseInteractionUiCoordinator"), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { expansion, Hud, (Func<IReadOnlyList<InteractionRequest>>)VisibleRequests, (Func<int>)(() => 2),
                        (Action<GameCommand>)(_ => DirectSubmissions++) }, null);
                target.GetType().GetProperty("CanSubmit").SetValue(target, permitted);
                BindRefreshFlag(target); interactionRouter.Register((IInteraction)target); additionalConsumers.Add((IDisposable)target);
                return target;
            }
            internal object CreateResultReceiver(string name)
            {
                if (name == "character") return consumer;
                if (name == "enterprise") return CreateEnterpriseReceiver(() => CanSubmit);
                var query = new MapQueryService(StaticMapDefinitions.CreateThreePlayerMap());
                var target = Activator.CreateInstance(PresentationType("RouteResourceSelectionInteractionUiCoordinator"), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { (Func<GameState>)(() => Adapter.CurrentState), (Func<GameStateView>)(() => Session.View), (Func<int>)(() => 2),
                        (Func<IReadOnlyList<InteractionRequest>>)VisibleRequests, new RouteSelectionSessionAuthority(query), new RouteResourceSelectionService(query),
                        null, null, (Action<GameCommand>)(_ => DirectSubmissions++), (Action)(() => { }) }, null);
                BindRefreshFlag(target); interactionRouter.Register((IInteraction)target);
                return target;
            }
            internal WorkflowSubmissionResult SubmitDirect(GameCommand command)
            { DirectSubmissions++; return ((IGameCommandPort)Adapter).Submit(command); }
            internal bool SubmitCurrent() => (bool)coordinatorType.GetMethod("TrySubmitUseSelection").Invoke(consumer,
                new object[] { RequestRouter.CurrentRequest, "strategy" });
            internal void DriveTransport() => transportType.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(transport, null);
            private void AwaitPublished(Func<bool> ready)
            {
                // 隔离夹具只排空正式运输通知，不同步Pump规则或改写会话。
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    DriveTransport();
                    if (ready()) return;
                    if (Host.BackgroundExecutionException != null) throw Host.BackgroundExecutionException;
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) Assert.Fail("等待真实执行线程发表结果超时。");
                    System.Threading.Thread.Sleep(1);
                }
            }
            internal void AwaitLocalApplication(long sequence)
            {
                AwaitPublished(() => Host.LastAppliedSequence >= sequence && ResultNotifications > 0);
                Assert.That(Host.LastAppliedSequence, Is.EqualTo(sequence));
            }
            internal void PermitInitialWindow()
            {
                var boundary = Host.ReadBoundary(0); Assert.That(boundary, Is.Not.Null);
                Assert.That(peer.ReceiveBoundary(boundary), Is.True);
                Assert.That(Host.ReceiveWindowReady(1, new SimulationWindowReadyDto { GameId = boundary.GameId,
                    SimulationEpoch = boundary.SimulationEpoch, MembershipVersion = boundary.MembershipVersion,
                    PlayerId = 1, ReplicaId = DeterministicSimulationSessionOptions.ReplicaIdForPlayer(1), Boundary = peer.ReadBoundary(0) }), Is.True);
                AwaitPublished(() => CanSubmit);
                Refresh();
            }
            internal bool ResolveForeign(string command, int player, string game) => (bool)submissionType.GetMethod("ResolveSubmitted",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(submission,
                    new object[] { command, player, game, true, CommandErrorCode.None, false });
            internal void StopSubmission() { ((IDisposable)submission).Dispose(); submissionStopped = true; }
            public void Dispose()
            {
                if (submission != null && !submissionStopped) ((IDisposable)submission).Dispose();
                if (consumer != null) ((IDisposable)consumer).Dispose();
                foreach (var extra in additionalConsumers)
                {
                    ((IInteraction)extra).Cancel();
                    foreach (var field in new[] { "enterprise", "department" })
                    {
                        var page = extra.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(extra) as Component;
                        if (page != null) UnityEngine.Object.DestroyImmediate(page.gameObject);
                    }
                    extra.Dispose();
                }
                peer?.Stop();
                if (transport != null)
                {
                    transportType.GetMethod("Shutdown").Invoke(transport, null);
                    transportType.GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(transport, null);
                    UnityEngine.Object.DestroyImmediate(((Component)transport).gameObject);
                }
                if (Frame != null) Frame.GetType().GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Frame, null);
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (contextObject != null) UnityEngine.Object.DestroyImmediate(contextObject);
                launchInstance?.GetSetMethod(true).Invoke(null, new[] { previousLaunch });
            }
        }
    }
}
