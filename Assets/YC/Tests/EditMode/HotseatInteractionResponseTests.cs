using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Harvest;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;
#if YC_UI_ISOLATED
using Assert = YC.UiIsolated.OfflineAssert;
#endif
#if !YC_UI_ISOLATED
using UnityEngine;
using UnityEngine.UI;
using UnityEditor.SceneManagement;
#endif

namespace YC.Tests.EditMode
{
    public sealed class HotseatInteractionResponseTests
    {
#if !YC_UI_ISOLATED
        [Test, Category("原生Unity")]
        public void ActualActionPanelBlocksNewActionCallbacksAndKeepsBothInspectionButtons()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Additive);
            try
            {
                Type Runtime(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
                var mobile = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(Runtime("MobileCityInteractionController"), true)).Single();
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var hud = (Component)mobile.GetType().GetField("gameplayInteractionHud", flags).GetValue(mobile);
                var view = hud.GetType().GetProperty("ActionPanelView", flags).GetValue(hud);
                var registry = hud.GetType().GetProperty("DialogRegistry", flags).GetValue(hud);
                var catalog = registry.GetType().GetProperty("CardVisualCatalog", flags).GetValue(registry);
                int commands = 0, inspection = 0, endings = 0;
                Action action = () => commands++;
                var type = Runtime("ActionPanelController");
                object controller = type.GetMethod("Bind", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] {
                    view, catalog, (Action<string>)(_ => commands++), (Action)(() => inspection++), action, action, action, action,
                    (Action)(() => endings++), (Action)(() => inspection++), action });
                bool canBegin = false;
                type.GetProperty("CanBeginAction").SetValue(controller, (Func<bool>)(() => canBegin));
                type.GetMethod("SetButtonStates").Invoke(controller, new object[] { true, true, true, true, true, true, true, true, true });
                Button Button(string name) => (Button)view.GetType().GetProperty(name).GetValue(view);
                foreach (string name in new[] { "DeployButton", "DispatchButton", "ExploreButton", "MoveCityButton", "SpecialButton" })
                {
                    var button = Button(name); Assert.That(button.interactable, Is.False);
                    button.onClick.Invoke();
                }
                Assert.That(Button("BuildButton").interactable, Is.True);
                Assert.That(Button("DeclareCityStyleButton").interactable, Is.True);
                Button("BuildButton").onClick.Invoke(); Button("DeclareCityStyleButton").onClick.Invoke();
                Button("EndRoundButton").onClick.Invoke();
                Assert.That(commands, Is.EqualTo(0)); Assert.That(inspection, Is.EqualTo(2)); Assert.That(endings, Is.EqualTo(1));
                canBegin = true;
                type.GetMethod("SetButtonStates").Invoke(controller, new object[] { true, true, true, true, true, true, true, true, true });
                Button("MoveCityButton").onClick.Invoke(); Assert.That(commands, Is.EqualTo(1));
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }
#endif
        [Test]
        public void SettlementAdmissionUsesLifecycleAndVisibilityInsteadOfPageShowing()
        {
            var state = new GameState();
            var view = new GameStateView { ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 1 };
            Assert.That(TurnActionPanelPresenter.HasCommittedSettlementContext(state, view, 1), Is.False);
            view.CharacterUses.Add(new YC.Domain.Cards.CharacterUseRuntimeState { ExecutingPlayerId = 1, UseId = "active-use" });
            Assert.That(TurnActionPanelPresenter.HasCommittedSettlementContext(state, view, 1), Is.True);
            view.CharacterUses[0].Finished = true;
            Assert.That(TurnActionPanelPresenter.HasCommittedSettlementContext(state, view, 1), Is.False);
            view.ActiveSettlementWindowId = "before-mode";
            Assert.That(TurnActionPanelPresenter.HasCommittedSettlementContext(state, view, 1), Is.True);
            view.ActiveSettlementWindowId = string.Empty;
            view.Interactions.Add(new InteractionView { Kind = "collection.task", AnsweringPlayerId = 1, Status = "open" });
            Assert.That(TurnActionPanelPresenter.HasCommittedSettlementContext(state, view, 1), Is.False);
            view.Interactions.Clear();
            view.Interactions.Add(new InteractionView { Kind = "character.use.mode", AnsweringPlayerId = 2, Status = "open" });
            Assert.That(TurnActionPanelPresenter.HasCommittedSettlementContext(state, view, 1), Is.False);
        }

        [Test]
        public void CommittedSettlementBlocksNewActionsWhileBothInspectionEntriesRemainAvailable()
        {
            var fixture = typeof(TurnActionPresenterTests).GetMethod("CreateFixture", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { false });
            var turn = (TurnActionPresenter)fixture.GetType().GetField("Presenter").GetValue(fixture);
            turn.ActionPanelPresenter.HasCommittedSettlement = () => true;
            var model = turn.BuildActionPanelViewModel();
            Assert.That(model.CanInspectCityStyles, Is.True);
            Assert.That(model.CanViewFacilitySupply, Is.True);
            Assert.That(model.CanDeclareCityStyle || model.CanBuild || model.CanMoveCity || model.CanExplore || model.CanDeploy || model.CanDispatch, Is.False);
            Assert.That(turn.ActionPanelPresenter.CanStartMainAction(), Is.False);
            Assert.That(turn.ActionPanelPresenter.CanStartQuickAction(), Is.False);
            turn.ActionPanelPresenter.HasCommittedSettlement = () => false;
            turn.BeginMoveAction();
            Assert.That(turn.MoveInteraction.IsActive, Is.True, "未提交本地草稿不是已挂链结算");
            Assert.That(turn.ActionPanelPresenter.CanStartMainAction(), Is.True);
        }

        [Test]
        public void CollectionAppliedResultReleasesBusyBeforeSynchronizingSuccessor()
        {
            var source = typeof(ResourceCollectionPresenterTests);
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var state = (GameState)source.GetMethod("CreateState", flags).Invoke(null, null);
            var map = new MapQueryService((GameMapDefinition)source.GetMethod("CreateMap", flags).Invoke(null, null));
            var context = new CollectionContext { State = state };
            var port = new AppliedCollectionPort(context);
            var view = new CollectionView();
            var presenter = new ResourceCollectionPresenter(context, port, view, map, new ResourceCollectionService(map));
            view.Presenter = presenter;
            presenter.Begin();
            var selected = new List<string>(presenter.SelectedLocationIds);
            Assert.That(selected, Is.Not.Empty);
            presenter.Submit();
            Assert.That(port.Commands.Count, Is.EqualTo(1));
            Assert.That(port.Commands[0].Kind, Is.EqualTo(GameCommandKind.CollectResource));
            Assert.That(view.Synchronizations, Is.EqualTo(1));
            Assert.That(view.BusyWhenSynchronized, Is.False, "准确成功收尾必须在同步后继阶段前释放本人提交等待");
            Assert.That(presenter.IsSubmissionPending, Is.False);
            Assert.That(context.State.Phase, Is.EqualTo(GamePhase.ActionRound1));
        }

        private sealed class CollectionContext : IGameplayContext
        {
            public GameState State;
            public GameState CurrentState => State;
            public int LocalPlayerId => 1;
        }
        private sealed class AppliedCollectionPort : IGameCommandPort
        {
            private readonly CollectionContext context;
            public readonly List<GameCommand> Commands = new List<GameCommand>();
            public AppliedCollectionPort(CollectionContext context) { this.context = context; }
            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                Commands.Add(command);
                context.State.FindPlayer(1).HasCollectedResourcesThisRound = true;
                context.State.Phase = GamePhase.ActionRound1;
                return new WorkflowSubmissionResult(CommandResult.SuccessResult(new List<GameEvent>(), string.Empty), true);
            }
        }
        private sealed class CollectionView : IResourceCollectionView
        {
            public ResourceCollectionPresenter Presenter;
            public int Synchronizations;
            public bool BusyWhenSynchronized;
            public void ShowPrompt(string message) { }
            public void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights) { }
            public void ClearHighlights() { }
            public void RefreshSelectionView() { }
            public void RefreshFromState() { Synchronizations++; BusyWhenSynchronized = Presenter.IsSubmissionPending; }
            public void ShowRoutePaymentOptions(string routeId, int cost, IReadOnlyList<int> recipients) { }
            public string GetPlayerDisplayName(int playerId) => playerId.ToString();
        }
    }
}
