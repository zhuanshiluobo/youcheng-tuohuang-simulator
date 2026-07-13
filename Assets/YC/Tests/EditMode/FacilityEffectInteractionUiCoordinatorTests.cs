using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class FacilityEffectInteractionUiCoordinatorTests
    {
        private GameObject canvasObject;

        [TearDown]
        public void TearDown()
        {
            if (canvasObject != null)
            {
                UnityEngine.Object.DestroyImmediate(canvasObject);
                canvasObject = null;
            }
        }

        [Test]
        public void Synchronize_SameSession_KeepsOverlayAndTradeInput()
        {
            var state = CreateState("facility-session-1", FacilityPendingChoiceTypes.ScenarioId);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var firstOverlay = GetOverlay(fixture.Dialog);
            Assert.That(firstOverlay, Is.Not.Null);

            ClickButton(firstOverlay, "Increase 0");
            Assert.That(GetText(firstOverlay, "Value 0"), Is.EqualTo("1"));

            Assert.That(Synchronize(fixture.Coordinator), Is.True);

            var synchronizedOverlay = GetOverlay(fixture.Dialog);
            Assert.That(synchronizedOverlay, Is.SameAs(firstOverlay));
            Assert.That(GetText(synchronizedOverlay, "Value 0"), Is.EqualTo("1"));
        }

        [Test]
        public void Synchronize_ChangedSession_RebuildsOverlayAndResetsTradeInput()
        {
            var state = CreateState("facility-session-1", FacilityPendingChoiceTypes.ScenarioId);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var firstOverlay = GetOverlay(fixture.Dialog);
            ClickButton(firstOverlay, "Increase 0");
            Assert.That(GetText(firstOverlay, "Value 0"), Is.EqualTo("1"));

            state.PendingCardSession.SessionId = "facility-session-2";
            Assert.That(Synchronize(fixture.Coordinator), Is.True);

            var rebuiltOverlay = GetOverlay(fixture.Dialog);
            Assert.That(rebuiltOverlay, Is.Not.Null);
            Assert.That(rebuiltOverlay, Is.Not.SameAs(firstOverlay));
            Assert.That(GetText(rebuiltOverlay, "Value 0"), Is.EqualTo("0"));
        }

        [Test]
        public void Synchronize_OrdinaryEventSession_DoesNotTakeOverAndHidesFacilityOverlay()
        {
            var state = CreateState("facility-session", FacilityPendingChoiceTypes.ScenarioId);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            Assert.That(GetOverlay(fixture.Dialog), Is.Not.Null);

            state.PendingCardSession = CreatePending("event-session", "explore_event");

            Assert.That(Synchronize(fixture.Coordinator), Is.False);
            Assert.That(GetOverlay(fixture.Dialog), Is.Null);
        }

        private CoordinatorFixture CreateCoordinator(GameState state)
        {
            var coordinatorType = Type.GetType(
                "YC.Presentation.FacilityEffectInteractionUiCoordinator, Assembly-CSharp",
                false);
            var dialogType = Type.GetType(
                "YC.Presentation.FacilityEffectChoiceDialog, Assembly-CSharp",
                false);
            Assert.That(coordinatorType, Is.Not.Null, "Missing FacilityEffectInteractionUiCoordinator.");
            Assert.That(dialogType, Is.Not.Null, "Missing FacilityEffectChoiceDialog.");

            canvasObject = new GameObject(
                "Facility Effect Coordinator Test Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

            var dialog = Activator.CreateInstance(dialogType, true);
            var mapQuery = new MapQueryService(new GameMapDefinition { MapId = "facility-ui-test" });
            Func<GameState> getState = () => state;
            Func<int> getLocalPlayerId = () => 1;
            Func<RectTransform> getCanvas = () => canvasObject.GetComponent<RectTransform>();
            Action<IReadOnlyList<WorkflowHighlight>> setHighlights = ignored => { };
            Action clearHighlights = () => { };
            Action<GameCommand> submit = ignored => { };
            Action<string> setPrompt = ignored => { };

            var constructors = coordinatorType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(constructors.Length, Is.EqualTo(1));
            var coordinator = constructors[0].Invoke(new object[]
            {
                getState,
                getLocalPlayerId,
                getCanvas,
                mapQuery,
                dialog,
                setHighlights,
                clearHighlights,
                submit,
                setPrompt
            });

            return new CoordinatorFixture(coordinator, dialog);
        }

        private static GameState CreateState(string sessionId, string scenarioId)
        {
            return new GameState
            {
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Resources =
                        {
                            Originium = 3,
                            OriginiumShard = 3,
                            Iron = 3,
                            PureOriginium = 3
                        }
                    }
                },
                PendingCardSession = CreatePending(sessionId, scenarioId)
            };
        }

        private static PendingCardSessionState CreatePending(string sessionId, string scenarioId)
        {
            return new PendingCardSessionState
            {
                SessionId = sessionId,
                ScenarioId = scenarioId,
                ChoiceType = FacilityPendingChoiceTypes.SellResources,
                CardId = "building_027",
                PlayerId = 1,
                OptionIds =
                {
                    FacilityPendingChoiceTypes.ConfirmOption,
                    FacilityPendingChoiceTypes.SkipOption
                }
            };
        }

        private static bool Synchronize(object coordinator)
        {
            var method = coordinator.GetType().GetMethod(
                "Synchronize",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(coordinator, null);
        }

        private static GameObject GetOverlay(object dialog)
        {
            var field = dialog.GetType().GetField(
                "overlay",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (GameObject)field.GetValue(dialog);
        }

        private static void ClickButton(GameObject root, string objectName)
        {
            var child = FindChild(root, objectName);
            Assert.That(child, Is.Not.Null, "Missing button " + objectName + ".");
            var button = child.GetComponent<Button>();
            Assert.That(button, Is.Not.Null);
            button.onClick.Invoke();
        }

        private static string GetText(GameObject root, string objectName)
        {
            var child = FindChild(root, objectName);
            Assert.That(child, Is.Not.Null, "Missing text " + objectName + ".");
            var text = child.GetComponent<Text>();
            Assert.That(text, Is.Not.Null);
            return text.text;
        }

        private static GameObject FindChild(GameObject root, string objectName)
        {
            var children = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < children.Length; i++)
            {
                if (children[i] != null && children[i].name == objectName)
                {
                    return children[i].gameObject;
                }
            }

            return null;
        }

        private sealed class CoordinatorFixture
        {
            public CoordinatorFixture(object coordinator, object dialog)
            {
                Coordinator = coordinator;
                Dialog = dialog;
            }

            public object Coordinator { get; private set; }

            public object Dialog { get; private set; }
        }
    }
}
