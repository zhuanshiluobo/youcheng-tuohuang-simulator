using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class AmiyaInsertionCharacterViewerTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type Runtime(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Invoke(object value, string method, params object[] args) =>
            value.GetType().GetMethods(Members).Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(value, args);

        [TestCase("strategy", "strategy-first"), TestCase("tactic", "tactic-first")]
        [Category("需要Unity")]
        public void ExistingCardButtonSubmitsTheExactOrderCandidateOnceWithoutAnotherSelectionPage(string mode, string expected)
        {
            var root = new GameObject("阿米娅原卡面组件接缝", typeof(RectTransform), typeof(Canvas));
            var events = new GameObject("本测试指针事件", typeof(EventSystem));
            var hudPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            var viewerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CardViewer.prefab");
            var viewer = UnityEngine.Object.Instantiate(viewerPrefab, root.transform).GetComponent(Runtime("CardViewer"));
            viewer.GetType().GetMethod("Awake", Members).Invoke(viewer, null);
            // 临时Sprite只承载本组件事件夹具，不选材、不保存图片、不计角色获得或真实画面验收。
            var texture = new Texture2D(2, 3); var sprite = Sprite.Create(texture, new Rect(0, 0, 2, 3), Vector2.one * .5f);
            try
            {
                var hudView = hudPrefab.GetComponent(Runtime("GameplayInteractionHudView"));
                var registry = hudView.GetType().GetProperty("DialogRegistry").GetValue(hudView);
                var dialog = Activator.CreateInstance(Runtime("CharacterCardEffectChoiceDialog"), Members, null,
                    new[] { registry, root.transform }, null);
                var state = new GameState { GameId = "original-card-seam", EffectRuntime = new EffectRuntimeState { StateRevision = 7 } };
                state.Players.Add(new PlayerState { PlayerId = 1 });
                var request = new InteractionRequest { RequestId = "same-order-request", InteractionTypeId = CharacterLifecycleService.OrderInteractionTypeId,
                    OwnerEffectId = "same-use-owner", AnsweringPlayerId = 1, StateRevision = 7, Status = "open", Visibility = "owner",
                    CandidateSetId = "same-order-candidates", CandidateSetVersion = 1, MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_ids" };
                request.CandidateIds.AddRange(new[] { "strategy-first", "tactic-first" });
                state.EffectRuntime.InteractionRequests.Add(request);
                var sent = new List<GameCommand>();
                var coordinator = Activator.CreateInstance(Runtime("CharacterAbilityInteractionUiCoordinator"), Members, null,
                    new object[] { (Func<GameState>)(() => state), (Func<int>)(() => 1), dialog,
                        (Action<IReadOnlyList<WorkflowHighlight>>)(_ => {}), (Action)(() => {}),
                        (Action<GameCommand>)(command => sent.Add(command)), (Action<string>)(_ => {}) }, null);
                coordinator.GetType().GetProperty("GetVisibleRequests").SetValue(coordinator,
                    (Func<IReadOnlyList<InteractionRequest>>)(() => state.EffectRuntime.InteractionRequests));
                coordinator.GetType().GetProperty("CanSubmit").SetValue(coordinator, (Func<bool>)(() => true));
                Action<InteractionRequestProjection> render = projection =>
                {
                    var bridge = new ChoiceBridge { Request = projection, Coordinator = coordinator };
                    var handlerType = typeof(Action<>).MakeGenericType(Runtime("CardViewerEffect"));
                    var handler = Delegate.CreateDelegate(handlerType, bridge,
                        typeof(ChoiceBridge).GetMethod("Choose").MakeGenericMethod(Runtime("CardViewerEffect")));
                    var open = viewer.GetType().GetMethods().Single(m => m.Name == "OpenCharacter" && m.GetParameters()[5].ParameterType == typeof(string));
                    open.Invoke(viewer, new object[] { "same-card", sprite, true, true, "", "",
                        (Func<bool>)(() => request.StateRevision == projection.StateRevision), handler, (Action)(() => {}) });
                };
                coordinator.GetType().GetProperty("RenderUseSelection").SetValue(coordinator, render);
                var projection = InteractionRequestProjector.ProjectForPlayer(request, 1);
                Invoke(coordinator, "Render", projection);
                Assert.That(dialog.GetType().GetProperty("IsShowing").GetValue(dialog), Is.EqualTo(false));
                var button = (Button)viewer.GetType().GetProperty(mode == "strategy" ? "StrategyButton" : "PlotButton").GetValue(viewer);
                var pointer = new PointerEventData(events.GetComponent<EventSystem>()) { button = PointerEventData.InputButton.Left };
                ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
                ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
                Assert.That(sent, Has.Count.EqualTo(1));
                Assert.That(sent[0].Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
                Assert.That(sent[0].OptionIds, Is.EqualTo(new[] { expected }));
                Assert.That(sent[0].Parameters["interactionId"], Is.EqualTo(projection.InteractionId));
                Assert.That(sent[0].Parameters["expectedRevision"], Is.EqualTo("7"));
                Assert.That(request.CandidateIds.Contains(CharacterLifecycleService.CancelChoiceId), Is.False);
                Assert.That(dialog.GetType().GetProperty("IsShowing").GetValue(dialog), Is.EqualTo(false));
                Invoke(coordinator, "NotifyInteractionCommandResolved", state.GameId, sent[0].CommandId, true, YC.Domain.Rules.CommandErrorCode.None);
                request.StateRevision = 8; state.EffectRuntime.StateRevision = 8;
                Assert.That(Invoke(coordinator, "TrySubmitUseSelection", projection, expected), Is.EqualTo(false), "旧卡面请求不能重建成新版本回答");
                Assert.That(sent, Has.Count.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(events);
                UnityEngine.Object.DestroyImmediate(sprite); UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        public sealed class ChoiceBridge
        {
            public InteractionRequestProjection Request;
            public object Coordinator;
            public void Choose<T>(T effect)
            {
                string mode = effect.ToString() == "Plot" ? "tactic" : "strategy";
                Invoke(Coordinator, "TrySubmitUseSelection", Request, CharacterCardPanelPresenter.ModeSelectionCandidate(Request, mode));
            }
        }
    }
}
