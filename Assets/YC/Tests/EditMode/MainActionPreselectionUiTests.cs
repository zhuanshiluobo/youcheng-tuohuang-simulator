using System;
using System.Collections;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Linq.Expressions;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.TestTools;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.Exploration;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using YC.Infrastructure.Multiplayer;
using YC.Presentation.Workflows;
using InteractionMode = YC.Presentation.Workflows.InteractionMode;

namespace YC.Tests.EditMode
{
    public sealed class MainActionPreselectionUiTests
    {
        [Test]
        public void DeployDraftSwitchClearsConfirmationAndExactRejectionReleasesBusy()
        {
            var f = Production(); var before = Snapshot(f.State);
            var target = f.Influence.GetLegalPlacementSlotIds(f.State, 1).First();
            f.Presenter.BeginDeploy(); f.Presenter.SelectSlot(target);
            Assert.That(f.Port.Commands, Is.Empty); Assert.That(Snapshot(f.State), Is.EqualTo(before));
            f.Presenter.Cancel(); f.Presenter.BeginDeploy(); f.Presenter.SelectSlot(target);
            Assert.That(f.Port.Commands, Is.Empty, "切换前首击不能确认新草稿");
            f.Presenter.SelectSlot(target); var command = f.Port.Commands.Single();
            Assert.That(command.TargetId, Is.EqualTo(target)); Assert.That(f.Presenter.IsDeploySubmissionInFlight, Is.True);
            f.Presenter.Cancel(); f.Presenter.BeginDispatch();
            Assert.That(f.Presenter.IsDeploySubmissionInFlight, Is.True);
            f.Presenter.NotifyInteractionCommandResolved("other.game", command.CommandId, false, CommandErrorCode.InvalidTarget);
            Assert.That(f.Presenter.IsDeploySubmissionInFlight, Is.True);
            f.Presenter.NotifyInteractionCommandResolved(f.State.GameId, command.CommandId, false, CommandErrorCode.InvalidTarget);
            Assert.That(f.Presenter.IsDeploySubmissionInFlight, Is.False); Assert.That(f.Presenter.HasPendingConfirmation, Is.False);
            f.Presenter.NotifyInteractionCommandResolved(f.State.GameId, command.CommandId, false, CommandErrorCode.InvalidTarget);
            Assert.That(f.Port.Commands.Count, Is.EqualTo(1));
        }

