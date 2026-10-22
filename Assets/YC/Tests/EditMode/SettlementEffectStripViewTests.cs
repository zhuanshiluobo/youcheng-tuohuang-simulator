using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Presentation;
using UnityEngine;
using YC.Domain.State;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Presentation.Workflows;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace YC.Tests.EditMode
{
    public sealed class SettlementEffectStripViewTests
    {
        private object binding;
        [SetUp] public void SetUp()
        { binding = New("SettlementEffectStripBinding"); Call(binding, "SetScope", "game-1/session-1", 1); }

        [Test] public void SameIdentityRefreshKeepsPositionAndPending()
        {
            Bind(Option("b", 2), Option("a", 1));
            var a = Entry("a"); var token = Scope;
            Assert.That(BeginUse(a, out _), Is.True);
            Bind(Option("a", 9, 2), Option("b", 0, 2), Option("c", 3, 2));
            Assert.That(Ids(), Is.EqualTo(new[] { "a", "b", "c" }));
            Assert.That(Entry("a"), Is.SameAs(a));
            Assert.That(Property<bool>(a, "Pending"), Is.True);
            Assert.That(Scope, Is.EqualTo(token));
            Assert.That(BeginUse(a, out _), Is.False);
        }

        [Test] public void InitialRegistrationTiesKeepProjectionOrder()
        { Bind(Option("b", 1), Option("a", 1)); Assert.That(Ids(), Is.EqualTo(new[] { "b", "a" })); }

        [Test] public void DisabledSourceStillInspectsWithoutBeginningUse()
        {
            var option = Option("a"); option.IsEnabled = false; Bind(option);
            var entry = Entry("a");
            Assert.That(BeginUse(entry, out _), Is.False);
            Assert.That(Inspect(entry, out var source), Is.True);
            Assert.That(source, Is.EqualTo("source-a"));
            Assert.That(Property<bool>(entry, "Pending"), Is.False);
        }

        [Test] public void DisabledAndPendingOptionsStillRequireButtonLayout()
        {
            var disabled = Option("a"); disabled.IsEnabled = false; Bind(disabled);
            Assert.That(Property<bool>(binding, "HasVisibleOptions"), Is.True);
            Call(binding, "SetPending", "window-1", "a", true);
            Assert.That(Property<bool>(binding, "HasVisibleOptions"), Is.True);
            Bind(); Assert.That(Property<bool>(binding, "HasVisibleOptions"), Is.False);
        }

        [Test] public void VisibleRequestAdapterPreservesAndClonesPresentation()
        {
            var presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:prompt") };
            presentation.Options.Add(new GameplayOptionPresentation { CandidateId = "candidate", Label = new GameplayTextRef("core:label") });
            presentation.QuantityRows.Add(new GameplayQuantityRowPresentation { RowId = "row", Unit = new GameplayTextRef("core:unit") });
            var view = new GameStateView { ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 1 };
            view.Interactions.Add(new InteractionView { InteractionId = "request", Status = "open", AnsweringPlayerId = 1,
                Visibility = "owner", Presentation = presentation });
            var copy = VisibleInteractionRequestSource.Read(null, view, 1).Single();
            presentation.Prompt.Key = "core:changed";
            presentation.Options[0].Label.Key = "core:changed";
            presentation.QuantityRows.Clear();
            Assert.That(copy.Presentation.Prompt.Key, Is.EqualTo("core:prompt"));
            Assert.That(copy.Presentation.Options[0].Label.Key, Is.EqualTo("core:label"));
            Assert.That(copy.Presentation.QuantityRows.Single().Unit.Key, Is.EqualTo("core:unit"));
            Assert.That(VisibleInteractionRequestSource.Read(null, view, 2), Is.Empty);
        }

        [Test] public void PersistingCleanupUsesWithoutSourceCard()
        {
            var option = Option("cleanup"); option.EntryKind = "cleanup_order";
            option.TargetEffectId = "existing-effect"; option.SourceItemId = "";
            Bind(option); var entry = Entry("cleanup");
            Assert.That(Inspect(entry, out _), Is.False);
            Assert.That(BeginUse(entry, out var submitted), Is.True);
            Assert.That(submitted.TargetEffectId, Is.EqualTo("existing-effect"));
        }

        [Test] public void VisibleReadOnlyObserverCannotExecuteAnotherPlayersOption()
        {
            var option = Option("a"); option.AnsweringPlayerId = 2; Bind(option);
            Assert.That(BeginUse(Entry("a"), out _), Is.False);
            Assert.That(Inspect(Entry("a"), out _), Is.True);
        }

        [Test] public void SourcePermissionDoesNotDetermineExecutionEligibility()
        {
            var option = Option("a"); option.CanInspectSource = false; Bind(option);
            var entry = Entry("a");
            Assert.That(Inspect(entry, out _), Is.False);
            Assert.That(Call(binding, "TryGetSource", Lease(entry)), Is.EqualTo(false));
            Assert.That(BeginUse(entry, out _), Is.True);
        }

        [Test] public void AuthorizedDefinitionOnlyDetailsSurviveSourceDeparture()
        {
            var option = Option("a"); option.EntryKind = "cleanup_order"; option.TargetEffectId = "existing-effect";
            option.SourceItemId = ""; option.SourceDefinitionId = "infected:character.w";
            Bind(option); var args = Lease(Entry("a"));
            Assert.That(Inspect(Entry("a"), out _), Is.False);
            Assert.That(Call(binding, "TryGetSource", args), Is.EqualTo(true));
            Assert.That(((SettlementOptionView)args[4]).SourceDefinitionId, Is.EqualTo(option.SourceDefinitionId));
            Assert.That(BeginUse(Entry("a"), out _), Is.True);
        }

        [Test] public void HiddenOptionsAreOmittedAndViewerChangeInvalidatesOldLease()
        {
            var hidden = Option("secret"); hidden.VisibleToViewer = false;
            Bind(Option("a"), hidden); var old = Lease(Entry("a"));
            Assert.That(Ids(), Is.EqualTo(new[] { "a" }));
            Call(binding, "SetScope", "game-1/session-1", 2);
            Assert.That(Ids(), Is.Empty);
            var next = Option("a"); next.AnsweringPlayerId = 2; Bind(next);
            Assert.That(BeginUse(old, out _), Is.False);
        }

        [Test] public void RefreshRevisionAndRemoveThenReaddRejectOldPointerLease()
        {
            Bind(Option("a")); var first = Lease(Entry("a"));
            Bind(Option("a", 0, 2)); Assert.That(BeginUse(first, out _), Is.False);
            var refreshed = Lease(Entry("a"));
            Bind(); Bind(Option("a", 0, 2));
            Assert.That(BeginUse(refreshed, out _), Is.False);
            Assert.That(BeginUse(Entry("a"), out _), Is.True);
        }

        [Test] public void OldReceiptCannotClearNewSessionOrNewRequest()
        {
            Bind(Option("a")); Assert.That(BeginUse(Entry("a"), out _), Is.True);
            var first = Call(binding, "CapturePending", "a");
            Assert.That(Call(binding, "SetPending", "window-1", "a", false, first), Is.EqualTo(true));
            Assert.That(BeginUse(Entry("a"), out _), Is.True);
            Assert.That(Call(binding, "SetPending", "window-1", "a", false, first), Is.EqualTo(false));
            var second = Call(binding, "CapturePending", "a");
            Call(binding, "SetScope", "game-1/session-2", 1); Bind(Option("a"));
            Assert.That(BeginUse(Entry("a"), out _), Is.True);
            Assert.That(Call(binding, "SetPending", "window-1", "a", false, second), Is.EqualTo(false));
            Assert.That(Property<bool>(Entry("a"), "Pending"), Is.True);
        }

        [Test] public void NewWindowInvalidatesCallbacksAndClearsPending()
        {
            Bind(Option("a")); var old = Lease(Entry("a")); BeginUse(old, out _);
            var next = Option("a"); next.WindowId = "window-2";
            Call(binding, "BindWindow", "window-2", new[] { next });
            Assert.That(BeginUse(old, out _), Is.False);
            Assert.That(Property<bool>(Entry("a"), "Pending"), Is.False);
            Assert.That(BeginUse(Entry("a"), out _), Is.True);
        }

        [Test] public void InvalidBatchAndStaleRevisionDoNotPartiallyReplaceWindow()
        {
            Bind(Option("a", 1, 2)); var entry = Entry("a");
            Assert.Throws<ArgumentException>(() => Bind(Option("b"), Option("b")));
            Assert.Throws<ArgumentException>(() => Bind(Option("a", 1, 1)));
            var malformed = Option("bad"); malformed.EntryKind = "cleanup_order";
            Assert.Throws<ArgumentException>(() => Bind(malformed));
            malformed.EntryKind = "optional_insertion"; malformed.TargetEffectId = "existing-effect";
            Assert.Throws<ArgumentException>(() => Bind(malformed));
            var missingText = Option("b"); missingText.Presentation.Description = null;
            Assert.Throws<ArgumentException>(() => Bind(missingText));
            Assert.That(Ids(), Is.EqualTo(new[] { "a" }));
            Assert.That(Entry("a"), Is.SameAs(entry));
            Assert.That(Property<SettlementOptionView>(entry, "Option").StateRevision, Is.EqualTo(2));
        }

        [Test] public void ProjectionAndSubmittedCopyCannotMutateBindingSnapshot()
        {
            var input = Option("a"); Bind(input);
            input.IsEnabled = false; input.Presentation.Label.Key = "core:changed";
            Assert.That(BeginUse(Entry("a"), out var submitted), Is.True);
            Assert.That(submitted.Presentation.Label.Key, Is.EqualTo("core:name"));
            submitted.Presentation.Label.Key = "core:submitted_change";
            submitted.DisabledReason.Key = "core:changed_reason";
            Assert.That(Property<SettlementOptionView>(Entry("a"), "Option").Presentation.Label.Key, Is.EqualTo("core:name"));
            Assert.That(Property<SettlementOptionView>(Entry("a"), "Option").DisabledReason.Key, Is.EqualTo("core:disabled_reason"));
        }

        [Test] public void ClearInvalidatesPendingAndLease()
        {
            Bind(Option("a")); var old = Lease(Entry("a")); BeginUse(old, out _);
            Call(binding, "Clear"); Bind(Option("a"));
            Assert.That(BeginUse(old, out _), Is.False);
            Assert.That(Property<bool>(Entry("a"), "Pending"), Is.False);
        }

        [Test] public void OrdinaryPointerClickIsReadyImmediatelyAndConsumesOnce()
        {
            var gesture = New("SettlementEffectPointerGesture");
            Call(gesture, "Begin", 1, 0f, 0f, 100f); Call(gesture, "Release", 1);
            Assert.That(Call(gesture, "ConsumeClick", 1), Is.EqualTo(true));
            Assert.That(Call(gesture, "ConsumeClick", 1), Is.EqualTo(false));
        }

        [Test] public void DragCancelsOnlyItsPressAndNextClickHasNoDelay()
        {
            var gesture = New("SettlementEffectPointerGesture");
            Call(gesture, "Begin", 1, 0f, 0f, 100f); Call(gesture, "Move", 1, 7f, 0f, 6f);
            Call(gesture, "Release", 1); Assert.That(Call(gesture, "ConsumeClick", 1), Is.EqualTo(false));
            Call(gesture, "Begin", 2, 0f, 0f, 100f); Call(gesture, "Cancel", 1); Call(gesture, "Release", 2);
            Assert.That(Call(gesture, "ConsumeClick", 2), Is.EqualTo(true));
        }

        [Test] public void LongPressInspectsOnceAndSuppressesReleaseUse()
        {
            var gesture = New("SettlementEffectPointerGesture");
            Call(gesture, "Begin", 1, 0f, 0f, 100f);
            Assert.That(Call(gesture, "TryLongPress", 100.1f, .5f), Is.EqualTo(false));
            Assert.That(Call(gesture, "TryLongPress", 100.5f, .5f), Is.EqualTo(true));
            Assert.That(Call(gesture, "TryLongPress", 101f, .5f), Is.EqualTo(false));
            Call(gesture, "Release", 1); Assert.That(Call(gesture, "ConsumeClick", 1), Is.EqualTo(false));
        }

        [Test] public void WrongPointerReleaseAndResetCannotGenerateClick()
        {
            var gesture = New("SettlementEffectPointerGesture");
            Call(gesture, "Begin", 1, 0f, 0f, 100f); Call(gesture, "Release", 2);
            Assert.That(Call(gesture, "ConsumeClick", 2), Is.EqualTo(false));
            Call(gesture, "Reset"); Call(gesture, "Release", 1);
            Assert.That(Call(gesture, "ConsumeClick", 1), Is.EqualTo(false));
        }

        [Test, Category("需要Unity")]
        public void MissingSerializedReferencesFailWithoutConstructingHud()
        {
            var root = new GameObject("结算组件引用验证");
            try
            {
                var strip = root.AddComponent(TypeOf("SettlementEffectStripView"));
                var args = new object[] { null };
                Assert.That(Call(strip, "ValidateReferences", args), Is.EqualTo(false));
                Assert.That(args[0], Is.Not.Empty);
                Assert.Throws<InvalidOperationException>(() => Call(strip, "BindWindow", "window-1", Array.Empty<SettlementOptionView>(), null, null));
                Assert.That(root.transform.childCount, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test, Category("需要Unity"), Category("入场生命周期")]
        public void HudStartupReferencesAndChipSerialization()
        {
            var failures = new List<string>();
            const string hudPath = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
            const string chipPath = "Assets/YC/Presentation/Settlement/Prefabs/SettlementEffectChip.prefab";
            var hudPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(hudPath);
            Assert.That(hudPrefab, Is.Not.Null, hudPath);
            foreach (var frame in hudPrefab.GetComponentsInChildren(TypeOf("GameplayHudFrame"), true))
            {
                var args = new object[] { null };
                var valid = (bool)Call(frame, "TryValidateConfiguration", args);
                RecordHudReferences(hudPath, frame, valid, (string)args[0]);
                if (!valid) failures.Add(hudPath + "：" + args[0]);
            }
            var chipPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(chipPath);
            Assert.That(chipPrefab, Is.Not.Null, chipPath);
            var sourceChip = chipPrefab.GetComponent(TypeOf("SettlementEffectChipView"));
            Assert.That(sourceChip, Is.Not.Null);
            var sourceText = (RectOffset)SerializedField(sourceChip, "textContentPadding");
            var sourceIcon = (RectOffset)SerializedField(sourceChip, "iconContentPadding");
            GameObject chipInstance = null;
            try
            {
                chipInstance = UnityEngine.Object.Instantiate(chipPrefab);
                var chip = chipInstance.GetComponent(TypeOf("SettlementEffectChipView"));
                var text = (RectOffset)SerializedField(chip, "textContentPadding");
                var icon = (RectOffset)SerializedField(chip, "iconContentPadding");
                var originalText = PaddingValues(text); var originalIcon = PaddingValues(icon);
                chip.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(chip, null);
                Assert.That(SerializedField(chip, "textContentPadding"), Is.SameAs(text));
                Assert.That(SerializedField(chip, "iconContentPadding"), Is.SameAs(icon));
                Assert.That(PaddingValues(text), Is.EqualTo(originalText));
                Assert.That(PaddingValues(icon), Is.EqualTo(originalIcon));
                Assert.That(originalText, Is.EqualTo(PaddingValues(sourceText)));
                Assert.That(originalIcon, Is.EqualTo(PaddingValues(sourceIcon)));
                TestContext.Progress.WriteLine("[入场诊断] Chip原生实例化及Awake通过；已序列化padding保持：text=" +
                    string.Join(",", originalText) + "；icon=" + string.Join(",", originalIcon));
            }
            finally { if (chipInstance != null) UnityEngine.Object.DestroyImmediate(chipInstance); }

            foreach (var path in new[] { "Assets/Scenes/ThreePlayerScene.unity", "Assets/Scenes/SampleScene.unity" })
            {
                var scene = SceneManager.GetSceneByPath(path);
                var alreadyLoaded = scene.IsValid() && scene.isLoaded;
                if (!alreadyLoaded) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                try
                {
                    var controllers = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(TypeOf("MobileCityInteractionController"), true)).ToArray();
                    Assert.That(controllers, Has.Length.EqualTo(1), path);
                    foreach (var controller in controllers)
                    {
                        var hud = (Component)SerializedField(controller, "gameplayInteractionHud");
                        var buildPanel = SerializedField(controller, "buildInfoPanel");
                        var args = new object[] { hud, controller, buildPanel, null };
                        var valid = (bool)TypeOf("GameplayInteractionHudView").GetMethod("TryValidateSceneBinding", BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
                        var frame = (Component)hud.GetType().GetProperty("Frame").GetValue(hud);
                        RecordHudReferences(path, frame, valid, (string)args[3]);
                        TestContext.Progress.WriteLine("[入场诊断] 实际场景HUD来源=" + PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(hud) +
                            "；场景覆盖=" + EditorJsonUtility.ToJson(controller));
                        if (!valid) failures.Add(path + "：" + args[3]);
                    }
                }
                finally { if (!alreadyLoaded) EditorSceneManager.CloseScene(scene, true); }
            }
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        [UnityTest, Category("需要Unity")]
        public IEnumerator StripResizesWithConfiguredInsetsAndRestoresTheSameCaption()
        {
            // 在真实播放生命周期内测试当前Prefab克隆，不手动调用组件消息。
            yield return new EnterPlayMode();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Settlement/Prefabs/SettlementEffectStrip.prefab");
            var root = UnityEngine.Object.Instantiate(prefab);
            root.SetActive(false);
            var caption = new GameObject("本测试原标题");
            try
            {
                var strip = root.GetComponent(TypeOf("SettlementEffectStripView"));
                var rect = (RectTransform)root.transform;
                var viewport = (RectTransform)SerializedField(strip, "viewport");
                var reserved = (float)SerializedField(strip, "headerReservedWidth");
                var threshold = (float)SerializedField(strip, "hideHeaderBelowWidth");
                var inset = (float)SerializedField(strip, "horizontalInset");
                var originalHeight = viewport.rect.height;
                strip.GetType().GetField("header", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(strip, caption);
                root.SetActive(true);
                yield return null;
                foreach (var width in new[] { threshold + reserved + 10, Mathf.Max(inset * 2 + 1, threshold - 1), threshold + reserved + 30 })
                {
                    rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
                    yield return null;
                    bool show = width >= threshold;
                    Assert.That(caption.activeSelf, Is.EqualTo(show));
                    Assert.That(viewport.offsetMin.x, Is.EqualTo(inset + (show ? reserved : 0)).Within(.01f));
                    Assert.That(viewport.offsetMax.x, Is.EqualTo(-inset).Within(.01f));
                    Assert.That(viewport.rect.width, Is.EqualTo(width - (show ? reserved : 0) - inset * 2).Within(.01f));
                    Assert.That(viewport.rect.height, Is.EqualTo(originalHeight).Within(.01f));
                    var center = rect.InverseTransformPoint(viewport.TransformPoint(viewport.rect.center));
                    Assert.That(center.y, Is.EqualTo(rect.rect.center.y).Within(.01f));
                }
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Mathf.Max(inset * 2 + 1, threshold - 1));
                yield return null;
                root.SetActive(false);
                yield return null;
                Assert.That(caption.activeSelf, Is.True, "离开按钮模式时恢复同一个原标题对象");
                caption.SetActive(false);
                root.SetActive(true);
                yield return null;
                root.SetActive(false);
                yield return null;
                Assert.That(caption.activeSelf, Is.False, "不强行恢复配置中原本关闭的标题");

                var hudPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
                var hudStrip = hudPrefab.GetComponentsInChildren(TypeOf("SettlementEffectStripView"), true).Single();
                var actualCaption = (GameObject)SerializedField(hudStrip, "header");
                Assert.That(actualCaption, Is.Not.Null, "实际HUD嵌套Strip必须绑定原槽标题");
                Assert.That(actualCaption.transform.parent, Is.SameAs(hudStrip.transform.parent.parent));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(caption); }
            yield return new ExitPlayMode();
        }

        [Test, Category("需要Unity")]
        public void ChipArrivalUsesTheClipEdgeWithoutMovingItsLayoutSlotAndReducedMotionRestoresIt()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Settlement/Prefabs/SettlementEffectChip.prefab");
            var root = UnityEngine.Object.Instantiate(prefab);
            try
            {
                var chip = root.GetComponent(TypeOf("SettlementEffectChipView"));
                var visual = (RectTransform)SerializedField(chip, "visual");
                var group = (CanvasGroup)SerializedField(chip, "visualGroup");
                var slot = (RectTransform)root.transform;
                var origin = visual.anchoredPosition; var slotPosition = slot.anchoredPosition;
                var edge = (float)SerializedField(chip, "arrivalEdgeInset");
                chip.GetType().GetField("visualOrigin", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(chip, origin);
                Call(chip, "PrepareArrival", false);
                Assert.That(group.alpha, Is.Zero, "等待本批布局时不能先在终点闪现");
                float viewportWidth = slot.rect.width * 3, visibleLeft = -slot.rect.width;
                Call(chip, "PlayArrival", false, 0f, viewportWidth, visibleLeft);
                Assert.That(visibleLeft + visual.anchoredPosition.x - origin.x, Is.EqualTo(viewportWidth + edge).Within(.01f));
                Assert.That(slot.anchoredPosition, Is.EqualTo(slotPosition));
                Call(chip, "PlayArrival", true, .18f, viewportWidth, visibleLeft);
                Assert.That(visual.anchoredPosition, Is.EqualTo(origin));
                Assert.That(group.alpha, Is.EqualTo(1));
                Assert.That(slot.anchoredPosition, Is.EqualTo(slotPosition));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static object SerializedField(object value, string field) => value.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(value);
        private static int[] PaddingValues(RectOffset value) => new[] { value.left, value.right, value.top, value.bottom };
        private static void RecordHudReferences(string asset, Component frame, bool valid, string reason)
        {
            var names = new List<string>();
            for (var current = frame.transform; current != null; current = current.parent) names.Add(current.name);
            names.Reverse();
            TestContext.Progress.WriteLine("[入场诊断] asset=" + asset + "；object=" + string.Join("/", names) +
                "；有效=" + valid + "；原因=" + reason);
            var serialized = new SerializedObject(frame); var property = serialized.GetIterator();
            while (property.NextVisible(true))
                if (property.propertyType == SerializedPropertyType.ObjectReference)
                {
                    var reference = property.objectReferenceValue;
                    TestContext.Progress.WriteLine("[入场引用] " + property.propertyPath + "=" +
                        (reference == null ? "null" : reference.name + " (" + reference.GetType().FullName + ") asset=" + AssetDatabase.GetAssetPath(reference)));
                }
            var style = SerializedField(frame, "gameplayTextStyle") as UnityEngine.Object;
            if (style != null) TestContext.Progress.WriteLine("[入场引用] gameplayTextStyle原序列化=" + EditorJsonUtility.ToJson(style));
        }

        private void Bind(params SettlementOptionView[] options) => Call(binding, "BindWindow", "window-1", options);
        private long Scope => Property<long>(binding, "Scope");
        private object[] Lease(object entry)
        {
            var o = Property<SettlementOptionView>(entry, "Option");
            return new object[] { Scope, Property<long>(entry, "Identity"), o.StateRevision, o.OptionId, null };
        }
        private bool BeginUse(object entry, out SettlementOptionView option) => BeginUse(Lease(entry), out option);
        private bool BeginUse(object[] lease, out SettlementOptionView option)
        { var result = (bool)Call(binding, "TryBeginUse", lease); option = (SettlementOptionView)lease[4]; return result; }
        private bool Inspect(object entry, out string source)
        { var args = Lease(entry); var result = (bool)Call(binding, "TryInspect", args); source = (string)args[4]; return result; }
        private object Entry(string id) => ((IEnumerable)binding.GetType().GetProperty("Entries").GetValue(binding))
            .Cast<object>().Single(e => Property<SettlementOptionView>(e, "Option").OptionId == id);
        private string[] Ids() => ((IEnumerable)binding.GetType().GetProperty("Entries").GetValue(binding))
            .Cast<object>().Select(e => Property<SettlementOptionView>(e, "Option").OptionId).ToArray();
        [Test, Category("付款收口")] public void PaymentRangeKeepsUnsetUpperBoundAndExistingExactMinimumRules()
        {
            var spec = New("ResourceAllocationSpec");
            Assert.That(spec.GetType().GetField("MaximumTotal").GetValue(spec), Is.EqualTo(-1));
            Assert.That(Call(spec, "IsTotalAllowed", (long)int.MaxValue * 5), Is.True);
            spec.GetType().GetField("MaximumTotal").SetValue(spec, 2);
            Assert.That(Call(spec, "IsTotalAllowed", 0L), Is.True);
            Assert.That(Call(spec, "IsTotalAllowed", 2L), Is.True);
            Assert.That(Call(spec, "IsTotalAllowed", 3L), Is.False);
            Assert.That(Call(spec, "CanIncreaseTotal", 2L, 0), Is.False);
            spec.GetType().GetField("ExclusiveChoice").SetValue(spec, true);
            Assert.That(Call(spec, "CanIncreaseTotal", 2L, 0), Is.True);
            spec.GetType().GetField("ExactTotal").SetValue(spec, 1);
            Assert.That(Call(spec, "IsTotalAllowed", 0L), Is.False);
            Assert.That(Call(spec, "IsTotalAllowed", 1L), Is.True);
            spec.GetType().GetField("ExactTotal").SetValue(spec, -1);
            spec.GetType().GetField("MinimumTotal").SetValue(spec, 2);
            Assert.That(Call(spec, "IsTotalAllowed", 1L), Is.False);
            Assert.That(Call(spec, "IsTotalAllowed", 2L), Is.True);
        }

        [Test, Category("付款收口")] public void PaymentBoundedAnswerAcceptsZeroAndRejectsRowAndTotalOverflow()
        {
            var ids = new[] { "Originium", "OriginiumShard", "Iron" };
            var limits = new Dictionary<string, int> { { ids[0], 1 }, { ids[1], 2 }, { ids[2], 2 } };
            Assert.That(ResourceAllocationAnswer.TryParseBounded("Originium=0,OriginiumShard=0,Iron=0", ids, 0, 2, limits, out _, out _), Is.True);
            Assert.That(ResourceAllocationAnswer.TryParseBounded("Originium=1,Iron=1", ids, 0, 2, limits, out _, out _), Is.True);
            Assert.That(ResourceAllocationAnswer.TryParseBounded("Originium=2", ids, 0, 2, limits, out _, out _), Is.False);
            Assert.That(ResourceAllocationAnswer.TryParseBounded("OriginiumShard=2,Iron=1", ids, 0, 2, limits, out _, out _), Is.False);
            Assert.That(ResourceAllocationAnswer.TryParseBounded("GoldVoucher=0", ids, 0, 2, limits, out _, out _), Is.False);
        }

        [Test, Category("付款收口")] public void PaymentOpaqueCandidatesMatchOnlyCompleteStructuredCosts()
        {
            var page = New("PaymentPageDraftBinding"); Call(page, "Bind", "game/player/hash", PaymentRequest());
            Assert.That(Call(page, "Match", PaymentCostA), Is.EqualTo("候选甲:不含费用编码"));
            Assert.That(Call(page, "Match", PaymentCostB), Is.EqualTo("opaque/b"));
            Assert.That(Call(page, "Match", new[] { 3, 0, 0, 0, 0 }), Is.Null);
            Assert.That(Call(page, "Match", new[] { 3, 1 }), Is.Null);
            Assert.That(Property<int[]>(page, "Maximums"), Is.EqualTo(new[] { 3, 1, 3, 2, 4 }));
        }

        [Test, Category("付款收口")] public void PaymentReceiptRequiresFreshMatchingHostProjection()
        {
            var page = New("PaymentPageDraftBinding"); var request = PaymentRequest();
            Call(page, "Bind", "scope", request); Call(page, "SetDesired", PaymentCostA);
            var command = (GameCommand)Call(page, "PrepareCommand", request, 1);
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.PreparePaymentDraft));
            Assert.That(command.Parameters.Count, Is.EqualTo(4));
            Assert.That(command.Parameters["ownerEffectId"], Is.EqualTo("payment-owner"));
            Assert.That(command.Parameters["interactionId"], Is.EqualTo("payment-request"));
            Assert.That(command.Parameters["candidateId"], Is.EqualTo(request.CandidateIds[0]));
            Assert.That(command.Parameters["expectedRevision"], Is.EqualTo("10"));
            Assert.That(Call(page, "PrepareCommand", request, 1), Is.Null);
            Call(page, "Settle", command.CommandId, true);
            Assert.That(Property<bool>(page, "CanConfirm"), Is.False);
            Call(page, "Bind", "scope", PaymentRequest(11, request.CandidateIds[1]));
            Assert.That(Property<bool>(page, "CanConfirm"), Is.False);
            Call(page, "Bind", "scope", PaymentRequest(12, request.CandidateIds[0]));
            Assert.That(Property<bool>(page, "CanConfirm"), Is.True);
            Assert.That(Property<bool>(page, "CanChange"), Is.True);
            Assert.That(Call(page, "PrepareCommand", PaymentRequest(12, request.CandidateIds[0]), 1), Is.Null);
        }

        [Test, Category("付款收口")] public void PaymentProjectionBeforeReceiptAndOlderRevisionCannotUnlockEarly()
        {
            var page = New("PaymentPageDraftBinding"); var request = PaymentRequest();
            Call(page, "Bind", "scope", request); Call(page, "SetDesired", PaymentCostA);
            var command = (GameCommand)Call(page, "PrepareCommand", request, 1);
            Call(page, "Bind", "scope", PaymentRequest(11, request.CandidateIds[0]));
            Assert.That(Property<bool>(page, "CanConfirm"), Is.False);
            Call(page, "Settle", command.CommandId, true);
            Assert.That(Property<bool>(page, "CanConfirm"), Is.True);
            Call(page, "Bind", "scope", PaymentRequest(9));
            Assert.That(Property<int>(page, "Revision"), Is.EqualTo(11));
            Assert.That(Call(page, "PrepareCommand", PaymentRequest(9), 1), Is.Null);
        }

        [Test, Category("付款收口")] public void PaymentEditingDuringSubmissionKeepsLatestDraftAndPreparesOnce()
        {
            var page = New("PaymentPageDraftBinding"); var request = PaymentRequest();
            Call(page, "Bind", "scope", request); Call(page, "SetDesired", PaymentCostA);
            var first = (GameCommand)Call(page, "PrepareCommand", request, 1);
            Call(page, "SetDesired", new[] { 1, 1, 0, 0, 0 });
            Assert.That(Call(page, "PrepareCommand", request, 1), Is.Null);
            Call(page, "SetDesired", PaymentCostB);
            Assert.That(Property<bool>(page, "CanChange"), Is.True);
            Assert.That(Call(page, "PrepareCommand", request, 1), Is.Null);
            Call(page, "Settle", first.CommandId, true); var frame = PaymentRequest(11, request.CandidateIds[0]);
            Call(page, "Bind", "scope", frame);
            Assert.That(Property<bool>(page, "CanConfirm"), Is.False);
            var second = (GameCommand)Call(page, "PrepareCommand", frame, 1);
            Assert.That(second.Parameters["candidateId"], Is.EqualTo(request.CandidateIds[1]));
            Assert.That(second.Parameters["expectedRevision"], Is.EqualTo("11"));
            Assert.That(Call(page, "PrepareCommand", frame, 1), Is.Null);
            Call(page, "Settle", second.CommandId, true); Call(page, "Bind", "scope", PaymentRequest(12, request.CandidateIds[1]));
            Assert.That(Property<bool>(page, "CanConfirm"), Is.True);
            Call(page, "SetInputValid", false);
            Assert.That(Property<bool>(page, "CanConfirm"), Is.False);
            Call(page, "SetInputValid", true);
            Assert.That(Property<bool>(page, "CanConfirm"), Is.True);
        }

        [Test, Category("付款收口")] public void PaymentRejectedSameDraftDoesNotRetryOnRefreshButAllowsNewChoice()
        {
            var page = New("PaymentPageDraftBinding"); var request = PaymentRequest();
            Call(page, "Bind", "scope", request); Call(page, "SetDesired", PaymentCostA);
            var first = (GameCommand)Call(page, "PrepareCommand", request, 1);
            Call(page, "Settle", first.CommandId, false); Call(page, "Bind", "scope", PaymentRequest(11));
            Call(page, "SetDesired", PaymentCostA);
            Assert.That(Call(page, "PrepareCommand", PaymentRequest(11), 1), Is.Null);
            Call(page, "SetDesired", PaymentCostB);
            Assert.That(Call(page, "PrepareCommand", PaymentRequest(11), 1), Is.Not.Null);
        }

        [Test, Category("付款收口")] public void PaymentLateReceiptAndReusedRequestAcrossScopeCannotReviveOldDraft()
        {
            var page = New("PaymentPageDraftBinding"); var request = PaymentRequest();
            Call(page, "Bind", "old-scope", request); Call(page, "SetDesired", PaymentCostA);
            var old = (GameCommand)Call(page, "PrepareCommand", request, 1);
            Call(page, "Bind", "new-scope", PaymentRequest()); Call(page, "SetDesired", PaymentCostB);
            Assert.That(Call(page, "Settle", old.CommandId, true), Is.False);
            Assert.That(Call(page, "Settle", string.Empty, false), Is.False);
            Assert.That(Call(page, "IsScopeCurrent", "old-scope"), Is.False);
            Assert.That(Property<bool>(page, "CanConfirm"), Is.False);
            var newRequest = PaymentRequest(); newRequest.OwnerEffectId = "different-owner";
            Assert.That(Call(page, "IsIdentityCurrent", newRequest), Is.False);
            newRequest = PaymentRequest(); newRequest.CandidateSetVersion++;
            Assert.That(Call(page, "IsIdentityCurrent", newRequest), Is.False);
        }

        [Test, Category("付款收口")] public void PaymentChangedWindowClearsPendingAndIsolatesReceiptWithSameRequestId()
        {
            var page = New("PaymentPageDraftBinding"); var request = PaymentRequest();
            Call(page, "Bind", "scope", request); Call(page, "SetDesired", PaymentCostA);
            var command = (GameCommand)Call(page, "PrepareCommand", request, 1);
            Call(page, "Bind", "scope", PaymentRequest(11, request.CandidateIds[0]));
            var changedWindow = PaymentRequest(12, request.CandidateIds[0]);
            changedWindow.PromptParameters.Properties.Find(entry => entry.Name == "paymentDraft").Value.Properties
                .Find(entry => entry.Name == "windowId").Value = NormalizedValue.CreateString("new-window");
            Assert.That(Call(page, "Bind", "scope", changedWindow), Is.True);
            Assert.That(Call(page, "Settle", command.CommandId, true), Is.False);
            Assert.That(Property<bool>(page, "CanConfirm"), Is.False);
        }

        [Test, Category("付款收口")] public void PaymentRecoveredFrozenDraftConfirmsOriginalCostWithoutPreparingAgain()
        {
            var page = New("PaymentPageDraftBinding"); var request = PaymentRequest(20, "opaque/b", false);
            Call(page, "Bind", "recovered", request); Call(page, "SetDesired", Property<int[]>(page, "InitialCost"));
            Assert.That(Property<bool>(page, "CanChange"), Is.False);
            Assert.That(Property<bool>(page, "CanConfirm"), Is.True);
            Assert.That(Call(page, "PrepareCommand", request, 1), Is.Null);
            Call(page, "SetDesired", PaymentCostA);
            Assert.That(Property<bool>(page, "CanConfirm"), Is.False);
            Assert.That(Call(page, "PrepareCommand", request, 1), Is.Null);
        }

        private static readonly int[] PaymentCostA = { 3, 1, 0, 0, 0 };
        private static readonly int[] PaymentCostB = { 0, 1, 3, 2, 4 };
        private static NormalizedValue PaymentObject(params object[] values)
        {
            var entries = new List<NormalizedValueEntry>();
            for (var i = 0; i < values.Length; i += 2) entries.Add(new NormalizedValueEntry { Name = (string)values[i], Value = (NormalizedValue)values[i + 1] });
            return NormalizedValue.CreateObject(entries);
        }
        private static InteractionRequest PaymentRequest(int revision = 10, string selected = "", bool canChange = true)
        {
            var ids = new[] { "候选甲:不含费用编码", "opaque/b" };
            var fields = new[] { "originium", "originiumShard", "iron", "pureOriginium", "goldVoucher" };
            NormalizedValue Cost(int[] amounts) => NormalizedValue.CreateObject(fields.Select((key, i) =>
                new NormalizedValueEntry { Name = key, Value = NormalizedValue.CreateInteger(amounts[i]) }).ToList());
            return new InteractionRequest { InteractionId = "payment-request", OwnerEffectId = "payment-owner",
                CandidateSetId = "payment-candidates", CandidateSetVersion = 2, StateRevision = revision,
                AnsweringPlayerId = 1, Visibility = "owner", Status = "open", AnswerSchema = "candidate_id",
                InteractionTypeId = "effect.resource.payment_choice", CandidateIds = ids.ToList(),
                PromptParameters = PaymentObject("paymentOptions", NormalizedValue.CreateArray(new[] {
                    PaymentObject("candidateId", NormalizedValue.CreateString(ids[0]), "cost", Cost(PaymentCostA)),
                    PaymentObject("candidateId", NormalizedValue.CreateString(ids[1]), "cost", Cost(PaymentCostB)) }),
                    "paymentDraft", PaymentObject("candidateId", NormalizedValue.CreateString(selected),
                        "canChange", NormalizedValue.CreateBoolean(canChange), "paymentId", NormalizedValue.CreateString(selected.Length > 0 ? "real-payment" : ""),
                        "windowId", NormalizedValue.CreateString(selected.Length > 0 ? "real-window" : ""))) };
        }

        private static SettlementOptionView Option(string id, long sequence = 0, int revision = 1) => new SettlementOptionView {
            WindowId = "window-1", OptionId = id, EntryKind = "optional_insertion", RegistrationSequence = sequence,
            AnsweringPlayerId = 1, StateRevision = revision, IsEnabled = true, VisibleToViewer = true, SourceItemId = "source-" + id,
            CanInspectSource = true, SourceDefinitionId = "core:definition", DisabledReason = new GameplayTextRef("core:disabled_reason"),
            Presentation = new SettlementPresentationDefinition { Label = new GameplayTextRef("core:name"), Description = new GameplayTextRef("core:description") }
        };
        private static Type TypeOf(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object New(string name) => Activator.CreateInstance(TypeOf(name));
        private static T Property<T>(object instance, string name) => (T)instance.GetType().GetProperty(name).GetValue(instance);
        private static object Call(object instance, string method, params object[] args)
        {
            var target = instance.GetType().GetMethods().Single(m => m.Name == method && m.GetParameters().Length == args.Length);
            try { return target.Invoke(instance, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
