using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class MobileCityInteractionControllerTests
    {
        private GameObject owner;

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                UnityEngine.Object.DestroyImmediate(owner);
                owner = null;
            }
        }

        [Test]
        public void CharacterViewerPermitRefreshKeepsBindingRulesAndLiveClickGate()
        {
            var fixture = new CardViewerTests();
            fixture.SetUp();
            try
            {
                var viewer = (Component)typeof(CardViewerTests).GetField("viewer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture);
                var sprite = (Sprite)typeof(CardViewerTests).GetField("sprite", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture);
                var type = viewer.GetType();
                var effect = Type.GetType("YC.Presentation.CardViewerEffect, Assembly-CSharp", true);
                var commands = 0; var closes = 0; var current = true; var permitted = true;
                var bridge = new CardViewerTests.CallbackBridge { callback = () => commands++ };
                var callback = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(effect), bridge,
                    typeof(CardViewerTests.CallbackBridge).GetMethod("Run").MakeGenericMethod(effect));
                var open = type.GetMethod("OpenCharacter", new[] { typeof(string), typeof(Sprite), typeof(bool), typeof(bool),
                    typeof(string), typeof(string), typeof(Func<bool>), callback.GetType(), typeof(Action) });
                var availability = type.GetMethod("RefreshSubmissionAvailability");
                var update = type.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                var plot = (Button)type.GetProperty("PlotButton").GetValue(viewer);
                var strategy = (Button)type.GetProperty("StrategyButton").GetValue(viewer);
                void Bind()
                {
                    open.Invoke(viewer, new object[] { "许可绑定角色", sprite, false, true, "规则禁止", string.Empty,
                        (Func<bool>)(() => current), callback, (Action)(() => closes++) });
                    type.GetProperty("CanSubmit").SetValue(viewer, (Func<bool>)(() => permitted));
                }
                Bind();
                permitted = false;
                availability.Invoke(viewer, new object[] { false });
                update.Invoke(viewer, null);
                Assert.That(viewer.gameObject.activeSelf, Is.True, "许可暂撤不得关闭仍有效的页面。");
                Assert.That(type.GetProperty("CardId").GetValue(viewer), Is.EqualTo("许可绑定角色"));
                Assert.That(type.GetProperty("DisplayedSprite").GetValue(viewer), Is.SameAs(sprite));
                Assert.That(plot.interactable, Is.False); Assert.That(strategy.interactable, Is.False);
                strategy.onClick.Invoke();
                Assert.That(commands, Is.Zero); Assert.That(closes, Is.Zero);
                permitted = true;
                availability.Invoke(viewer, new object[] { true });
                Assert.That(plot.interactable, Is.False, "规则禁用模式不得因许可恢复而启用。");
                Assert.That(strategy.interactable, Is.True);
                permitted = false; // 尚未刷新按钮时也必须读取当前许可，不能先Dismiss再拒绝。
                strategy.onClick.Invoke();
                Assert.That(commands, Is.Zero); Assert.That(viewer.gameObject.activeSelf, Is.True);
                permitted = true;
                strategy.onClick.Invoke(); strategy.onClick.Invoke();
                Assert.That(commands, Is.EqualTo(1)); Assert.That(closes, Is.Zero);
                Assert.That(viewer.gameObject.activeSelf, Is.False);
                Bind(); permitted = false;
                availability.Invoke(viewer, new object[] { false });
                type.GetMethod("Close").Invoke(viewer, null);
                Assert.That(closes, Is.EqualTo(1), "现有用户关闭仍沿原返回回调。");
                permitted = true; Bind(); current = false;
                update.Invoke(viewer, null);
                Assert.That(viewer.gameObject.activeSelf, Is.False, "真实请求失效仍按原路径清理。");
                Assert.That(commands, Is.EqualTo(1)); Assert.That(closes, Is.EqualTo(1));
            }
            finally { fixture.TearDown(); }
        }

        [Test]
        public void SimulationPermissionObservationPreservesResultRootViewerAndLocalBoundaries()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.GameId = "许可观察局"; state.ContentPackHash = "许可观察内容包";
            var session = new GameSession(state);
            session.RefreshLocalProjection(GameStateViewer.Player(1));
            var view = session.View;
            var commands = new RecordingCommandPort();
            var controller = CreateController(state, commands);
            SetPrivateField(controller, "session", session);
            SetPrivateField(controller, "localPlayerId", 1);
            SetPrivateField(controller, "lastSimulationUiSession", session);
            SetPrivateField(controller, "lastSimulationUiState", session.State);
            SetPrivateField(controller, "lastSimulationUiView", view);
            SetPrivateField(controller, "lastSimulationUiGame", view.GameId);
            SetPrivateField(controller, "lastSimulationUiContent", view.ContentPackHash);
            SetPrivateField(controller, "lastSimulationUiRevision", view.StateRevision);
            SetPrivateField(controller, "lastSimulationUiViewer", 1);
            SetPrivateField(controller, "lastSimulationUiReady", true);
            var same = controller.GetType().GetMethod("IsSameSimulationUiContext", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That((bool)same.Invoke(controller, new object[] { view, true, false }), Is.True);
            Assert.That((bool)same.Invoke(controller, new object[] { view, true, true }), Is.False,
                "同revision准确拒绝仍必须走完整同步并重路由。");
            Assert.That((bool)same.Invoke(controller, new object[] { view, false, false }), Is.False);
            Assert.That((bool)same.Invoke(controller, new object[] { null, true, false }), Is.False);
            var replacedView = GameStateViewProjector.ProjectForPlayer(state, 1);
            Assert.That(replacedView.StateRevision, Is.EqualTo(view.StateRevision));
            Assert.That((bool)same.Invoke(controller, new object[] { replacedView, true, false }), Is.False,
                "相同revision的新投影不能沿用旧展示缓存。");
            SetPrivateField(controller, "lastSimulationUiState", GameStateCloneService.DeepClone(state));
            Assert.That((bool)same.Invoke(controller, new object[] { view, true, false }), Is.False,
                "恢复换State根不能仅靠revision短路。");
            SetPrivateField(controller, "lastSimulationUiState", session.State);
            SetPrivateField(controller, "localPlayerId", 2);
            Assert.That((bool)same.Invoke(controller, new object[] { view, true, false }), Is.False);
            SetPrivateField(controller, "localPlayerId", 1);
            SetPrivateField(controller, "viewerChanged", true);
            Assert.That((bool)same.Invoke(controller, new object[] { view, true, false }), Is.False);
            SetPrivateField(controller, "viewerChanged", false);
            SetPrivateField(controller, "lastSimulationUiSession", new GameSession(state));
            Assert.That((bool)same.Invoke(controller, new object[] { view, true, false }), Is.False);
            var permissionOnly = controller.GetType().GetMethod("RefreshSimulationPermissionOnly", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That((bool)permissionOnly.Invoke(controller, null), Is.False,
                "无新模拟提交器的旧Local入口不走许可捷径。");
            Assert.That(commands.ReceivedCommand, Is.Null);
        }

        [Test]
        public void BeginNextRound_DuringCleanup_SubmitsEndActionCommand()
        {
            var state = CreateState(GamePhase.Cleanup);
            var originalState = JsonUtility.ToJson(state);
            var commands = new RecordingCommandPort();
            var controller = CreateController(state, commands);

            InvokeBeginNextRound(controller);

            Assert.That(commands.ReceivedCommand, Is.Not.Null);
            Assert.That(commands.ReceivedCommand.Kind, Is.EqualTo(GameCommandKind.EndAction));
            Assert.That(commands.ReceivedCommand.PlayerId, Is.EqualTo(1));
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(originalState));
        }

        [Test]
        public void BeginNextRound_DuringWrongPhase_DoesNotSubmitOrMutateState()
        {
            var state = CreateState(GamePhase.RoundStart);
            var originalState = JsonUtility.ToJson(state);
            var commands = new RecordingCommandPort();
            var controller = CreateController(state, commands);

            InvokeBeginNextRound(controller);

            Assert.That(commands.ReceivedCommand, Is.Null);
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(originalState));
        }

        [Test]
        public void PointerMapCoordinate_WithPerspectiveCamera_IntersectsRendererPlane()
        {
            var geometryType = Type.GetType(
                "YC.Presentation.MapCameraGeometry, Assembly-CSharp",
                false);
            Assert.That(geometryType, Is.Not.Null);

            owner = new GameObject("Perspective Map Coordinate Test");
            var cameraOwner = new GameObject("Camera");
            cameraOwner.transform.SetParent(owner.transform, false);
            var camera = cameraOwner.AddComponent<Camera>();
            camera.orthographic = false;
            camera.fieldOfView = 45f;
            camera.transform.position = new Vector3(0f, -10f, -10f);
            camera.transform.LookAt(Vector3.zero);

            var mapOwner = new GameObject("Map");
            mapOwner.transform.SetParent(owner.transform, false);
            var renderer = mapOwner.AddComponent<SpriteRenderer>();
            var texture = new Texture2D(16, 16);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, 16f, 16f), new Vector2(0.5f, 0.5f));
            try
            {
                renderer.sprite = sprite;
                var method = geometryType.GetMethod(
                    "TryScreenToMapPlane",
                    BindingFlags.Static | BindingFlags.Public);
                Assert.That(method, Is.Not.Null);

                var arguments = new object[]
                {
                    camera,
                    (Vector2)camera.WorldToScreenPoint(Vector3.zero),
                    renderer,
                    Vector3.zero
                };
                Assert.That(method.Invoke(null, arguments), Is.True);
                Assert.That(((Vector3)arguments[3] - Vector3.zero).sqrMagnitude, Is.LessThan(0.000001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sprite);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private Component CreateController(GameState state, RecordingCommandPort commands)
        {
            var controllerType = Type.GetType(
                "YC.Presentation.MobileCityInteractionController, Assembly-CSharp",
                false);
            Assert.That(controllerType, Is.Not.Null, "Missing MobileCityInteractionController.");

            owner = new GameObject("Mobile City Interaction Controller Test");
            owner.SetActive(false);
            var controller = owner.AddComponent(controllerType);

            SetPrivateField(controller, "turnActionPresenter", CreateTurnActionPresenter(state, commands));
            return controller;
        }

        private static TurnActionPresenter CreateTurnActionPresenter(
            GameState state,
            RecordingCommandPort commands)
        {
            var context = new FakeContext { State = state };
            var view = new FakeView();
            var mapQuery = new MapQueryService(new GameMapDefinition
            {
                MapId = "controller-forwarding-test",
                Locations =
                {
                    new MapLocationDefinition
                    {
                        LocationId = "A",
                        CanDockCity = true,
                        InfluenceSlotCount = 1
                    }
                }
            });
            var influenceService = new InfluenceService(mapQuery);
            var resourceCollection = new ResourceCollectionPresenter(
                context,
                commands,
                view,
                mapQuery,
                new ResourceCollectionService(mapQuery));
            var influence = new InfluenceActionPresenter(
                context,
                commands,
                view,
                mapQuery,
                influenceService);
            var exploration = new ExplorationEventPresenter(
                context,
                commands,
                view,
                mapQuery,
                new ExplorationService(mapQuery),
                influenceService);
            var coordinator = new InteractionFlowCoordinator();
            coordinator.ResetToChooseAction();
            return new TurnActionPresenter(
                context,
                commands,
                view,
                coordinator,
                mapQuery,
                resourceCollection,
                influence,
                exploration);
        }

        private static void InvokeBeginNextRound(Component controller)
        {
            var method = controller.GetType().GetMethod(
                "BeginNextRound",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Missing BeginNextRound.");
            method.Invoke(controller, null);
        }

        private static void SetPrivateField(Component controller, string fieldName, object value)
        {
            var field = controller.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing MobileCityInteractionController." + fieldName + ".");
            field.SetValue(controller, value);
        }

        private static GameState CreateState(GamePhase phase)
        {
            return new GameState
            {
                Phase = phase,
                Round = 3,
                MaxRounds = 8,
                ActionRound = 0,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        ActedMainActionThisTurn = true,
                        HasMovedCityThisRound = true,
                        HasCollectedResourcesThisRound = true
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Color = PlayerColor.Blue,
                        ActedMainActionThisTurn = true,
                        HasMovedCityThisRound = true,
                        HasCollectedResourcesThisRound = true
                    }
                }
            };
        }

        private sealed class FakeContext : IWritableGameplayContext
        {
            public GameState State;

            public GameState CurrentState
            {
                get { return State; }
            }

            public int LocalPlayerId { get; private set; } = 1;

            public bool ControlsCurrentPlayerLocally
            {
                get { return true; }
            }

            public void SetLocalPlayerId(int playerId) { LocalPlayerId = playerId; }
        }

        private sealed class RecordingCommandPort : IGameCommandPort
        {
            public GameCommand ReceivedCommand { get; private set; }

            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                ReceivedCommand = command;
                return new WorkflowSubmissionResult(
                    CommandResult.Invalid(ValidationResult.Failure(
                        CommandErrorCode.InvalidTarget,
                        "Test command port rejected the command.")),
                    false);
            }
        }

        private sealed class FakeView : ITurnActionView, IResourceCollectionView,
            IInfluenceActionView, IExplorationEventView
        {
            public void ShowPrompt(string message) { }
            public void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights) { }
            public void ClearHighlights() { }
            public void RefreshFromState() { }
            public void RefreshInformation() { }
            public void RefreshActionPanel() { }
            public void ShowPendingChoice() { }
            public void CompleteMainActionPresentation(string actionName) { }
            public void ShowBuildFacilityDraft(BuildFacilityDraftViewModel viewModel) { }
            public void HideBuildFacilityDraft() { }
            public void ShowCityStyleOptions(CityStyleOptionsViewModel viewModel) { }
            public void ShowRoutePaymentOptions(
                string routeId,
                int cost,
                IReadOnlyList<int> recipientPlayerIds) { }
            public string GetPlayerDisplayName(int playerId) { return playerId.ToString(); }
            public void RefreshSelectionView() { }
            public void SetInteractionMode(InteractionMode mode) { }
            public void ShowDispatchDecision(DispatchDecisionViewModel viewModel) { }
            public void HideDispatchDecision() { }
            public void RefreshInfluencePreview(
                bool hasPendingFirstMove,
                string firstSourceSlotId,
                string firstTargetSlotId) { }
            public void CompleteAction(string actionName) { }
            public void ShowExplorePathOptions(ExplorePathOptionsViewModel viewModel) { }
            public void ShowExplorePaymentOptions(ExplorePaymentOptionsViewModel viewModel) { }
            public void ShowEventCardOptions(EventCardOptionsViewModel viewModel) { }
            public void CollapseEventOptions() { }
            public void HideEventOptions() { }
            public void RefreshResourceAndInfluence() { }
        }
    }
}
