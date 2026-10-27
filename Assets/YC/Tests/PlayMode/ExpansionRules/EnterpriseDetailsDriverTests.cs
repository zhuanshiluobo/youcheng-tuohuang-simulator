using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Presentation.Workflows;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 隔离检查：实例化原企业页与原指针事件，不启动正常局，不代表外部鼠标或正常U02验收。
    public sealed class EnterpriseDetailsDriverTests
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private GameObject pageRoot, hudRoot, eventHost, blocker;
        private Component page;
        private EnterpriseSelectionProjection expected;
        private int submissions;
        private readonly List<object> trace = new List<object>();
        private static Type PageType => Type.GetType("YC.Presentation.EnterpriseSelectionDialogView, Assembly-CSharp", true);
        private static Type DriverType => Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
        private static object Read(object target, string name) => target.GetType().GetField(name, Members)?.GetValue(target) ??
            target.GetType().GetProperty(name, Members)?.GetValue(target);
        private object Call(string name, params object[] args) => PageType.GetMethods(Members)
            .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(page, args);
        private EnterpriseSelectionDraft Draft => (EnterpriseSelectionDraft)Read(page, "Draft");
        private GameObject Details => (GameObject)Read(page, "detailPage");
        private GameObject Surface => (GameObject)Read(page, "detailsReturnSurface");
        private static Vector2 Point(GameObject target) => (Vector2)DriverType.GetMethod("UiPointerPoint", Members).Invoke(null, new object[] { target });
        private static GameObject First(Vector2 point) => (GameObject)DriverType.GetMethod("FirstUiClickTarget", Members).Invoke(null, new object[] { point });
        private void Return(Func<bool> current) => DriverType.GetMethod("ClickEnterpriseDetailsReturnSurface", Members)
            .Invoke(null, new object[] { page, expected, current, new Action<object>(trace.Add) });

        [UnitySetUp]
        public IEnumerator OpenOriginalEnterpriseDetails()
        {
            trace.Clear(); submissions = 0;
            if (EventSystem.current == null) eventHost = new GameObject("企业详情驱动隔离事件系统", typeof(EventSystem), typeof(StandaloneInputModule));
            var themeType = Type.GetType("YC.Presentation.UiThemeCatalog, Assembly-CSharp", true);
            var theme = UnityEditor.AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", themeType);
            Type.GetType("YC.Presentation.UiTheme, Assembly-CSharp", true).GetMethod("Initialize").Invoke(null, new[] { theme });
            hudRoot = UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            yield return null;
            var registry = hudRoot.GetComponentInChildren(Type.GetType("YC.Presentation.GameplayDialogRegistry, Assembly-CSharp", true), true);
            var hud = hudRoot.GetComponent(Type.GetType("YC.Presentation.GameplayInteractionHudView, Assembly-CSharp", true));
            var parent = (RectTransform)Read(Read(hud, "Frame"), "ContentRect");
            page = (Component)registry.GetType().GetMethod("InstantiateEnterpriseSelection").Invoke(registry, new object[] { parent, false });
            pageRoot = page.gameObject;
            expected = new EnterpriseSelectionProjection {
                GameId = "isolated-driver-return", RequestId = "original-preview", OwnerEffectId = "preview-owner",
                CandidateSetId = "original-candidates", CandidateSetVersion = 1, RequestContract = "isolated-read-only-contract",
                Revision = 12, ViewerPlayerId = 1, Mode = EnterpriseSelectionMode.Company, ReadOnly = true,
                MinSelections = 0, MaxSelections = 0, SubmissionAvailable = false,
                Boards = new[] { new EnterpriseBoardProjection { Id = "original-board", VisualKey = "enterprise-2", Label = "隔离企业板" } },
                Targets = new[] { new EnterpriseSelectionTarget { Id = "original-board", BoardId = "original-board", CompanyId = "original-board", Available = false } }
            };
            Call("BindSubmission", new Func<EnterpriseSelectionProjection>(() => expected),
                new Action<EnterpriseSelectionIntent>(_ => submissions++), new Action(() => submissions++));
            Call("Present", expected, false);
            yield return null;
            Canvas.ForceUpdateCanvases();
            var targetType = Type.GetType("YC.Presentation.EnterpriseTargetView, Assembly-CSharp", true);
            var target = pageRoot.GetComponentsInChildren(targetType).Cast<Component>().First(value => value.gameObject.activeInHierarchy);
            var button = (Button)Read(target, "button");
            var point = Point(button.gameObject);
            Assert.That(First(point), Is.EqualTo(button.gameObject), "隔离开详情也必须命中原企业板");
            var pointer = new PointerEventData(EventSystem.current) { position = point, button = PointerEventData.InputButton.Right };
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
            yield return null;
            Canvas.ForceUpdateCanvases();
            Assert.That(Details.activeInHierarchy, Is.True, "原企业板事件必须先打开详情");
            expected = Draft.Projection;
        }

        [UnityTest]
        public IEnumerator OriginalSurfaceRoutesToPageHandlerAndReturnsWithoutSubmitting()
        {
            Assert.That(First(Point(Surface)), Is.EqualTo(pageRoot));
            var back = (Button)Read(page, "back");
            Assert.That(First(Point(back.gameObject)), Is.EqualTo(pageRoot), "复现a013：可见返回按钮上方的处理器是原返回面根对象");
            string before = JsonConvert.SerializeObject(Draft.Projection);
            Return(() => true);
            Assert.That(Details.activeSelf, Is.False);
            Assert.That(Surface.activeSelf, Is.False);
            Assert.That(((RectTransform)Read(page, "Body")).gameObject.activeInHierarchy, Is.True);
            Assert.That(JsonConvert.SerializeObject(Draft.Projection), Is.EqualTo(before));
            Assert.That(Draft.SelectedIds, Is.Empty);
            Assert.That(submissions, Is.Zero);
            var callbacks = trace.Select(value => JsonConvert.SerializeObject(value)).ToArray();
            Assert.That(callbacks.Count(line => line.Contains("\"Callback\":\"PointerClick\"") && line.Contains("\"Handled\":true")), Is.EqualTo(1));
            TestContext.WriteLine(JsonConvert.SerializeObject(new { Isolated = true, NormalAcceptance = false,
                DriverAssemblyMvid = DriverType.Assembly.ManifestModule.ModuleVersionId, Trace = trace }, Formatting.Indented));
            yield return null;
        }

        [UnityTest]
        public IEnumerator OcclusionStaleContextAndReboundRequestDoNotDispatch()
        {
            blocker = new GameObject("隔离遮挡", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(Image), typeof(Button));
            var canvas = blocker.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true; canvas.sortingOrder = pageRoot.GetComponentInParent<Canvas>().sortingOrder + 1;
            yield return null;
            Canvas.ForceUpdateCanvases();
            Assert.That(First(Point(Surface)), Is.EqualTo(blocker));
            AssertRejected(() => true);
            UnityEngine.Object.Destroy(blocker); blocker = null;
            yield return null;
            Canvas.ForceUpdateCanvases();
            AssertRejected(() => false);
            var next = JsonConvert.DeserializeObject<EnterpriseSelectionProjection>(JsonConvert.SerializeObject(expected));
            next.RequestId = "rebound-preview";
            Draft.Refresh(next);
            AssertRejected(() => true);
            Assert.That(submissions, Is.Zero);
            yield return null;
        }

        private void AssertRejected(Func<bool> current)
        {
            int before = trace.Count;
            var error = Assert.Throws<TargetInvocationException>(() => Return(current));
            Assert.That(error.InnerException, Is.InstanceOf<InvalidOperationException>());
            Assert.That(Details.activeInHierarchy, Is.True);
            Assert.That(trace.Count, Is.EqualTo(before), "拒绝时不能发出任何指针事件");
        }

        [UnityTearDown]
        public IEnumerator ReleaseInstances()
        {
            if (blocker != null) UnityEngine.Object.Destroy(blocker);
            if (hudRoot != null) UnityEngine.Object.Destroy(hudRoot);
            else if (pageRoot != null) UnityEngine.Object.Destroy(pageRoot);
            if (eventHost != null) UnityEngine.Object.Destroy(eventHost);
            yield return null;
        }
    }
}
