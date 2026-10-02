using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Cards;

namespace YC.Tests.PlayMode
{
    public sealed class ButtonArtworkScopePlayModeTests
    {
        [UnityTest]
        public IEnumerator BothScenes_EventArtworkOptionsStayTransparentAndClickableAcrossFrames()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
                var context = contextType.GetProperty("Instance").GetValue(null) as Component;
                if (context != null) { UnityEngine.Object.Destroy(context.gameObject); yield return null; }
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
#if UNITY_EDITOR
                var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
                capture.GetMethod("PrepareGameView", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
                capture.GetMethod("SelectSize", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { new Vector2Int(1920,1080) });
#endif
                for (var i = 0; i < 8; i++) yield return null;
                var registry = UnityEngine.Object.FindObjectOfType(
                    Type.GetType("YC.Presentation.GameplayDialogRegistry, Assembly-CSharp", true)) as Component;
                var canvasObject = new GameObject("Event Artwork Test Canvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
                var canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var dialogType = Type.GetType("YC.Presentation.EventChoiceDialog, Assembly-CSharp", true);
                var parent = (RectTransform)canvasObject.transform;
                var dialog = Activator.CreateInstance(dialogType, new object[] { registry, new Func<RectTransform>(() => parent) });
                try
                {
                    foreach (var cardId in new[] { "event_red_01", "event_green_01", "event_yellow_01" })
                    {
                        var card = EventCardDatabase.Get(cardId);
                        var calls = 0; var selected = -1;
                        dialogType.GetMethod("ShowEventCardOptions").Invoke(dialog, new object[] {
                            card, "资源点：测试", null, null, null,
                            new Action<int>(index => { calls++; selected = index; }), null });
                        for (var i = 0; i < 8; i++) yield return null;
                        var view = Field(dialog, "view") as Component;
                        var artwork = (RawImage)view.GetType().GetProperty("EventCardArtworkImage").GetValue(view);
                        Assert.That(artwork.texture, Is.Not.Null);
                        var host = (RectTransform)view.GetType().GetProperty("ArtworkChoiceHost").GetValue(view);
                        Assert.That(host.childCount, Is.EqualTo(card.ChoiceRewards.Count));
                        var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
                        foreach (Transform child in host)
                        {
                            var button = child.GetComponent<Button>();
                            var hit = child.GetComponent<Image>();
                            Assert.That(button, Is.Not.Null);
                            Assert.That(child.GetComponent(Type.GetType("YC.Presentation.UiMainButtonState, Assembly-CSharp", true)), Is.Null,
                                "卡牌热点不能附加通用按钮换图组件");
                            Assert.That(hit.color.a, Is.Zero, "静止多帧后仍不能遮住事件牌选项");
                            Assert.That(hit.sprite, Is.Null);
                            Assert.That(button.targetGraphic, Is.SameAs(hit));
                            pointer.position = RectTransformUtility.WorldToScreenPoint(null, child.position);
                            var hits = new List<RaycastResult>();
                            EventSystem.current.RaycastAll(pointer, hits);
                            Assert.That(hits.Count, Is.GreaterThan(0));
                            Assert.That(ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject), Is.SameAs(button.gameObject),
                                "实际选项位置应命中选项按钮");
                            ExecuteEvents.Execute(child.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
                            ExecuteEvents.Execute(child.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                            for (var i = 0; i < 6; i++) yield return null;
                            Assert.That(hit.color.a, Is.Zero, "悬停和按下反馈不能画出不透明底板");
                            ExecuteEvents.Execute(child.gameObject, pointer, ExecuteEvents.pointerUpHandler);
                            ExecuteEvents.Execute(child.gameObject, pointer, ExecuteEvents.pointerExitHandler);
                        }
                        yield return Capture(scene + "-" + cardId + "-options");
                        host.GetChild(0).GetComponent<Button>().onClick.Invoke();
                        Assert.That(calls, Is.EqualTo(1));
                        Assert.That(selected, Is.InRange(0, card.ChoiceRewards.Count - 1));
                        var hide = dialogType.GetMethod("Hide");
                        hide.Invoke(dialog, null);
                        yield return null;
                    }
                }
                finally { dialogType.GetMethod("Hide").Invoke(dialog, null); UnityEngine.Object.Destroy(canvasObject); }
            }
        }

        private static IEnumerator Capture(string name)
        {
            var directory = Environment.GetEnvironmentVariable("YC_HUD_SUPPLY_CAPTURE_OUTPUT");
            if (string.IsNullOrWhiteSpace(directory)) yield break;
            var path = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "../", directory, name + ".png"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            ScreenCapture.CaptureScreenshot(path);
            for (var i = 0; i < 12; i++) yield return null;
            Assert.That(File.Exists(path), Is.True);
        }
        private static object Field(object owner, string name) => owner.GetType().GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance).GetValue(owner);
    }
}
