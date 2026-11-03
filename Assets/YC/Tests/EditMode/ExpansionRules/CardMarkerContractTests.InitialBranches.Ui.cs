using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Presentation;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class CardMarkerContractTests
    {
        private static Type InitialUiType(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object InitialUiCall(object target, string name, params object[] args) => target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args);

        [TestCase("Assets/Scenes/SampleScene.unity", false)] [TestCase("Assets/Scenes/SampleScene.unity", true)]
        [TestCase("Assets/Scenes/ThreePlayerScene.unity", false)] [TestCase("Assets/Scenes/ThreePlayerScene.unity", true)]
        public void InitialMarkerOptionalBranchesUseActualSceneExecutionRowsAndOriginalDecline(string path, bool decline)
        {
            StartActualInitialBranch("infected.character.lin");
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            var scene = SceneManager.GetSceneByPath(path); bool close = !scene.IsValid() || !scene.isLoaded;
            if (close) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            GameObject canvas = null; IInteractionRequestRenderer renderer = null;
            try
            {
                var actualRegistry = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(InitialUiType("GameplayDialogRegistry"), true)).Single();
                canvas = new GameObject("初始标记组件隔离画布", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var dialog = Activator.CreateInstance(InitialUiType("CharacterCardEffectChoiceDialog"), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { actualRegistry, (RectTransform)canvas.transform }, null);
                var resolver = new GameplayTextResolver(pack.Presentation);
                GameplayTextDocument Resolve(GameplayTextRef text) => resolver.Resolve(text, new GameplayTextContext { Catalog = pack.Presentation,
                    ContentHash = pack.ContentHash, Locale = "zh-CN", ViewerPlayerId = 1, StateRevision = state.EffectRuntime.StateRevision });
                void Render(Component component, GameplayTextDocument doc, bool single) { var text = component as Text ?? component.GetComponent<Text>(); if (text != null) text.text = doc?.ToPlainText() ?? ""; }
                InitialUiCall(dialog, "BindGameplayText", new Func<GameplayTextRef, GameplayTextDocument>(Resolve), new Action<Component, GameplayTextDocument, bool>(Render));
                var commands = new List<GameCommand>();
                renderer = (IInteractionRequestRenderer)Activator.CreateInstance(InitialUiType("CharacterAbilityInteractionUiCoordinator"), new object[] {
                    new Func<GameState>(() => state), new Func<int>(() => 1), dialog, new Action<IReadOnlyList<WorkflowHighlight>>(_ => {}),
                    new Action(() => {}), new Action<GameCommand>(commands.Add), new Action<string>(_ => {}) });
                renderer.GetType().GetProperty("GetVisibleState").SetValue(renderer, new Func<GameStateView>(() => GameStateViewProjector.ProjectForPlayer(state, 1)));
                renderer.Render(InteractionRequestProjector.ProjectForPlayer(request, 1));
                Assert.That(Stored, Is.Empty); Assert.That(commands, Is.Empty);
                var actions = canvas.GetComponentsInChildren(InitialUiType("UiEffectRowView"), true)
                    .Where(row => row.gameObject.activeInHierarchy).Select(row => (Button)row.GetType().GetProperty("StatusButton").GetValue(row)).ToArray();
                Assert.That(actions.Length, Is.EqualTo(2));
                var cancel = canvas.GetComponentsInChildren<Button>(true).Single(b => b.name == "Cancel Character Effect" && b.gameObject.activeInHierarchy);
                var button = decline ? cancel : actions.Single(b => b.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "PlayerAutomationId" &&
                    (string)(c.GetType().GetProperty("Id")?.GetValue(c) ?? c.GetType().GetField("Id")?.GetValue(c)) == "2"));
                button.onClick.Invoke(); button.onClick.Invoke(); Assert.That(commands.Count, Is.EqualTo(1));
                if (decline) Assert.That(commands[0].OptionIds, Is.Empty); else Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { "2" }));
                var result = new AnswerInteractionCommandHandler(registry).Handle(state, commands[0]); Assert.That(result.Succeeded, Is.True, result.LogMessage);
                Assert.That(Stored.Count, Is.EqualTo(decline ? 0 : 2));
                Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1)); Assert.That(state.ActionRound, Is.EqualTo(1));
                artifacts.WriteLabeledText("实际场景引用与原控件命令", Newtonsoft.Json.JsonConvert.SerializeObject(new { path, decline, commands,
                    scope = "实际场景含覆盖引用的组件隔离及Host回答；非正常菜单、独立程序或美术视觉验收，无保存／重建" }));
            }
            finally
            {
                renderer?.Clear(); if (canvas != null) UnityEngine.Object.DestroyImmediate(canvas);
                if (close && scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
