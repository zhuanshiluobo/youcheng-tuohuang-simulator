using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class GameplayFeedbackTests
    {
        private GameObject hud;
        private Component frame;
        private Component registry;
        private static Type Runtime(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Call(object target, string method, params object[] args) =>
            target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(target, args);
        private static T Get<T>(object target, string name) => (T)target.GetType().GetProperty(name).GetValue(target);
        private static T Field<T>(object target, string name) => (T)target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        [SetUp] public void SetUp()
        {
            hud = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            frame = hud.GetComponentInChildren(Runtime("GameplayHudFrame"), true);
            registry = hud.GetComponentInChildren(Runtime("GameplayDialogRegistry"), true);
            if ((Component)Runtime("GameplayHudFrame").GetProperty("Active").GetValue(null) != frame) Call(frame, "Awake");
        }

        [TearDown] public void TearDown() => UnityEngine.Object.DestroyImmediate(hud);

        [Test] public void Feedback_UsesExistingSummary_AndExpiresBackToPendingSummary()
        {
            var longText = Field<Text>(frame, "summaryText");
            var shortText = Field<Text>(frame, "shortSummaryText");
            var nodeCount = hud.GetComponentsInChildren<Transform>(true).Length;
            Call(frame, "SetInteractionMessage", "支付失败", "请选择地图目标");
            Assert.That(longText.text, Does.Contain("支付失败").And.Contain("请选择地图目标"));
            Assert.That(shortText.text, Is.EqualTo(longText.text));
            Call(frame, "Refresh", null, null, null);
            Assert.That(Get<string>(frame, "InteractionMessage"), Does.Contain("支付失败"));
            frame.GetType().GetField("interactionMessageExpiresAt", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(frame, -1f);
            Call(frame, "UpdateInteractionMessage", true);
            Assert.That(longText.text, Does.Contain("支付失败"), "地图确认期间保留反馈。");
            frame.GetType().GetField("interactionMessageExpiresAt", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(frame, -1f);
            Call(frame, "UpdateInteractionMessage", false);
            Assert.That(Get<string>(frame, "InteractionMessage"), Is.Empty);
            Assert.That(longText.text, Does.Not.Contain("支付失败"));
            Assert.That(hud.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(nodeCount));
        }

        [Test] public void EventMapSelection_UsesHudSuspension_AndRestoresWithoutSubmitting()
        {
            Call(frame, "SetRequest", "event-map-selection", 1);
            var parent = Get<RectTransform>(frame, "ContentRect");
            var dialog = Activator.CreateInstance(Runtime("EventChoiceDialog"), registry, new Func<RectTransform>(() => parent));
            var submitted = 0;
            var card = new EventCardDefinition { CardId = "feedback-test", Name = "事件测试",
                ChoiceDescriptions = { "执行选项" }, ChoiceRewards = { new ResourceSet() } };
            Call(dialog, "ShowEventCardOptions", card, "", null, null, null,
                new Action<int>(_ => submitted++), null);
            var view = parent.GetComponentInChildren(Runtime("EventChoiceDialogView"), true);
            Assert.That(view.gameObject.activeSelf, Is.True);
            Call(dialog, "SuspendForMapInteraction");
            Assert.That(view.gameObject.activeSelf, Is.False, "整个效果页退出命中，地图可以接收点击。");
            Assert.That(Runtime("GameplayHudFrame").GetProperty("EffectInputSuspended").GetValue(null),
                Is.False, "为地图选点让出空间时必须保留效果输入。");
            Assert.That(submitted, Is.Zero);
            Call(frame, "SuspendEffectForInformation");
            Assert.That(Runtime("GameplayHudFrame").GetProperty("EffectInputSuspended").GetValue(null),
                Is.True, "转为查看资料时暂停地图提交。");
            Call(dialog, "SuspendForMapInteraction");
            Get<Button>(frame, "FoldButton").onClick.Invoke();
            Assert.That(view.gameObject.activeSelf, Is.True);
            Assert.That(submitted, Is.Zero);
            Call(dialog, "SuspendForMapInteraction");
            Call(frame, "SetRequest", "different-request", 2);
            Get<Button>(frame, "FoldButton").onClick.Invoke();
            Assert.That(view.gameObject.activeSelf, Is.False, "不能恢复上一请求的窗口。");
            Call(dialog, "Hide");
        }

        [TestCase("SampleScene")] [TestCase("ThreePlayerScene")]
        public void ActualScene_UsesCleanSharedHudAndValidDialogReferences(string name)
        {
            var path = "Assets/Scenes/" + name + ".unity";
            var scene = SceneManager.GetSceneByPath(path);
            var wasLoaded = scene.IsValid() && scene.isLoaded;
            if (!wasLoaded) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                var sceneHud = scene.GetRootGameObjects().SelectMany(root =>
                    root.GetComponentsInChildren(Runtime("GameplayInteractionHudView"), true)).Single();
                var args = new object[] { null };
                Assert.That(Call(sceneHud, "TryValidateConfiguration", args), Is.True, args[0] as string);
                var source = Get<Component>(sceneHud, "DialogRegistry");
                foreach (var property in new[] { "EffectDialogShellPrefab", "EventChoiceDialogPrefab", "CardPickerPrefab", "CardViewerPrefab" })
                {
                    var page = Get<Component>(source, property);
                    Assert.That(Call(page, "TryValidateConfiguration", args), Is.True, property + ": " + args[0]);
                    foreach (var node in page.GetComponentsInChildren<Transform>(true))
                        Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(node.gameObject), Is.Zero);
                }
                foreach (var node in sceneHud.GetComponentsInChildren<Transform>(true))
                {
                    Assert.That(node.name, Is.Not.EqualTo("Prompt Bounds"));
                    Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(node.gameObject), Is.Zero);
                }
            }
            finally { if (!wasLoaded) EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
