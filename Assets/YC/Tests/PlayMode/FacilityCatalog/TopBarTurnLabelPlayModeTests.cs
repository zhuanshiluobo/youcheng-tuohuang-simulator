using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.PlayMode
{
    public sealed class TopBarTurnLabelPlayModeTests
    {
        [UnityTest]
        public IEnumerator SampleScene_CurrentPlayerLabelAndColorRestore()
        {
            var run = Flatten(VerifyScene("SampleScene"));
            while (run.MoveNext()) yield return run.Current;
        }

        [UnityTest]
        public IEnumerator ThreePlayerScene_CurrentPlayerLabelAndColorRestore()
        {
            var run = Flatten(VerifyScene("ThreePlayerScene"));
            while (run.MoveNext()) yield return run.Current;
        }

        private static bool completed;
        [SetUp] public void BeforeTest() { completed = false; }
        [TearDown] public void AfterTest() { Assert.That(completed, Is.True, "验收必须执行到末尾，不能仅加载场景后结束"); }

        private static IEnumerator Flatten(IEnumerator routine)
        {
            while (routine.MoveNext())
            {
                if (routine.Current is IEnumerator nested)
                {
                    var child = Flatten(nested);
                    while (child.MoveNext()) yield return child.Current;
                }
                else yield return routine.Current;
            }
        }

        private static IEnumerator VerifyScene(string sceneName)
        {
            var launchType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var launch = launchType.GetProperty("Instance").GetValue(null) as Component;
            if (launch != null)
            {
                UnityEngine.Object.Destroy(launch.gameObject);
                yield return null;
            }
#if UNITY_EDITOR
            var captureType = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
            captureType.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            captureType.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new Vector2Int(1920, 1080) });
#endif
            yield return SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            yield return null;
            yield return null;
            var frameType = Type.GetType("YC.Presentation.GameplayHudFrame, Assembly-CSharp", true);
            var frame = UnityEngine.Object.FindObjectOfType(frameType);
            Assert.That(frame, Is.Not.Null);
            var label = (Text)Field(frame, "turnTagText");
            var originalColor = (Color?)Field(frame, "turnTagDefaultColor") ?? label.color;
            var themeType = Type.GetType("YC.Presentation.UiTheme, Assembly-CSharp", true);
            var colorMethod = themeType.GetMethod("GetPlayerColor");
            var refresh = frameType.GetMethod("Refresh");
            var modelType = refresh.GetParameters()[1].ParameterType;
            var ctor = modelType.GetConstructors()[0];
            var parameters = ctor.GetParameters();
            var args = new object[parameters.Length];
            for (var i = 0; i < args.Length; i++)
            {
                var type = parameters[i].ParameterType;
                args[i] = type == typeof(string) ? string.Empty : Activator.CreateInstance(type);
                if (parameters[i].Name == "hasLocalPlayer") args[i] = true;
                if (parameters[i].Name == "localPlayerColor") args[i] = PlayerColor.Blue;
            }
            var model = ctor.Invoke(args);
            var state = new GameState { Phase = GamePhase.ActionRound1, Round = 1 };
            state.Players.Add(new PlayerState { PlayerId = 1, Name = "本机玩家", Color = PlayerColor.Blue });
            state.Players.Add(new PlayerState { PlayerId = 2, Name = "红色玩家", Color = PlayerColor.Red });
            state.Players.Add(new PlayerState { PlayerId = 3, Name = "绿色玩家", Color = PlayerColor.Green });
            state.Players.Add(new PlayerState { PlayerId = 4, Name = "黄色玩家", Color = PlayerColor.Yellow });
            // 验证实际场景的 HUD 实例；只替换传入的内存展示数据，不提交游戏命令或保存资产。
            foreach (var player in state.Players)
            {
                state.CurrentPlayerId = player.PlayerId;
                refresh.Invoke(frame, new[] { state, model, string.Empty });
                var expected = player.PlayerId == 1 ? (string)Field(frame, "localTurnText") :
                    string.Format((string)Field(frame, "otherPlayerTurnFormat"), player.Name);
                Assert.That(label.text, Is.EqualTo(expected));
                var expectedColor = player.PlayerId == 1 ? originalColor :
                    (Color)colorMethod.Invoke(null, new object[] { player.Color, 1f });
                Assert.That(label.color, Is.EqualTo(expectedColor));
                Canvas.ForceUpdateCanvases();
                yield return null;
                Assert.That(label.text, Is.EqualTo(expected), "下一帧不能被旧等待文案覆盖");
                yield return Capture(sceneName + "-" + player.Color, label);
            }
            // 切回本机后必须恢复原色。
            state.CurrentPlayerId = 1;
            refresh.Invoke(frame, new[] { state, model, string.Empty });
            Assert.That(label.text, Is.EqualTo(Field(frame, "localTurnText")));
            Assert.That(label.color, Is.EqualTo(originalColor));
            yield return Capture(sceneName + "-LocalRestored", label);
            // 本机改为红色后，蓝色行动玩家也必须使用玩家标记色。
            for (var i = 0; i < args.Length; i++)
                if (parameters[i].Name == "localPlayerColor") args[i] = PlayerColor.Red;
            refresh.Invoke(frame, new[] { state, ctor.Invoke(args), string.Empty });
            Assert.That(label.color, Is.EqualTo((Color)colorMethod.Invoke(null,
                new object[] { PlayerColor.Blue, 1f })));
            Assert.That(label.text, Is.EqualTo(string.Format((string)Field(frame, "otherPlayerTurnFormat"), "本机玩家")));
            yield return Capture(sceneName + "-BlueOpponent", label);
            state.CurrentPlayerId = 2;
            state.Players[1].Name = " ";
            refresh.Invoke(frame, new[] { state, model, string.Empty });
            Assert.That(label.text, Is.EqualTo(string.Format((string)Field(frame, "otherPlayerTurnFormat"),
                string.Format((string)Field(frame, "unnamedPlayerFormat"), 2))));
            state.CurrentPlayerId = -1;
            refresh.Invoke(frame, new[] { state, model, string.Empty });
            Assert.That(label.text, Is.EqualTo(Field(frame, "waitingTurnText")));
            Assert.That(label.color, Is.EqualTo(originalColor));
            Assert.That(label.fontStyle, Is.EqualTo(FontStyle.Normal), "应使用真实粗字体，不能合成加粗");
#if UNITY_EDITOR
            var fontPath = UnityEditor.AssetDatabase.GUIDToAssetPath("5c5a187d1eb86944c93f847ec521c324");
            Assert.That(label.font, Is.SameAs(UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(fontPath)));
#endif
            completed = true;
            Debug.Log("TOPBAR_ACCEPTED " + sceneName);
        }

        private static IEnumerator Capture(string name, Text label)
        {
            var folder = Path.GetFullPath("Logs/TopBarTurn-20260928");
            Directory.CreateDirectory(folder);
            Canvas.ForceUpdateCanvases();
            yield return null;
            var path = Path.Combine(folder, name + ".png");
            Assert.That(label.cachedTextGenerator.characterCountVisible, Is.EqualTo(label.text.Length),
                "当前行动提示不能截字：" + label.text);
            ScreenCapture.CaptureScreenshot(path);
            for (var i = 0; i < 15; i++) yield return null;
            Assert.That(File.Exists(path), Is.True, "必须生成实际画面证据");
            Debug.Log(name + ": " + label.text + "; color=" + label.color +
                "; screen=" + Screen.width + "x" + Screen.height + "; screenshot=" + File.Exists(path));
        }

        private static object Field(object target, string name) => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }
}
