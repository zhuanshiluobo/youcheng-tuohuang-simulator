using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Presentation;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    // 本轮仅备好；待相关写者结束后串行Unity编译/执行，不冒正常界面验收。
    public sealed class UiBindingReadAndMeasurementR58Tests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Flags).SetValue(target, value);
        private static object Read(object target, string field) => target.GetType().GetField(field, Flags).GetValue(target);
        // EditMode有asmdef，不能静态引用预定义Assembly-CSharp；保留真实组件与原断言。
        private static Type UiType(string fullName) => Type.GetType(fullName + ", Assembly-CSharp", true);
        private static string MeasurementKey(Component host) => (string)host.GetType().GetProperty("MeasurementCacheKey").GetValue(host);
        private static void Call(Component host, string method, params object[] arguments) => host.GetType().GetMethod(method).Invoke(host, arguments);

        [Test]
        public void MeasurementKeyReusesUnchangedInputsAndClearsDocumentReference()
        {
            var root = new GameObject("R58只读文本键隔离", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            root.SetActive(false);
            try
            {
                var text = root.GetComponent<Text>();
                var host = root.AddComponent(UiType("YC.Presentation.GameplayTextView"));
                Set(host, "source", text);
                Set(host, "document", new GameplayTextDocument(Array.Empty<GameplayTextRun>(), "document", "measurement"));
                string first = MeasurementKey(host);
                Assert.That(ReferenceEquals(first, MeasurementKey(host)), Is.True);
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
                Assert.That(MeasurementKey(host), Is.Not.EqualTo(first));
                Set(host, "documentVersion", 9);
                string revised = MeasurementKey(host);
                Assert.That(ReferenceEquals(revised, MeasurementKey(host)), Is.True);
                Call(host, "ClearBinding");
                Assert.That(MeasurementKey(host), Is.Empty);
                Assert.That(Read(host, "measurementKeyIdentity"), Is.Null);
                Assert.That(Read(host, "measurementKey"), Is.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void RepeatedCityBindingSkipsOnlySameSnapshotAndExplicitRefreshInvalidates()
        {
            var root = new GameObject("R58城市绑定隔离"); root.SetActive(false);
            var ghost = new GameObject("隔离虚影", typeof(RectTransform)); ghost.transform.SetParent(root.transform);
            try
            {
                var panel = root.AddComponent(UiType("YC.Presentation.BuildInfoPanel"));
                var state = new GameState { GameId = "r58", ContentPackHash = "content" };
                Set(panel, "initialized", true); Set(panel, "currentState", state); Set(panel, "currentPlayerId", 1);
                Set(panel, "pendingBuildGhost", ghost.GetComponent<RectTransform>());
                var visible = new GameStateView { GameId = state.GameId, ContentPackHash = state.ContentPackHash,
                    ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 1, StateRevision = 4 };
                visible.Players.Add(new PlayerStateView { PlayerId = 1 });
                Call(panel, "RefreshViewedCity", visible, 1);
                ghost.SetActive(false);
                Call(panel, "RefreshViewedCity", visible, 1);
                Assert.That(ghost.activeSelf, Is.False, "同快照第二次不重画；这只是空宿主的调用隔离");
                visible.StateRevision++;
                Call(panel, "RefreshViewedCity", visible, 1);
                Assert.That(ghost.activeSelf, Is.True, "新版本仍重画");
                Call(panel, "Refresh", state, 1);
                Assert.That(Read(panel, "renderedCityView"), Is.Null, "显式状态刷新不缓存原地变化");
                ghost.SetActive(false);
                Call(panel, "RefreshViewedCity", visible, 1);
                Assert.That(ghost.activeSelf, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
