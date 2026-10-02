using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.PlayMode
{
    public sealed class MapHighlightPlayModeTests
    {
        [UnityTest]
        public IEnumerator BothActualScenes_MoveSelectionSurvivesRefreshAndConfirmsOnce()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                var launch = Find("GameLaunchContext");
                if (launch != null) { UnityEngine.Object.Destroy(launch.gameObject); yield return null; }
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null; yield return null;
                SetSize(1920, 1080);
                for (var i = 0; i < 6; i++) yield return null;
                Call(Find("MapDisplayController"), "FitCameraToMap");
                var controller = Find("MobileCityInteractionController");
                var state = (GameState)Property(Field(controller, "session"), "State");
                var playerId = (int)Field(controller, "localPlayerId");
                var player = state.FindPlayer(playerId);
                ResetPending(state);
                state.Phase = GamePhase.ActionRound1;
                state.Round = 2;
                state.ActionRound = 1;
                state.CurrentPlayerId = playerId;
                player.CityLocationId = "A-01";
                player.Resources.OriginiumShard = 10;
                player.ActedMainActionThisTurn = false;
                player.CompletedMainActionsThisTurn = 0;
                Call(controller, "SynchronizeInteractionFromState");
                Call(controller, "BeginMoveAction");
                yield return null;
                Physics2D.SyncTransforms();
                var buttons = ((IEnumerable)Property(Find("MapView"), "Buttons")).Cast<Component>().ToArray();
                var candidates = buttons.Where(b => Property(b, "TargetKind").ToString() == "Location" &&
                    IsInteractive(b) && IsTopHit(b)).ToArray();
                Assert.That(candidates.Length, Is.GreaterThanOrEqualTo(2));
                var first = candidates[0];
                var second = candidates[1];
                var availableSprite = ((SpriteRenderer)Property(first, "Image")).sprite;
                var router = Field(controller, "interactionRouter");
                Click(first);
                Assert.That(IsSelected(first), Is.True, "点击入口返回后的统一刷新必须保留城市移动预选");
                Assert.That(((SpriteRenderer)Property(first, "Image")).sprite, Is.Not.SameAs(availableSprite));
                Assert.That(player.CityLocationId, Is.EqualTo("A-01"));
                Assert.That(player.Resources.OriginiumShard, Is.EqualTo(10));
                Assert.That(state.HasPendingChoice(), Is.False, "首击不能提交移动或抽事件卡");
                Click(first);
                Assert.That(state.HasPendingChoice(), Is.False, "同帧重复回调不能确认");
                yield return null;
                Call(controller, "RefreshRoutedMapPresentation");
                Call(controller, "RefreshActionPanel");
                Assert.That(IsSelected(first), Is.True, "跨帧和行动面板刷新必须保留预选");
                Assert.That(candidates.All(IsInteractive), Is.True, "预选不能隐藏其他候选");
                yield return Capture(scene + "-城市移动首次预选");
                Click(second);
                Assert.That(IsSelected(first), Is.False);
                Assert.That(IsSelected(second), Is.True);
                Assert.That(state.HasPendingChoice(), Is.False, "改选目标不能直接提交");
                Assert.That((bool)Call(controller, "TryHandleInteractionEscape"), Is.True);
                Assert.That(IsSelected(second), Is.False, "取消恢复可选");
                yield return null;
                Click(first);
                Assert.That(IsSelected(first), Is.True);
                Call(controller, "SynchronizeInteractionFromState");
                Assert.That(IsSelected(first), Is.False, "权威同步清除临时预选");
                Call(controller, "BeginMoveAction");
                yield return null;
                Click(second);
                Assert.That(IsSelected(second), Is.True, "重新开始仍需首次预选");
                yield return Capture(scene + "-城市移动改选后");
                Click(second);
                Assert.That(state.HasPendingChoice() || player.CityLocationId == (string)Property(second, "TargetId"),
                    Is.True, "第二次独立点击应进入正式移动结算");
                Assert.That((bool)Property(router, "HasPendingMapConfirmation"), Is.False);
                Assert.That(candidates.Any(IsInteractive), Is.False, "提交后不能继续点击旧移动候选");
            }
        }

        [UnityTest]
        public IEnumerator BothActualScenes_CollectionAndIndependentConfirmation_ProduceEvidence()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                var launch = Find("GameLaunchContext");
                if (launch != null) { UnityEngine.Object.Destroy(launch.gameObject); yield return null; }
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null; yield return null;
                SetSize(1920, 1080);
                for (var i = 0; i < 6; i++) yield return null;
                Call(Find("MapDisplayController"), "FitCameraToMap");
                yield return null;
                var controller = Find("MobileCityInteractionController");
                var state = (GameState)Property(Field(controller, "session"), "State");
                var playerId = (int)Field(controller, "localPlayerId");
                var player = state.FindPlayer(playerId);
                ResetPending(state);
                state.Phase = GamePhase.ResourceCollection;
                state.Round = 2;
                state.CurrentPlayerId = playerId;
                player.CityLocationId = "A-01";
                player.Resources.GoldVoucher = 4;
                player.ResourceCollectionStartGoldVoucher = 4;
                player.HasCollectedResourcesThisRound = false;
                state.Map.ResourceTokens.Clear();
                state.Map.Influences.Clear();
                foreach (var id in new[] { "A-01", "A-02", "B-02" })
                    state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = id, ResourceType = ResourceType.Iron, Amount = 1 });
                foreach (var id in new[] { "A-02", "B-02" })
                    state.Map.Influences.Add(new InfluencePlacement { InfluenceId = "fixture-" + id, PlayerId = playerId,
                        SlotId = InfluenceService.GetLocationSlotId(id, 0), LocationId = id });
                Call(controller, "SynchronizeInteractionFromState");
                yield return null;
                var view = Find("MapView");
                var buttons = ((IEnumerable)Property(view, "Buttons")).Cast<Component>().ToArray();
                Component Button(string id) => buttons.Single(b => (string)Property(b, "TargetId") == id);
                var collection = Field(controller, "resourceCollectionPresenter");
                var dialog = Field(controller, "eventChoiceDialog");
                Assert.That(IsInteractive(Button("A-02")), Is.False);
                Assert.That(((SpriteRenderer)Property(Button("A-02"), "Image")).enabled, Is.True);
                Assert.That((bool)Property(collection, "AllLocationsCovered"), Is.False);
                var selected = ((IEnumerable)Property(collection, "SelectedLocationIds")).Cast<string>().ToArray();
                Click(Button("A-02"));
                Assert.That(((IEnumerable)Property(collection, "SelectedLocationIds")).Cast<string>(), Is.EquivalentTo(selected));
                Assert.That((bool)Property(dialog, "IsShowing"), Is.False);
                yield return Capture(scene + "-采集候选-1920x1080");

                Click(Button("A1"), true);
                Assert.That((bool)Property(dialog, "IsShowing"), Is.False, "拖拽结束不能当作点击");

                Click(Button("A1"));
                Assert.That((bool)Property(dialog, "IsShowing"), Is.True, "航道首次点击必须打开支付窗口");
                Assert.That(((IEnumerable)Property(collection, "PaidRouteIds")).Cast<string>(), Is.Empty);
                var paymentView = Field(dialog, "view");
                yield return null;
                var bankButton = (Button)Property(paymentView, "ResourcePaymentBankButton");
                var bankScreen = RectTransformUtility.WorldToScreenPoint(null, bankButton.transform.position);
                var pointerClassifier = Type.GetType("YC.Presentation.TabletopPointerClassifier, Assembly-CSharp", true);
                Assert.That(pointerClassifier.GetMethod("CanRouteMapPointer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { bankScreen }), Is.False, "支付窗口 UI 不穿透地图");
                Click(Button("A2"));
                Assert.That(((IEnumerable)Property(collection, "PaidRouteIds")).Cast<string>(), Is.Empty);
                ((Button)Property(paymentView, "CloseButton")).onClick.Invoke();
                Assert.That(IsSelected(Button("A1")), Is.False);
                yield return null;
                Click(Button("A1"));
                ((Button)Property(Field(dialog, "view"), "ResourcePaymentBankButton")).onClick.Invoke();
                Assert.That(IsSelected(Button("A1")), Is.True);
                Assert.That(IsInteractive(Button("A1")), Is.True);
                Assert.That(IsSelected(Button("A-02")), Is.True, "连通后自动纳入");
                Assert.That(player.Resources.GoldVoucher, Is.EqualTo(4), "草稿不提前扣款");
                yield return null;
                Physics2D.SyncTransforms();
                var doubleRoute = buttons.First(b => Property(b, "TargetKind").ToString() == "Route" &&
                    (string)Field(b, "shapeId") == "route-double" && IsInteractive(b) && IsTopHit(b));
                foreach (var suffix in new[] { ":0", ":1" })
                    Assert.That(TopHit(Button("route:" + Property(doubleRoute, "TargetId") + suffix).transform.position),
                        Is.EqualTo(doubleRoute.gameObject), "双槽共用整个航道命中入口");
                Click(Button("A-02"));
                Assert.That(IsSelected(Button("A-02")), Is.False);
                Assert.That(IsInteractive(Button("A-02")), Is.True);
                Call(controller, "SynchronizeInteractionFromState");
                Assert.That(IsSelected(Button("A-02")), Is.False, "权威刷新保留取消记录");
                Assert.That(IsSelected(Button("A1")), Is.True, "权威刷新保留已付航道");
                yield return null;
                Click(Button("A-02"));
                Assert.That(IsSelected(Button("A1")), Is.True, "重新加入资源点保留已付航道");
                yield return null;
                Click(Button("A1"));
                Assert.That((bool)Property(dialog, "IsShowing"), Is.True, "已付航道可重新打开");
                Assert.That(IsSelected(Button("A1")), Is.True, "打开编辑窗口保留航道高亮");
                Assert.That(EventSystem.current.currentSelectedGameObject,
                    Is.EqualTo(((Button)Property(Field(dialog, "view"), "ResourcePaymentBankButton")).gameObject), "回显银行支付对象");
                ((Button)Property(Field(dialog, "view"), "CloseButton")).onClick.Invoke();
                Assert.That(IsSelected(Button("A1")), Is.True, "编辑取消保留原支付");
                yield return null;
                Click(Button("B1"));
                Assert.That((bool)Property(dialog, "IsShowing"), Is.True);
                ((Button)Property(Field(dialog, "view"), "ResourcePaymentBankButton")).onClick.Invoke();
                Assert.That((bool)Property(collection, "AllLocationsCovered"), Is.True);
                var hud = Find("GameplayInteractionHudView");
                var action = Property(hud, "ActionPanelView");
                var completionText = (string)Property(action, "AllCollectionLocationsSelectedText");
                Assert.That(completionText, Is.Not.Empty);
                Assert.That(((Text)Property(action, "StatusText")).text, Does.Contain(completionText));
                Assert.That(((Text)Field(Property(hud, "Frame"), "summaryText")).text, Does.Contain(completionText), "实际下方结算区必须显示");
                yield return Capture(scene + "-采集已选与全覆盖-1920x1080");

                var navigation = Find("MapDisplayController");
                SetField(navigation, "targetZoom", 1.4f);
                for (var i = 0; i < 25; i++) yield return null;
                SetSize(1440, 1080);
                yield return null; yield return null;
                var mapRenderer = (SpriteRenderer)Property(view, "MapRenderer");
                var original = mapRenderer.transform.position;
                mapRenderer.transform.position += new Vector3(.6f, .3f, 0);
                yield return null;
                foreach (var b in buttons)
                    Assert.That(mapRenderer.transform.InverseTransformPoint(b.transform.position).z, Is.EqualTo(0).Within(.0001f));
                yield return Capture(scene + "-放大平移与4比3窗口");
                mapRenderer.transform.position = original;
                SetSize(1920, 1080);
                Call(navigation, "FitCameraToMap");
                yield return null; yield return null;

                ResetPending(state);
                state.Phase = GamePhase.ActionRound1;
                state.ActionRound = 1;
                player.ActedMainActionThisTurn = false;
                player.CompletedMainActionsThisTurn = 0;
                player.InfluenceSupply = 10;
                Call(controller, "SynchronizeInteractionFromState");
                Call(controller, "BeginDeployAction");
                yield return null;
                var candidates = buttons.Where(b => Property(b, "TargetKind").ToString() == "InfluenceSlot" && IsInteractive(b)).ToArray();
                Assert.That(candidates.Length, Is.GreaterThan(0), "真实部署流程应启用槽位按钮");
                var candidate = candidates.First(b => IsTopHit(b));
                var influenceCount = state.Map.Influences.Count;
                Click(candidate);
                Assert.That(state.Map.Influences.Count, Is.EqualTo(influenceCount), "首击不落子");
                Assert.That(IsSelected(candidate), Is.True);
                Click(candidate);
                Assert.That(state.Map.Influences.Count, Is.EqualTo(influenceCount), "同帧重复回调不确认");
                yield return Capture(scene + "-部署临时预选-1920x1080");
                Click(candidate);
                yield return null;
                Assert.That(state.Map.Influences.Count, Is.EqualTo(influenceCount + 1), "第二次独立点击只落一子");
                yield return Capture(scene + "-部署确认后");
            }
        }

        private static void ResetPending(GameState state)
        {
            state.PendingChoice = null; state.PendingCardSession = null;
            state.PendingCharacterEffect = null; state.PendingSpecialAction = null;
            state.EffectRuntime = new EffectRuntimeState();
        }
        private static bool IsInteractive(Component b) => (bool)Property(b, "IsInteractive");
        private static bool IsSelected(Component b) => (bool)Property(b, "IsSelected");
        private static bool IsTopHit(Component button)
        {
            return TopHit(button.transform.position) == button.gameObject;
        }
        private static GameObject TopHit(Vector3 world)
        {
            var point = (Vector2)Camera.main.WorldToScreenPoint(world);
            var hits = new System.Collections.Generic.List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            return hits.Count > 0 ? hits[0].gameObject : null;
        }
        private static void Click(Component button, bool dragging = false)
        {
            var point = (Vector2)Camera.main.WorldToScreenPoint(button.transform.position);
            var pointer = new PointerEventData(EventSystem.current) { position = point, pressPosition = point, button = PointerEventData.InputButton.Left, dragging = dragging };
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
        }
        private static IEnumerator Capture(string name)
        {
            Canvas.ForceUpdateCanvases(); yield return null;
            var path = Path.GetFullPath("prompt/地图按钮与选择交互/实施记录/场景截图");
            Directory.CreateDirectory(path);
            var file = Path.Combine(path, name + ".png");
            ScreenCapture.CaptureScreenshot(file);
            for (var i = 0; i < 12; i++) yield return null;
            Assert.That(File.Exists(file), Is.True, "实际 GameView 截图必须落盘");
        }
        private static void SetSize(int width, int height)
        {
#if UNITY_EDITOR
            var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
            capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new Vector2Int(width, height) });
#else
            Screen.SetResolution(width, height, false);
#endif
        }
        private static Component Find(string name) => UnityEngine.Object.FindObjectOfType(Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true)) as Component;
        private static object Property(object o, string name) => o.GetType().GetProperty(name).GetValue(o);
        private static object Field(object o, string name) => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);
        private static void SetField(object o, string name, object value) => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(o, value);
        private static object Call(object o, string name, params object[] args) => o.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args);
    }
}
