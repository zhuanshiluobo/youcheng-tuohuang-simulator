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
    public sealed class HudSupplyMarkersPlayModeTests
    {
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static Component Find(string name) => (Component)UnityEngine.Object.FindObjectOfType(T(name));
        private static object Field(object target, string name) => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static object Property(object target, string name) => target.GetType().GetProperty(name).GetValue(target);

        [UnityTest]
        public IEnumerator BothScenes_ShowSupplyIncludingZeroAndFollowPublicPlayerProjection()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return Load(scene);
                var modules = Find("GameplayMainModules");
                var local = (Component)Field(modules, "localSupplyMarker");
                var opponents = (Array)Field(modules, "opponentSupplyMarkers");
                var state = new GameState();
                var colors = new[] { PlayerColor.Red, PlayerColor.Blue, PlayerColor.Green, PlayerColor.Yellow };
                for (var i = 0; i < colors.Length; i++)
                    state.Players.Add(new PlayerState { PlayerId = i + 1, Color = colors[i], InfluenceSupply = i * 9,
                        Score = 50 + i, Resources = new ResourceSet { Originium = 3 + i, GoldVoucher = 7 + i } });
                // 供应堆、分数和资源故意各不相同；供给数不能来自任何其他数量。
                foreach (var player in state.Players)
                {
                    Render(modules, state, null, player.PlayerId);
                    CheckMarker(local, player.Color, player.InfluenceSupply);
                    var slot = 0;
                    foreach (var opponent in state.Players)
                        if (opponent.PlayerId != player.PlayerId)
                            CheckMarker((Component)opponents.GetValue(slot++), opponent.Color, opponent.InfluenceSupply);
                }

                var visible = GameStateViewProjector.Project(state, GameStateViewer.Player(1));
                foreach (var player in state.Players) player.InfluenceSupply = 99;
                Render(modules, state, visible, 1);
                CheckMarker(local, colors[0], 0);
                for (var i = 0; i < 3; i++) CheckMarker((Component)opponents.GetValue(i), colors[i + 1], (i + 1) * 9);
                visible.Players[1].InfluenceSupply = 0;
                visible.Players[1].Color = PlayerColor.Yellow;
                Render(modules, GameStateViewProjector.ToClientState(visible), visible, 1);
                CheckMarker((Component)opponents.GetValue(0), PlayerColor.Yellow, 0);
                visible.Players.RemoveAt(1);
                Render(modules, GameStateViewProjector.ToClientState(visible), visible, 1);
                CheckMarker((Component)opponents.GetValue(0), colors[2], 18);
                Assert.That(((Component)opponents.GetValue(2)).gameObject.activeInHierarchy, Is.False,
                    "离开的玩家不应留下供应堆方块。");
            }
        }

        [UnityTest]
        public IEnumerator ActualSceneSupplyMarkers_ParticipateInNativePlayerInformationLayoutsAcrossSizes()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return Load(scene);
                var controller = Find("MobileCityInteractionController");
                var state = (GameState)Property(controller, "CurrentState");
                var localId = (int)Field(controller, "localPlayerId");
                var modules = Find("GameplayMainModules");
                var colors = new[] { PlayerColor.Blue, PlayerColor.Red, PlayerColor.Green, PlayerColor.Yellow };
                state.Players.Clear();
                var playerCount = scene == "ThreePlayerScene" ? 3 : 4;
                for (var i = 0; i < playerCount; i++)
                    state.Players.Add(new PlayerState { PlayerId = i == 0 ? localId : localId + i,
                        Name = "玩家 " + (i + 1), Color = colors[i], InfluenceSupply = 29 - i * 7,
                        Resources = new ResourceSet { Originium = 101, OriginiumShard = 202, Iron = 303,
                            PureOriginium = 404, GoldVoucher = 505 } });
                Render(modules, state, GameStateViewProjector.Project(state, GameStateViewer.Player(localId)), localId);
                var markers = new Component[4];
                markers[0] = (Component)Field(modules, "localSupplyMarker");
                ((Array)Field(modules, "opponentSupplyMarkers")).CopyTo(markers, 1);
                var hands=(Text[])Field(modules,"opponentHandCount");var styles=(Text[])Field(modules,"opponentStyleCount");
                foreach (var size in new[] { new Vector2Int(1920,1080), new Vector2Int(1920,1200),
                    new Vector2Int(2560,1080),new Vector2Int(1280,720),new Vector2Int(900,600),new Vector2Int(1920,1080) })
                {
#if UNITY_EDITOR
                    SetSize(size);
#endif
                    for (var i = 0; i < 12; i++) yield return null;
                    Canvas.ForceUpdateCanvases();
                    foreach (var marker in markers)
                    {
                        if (!marker.gameObject.activeInHierarchy) continue;
                        var slot = (RectTransform)marker.transform.parent;
                        var row = (RectTransform)slot.parent;
                        Assert.That(row.GetComponent<HorizontalLayoutGroup>(), Is.Not.Null);
                        Assert.That(slot.GetComponent<LayoutElement>(), Is.Not.Null);
                        Contains(ScreenRect(slot), ScreenRect((RectTransform)marker.transform));
                        Contains(ScreenRect(row), ScreenRect(slot));
                        Rect? previous = null;
                        foreach (RectTransform metric in row)
                        {
                            if (!metric.gameObject.activeInHierarchy) continue;
                            var current = ScreenRect(metric);
                            Contains(ScreenRect(row), current);
                            if (previous.HasValue)
                                Assert.That(current.xMin, Is.GreaterThanOrEqualTo(previous.Value.xMax - .5f),
                                    "同一行的数据项不能重叠。");
                            previous = current;
                        }
                    }
                    Text firstNumber=null;
                    for(var i=0;i<3;i++)
                    {
                        var marker=markers[i+1];if(!marker.gameObject.activeInHierarchy)continue;
                        var status=(RectTransform)marker.transform.parent.parent;
                        Assert.That(status,Is.SameAs(hands[i].transform.parent.parent));
                        Assert.That(status,Is.SameAs(styles[i].transform.parent.parent));
                        Assert.That(marker.transform.parent.GetSiblingIndex(),Is.EqualTo(status.childCount-1));
                        var metrics=(RectTransform)status.parent;var body=(RectTransform)metrics.parent;
                        Contains(ScreenRect(body),ScreenRect((RectTransform)body.Find("Portrait Slot")));
                        Contains(ScreenRect(body),ScreenRect(metrics));Contains(ScreenRect(metrics),ScreenRect(status));
                        Contains(ScreenRect(metrics),ScreenRect((RectTransform)metrics.GetChild(0)));
                        var number=(Text)Property(marker,"NumberText");
                        Assert.That(number.resizeTextForBestFit,Is.False);
                        if(firstNumber!=null)
                        {
                            Assert.That(number.fontSize,Is.EqualTo(firstNumber.fontSize));
                            Assert.That(number.transform.lossyScale.y,Is.EqualTo(firstNumber.transform.lossyScale.y).Within(.001f));
                        }
                        firstNumber=number;
                    }
                    yield return Capture(scene + "-" + size.x + "x" + size.y);
                }
            }