        [Test]
        public void DispatchPreselectsExactInstanceAndTargetWithoutWritingState()
        {
            var f = Production();
            var source = PlaceMovable(f); var before = Snapshot(f.State);
            f.Presenter.BeginDispatch(); f.Presenter.SelectSlot(source.SlotId);
            Assert.That(f.Presenter.SourceInfluenceId, Is.EqualTo(source.InfluenceId));
            Assert.That(f.Presenter.SourceSlotId, Is.EqualTo(source.SlotId));
            Assert.That(f.Port.Commands, Is.Empty); Assert.That(Snapshot(f.State), Is.EqualTo(before));
            var target = f.Influence.GetLegalMovementSlotIds(f.State, source.InfluenceId)
                .First(id => f.Influence.CanMove(f.State, 1, source.InfluenceId, id).IsValid);
            f.Presenter.SelectSlot(target); f.Presenter.SelectSlot(target);
            var command = f.Port.Commands.Single();
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.DispatchInfluence));
            Assert.That(command.SourceId, Is.EqualTo(source.InfluenceId)); Assert.That(command.TargetId, Is.EqualTo(target));
            f.Presenter.Cancel(); Assert.That(f.Presenter.IsDispatchSubmissionInFlight, Is.True);
            f.Presenter.NotifyInteractionCommandResolved(f.State.GameId, "other.command", false, CommandErrorCode.InvalidTarget);
            Assert.That(f.Presenter.IsDispatchSubmissionInFlight, Is.True);
            f.Presenter.NotifyInteractionCommandResolved(f.State.GameId, command.CommandId, false, CommandErrorCode.InvalidTarget);
            Assert.That(f.Presenter.IsDispatchSubmissionInFlight, Is.False); Assert.That(f.Port.Commands.Count, Is.EqualTo(1));
        }

        [Test]
        public void MainDraftsSwitchToOtherMainBuildAndQuickWithoutCancellationCommands()
        {
            var f = Production(); var flow = new InteractionFlowCoordinator(); flow.ResetToChooseAction();
            var collection = new ResourceCollectionPresenter(f.Context, f.Port, f.View, f.Map, new ResourceCollectionService(f.Map));
            var exploration = new ExplorationEventPresenter(f.Context, f.Port, f.View, f.Map, f.Exploration, f.Influence);
            var turn = new TurnActionPresenter(f.Context, f.Port, f.View, flow, f.Map, collection, f.Presenter, exploration);
            bool localExplore = false;
            exploration.BeginRouteSelection = () => localExplore = true;
            turn.HasExplorationDraft = () => localExplore;
            var router = new InteractionRouter(_ => { }); router.Register(turn.MoveInteraction);
            turn.MainActionDraftCleared = () => { localExplore = false; router.ClearMapConfirmation(); };
            var before = Snapshot(f.State);
            turn.BeginDeployAction(); f.Presenter.SelectSlot(f.Influence.GetLegalPlacementSlotIds(f.State, 1).First());
            turn.BeginMoveAction(); Assert.That(f.Presenter.IsActive, Is.False);
            var target = turn.MoveInteraction.BuildPresentation().Highlights.First().TargetId;
            router.OnLocationClicked(target, 10); Assert.That(router.HasPendingMapConfirmation, Is.True);
            turn.BeginExploreAction(); Assert.That(router.HasPendingMapConfirmation, Is.False); Assert.That(localExplore, Is.True);
            turn.BeginDispatchAction(); Assert.That(localExplore, Is.False);
            Assert.That(turn.ActionPanelPresenter.CanStartQuickAction(), Is.True);
            Assert.That(turn.TryClearMainActionDraft(), Is.True);
            turn.BeginExploreAction(); turn.BeginBuildAction();
            Assert.That(localExplore, Is.False); Assert.That(f.Presenter.IsActive, Is.False);
            Assert.That(f.Port.Commands, Is.Empty); Assert.That(Snapshot(f.State), Is.EqualTo(before));
        }

        [Test]
        public void MoveConfirmationCannotSubmitChangedRevisionAndCannotCancelInFlight()
        {
            var f = Production(); var flow = new InteractionFlowCoordinator(); flow.ResetToChooseAction();
            MoveInteraction move = null;
            move = new MoveInteraction(f.Context, f.Port, f.View, flow, f.Map, () => move.Activate(), () => true, _ => { });
            var router = new InteractionRouter(_ => { }); router.Register(move); move.Begin();
            var target = move.BuildPresentation().Highlights.First().TargetId;
            router.OnLocationClicked(target, 21); f.State.EffectRuntime.StateRevision++;
            router.OnLocationClicked(target, 22); Assert.That(f.Port.Commands, Is.Empty);
            move.Cancel(); move.Begin(); router.ClearMapConfirmation();
            router.OnLocationClicked(target, 23); router.OnLocationClicked(target, 24);
            var command = f.Port.Commands.Single(); Assert.That(command.Kind, Is.EqualTo(GameCommandKind.MoveCity));
            move.Cancel(); Assert.That(move.IsSubmissionInFlight, Is.True);
            move.NotifyInteractionCommandResolved(f.State.GameId, "wrong", false, CommandErrorCode.InvalidTarget);
            Assert.That(move.IsSubmissionInFlight, Is.True);
            move.NotifyInteractionCommandResolved(f.State.GameId, command.CommandId, false, CommandErrorCode.InvalidTarget);
            Assert.That(move.IsSubmissionInFlight, Is.False);
        }

        [Test]
        public void LocalExploreUsesPublicPreviewAndDomainPruningWithoutAuthorityWrites()
        {
            var f = Production(); var visible = GameStateViewProjector.ProjectForPlayer(f.State, 1);
            var client = GameStateViewProjector.ToClientState(visible); var before = Snapshot(client);
            var service = new RouteResourceSelectionService(f.Map);
            Assert.That(service.TryCreateExplorationPreview(client, visible, 1, out var session, out var draft, out var projection).IsValid, Is.True);
            Assert.That(session.OpenedAtStateRevision, Is.EqualTo(visible.StateRevision));
            var presenter = new RouteResourceSelectionPresenter(() => client, service);
            presenter.BeginLocalPreview(session, draft, projection);
            Assert.That(presenter.RequestBinding, Is.Null);
            foreach (var route in new[] { "A2", "A1" })
            { Assert.That(presenter.SelectRoute(route), Is.Not.Null); Assert.That(presenter.ConfirmRecipient(route, "bank"), Is.True); }
            Assert.That(presenter.ToggleLocation("A-01"), Is.True); Assert.That(presenter.CanConfirm, Is.True);
            Assert.That(presenter.RemoveRoute("A2"), Is.True);
            Assert.That(presenter.Draft.SelectedLocationIds, Is.Empty);
            Assert.That(presenter.Draft.ConfirmedRouteIds, Is.Empty);
            Assert.That(Snapshot(client), Is.EqualTo(before)); Assert.That(client.EffectRuntime, Is.Null);
        }

        [Test]
        public void RealMuelsyseTacticAndCoverChainProjectsTwoExactInstancesThenOneAndZero()
        {
            var f = MuelsyseDoubleCover(); var presenter = new CharacterCardPanelPresenter();
            var model = presenter.BuildView(f.State, 1); Assert.That(model.CoveredCards.Count, Is.EqualTo(2));
            foreach (var card in model.CoveredCards)
            {
                var exact = presenter.BuildProjectedView(f.State, 1, GameStateViewProjector.ProjectForPlayer(f.State, 1), card.CardId);
                Assert.That(exact.CoveredCardId, Is.EqualTo(card.CardId));
                Assert.That(presenter.CreateBeginUseCommand(1, card.CardId).TargetId, Is.EqualTo(card.CardId));
            }
            Assert.That(presenter.BuildProjectedView(f.State, 1, null, "not.visible").CoveredCardId, Is.Empty);
            Transfer(f, model.CoveredCards[0].CardId); Assert.That(presenter.BuildView(f.State, 1).CoveredCards.Count, Is.EqualTo(1));
            Transfer(f, model.CoveredCards[1].CardId); Assert.That(presenter.BuildView(f.State, 1).CoveredCards, Is.Empty);
        }

        [Test, Category("原生Unity")]
        public void ActualMobileSubmissionGateReleasesOnlyItsUnsentLocalExploreLock()
        {
            var f = Production(); var before = Snapshot(f.State);
            var root = new GameObject("探索提交门禁接缝", typeof(RectTransform)); root.SetActive(false);
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
                var hudObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab); hudObject.transform.SetParent(root.transform, false);
                var hud = hudObject.GetComponent(Type.GetType("YC.Presentation.GameplayInteractionHudView, Assembly-CSharp", true));
                var registry = hud.GetType().GetProperty("DialogRegistry").GetValue(hud);
                var mainType = Type.GetType("YC.Presentation.MobileCityInteractionController, Assembly-CSharp", true);
                var main = root.AddComponent(mainType);
                var mainFlags = BindingFlags.Instance | BindingFlags.NonPublic;
                mainType.GetField("gameplayInteractionHud", mainFlags).SetValue(main, hud);
                mainType.GetField("pendingEffectCommandId", mainFlags).SetValue(main, "already.pending");
                var dialog = Activator.CreateInstance(Type.GetType("YC.Presentation.EventChoiceDialog, Assembly-CSharp", true),
                    new object[] { registry, (Func<RectTransform>)(() => (RectTransform)root.transform) });
                var mapType = Type.GetType("YC.Presentation.MapViewPresenter, Assembly-CSharp", true);
                var mapReader = Expression.Lambda(typeof(Func<>).MakeGenericType(mapType), Expression.Constant(null, mapType)).Compile();
                Action noop = () => { }; Action<string> prompt = _ => { };
                var view = Activator.CreateInstance(Type.GetType("YC.Presentation.MobileCityWorkflowViewAdapter, Assembly-CSharp", true),
                    new object[] { (Func<GameState>)(() => f.State), (Func<int>)(() => 1), (Func<RectTransform>)(() => (RectTransform)root.transform),
                        registry, mapReader, f.Map, dialog, prompt, noop, noop, noop, noop, noop, prompt, (Func<int,string>)(id => id.ToString()) });
                var coordinatorType = Type.GetType("YC.Presentation.RouteResourceSelectionInteractionUiCoordinator, Assembly-CSharp", true);
                var submitMethod = mainType.GetMethod("SubmitPendingEffectCommand", mainFlags);
                var coordinator = Activator.CreateInstance(coordinatorType, new object[] {
                    (Func<GameState>)(() => f.State), (Func<GameStateView>)(() => GameStateViewProjector.ProjectForPlayer(f.State, 1)),
                    (Func<int>)(() => 1), (Func<IReadOnlyList<InteractionRequest>>)(() => Array.Empty<InteractionRequest>()),
                    new RouteSelectionSessionAuthority(f.Map), new RouteResourceSelectionService(f.Map), dialog, view,
                    (Action<GameCommand>)(command => submitMethod.Invoke(main, new object[] { command })), noop });
                mainType.GetField("routeResourceSelection", mainFlags).SetValue(main, coordinator);
                coordinatorType.GetProperty("CanSubmit").SetValue(coordinator, (Func<bool>)(() => true));
                coordinatorType.GetMethod("BeginLocalExploration").Invoke(coordinator, null);
                Assert.That(coordinatorType.GetProperty("HasUnsubmittedMainActionDraft").GetValue(coordinator), Is.True);
                var command = new GameCommand { Kind = GameCommandKind.ExploreLocation, PlayerId = 1, TargetId = "A-01" };
                // 调用真实 Dispatch→Mobile 提交口：外层草稿许可与中心门禁不同，命令尚未进入提交口。
                coordinatorType.GetMethod("Dispatch", mainFlags).Invoke(coordinator, new object[] { command });
                Assert.That(coordinatorType.GetProperty("IsMainActionSubmissionInFlight").GetValue(coordinator), Is.False);
                Assert.That(coordinatorType.GetProperty("HasUnsubmittedMainActionDraft").GetValue(coordinator), Is.True);
                Assert.That(mainType.GetField("pendingEffectCommandId", mainFlags).GetValue(main), Is.EqualTo("already.pending"));
                Assert.That(Snapshot(f.State), Is.EqualTo(before));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test, Category("原生Unity")]
        public void ActualHudRowsReuseInstanceKeysAndClickDistinctCardIdsThenRetireOnlyRemovedRow()
        {
            var presenter = new CharacterCardPanelPresenter();
            // 读取实际构建场景的HUD实例，保留场景覆盖；不打开生成器，不保存场景。
            foreach (var path in new[] { "Assets/Scenes/SampleScene.unity", "Assets/Scenes/ThreePlayerScene.unity" })
            {
            var f = MuelsyseDoubleCover();
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                var mobile = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(
                    ProductionType("MobileCityInteractionController"), true)).Single();
                var owner = ((Component)Read(mobile, "gameplayInteractionHud")).gameObject;
                var type = Type.GetType("YC.Presentation.ActionPanelController, Assembly-CSharp", true);
                var viewType = Type.GetType("YC.Presentation.ActionPanelView, Assembly-CSharp", true);
                var view = owner.GetComponentInChildren(viewType, true);
                var catalog = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/CardVisualCatalog.asset",
                    Type.GetType("YC.Presentation.CardVisualCatalog, Assembly-CSharp", true));
                var clicks = new List<GameCommand>(); Action noop = () => { };
                var controller = type.GetMethod("Bind").Invoke(null, new object[] { view, catalog,
                    (Action<string>)(id => clicks.Add(presenter.CreateBeginUseCommand(1, id))), noop, noop, noop, noop, noop, noop, noop, noop });
                Assert.That(controller, Is.Not.Null);
                var render = type.GetMethod("RenderCharacterQuickAction"); var model = presenter.BuildView(f.State, 1);
                render.Invoke(controller, new object[] { model });
                var rows = (IDictionary)type.GetField("characterRows", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller);
                Assert.That(rows.Count, Is.EqualTo(2));
                var ids = model.CoveredCards.Select(card => card.CardId).ToArray();
                var retained = rows[ids[1]];
                foreach (var id in ids)
                {
                    var button = (Button)rows[id].GetType().GetProperty("Button").GetValue(rows[id]);
                    // 盖放当轮可能被规则锁定；此处仅测逐行绑定，规则可用性另由正式投影验证。
                    rows[id].GetType().GetMethod("SetAvailable").Invoke(rows[id], new object[] { true });
                    button.onClick.Invoke();
                }
                Assert.That(clicks.Select(command => command.TargetId), Is.EqualTo(ids));
                render.Invoke(controller, new object[] { model }); Assert.That(rows[ids[1]], Is.SameAs(retained));
                Transfer(f, ids[0]); render.Invoke(controller, new object[] { presenter.BuildView(f.State, 1) });
                Assert.That(rows.Count, Is.EqualTo(1)); Assert.That(rows[ids[1]], Is.SameAs(retained));
                Transfer(f, ids[1]); render.Invoke(controller, new object[] { presenter.BuildView(f.State, 1) });
                Assert.That(rows.Count, Is.Zero);
                Assert.That(((Button)viewType.GetProperty("UseCharacterButton").GetValue(view)).gameObject.activeSelf, Is.False);
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
            }
        }

        [Test, Category("原生Unity")]
        public void ActualMobileLocalExploreRejectionNoResultAndSuccessRouteCurrentEvent()
        {
            using (var native = new NativeExploreScene())
            {
                var route = Read(native.Mobile, "routeResourceSelection");
                Call(route, "BeginLocalExploration");
                var original = Snapshot(native.Session.State);
                Assert.That(Read(route, "HasUnsubmittedMainActionDraft"), Is.EqualTo(true));
                Call(route, "Dispatch", new GameCommand { Kind = GameCommandKind.ExploreLocation, PlayerId = 1, TargetId = "missing.location" });
                Assert.That(Read(native.Mobile, "pendingEffectCommandId"), Is.Null.Or.Empty);
                Assert.That(Read(route, "IsMainActionSubmissionInFlight"), Is.EqualTo(false));
                Assert.That(Snapshot(native.Session.State), Is.EqualTo(original));

                var gateway = Read(native.Mobile, "commandGateway");
                var missing = new MissingResultPort();
                Write(native.Mobile, "commandGateway", new CommandGateway(missing));
                Call(route, "Dispatch", new GameCommand { Kind = GameCommandKind.ExploreLocation, PlayerId = 1, TargetId = "A-01" });
                Write(native.Mobile, "commandGateway", gateway);
                Assert.That(missing.Calls, Is.EqualTo(1));
                Assert.That(Read(native.Mobile, "pendingEffectCommandId"), Is.Null.Or.Empty);
                Assert.That(Read(route, "IsMainActionSubmissionInFlight"), Is.EqualTo(false));
                Assert.That(Read(route, "HasUnsubmittedMainActionDraft"), Is.EqualTo(true));
                Assert.That(Snapshot(native.Session.State), Is.EqualTo(original));

                SelectExplore(route);
                Assert.That(Snapshot(native.Session.State), Is.EqualTo(original), "确认前只有本地草稿");
                var consumed = native.Session.State.FindPlayer(1).CompletedMainActionsThisTurn;
                Call(route, "Confirm");
                Assert.That(native.Observed.Last.CommandResult.Succeeded, Is.True, native.Observed.Last.CommandResult.Validation?.Reason);
                Assert.That(native.Observed.Last.AppliedLocally, Is.True);
                Assert.That(Read(native.Mobile, "pendingEffectCommandId"), Is.Null.Or.Empty);
                Assert.That(Read(route, "HasUnsubmittedMainActionDraft"), Is.EqualTo(false));
                Assert.That(Read(route, "IsMainActionSubmissionInFlight"), Is.EqualTo(false));
                Assert.That(native.Session.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(consumed), "探索尚在等待事件选择，不提前消费行动");
                var request = (YC.Domain.Interactions.InteractionRequestProjection)Read(
                    Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
                Assert.That(request, Is.Not.Null, "真实Mobile同步必须路由探索后的事件请求");
                Assert.That(request.InteractionTypeId, Is.EqualTo(EventCardEffectExecutor.OptionInteractionTypeId));
                var revision = native.Session.State.EffectRuntime.StateRevision;
                var next = EffectInteractionCommands.Answer(request, 1, new[] { request.CandidateIds.First() }, false);
                Call(native.Mobile, "SubmitPendingEffectCommand", next);
                Assert.That(native.Session.State.EffectRuntime.StateRevision, Is.GreaterThan(revision));
                Assert.That(Read(native.Mobile, "pendingEffectCommandId"), Is.Null.Or.Empty);
            }
        }

        private sealed class MissingResultPort : IGameCommandPort
        {
            public int Calls;
            public WorkflowSubmissionResult Submit(GameCommand command) { Calls++; return null; }
        }
        private sealed class ObservingPort : IGameCommandPort
        {
            private readonly IGameCommandPort inner;
            public readonly List<GameCommand> Commands = new List<GameCommand>();
            public WorkflowSubmissionResult Last;
            public ObservingPort(IGameCommandPort inner) { this.inner = inner; }
            public WorkflowSubmissionResult Submit(GameCommand command) { Commands.Add(command); return Last = inner.Submit(command); }
        }

        [UnityTest, Category("原生Unity")]
        public IEnumerator ActualMobileHotseatMoveConfirmationKeepsSuccessorEventVisible()
        {
            using (var native = new NativeExploreScene())
            {
                var move = Read(Read(native.Mobile, "turnActionPresenter"), "MoveInteraction");
                Write(move, "commandGateway", new CommandGateway(native.Observed));
                native.Session.ExecutionTimings.Enabled = true;
                var stages = new List<object>();
                Call(native.Mobile, "BeginMoveAction");
                var presentation = (InteractionPresentation)Call(move, "BuildPresentation");
                var target = presentation.Highlights.First(highlight => highlight.TargetKind == WorkflowHighlightTargetKind.Location && highlight.IsInteractive).TargetId;
                var before = Snapshot(native.Session.State);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.Location, target); watch.Stop();
                stages.Add(new { Stage = "地图首击", Milliseconds = watch.Elapsed.TotalMilliseconds, Commands = native.Observed.Commands.Count, Counters = native.Session.ExecutionTimings.Snapshot() });
                Assert.That(native.Observed.Commands, Is.Empty); Assert.That(Snapshot(native.Session.State), Is.EqualTo(before));
                yield return null;
                watch.Restart(); Call(native.Mobile, "OnMapButtonClicked", WorkflowHighlightTargetKind.Location, target); watch.Stop();
                var dialog = Read(native.Mobile, "eventChoiceDialog");
                var requests = native.Session.State.EffectRuntime.InteractionRequests.Where(request => request.Status == "open" && request.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId).ToList();
                var routed = Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
                stages.Add(new { Stage = "地图确认", Milliseconds = watch.Elapsed.TotalMilliseconds, Commands = native.Observed.Commands.Count,
                    Succeeded = native.Observed.Last?.CommandResult?.Succeeded, Applied = native.Observed.Last?.AppliedLocally,
                    OpenRequests = requests.Select(request => new { request.InteractionId, request.AnsweringPlayerId }).ToArray(), Routed = routed,
                    IsShowing = Read(dialog, "IsShowing"), InFlight = Read(move, "IsSubmissionInFlight"), Counters = native.Session.ExecutionTimings.Snapshot() });
                WriteHotseatEvidence(stages);
                Assert.That(native.Observed.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.EqualTo(1));
                Assert.That(native.Observed.Last.CommandResult.Succeeded && native.Observed.Last.AppliedLocally, Is.True);
                Assert.That(requests.Count, Is.EqualTo(1)); Assert.That(requests[0].AnsweringPlayerId, Is.EqualTo(1));
                Assert.That(Read(dialog, "IsShowing"), Is.EqualTo(true), "准确移城回执之后，后继事件牌页必须保持可见");
                Assert.That(routed, Is.Not.Null);
                var moveCommand = native.Observed.Commands.Single(command => command.Kind == GameCommandKind.MoveCity);
                Call(move, "NotifyInteractionCommandResolved", native.Session.State.GameId, moveCommand.CommandId, true, CommandErrorCode.None);
                Call(native.Mobile, "SynchronizeInteractionFromState"); Call(native.Mobile, "SynchronizeInteractionFromState");
                Assert.That(Read(dialog, "IsShowing"), Is.EqualTo(true));
                Assert.That(native.Observed.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.EqualTo(1));
                var revision = native.Session.State.EffectRuntime.StateRevision;
                var view = (Component)Read(dialog, "view");
                var markerType = Type.GetType("YC.PlayerJourney.PlayerAutomationId, YC.PlayerJourney", true);
                var button = view.GetComponentsInChildren(markerType, true).Single(marker =>
                    (string)markerType.GetField("Id").GetValue(marker) == "event.option.0").GetComponent<Button>();
                watch.Restart(); button.onClick.Invoke(); watch.Stop();
                stages.Add(new { Stage = "事件牌选项提交", Milliseconds = watch.Elapsed.TotalMilliseconds, Commands = native.Observed.Commands.Count,
                    Counters = native.Session.ExecutionTimings.Snapshot() }); WriteHotseatEvidence(stages);
                Assert.That(native.Session.State.EffectRuntime.StateRevision, Is.GreaterThan(revision));
                Assert.That(native.Observed.Last.CommandResult.Succeeded && native.Observed.Last.AppliedLocally, Is.True);
                Assert.That(Read(native.Mobile, "pendingEffectCommandId"), Is.Null.Or.Empty);
                Call(native.Mobile, "SynchronizeInteractionFromState"); Call(native.Mobile, "SynchronizeInteractionFromState");
                Assert.That(native.Observed.Commands.Count(command => command.Kind == GameCommandKind.MoveCity), Is.EqualTo(1));
            }
        }

        private static void WriteHotseatEvidence(List<object> stages)
        {
            var root = Environment.GetEnvironmentVariable("NMC_HOTSEAT_EVIDENCE") ?? Path.Combine(Environment.CurrentDirectory, "Logs/HotseatInteractionResponse/方法原件");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "移城分段原件.json"), JsonConvert.SerializeObject(new {
                Environment = "Unity EditMode真实SampleScene/Mobile生产回调；非外部点击或人工视觉", Stages = stages }, Formatting.Indented));
        }

        [Test, Category("原生Unity")]
        public void ActualMobileHotseatCoverSelectionKeepsRowsAndSubmitsOnce()
        {
            var prepared = Production();
            Run(prepared, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1));
            prepared.State.Phase = GamePhase.CharacterCover;
            using (var native = new NativeExploreScene(prepared))
            {
                var dialog = Read(native.Mobile, "characterCoverChoiceDialog");
                Assert.That(Read(dialog, "IsShowing"), Is.EqualTo(true));
                var shell = Read(dialog, "shell"); var view = (Component)Read(shell, "view");
                var markers = Type.GetType("YC.PlayerJourney.PlayerAutomationId, YC.PlayerJourney", true);
                Func<Dictionary<string, Button>> Rows = () => ((Component)Read(shell, "view")).GetComponentsInChildren(markers, true)
                    .Where(marker => ((string)markers.GetField("Id").GetValue(marker)).StartsWith("selection.card.", StringComparison.Ordinal))
                    .ToDictionary(marker => (string)markers.GetField("Id").GetValue(marker), marker => marker.GetComponent<Button>());
                var original = Rows(); Assert.That(original.Count, Is.GreaterThan(0));
                var row = original.First(pair => pair.Value.interactable);
                var before = Snapshot(native.Session.State); var watch = System.Diagnostics.Stopwatch.StartNew();
                row.Value.onClick.Invoke(); watch.Stop();
                var after = Rows();
                var stages = new List<object> { new { Stage = "盖牌只选中", Milliseconds = watch.Elapsed.TotalMilliseconds,
                    RowsBefore = original.Count, RowsAfter = after.Count, RetainedRows = original.Count(pair => after.ContainsKey(pair.Key) && ReferenceEquals(after[pair.Key], pair.Value)),
                    Commands = native.Observed.Commands.Count, Selected = Read(native.Mobile, "selectedCoverCardId") } };
                var root = Environment.GetEnvironmentVariable("NMC_HOTSEAT_EVIDENCE") ?? Path.Combine(Environment.CurrentDirectory, "Logs/HotseatInteractionResponse/方法原件");
                Directory.CreateDirectory(root); string evidence = Path.Combine(root, "盖牌分段原件.json");
                File.WriteAllText(evidence, JsonConvert.SerializeObject(stages, Formatting.Indented));
                Assert.That(native.Observed.Commands, Is.Empty); Assert.That(Snapshot(native.Session.State), Is.EqualTo(before));
                Assert.That(original.All(pair => after.ContainsKey(pair.Key) && ReferenceEquals(after[pair.Key], pair.Value)), Is.True,
                    "只改变盖牌选择必须保留已绑定的候选行");
                var alternative = after.FirstOrDefault(pair => pair.Key != row.Key && pair.Value.interactable);
                if (alternative.Value != null)
                {
                    watch.Restart(); alternative.Value.onClick.Invoke(); watch.Stop();
                    var changed = Rows();
                    stages.Add(new { Stage = "盖牌改选", Milliseconds = watch.Elapsed.TotalMilliseconds, Commands = native.Observed.Commands.Count,
                        RetainedRows = original.Count(pair => changed.ContainsKey(pair.Key) && ReferenceEquals(changed[pair.Key], pair.Value)),
                        Selected = Read(native.Mobile, "selectedCoverCardId") });
                    File.WriteAllText(evidence, JsonConvert.SerializeObject(stages, Formatting.Indented));
                    Assert.That(original.All(pair => changed.ContainsKey(pair.Key) && ReferenceEquals(changed[pair.Key], pair.Value)), Is.True);
                    Assert.That(native.Observed.Commands, Is.Empty); Assert.That(Snapshot(native.Session.State), Is.EqualTo(before));
                }
                var selected = (string)Read(native.Mobile, "selectedCoverCardId");
                view = (Component)Read(shell, "view");
                var confirm = view.GetComponentsInChildren<Button>(true).Single(button => button.gameObject.name == "Confirm Selection");
                Assert.That(confirm.interactable, Is.True);
                watch.Restart(); confirm.onClick.Invoke(); watch.Stop();
                stages.Add(new { Stage = "盖牌确认提交", Milliseconds = watch.Elapsed.TotalMilliseconds, Commands = native.Observed.Commands.Count,
                    Succeeded = native.Observed.Last?.CommandResult?.Succeeded, Applied = native.Observed.Last?.AppliedLocally });
                File.WriteAllText(evidence, JsonConvert.SerializeObject(stages, Formatting.Indented));
                Assert.That(native.Observed.Commands.Count, Is.EqualTo(1));
                Assert.That(native.Observed.Commands.Single().OptionIds, Is.EqualTo(new[] { selected }), "确认提交实际最后选中的实例");
                Assert.That(native.Observed.Last.CommandResult.Succeeded && native.Observed.Last.AppliedLocally, Is.True);
                Assert.That(Read(native.Mobile, "coverSubmissionInFlight"), Is.EqualTo(false));
            }
        }

        [Test, Category("原生Unity")]
        public void ActualMobileSimulationExploreWaitsForExactLocalResultAndRoutesSuccessor()
        {
            using (var native = new NativeExploreScene())
            using (var network = new NativeExploreSimulation(native))
            {
                var route = Read(native.Mobile, "routeResourceSelection");
                Call(route, "BeginLocalExploration");
                Assert.That(((GameState)Call(native.Mobile, "UiReadState")).EffectRuntime, Is.Not.Null,
                    "实际Mobile可见副本保留轻量运行时，不能误作完整隐藏状态");
                var previewCheck = new RouteResourceSelectionService(new MapQueryService(StaticMapDefinitions.Resolve(native.Session.State.MapId)))
                    .TryCreateExplorationPreview((GameState)Call(native.Mobile, "UiReadState"),
                        (GameStateView)Call(native.Mobile, "VisibleSaleState"), 1, out _, out _, out _);
                Assert.That(Read(route, "HasUnsubmittedMainActionDraft"), Is.EqualTo(true),
                    previewCheck.Reason + ";CanSubmit=" + Read(native.Mobile, "CanSubmitGameplayCommand") + ";pending=" + Read(native.Mobile, "pendingEffectCommandId"));
                var stale = new GameCommand { Kind = GameCommandKind.ExploreLocation, PlayerId = 1, TargetId = "A-01" };
                stale.Parameters["expectedRevision"] = (native.Session.State.EffectRuntime.StateRevision - 1).ToString();
                Call(route, "Dispatch", stale);
                Assert.That(native.Observed.Last.CommandResult.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
                Assert.That(network.Actor.HasPendingLocalApplication, Is.False);
                Assert.That(Read(native.Mobile, "pendingEffectCommandId"), Is.Null.Or.Empty);
                Assert.That(Read(route, "IsMainActionSubmissionInFlight"), Is.EqualTo(false));
                Call(route, "BeginLocalExploration"); SelectExplore(route);
                var consumed = native.Session.State.FindPlayer(1).CompletedMainActionsThisTurn;
                Call(route, "Confirm");
                Assert.That(native.Observed.Last.CommandResult.Succeeded, Is.True, native.Observed.Last.CommandResult.Validation?.Reason);
                Assert.That(native.Observed.Last.AppliedLocally, Is.False);
                var commandId = (string)Read(native.Mobile, "pendingEffectCommandId");
                Assert.That(commandId, Is.Not.Null.And.Not.Empty);
                Assert.That(network.Actor.HasPendingLocalApplication, Is.True);
                Call(route, "NotifyInteractionCommandResolved", "wrong.game", commandId, true, CommandErrorCode.UnknownCommand);
                Assert.That(Read(route, "IsMainActionSubmissionInFlight"), Is.EqualTo(true));
                Call(route, "Cancel"); Assert.That(Read(route, "IsMainActionSubmissionInFlight"), Is.EqualTo(true));
                network.ApplyPending();
                Assert.That(Read(native.Mobile, "pendingEffectCommandId"), Is.Null.Or.Empty);
                Assert.That(Read(route, "IsMainActionSubmissionInFlight"), Is.EqualTo(false));
                Assert.That(native.Session.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(consumed));
                var request = (YC.Domain.Interactions.InteractionRequestProjection)Read(Read(native.Mobile, "interactionRequestRouter"), "CurrentRequest");
                Assert.That(request?.InteractionTypeId, Is.EqualTo(EventCardEffectExecutor.OptionInteractionTypeId));
                var after = Snapshot(native.Session.State);
                Call(network.Transport, "OnSimulationApplied", network.LastApplied);
                Assert.That(Snapshot(native.Session.State), Is.EqualTo(after), "重复准确回执不得再次结算");
                Call(native.Mobile, "SubmitPendingEffectCommand", EffectInteractionCommands.Answer(request, 1, new[] { request.CandidateIds.First() }, false));
                Assert.That(network.Actor.HasPendingLocalApplication, Is.True, "后继实际事件答案必须通过同一Mobile入口");
                network.ApplyPending();
                Assert.That(Read(native.Mobile, "pendingEffectCommandId"), Is.Null.Or.Empty);
            }
        }

        private sealed class NativeExploreSimulation : IDisposable
        {
            public readonly object Transport;
            public DeterministicSimulationCoordinator Actor => replicas[1];
            public SimulationCommandAppliedDto LastApplied;
            private readonly DeterministicSimulationCoordinator[] replicas = new DeterministicSimulationCoordinator[3];
            private readonly object shell, previousTransport;
            public NativeExploreSimulation(NativeExploreScene native)
            {
                var shellType = typeof(MainActionPreselectionUiTests).Assembly.GetType("YC.Tests.EditMode.CommandSubmissionAcknowledgmentTests", true);
                shell = Activator.CreateInstance(shellType); var scenario = Call(shell, "Create");
                (Read(scenario, "Controller") as IDisposable)?.Dispose();
                (Read(native.Mobile, "commandSubmission") as IDisposable)?.Dispose();
                Transport = Read(scenario, "Transport");
                var instance = Transport.GetType().GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
                previousTransport = instance.GetValue(null); instance.SetValue(null, Transport);
                shellType.GetMethod("BindRealLocalConnection", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { scenario });
                Write(native.Context, "Mode", LaunchMode.Client);
                var sessions = new GameSession[3]; sessions[1] = native.Session;
                for (int i = 0; i < 3; i++)
                {
                    if (i != 1)
                    {
                        var result = ProductionType("GameSessionBootstrapper").GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                            .Single(method => method.Name == "Build" && method.GetParameters().Length == 4)
                            .Invoke(null, new object[] { native.Context, false, false, true });
                        sessions[i] = (GameSession)Read(result, "Session");
                        sessions[i].ReplaceState(GameStateCloneService.DeepClone(native.Session.State));
                    }
                    replicas[i] = new DeterministicSimulationCoordinator(sessions[i],
                        DeterministicSimulationSessionOptions.CreateInitial(i == 0, i == 0 ? 3 : i, sessions[i], new[] { 1, 2, 3 }));
                    replicas[i].Begin(); Assert.That(replicas[i].IsStopped, Is.False);
                }
                Seal(0);
                native.Session.RefreshLocalProjection(GameStateViewer.Player(1));
                Call(native.Mobile, "BuildCommandSubmission", native.Context);
                Write(scenario, "Controller", Read(native.Mobile, "commandSubmission"));
                Call(Transport, "AttachSimulation", Actor, true);
                Actor.SimulationCommandApplied += value => LastApplied = value;
                Assert.That(Read(native.Mobile, "CanSubmitGameplayCommand"), Is.EqualTo(true));
            }
            private void Seal(long sequence)
            {
                var host = replicas[0]; var boundary = host.ReadBoundary(sequence);
                // 同一玩家的后继请求可沿已有窗口继续；只有真实产生的新边界才需要汇合。
                if (boundary == null)
                {
                    Assert.That(sequence, Is.GreaterThan(0));
                    Assert.That(replicas.All(replica => replica.LastAppliedSequence == sequence && !replica.IsStopped), Is.True);
                    return;
                }
                SimulationWindowPermitDto permit = null; Action<SimulationWindowPermitDto> listener = value => permit = value;
                host.WindowPermitProduced += listener;
                try
                {
                    for (int i = 1; i < 3; i++)
                    {
                        Assert.That(replicas[i].ReceiveBoundary(boundary), Is.True);
                        Assert.That(host.ReceiveWindowReady(i, new SimulationWindowReadyDto { GameId = boundary.GameId,
                            SimulationEpoch = boundary.SimulationEpoch, MembershipVersion = boundary.MembershipVersion, PlayerId = i,
                            ReplicaId = DeterministicSimulationSessionOptions.ReplicaIdForPlayer(i), Boundary = replicas[i].ReadBoundary(sequence) }), Is.True);
                    }
                    if (permit != null) foreach (var replica in replicas.Skip(1)) Assert.That(replica.ReceiveWindowPermit(permit), Is.True);
                }
                finally { host.WindowPermitProduced -= listener; }
                Assert.That(host.IsBoundaryPermitted(sequence), Is.True);
            }
            public void ApplyPending()
            {
                var admitted = replicas[0].AdmitProposal(1, Actor.ReadPendingLocalProposal());
                Assert.That(admitted.Status, Is.EqualTo(OperationAdmissionStatus.Accepted), admitted.Reason);
                Actor.ReceiveProposalResult(admitted);
                foreach (var replica in replicas.Skip(1)) replica.ReceiveOrdered(admitted.OrderedCommand);
                foreach (var replica in replicas) Assert.That(replica.Pump(1), Is.EqualTo(1), replica.LastFault?.Diagnostic);
                foreach (var replica in replicas.Skip(1)) Assert.That(replica.ReceiveValidationPrefix(replicas[0].ReadLocalValidationPrefix()), Is.True);
                Seal(admitted.OrderedCommand.GlobalCommandSequence);
            }
            public void Dispose()
            {
                Call(shell, "Clear");
                Transport.GetType().GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, previousTransport);
            }
        }
        private static Type ProductionType(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private const BindingFlags NativeMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static object Read(object target, string name) => target.GetType().GetField(name, NativeMembers)?.GetValue(target)
            ?? target.GetType().GetProperty(name, NativeMembers)?.GetValue(target);
        private static void Write(object target, string name, object value) => target.GetType().GetField(name, NativeMembers).SetValue(target, value);
        private static object Call(object target, string name, params object[] args)
        {
            try { return target.GetType().GetMethods(NativeMembers).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args); }
            catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        private static void SelectExplore(object route)
        {
            var presenter = (RouteResourceSelectionPresenter)Read(route, "Presenter");
            foreach (var id in new[] { "A2", "A1" })
            { Assert.That(presenter.SelectRoute(id), Is.Not.Null); Assert.That(presenter.ConfirmRecipient(id, "bank"), Is.True); }
            Assert.That(presenter.ToggleLocation("A-01"), Is.True); Assert.That(presenter.CanConfirm, Is.True);
        }
        private sealed class NativeExploreScene : IDisposable
        {
            public readonly object Mobile, Context;
            public readonly GameSession Session;
            public readonly ObservingPort Observed;
            private readonly GameObject contextRoot;
            private readonly UnityEngine.SceneManagement.Scene scene;
            private readonly object previousContext;
            public NativeExploreScene(Fixture prepared = null)
            {
                var f = prepared ?? Production();
                var contextType = ProductionType("GameLaunchContext");
                var instance = contextType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
                previousContext = instance.GetValue(null);
                contextRoot = new GameObject("主要行动原生启动上下文"); contextRoot.SetActive(false);
                Context = contextRoot.AddComponent(contextType); instance.SetValue(null, Context);
                Write(Context, "Mode", LaunchMode.Local); Write(Context, "LocalPlayerId", 1);
                Write(Context, "GameId", f.State.GameId); Write(Context, "MapId", f.State.MapId);
                Write(Context, "EventDeckSeed", f.State.EventDeckSeed); Write(Context, "InitialRandomState", f.State.RandomRuntime.Clone());
                Write(Context, "RuleConfiguration", f.State.RuleConfiguration.Clone());
                Write(Context, "LaunchPlayerOrder", new List<int> { 1, 2, 3 });
                Write(Context, "Players", f.State.Players.Select(player => new PlayerSeat { PlayerId = player.PlayerId, Color = player.Color }).ToList());
                try
                {
                scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Additive);
                Mobile = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(
                    ProductionType("MobileCityInteractionController"), true)).Single();
                Call(Mobile, "Awake");
                Session = (GameSession)Read(Mobile, "session"); Assert.That(Session, Is.Not.Null);
                Session.ReplaceState(f.State);
                Call(Mobile, "SynchronizeInteractionFromState");
                Assert.That(Read(Mobile, "commandGateway"), Is.Not.Null);
                Observed = new ObservingPort((IGameCommandPort)Read(Mobile, "gameplayAdapter"));
                Write(Mobile, "commandGateway", new CommandGateway(Observed));
                }
                catch
                {
                    if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
                    instance.SetValue(null, previousContext);
                    UnityEngine.Object.DestroyImmediate(contextRoot);
                    throw;
                }
            }
            public void Dispose()
            {
                (Read(Mobile, "commandSubmission") as IDisposable)?.Dispose();
                EditorSceneManager.CloseScene(scene, true);
                ProductionType("GameLaunchContext").GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, previousContext);
                UnityEngine.Object.DestroyImmediate(contextRoot);
            }
        }

        private sealed class Context : IWritableGameplayContext
        {
            public GameState State; public GameState CurrentState => State;
            public int LocalPlayerId { get; private set; } = 1;
            public bool ControlsCurrentPlayerLocally => false;
            public void SetLocalPlayerId(int id) => LocalPlayerId = id;
        }
        private sealed class RecordingPort : IGameCommandPort
        {
            public readonly List<GameCommand> Commands = new List<GameCommand>();
            public WorkflowSubmissionResult Submit(GameCommand command)
            { Commands.Add(command); return new WorkflowSubmissionResult(CommandResult.SuccessResult(new List<GameEvent>(), "recording-port"), false); }
        }
        private sealed class View : IInfluenceActionView, ITurnActionView, IExplorationEventView, IResourceCollectionView
        {
            public void ShowPrompt(string value) { }
            public void SetHighlights(IReadOnlyList<WorkflowHighlight> value) { }
            public void ClearHighlights() { }
            public void SetInteractionMode(InteractionMode value) { }
            public void ShowDispatchDecision(DispatchDecisionViewModel value) { }
            public void HideDispatchDecision() { }
            public void RefreshInfluencePreview(bool pending, string source, string target) { }
            public void RefreshActionPanel() { }
            public void CompleteAction(string value) { }
            public void RefreshFromState() { }
            public void RefreshInformation() { }
            public void ShowPendingChoice() { }
            public void CompleteMainActionPresentation(string value) { }
            public void ShowBuildFacilityDraft(BuildFacilityDraftViewModel value) { }
            public void HideBuildFacilityDraft() { }
            public void ShowCityStyleOptions(CityStyleOptionsViewModel value) { }
            public void ShowExplorePathOptions(ExplorePathOptionsViewModel value) { }
            public void ShowExplorePaymentOptions(ExplorePaymentOptionsViewModel value) { }
            public void ShowEventCardOptions(EventCardOptionsViewModel value) { }
            public void CollapseEventOptions() { }
            public void HideEventOptions() { }
            public string GetPlayerDisplayName(int player) => player.ToString();
            public void RefreshResourceAndInfluence() { }
            public void ShowRoutePaymentOptions(string route, int cost, IReadOnlyList<int> recipients) { }
            public void RefreshSelectionView() { }
        }
        private sealed class Fixture
        {
            public GameState State; public IMapQueryService Map; public InfluenceService Influence; public ExplorationService Exploration;
            public EffectRegistry Registry; public Context Context; public RecordingPort Port; public View View; public InfluenceActionPresenter Presenter;
        }
        private static Fixture Production(bool infected = false)
        {
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            var config = new GameRuleConfiguration { PlayerCount = 3, MapId = StaticMapDefinitions.FourPlayerMapId,
                EnabledExpansionIds = infected ? new List<string> { "core", "enterprise", "infected" } : new List<string> { "core" } };
            var catalog = ActiveContentCatalogBuilder.Build(pack, config); config.ContentPackHash = catalog.ContentPackHash;
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, Enumerable.Range(1, 3).Select(id => new PlayerSeat { PlayerId = id }).ToList(),
                config, 321, catalog.SetupRecipe, catalog.ContentDefinitions, new[] { 1, 2, 3 }, "ui.main_action_preselection", null);
            var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)); var influence = new InfluenceService(map);
            var decks = new EventDeckService(321); var tokens = new ResourceTokenService();
            var f = new Fixture { State = state, Map = map, Influence = influence, Exploration = new ExplorationService(map, influence, decks, tokens),
                Registry = new EffectRegistry(), Context = new Context { State = state }, Port = new RecordingPort(), View = new View() };
            catalog.Register(f.Registry); GenericLuaEffectExecutor.Register(f.Registry); ResourceEffectExecutor.Register(f.Registry);
            InfluenceEffectExecutor.Register(f.Registry, influence); CharacterCoverEffectExecutor.Register(f.Registry);
            EventCardEffectExecutor.Register(f.Registry, map, influence, decks, tokens);
            ExplorationEffectExecutor.Register(f.Registry, map, f.Exploration, decks, tokens);
            var city = state.ItemZones.Items.Single(item => item.PhysicalKind == ItemPhysicalKind.MobileCity && item.OwnerSubject.PlayerId == 1);
            var parent = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == "A-03");
            SetPosition(state, city, state.ItemZones.Zones.Single(zone => zone.ParentId == parent.ZoneId && zone.TemplateId == "core.zone.city_dock").ZoneId);
            var builder = new RoundMainChainBuilder(); var order = new List<int> { 1, 2, 3 };
            var chain = builder.Build(state.GameId, builder.CreateRoundExecutionId(state.GameId, 2, order), 2, order, 1);
            var window = chain.Nodes.Single(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && node.PlayerId == 1 && node.ActionRound == 1);
            foreach (var node in chain.Nodes.Where(node => node.MainlineIndex < window.MainlineIndex)) node.Status = EffectNodeStatus.Completed;
            var runtime = state.EffectRuntime; runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = 2;
            runtime.CurrentRoundStartPlayerId = 1; runtime.PlayerOrderSnapshot = order; runtime.FirstMainNodeId = chain.FirstNodeId;
            runtime.MainNodes.AddRange(chain.Nodes); runtime.ActiveMainNodeId = window.NodeId; window.Status = EffectNodeStatus.Ready;
            Assert.That(new RoundExecutionService(f.Registry).Advance(state).IsValid, Is.True);
            state.FindPlayer(1).Resources.GoldVoucher = 8; state.FindPlayer(1).Resources.OriginiumShard = 20;
            f.Presenter = new InfluenceActionPresenter(f.Context, f.Port, f.View, map, influence); return f;
        }
        private static InfluencePlacement PlaceMovable(Fixture f)
        {
            foreach (var slot in f.Influence.GetLegalPlacementSlotIds(f.State, 1))
            {
                Run(f, InfluenceEffectSpecFactory.PlaceInfluence(1, slot));
                var placed = f.State.Map.Influences.Last();
                if (f.Influence.GetLegalMovementSlotIds(f.State, placed.InfluenceId).Any(target => f.Influence.CanMove(f.State, 1, placed.InfluenceId, target).IsValid)) return placed;
            }
            throw new InvalidOperationException("夹具没有可移动的本人影响力");
        }
        private static Fixture MuelsyseDoubleCover()
        {
            // 与 EventCharacterCatalogBootstrap 一致，注入已安装正式内容包的完整角色目录。
            if (!CharacterCardDatabase.IsInitialized)
                CharacterCardDatabase.Initialize(ExternalContentPack.Load(Path.Combine(
                    Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core")).CreateCharacters());
            var f = Production(true);
            var card = f.State.ItemZones.Items.Single(item => item.DefinitionId == "infected.character.muelsyse");
            var hand = f.State.ItemZones.Zones.Single(zone => zone.LocalZoneId == "hand" && zone.OwnerPlayerId == 1);
            Run(f, ItemTransferEffectExecutor.MoveSpec(1, new[] { card.ItemId }, card.ZoneId, hand.ZoneId, "unordered", 1, "core.character_card_transfer"));
            Cover(f, card.ItemId); Run(f, CharacterLifecycleService.CreateUseSpec(1, card.ItemId, "tactic"));
            Answer(f, "hand"); Answer(f, "hand"); Answer(f, "discard");
            // 先明确不占用额外槽，再由真实普通盖牌事件触发缪尔赛思的追加盖牌。
            for (var i = 0; i < 4 && f.State.EffectRuntime.InteractionRequests.Any(request => request.Status == "open" && !request.IsInternalMainlineInteraction()); i++)
            {
                var request = Open(f); var executor = new EffectTreeExecutor(f.State, f.Registry);
                Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                    NormalizedValue.CreateBoolean(false), out var stopReason), Is.True, stopReason); Pump(f);
            }
            var currentCard = f.State.ItemZones.Items.Single(item => item.ItemId == card.ItemId);
            Assert.That(f.State.ItemZones.Zones.Single(zone => zone.ZoneId == currentCard.ZoneId).LocalZoneId, Is.EqualTo("persistent"));
            var normal = f.State.ItemZones.Items.First(item => item.ZoneId == hand.ZoneId && item.TypeId == "core.basic_character_card");
            Cover(f, normal.ItemId); Answer(f, Open(f).CandidateIds.First(id => id != "decline"));
            Assert.That(CharacterLifecycleService.TryValidate(f.State, out var reason), Is.True, reason);
            return f;
        }
        private static void Cover(Fixture f, string id) { Run(f, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(f, id); }
        private static void Transfer(Fixture f, string id) => Run(f, EffectSpec.Create(CharacterLifecycleService.TransferTypeId,
            ItemTransferEffectExecutor.Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"),
                "cardInstanceId", NormalizedValue.CreateString(id), "recipientPlayer", NormalizedValue.CreateString("2")), 1));
        private static InteractionRequest Open(Fixture f) => f.State.EffectRuntime.InteractionRequests.Last(request => request.Status == "open" && !request.IsInternalMainlineInteraction());
        private static void Run(Fixture f, EffectSpec spec) { new EffectTreeExecutor(f.State, f.Registry).CreateRoot(spec); Pump(f); }
        private static void Pump(Fixture f) => Assert.That(new EffectTreeExecutor(f.State, f.Registry).RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
        private static void Answer(Fixture f, string id)
        {
            var request = Open(f); var executor = new EffectTreeExecutor(f.State, f.Registry);
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision, NormalizedValue.CreateString(id), out var reason), Is.True, reason);
            Pump(f);
        }
        private static void SetPosition(GameState state, ItemInstanceState item, string zone) =>
            typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { state, item, zone });
        private static string Snapshot(GameState state) => JsonConvert.SerializeObject(state);
    }
}
