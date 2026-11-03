using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    // 现行正式请求用例保留；旧专属阶段机正向的逐项归档见API-43实施记录。
    public sealed class FacilityEffectInteractionUiCoordinatorTests
    {
        private GameObject canvasObject;


        [Test]
        public void EffectRequest_MultipleTargetsRequiresExplicitSelectionAndConfirmation()
        {
            var state = CreateState();
            var commands = new List<GameCommand>();
            object dialog;
            var coordinator = CreateEffectRequestCoordinator(state, commands, out dialog);
            var request = new InteractionRequest
            {
                InteractionId = "facility.targets", InteractionTypeId = "facility.entry.choice",
                Status = "open", Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 11,
                MinSelections = 2, MaxSelections = 2, CandidateIds = new List<string> { "slot:a", "slot:b", "slot:c" }
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            state.EffectRuntime.StateRevision = state.EffectRuntime.InteractionRequests[0].StateRevision;
            Assert.That(Synchronize(coordinator), Is.True);
            ClickButton(GetOverlay(dialog), "Option 2");
            Assert.That(commands, Is.Empty, "不得自动补齐玩家未选择的目标。");
            ClickButton(GetOverlay(dialog), "Option 0");
            Assert.That(commands, Is.Empty);
            ClickButton(GetOverlay(dialog), "Confirm Selection");
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { "slot:c", "slot:a" }));
            Assert.That(commands[0].Parameters[YC.Application.Interactions.AnswerInteractionCommandHandler.ExpectedRevisionParameter], Is.EqualTo("11"));
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(request.CandidateIds, Has.Count.EqualTo(3));
        }



        [TestCase(true)]
        [TestCase(false)]
        public void EffectRequest_ResourceAllocationUsesAnswerProtocolWithoutChangingResources(bool sale)
        {
            var state = CreateState();
            var commands = new List<GameCommand>();
            object dialog;
            var coordinator = CreateEffectRequestCoordinator(state, commands, out dialog);
            state.EffectRuntime.InteractionRequests.Add(new InteractionRequest
            {
                InteractionId = "facility.resources", InteractionTypeId = "facility.entry.choice",
                Status = "open", Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 12,
                MinSelections = 1, MaxSelections = 1, AnswerSchema = "resource_allocation",
                CandidateIds = sale ? new List<string> { "choice.confirm", "choice.skip" }
                    : new List<string> { "Originium", "OriginiumShard", "Iron" }
            });
            state.EffectRuntime.StateRevision = state.EffectRuntime.InteractionRequests[0].StateRevision;
            Assert.That(Synchronize(coordinator), Is.True);
            var overlay = GetOverlay(dialog);
            for (var i = 0; i < (sale ? 1 : 5); i++) ClickButton(overlay, "Increase 0");
            Assert.That(commands, Is.Empty);
            ClickButton(overlay, "Confirm");
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            Assert.That(commands[0].Parameters[YC.Application.Interactions.AnswerInteractionCommandHandler.AnswerValueParameter],
                Is.EqualTo(sale ? "Originium=1" : "Originium=5"));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
        }

        [TestCase(FacilityPendingChoiceTypes.CopyAdjacentEntryEffect)]
        [TestCase(FacilityPendingChoiceTypes.BuildAdditionalFacility)]
        [TestCase(FacilityPendingChoiceTypes.BuildExtensionHub)]
        [TestCase(FacilityPendingChoiceTypes.SellResources)]
        [TestCase(FacilityPendingChoiceTypes.FreeCityMove)]
        [TestCase(FacilityPendingChoiceTypes.ChooseFiveBasicResources)]
        [TestCase(FacilityPendingChoiceTypes.ReplaceOneInfluence)]
        [TestCase(FacilityPendingChoiceTypes.DeployTwoInfluences)]
        [TestCase(FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore)]
        public void RetiredPendingFacilitySessionCannotActivateUiOrSubmitWithoutAuthoritativeRequest(string choiceType)
        {
            var state = CreateState();
            state.PendingCardSession = new PendingCardSessionState { SessionId = "retired-facility-session",
                ScenarioId = FacilityPendingChoiceTypes.ScenarioId, ChoiceType = choiceType, CardId = "building_027", PlayerId = 1 };
            var commands = new List<GameCommand>();
            object dialog;
            var coordinator = CreateEffectRequestCoordinator(state, commands, out dialog);
            Assert.That(Synchronize(coordinator), Is.False);
            Assert.That(GetOverlay(dialog), Is.Null);
            Assert.That(commands, Is.Empty, "旧会话不是正式InteractionRequest，不能恢复死协调器发命令。");
            Assert.That(state.PendingCardSession.ChoiceType, Is.EqualTo(choiceType));
        }


        private object CreateEffectRequestCoordinator(GameState state, List<GameCommand> commands, out object dialog)
        {
            canvasObject = new GameObject("设施正式请求验证", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var registryType = Type.GetType("YC.Presentation.GameplayDialogRegistry, Assembly-CSharp", true);
            var registry = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab").GetComponentInChildren(registryType, true);
            var type = Type.GetType("YC.Presentation.FacilityInteractionUiCoordinator, Assembly-CSharp", true);
            var coordinator = Activator.CreateInstance(type, new object[]
            {
                new Func<GameState>(() => state), new Func<int>(() => 1),
                new Func<RectTransform>(() => canvasObject.GetComponent<RectTransform>()), registry,
                new Action<IReadOnlyList<WorkflowHighlight>>(_ => { }), new Action(() => { }),
                new Action<GameCommand>(commands.Add), new Action<string>(_ => { })
            });
            dialog = type.GetField("dialog", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(coordinator);
            return coordinator;
        }



        [SetUp]
        public void SetUpViewerPrefab()
        {
            ViewerPrefabTestUtility.RegisterZoomablePrefab();
        }

        private static GameState CreateState() => new GameState
        {
            GameId = "facility-request-ui-contract", CurrentPlayerId = 1,
            Players = { new PlayerState { PlayerId = 1,
                Resources = { Originium = 3, OriginiumShard = 3, Iron = 3, PureOriginium = 3 } } }
        };

        [TearDown]
        public void TearDown()
        {
            if (canvasObject != null) UnityEngine.Object.DestroyImmediate(canvasObject);
            canvasObject = null;
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
            var shellField = dialog.GetType().GetField(
                "shell",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(shellField, Is.Not.Null);
            var shell = shellField.GetValue(dialog);
            Assert.That(shell, Is.Not.Null);
            var viewField = shell.GetType().GetField(
                "view",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(viewField, Is.Not.Null);
            var view = viewField.GetValue(shell) as Component;
            return view == null ? null : view.gameObject;
        }

        private static void ClickButton(GameObject root, string objectName)
        {
            var child = FindChild(root, objectName);
            Assert.That(child, Is.Not.Null, "Missing button " + objectName + ".");
            var button = child.GetComponent<Button>();
            Assert.That(button, Is.Not.Null);
            button.onClick.Invoke();
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
    }
}