#if UNITY_EDITOR
            SetSize(new Vector2Int(1920,1080));
#endif
        }

        private static void Render(Component modules, GameState state, GameStateView visible, int localId) =>
            modules.GetType().GetMethod("Render").Invoke(modules, new object[] { state, visible, localId, null });

        private static void CheckMarker(Component marker, PlayerColor color, int count)
        {
            Assert.That((bool)Property(marker, "ShowNumber"), Is.True);
            Assert.That(((Text)Property(marker, "NumberText")).text, Is.EqualTo(count.ToString()));
            var preset = Property(marker, "ColorPreset");
            Assert.That(preset.ToString(), Is.EqualTo(color.ToString()));
            var catalog = Property(marker, "Catalog");
            var entry = catalog.GetType().GetMethod("Find").Invoke(catalog, new[] { preset });
            Assert.That(((Image)Property(marker, "Face")).sprite,
                Is.SameAs(entry.GetType().GetField("numbered").GetValue(entry)));
        }

        private static IEnumerator Load(string scene)
        {
            var launch = Find("GameLaunchContext");
            if (launch != null) { UnityEngine.Object.Destroy(launch.gameObject); yield return null; }
            yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
#if UNITY_EDITOR
            SetSize(new Vector2Int(1920,1080));
#endif
            for (var i = 0; i < 8; i++) yield return null;
        }

#if UNITY_EDITOR
        private static void SetSize(Vector2Int size)
        {
            var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
            capture.GetMethod("PrepareGameView", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            capture.GetMethod("SelectSize", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { size });
        }
#endif

        private static Rect ScreenRect(RectTransform rect)
        {
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            var low = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
            var high = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
            return Rect.MinMaxRect(low.x, low.y, high.x, high.y);
        }

        private static void Contains(Rect outer, Rect inner)
        {
            Assert.That(inner.xMin, Is.GreaterThanOrEqualTo(outer.xMin - .5f));
            Assert.That(inner.yMin, Is.GreaterThanOrEqualTo(outer.yMin - .5f));
            Assert.That(inner.xMax, Is.LessThanOrEqualTo(outer.xMax + .5f));
            Assert.That(inner.yMax, Is.LessThanOrEqualTo(outer.yMax + .5f));
        }

        private static IEnumerator Capture(string name)
        {
            var configured=Environment.GetEnvironmentVariable("YC_HUD_SUPPLY_CAPTURE_OUTPUT");
            var directory = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath,"../",
                string.IsNullOrWhiteSpace(configured)?"Logs/HudSupply-20261002/visual":configured));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            for (var i = 0; i < 12; i++) yield return null;
            Assert.That(File.Exists(path), Is.True);
        }
    }
}
