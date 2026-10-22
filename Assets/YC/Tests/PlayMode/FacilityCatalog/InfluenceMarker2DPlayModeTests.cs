using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.Rules;
using YC.Domain.State;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YC.Tests.PlayMode
{
    public sealed class InfluenceMarker2DPlayModeTests
    {
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Property(object o, string name) => o.GetType().GetProperty(name).GetValue(o);
        private static object Field(object o, string name) => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);
        private static Component Find(string name) => UnityEngine.Object.FindObjectOfType(T(name)) as Component;
        private static object Call(object o, string name, params object[] args) => o.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args);

        [UnityTest]
        public IEnumerator ActualScenes_NativeStatusRowsShowNumberedMarkersAndClearZeroCounts()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                var launch = Find("GameLaunchContext");
                if (launch != null) { UnityEngine.Object.Destroy(launch.gameObject); yield return null; }
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null; yield return null;
                SetSize(1920, 1080);
                var controller = Find("MobileCityInteractionController");
                var state = (GameState)Property(Field(controller, "session"), "State");
                var localId = (int)Field(controller, "localPlayerId");
                state.PendingChoice = null; state.PendingCardSession = null;
                state.PendingCharacterEffect = null; state.PendingSpecialAction = null;
                state.EffectRuntime = new EffectRuntimeState();
                state.Phase = GamePhase.ActionRound1; state.Round = 1; state.CurrentPlayerId = localId;
                var id = CityStyleDatabase.MilitaryIndustrialArea;
                state.Decks.CityStyleSupply.Clear(); state.Decks.CityStyleSupply.Add(id);
                foreach (var player in state.Players)
                {
                    player.CityLocationId = "A-01";
                    player.DeclaredCityStyles.Clear();
                    for (var i = 0; i < player.PlayerId; i++)
                        player.DeclaredCityStyles.Add(new CityStyleDeclarationState
                        { CityStyleId = id, MarkerArea = "unused", InfluenceMarkerId = "二维标记测试-" + player.PlayerId + "-" + i });
                }
                Call(controller, "SynchronizeInteractionFromState");
                Call(Find("UiMainActionTabs"), "Select", 2);
                for (var i = 0; i < 8; i++) yield return null;
                Canvas.ForceUpdateCanvases();
                var row = UnityEngine.Object.FindObjectsOfType(T("CityStyleStatusRowView"))
                    .Cast<Component>().Single(component => (string)Property(component, "Id") == id);
                var squares = row.GetComponentsInChildren(T("InfluenceMarker2DView"), true).Cast<Component>().ToArray();
                var visible = squares.Where(s => ((Image)Property(s, "Face")).color.a > 0f).ToArray();
                Assert.That(visible.Length, Is.EqualTo(state.Players.Count));
                foreach (var square in visible)
                {
                    var label = (Text)Property(square, "NumberText");
                    Assert.That((bool)Property(square, "ShowNumber"), Is.True);
                    Assert.That(label.gameObject.activeInHierarchy, Is.True);
                    Assert.That(label.cachedTextGenerator.vertexCount, Is.GreaterThan(0), "数量必须实际生成字形");
                    Assert.That(label.color.a, Is.GreaterThan(0));
                    Assert.That(((Image)Property(square, "Face")).sprite.name, Does.StartWith("digit-base-"));
                }
                yield return Capture(scene + "-主界面二维数量方块");
                foreach (var player in state.Players) player.DeclaredCityStyles.Clear();
                Call(row, "Render", id, id, state);
                Assert.That(squares.All(s => ((Image)Property(s, "Face")).color.a == 0f), Is.True,
                    "清空声明后不能残留颜色方块");
                Assert.That(squares.All(s => ((Text)Property(s, "NumberText")).color.a == 0f), Is.True,
                    "清空声明后不能残留数字");
            }
        }

        [UnityTest]
        public IEnumerator SharedPrefabGallery_EveryColorRendersWithAndWithoutNumbers()
        {
#if UNITY_EDITOR
            SetSize(1920, 1080);
            for (var i = 0; i < 8; i++) yield return null;
            var catalog = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/YC/Presentation/Content/InfluenceMarker2DCatalog.asset");
            var entries = (Array)Property(catalog, "Entries");
            var plain = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/Pieces/InfluenceMarker2D.prefab");
            var numbered = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/Pieces/InfluenceMarker2DNumbered.prefab");
            var page = new GameObject("二维方块预制体验收画面", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(Image));
            var canvas = page.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 30000;
            var scaler = page.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            page.GetComponent<Image>().color = new Color(.14f, .17f, .16f);
            var font = AssetDatabase.LoadAssetAtPath<Font>(AssetDatabase.GUIDToAssetPath("43e9d69dcca63724db85852acd46004d"));
            var labels = new System.Collections.Generic.List<Text>();
            try
            {
                var index = 0;
                foreach (var entry in entries)
                {
                    var preset = entry.GetType().GetField("color").GetValue(entry);
                    var name = (string)entry.GetType().GetField("displayName").GetValue(entry);
                    var cell = new GameObject(name, typeof(RectTransform)); cell.transform.SetParent(page.transform, false);
                    var rect = (RectTransform)cell.transform;
                    rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1);
                    rect.anchoredPosition = new Vector2(60 + index % 6 * 300, -40 - index / 6 * 250);
                    rect.sizeDelta = new Vector2(260, 220);
                    for (var mode = 0; mode < 2; mode++)
                    {
                        var marker = UnityEngine.Object.Instantiate(mode == 0 ? plain : numbered, rect, false);
                        var markerRect = (RectTransform)marker.transform;
                        markerRect.anchorMin = markerRect.anchorMax = new Vector2(.5f, .5f);
                        markerRect.anchoredPosition = new Vector2(mode == 0 ? -60 : 60, 20);
                        markerRect.sizeDelta = new Vector2(88, 88);
                        var view = marker.GetComponent(T("InfluenceMarker2DView"));
                        Call(view, "SetAppearance", preset, mode == 1, 27, false);
                        if (mode == 1) labels.Add((Text)Property(view, "NumberText"));
                    }
                    var title = new GameObject("颜色名称", typeof(RectTransform), typeof(Text)); title.transform.SetParent(rect, false);
                    var titleRect = (RectTransform)title.transform;
                    titleRect.anchorMin = titleRect.anchorMax = new Vector2(.5f, .5f);
                    titleRect.anchoredPosition = new Vector2(0, -65); titleRect.sizeDelta = new Vector2(240, 45);
                    var text = title.GetComponent<Text>(); text.font = font; text.fontSize = 24;
                    text.alignment = TextAnchor.MiddleCenter; text.color = Color.white; text.text = name;
                    index++;
                }
                for (var i = 0; i < 8; i++) yield return null;
                Canvas.ForceUpdateCanvases();
                Assert.That(labels.All(t => t.cachedTextGenerator.vertexCount > 0), Is.True, "24 色数字版必须实际生成字形");
                yield return Capture("24色-无数字与数字版-预制体实绘");
            }
            finally { UnityEngine.Object.Destroy(page); }
#else
            Assert.Ignore("预制体资产验收需在编辑器中运行。");
            yield break;
#endif
        }

        private static void SetSize(int width, int height)
        {
#if UNITY_EDITOR
            var helper = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
            helper.GetMethod("PrepareGameView", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            helper.GetMethod("SelectSize", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { new Vector2Int(width, height) });
#endif
        }
        private static IEnumerator Capture(string name)
        {
            Canvas.ForceUpdateCanvases(); for (var i = 0; i < 8; i++) yield return null;
            var directory = Path.GetFullPath("Logs/InfluenceMarker2D-20261001/visual"); Directory.CreateDirectory(directory);
            ScreenCapture.CaptureScreenshot(Path.Combine(directory, name + ".png"));
            for (var i = 0; i < 12; i++) yield return null;
            Assert.That(File.Exists(Path.Combine(directory, name + ".png")), Is.True);
        }
    }
}
