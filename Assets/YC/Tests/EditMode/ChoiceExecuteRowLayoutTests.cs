using System;
using System.Linq;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Presentation;

namespace YC.Tests.EditMode
{
    public sealed class ChoiceExecuteRowLayoutTests
    {
        private const string SourcePath = "Assets/YC/Presentation/Effects/Prefabs/EffectRow.prefab";

        [TestCase(360f)] [TestCase(900f)]
        public void ExecuteRowsAllocateHorizontallyAndFitBodyWithoutShrinkingShortTitle(float width)
        {
            using (var fixture = new RowFixture(width))
            {
                int titleSize = fixture.Title.fontSize, bodySize = fixture.Body.fontSize;
                fixture.Bind("ChoiceExecute", "执行", new string('文', 240));
                fixture.Settle();
                Assert.That(fixture.Title.horizontalOverflow, Is.EqualTo(HorizontalWrapMode.Overflow));
                Assert.That(fixture.Body.horizontalOverflow, Is.EqualTo(HorizontalWrapMode.Wrap));
                if (FitsPlain(fixture.Title, titleSize))
                    Assert.That(fixture.Title.fontSize, Is.EqualTo(titleSize), "自身容纳得下的标题不能被长正文连带缩小。");
                Assert.That(fixture.Body.fontSize, Is.LessThanOrEqualTo(bodySize));
                if (bodySize > 1 && !FitsPlain(fixture.Body, bodySize))
                    Assert.That(fixture.Body.fontSize, Is.LessThan(bodySize));
                Assert.That(fixture.Right(fixture.Title), Is.LessThanOrEqualTo(fixture.Left(fixture.Body) + .1f));
                Assert.That(fixture.Body.rectTransform.rect.width, Is.GreaterThan(0));
                var settings = fixture.Body.GetGenerationSettings(fixture.Body.rectTransform.rect.size);
                settings.resizeTextForBestFit = false;
                Assert.That(fixture.Body.cachedTextGeneratorForLayout.GetPreferredHeight(fixture.Body.text, settings) /
                    fixture.Body.pixelsPerUnit, Is.LessThanOrEqualTo(fixture.Body.rectTransform.rect.height + .1f));
                Debug.Log("执行横排测量：宽=" + width + "，标题框=" + fixture.Title.rectTransform.rect +
                    "，正文框=" + fixture.Body.rectTransform.rect + "，字号=" + fixture.Title.fontSize + "/" + fixture.Body.fontSize);
                fixture.Capture("body-fit-" + width);
            }
        }

        [Test]
        public void EmptyTitleLeavesNoSlotAndSwitchingBackRestoresTheAssetDetailLayout()
        {
            using (var fixture = new RowFixture(560))
            {
                fixture.Bind("PersistentDetail", "明细", "原说明"); fixture.Settle();
                var titleRect = fixture.Title.rectTransform.rect.size;
                var bodyRect = fixture.Body.rectTransform.rect.size;
                var titlePosition = fixture.Title.rectTransform.anchoredPosition;
                var bodyPosition = fixture.Body.rectTransform.anchoredPosition;
                var titleWrap = fixture.Title.horizontalOverflow;
                fixture.Capture("detail-baseline");
                fixture.Bind("ChoiceExecute", string.Empty, "空标题时正文从左侧开始，竖直居中。"); fixture.Settle();
                Assert.That(fixture.Title.rectTransform.rect.width, Is.Zero.Within(.1f));
                var layout = (LayoutGroup)fixture.ChoiceLayout;
                Assert.That(fixture.Body.rectTransform.rect.width,
                    Is.EqualTo(((RectTransform)layout.transform).rect.width - layout.padding.horizontal).Within(.1f));
                Assert.That(fixture.Left(fixture.Body),
                    Is.EqualTo(fixture.Left((RectTransform)layout.transform) + layout.padding.left).Within(.1f));
                Assert.That(fixture.Body.alignment, Is.EqualTo(TextAnchor.MiddleLeft));
                fixture.Capture("empty-heading");
                fixture.Bind("PersistentDetail", "明细", "原说明"); fixture.Settle();
                Assert.That((bool)fixture.ChoiceLayout.GetType().GetProperty("ExecutionMode").GetValue(fixture.ChoiceLayout), Is.False);
                Assert.That(fixture.ChoiceLayout is VerticalLayoutGroup, Is.True);
                Assert.That(fixture.Title.horizontalOverflow, Is.EqualTo(titleWrap));
                Assert.That(Vector2.Distance(fixture.Title.rectTransform.rect.size, titleRect), Is.LessThan(.1f));
                Assert.That(Vector2.Distance(fixture.Body.rectTransform.rect.size, bodyRect), Is.LessThan(.1f));
                Assert.That(Vector2.Distance(fixture.Title.rectTransform.anchoredPosition, titlePosition), Is.LessThan(.1f));
                Assert.That(Vector2.Distance(fixture.Body.rectTransform.anchoredPosition, bodyPosition), Is.LessThan(.1f));
                fixture.Capture("detail-restored");
            }
        }

        [Test]
        public void LongTitleKeepsOneLineAndBodySpaceAcrossClearAndRebind()
        {
            using (var fixture = new RowFixture(360))
            {
                int bodySize = fixture.Body.fontSize;
                fixture.Bind("ChoiceExecute", new string('题', 80) + "\n末尾", "说明"); fixture.Settle();
                Assert.That(fixture.Title.text, Does.Not.Contain("\n"));
                Assert.That(fixture.Title.rectTransform.rect.width,
                    Is.LessThanOrEqualTo((float)Field(fixture.ChoiceLayout, "maximumTitleWidth") + .1f));
                Assert.That(fixture.Body.rectTransform.rect.width, Is.GreaterThan(0));
                if (FitsPlain(fixture.Body, bodySize)) Assert.That(fixture.Body.fontSize, Is.EqualTo(bodySize));
                fixture.Capture("long-heading");
                fixture.Row.GetType().GetMethod("ClearBinding").Invoke(fixture.Row, null);
                fixture.Bind("ChoiceExecute", string.Empty, "新的说明"); fixture.Settle();
                Assert.That(fixture.Title.rectTransform.rect.width, Is.Zero.Within(.1f));
                fixture.Bind("ChoiceExecute", "短标题", "短正文"); fixture.Settle();
                Assert.That(fixture.Title.text, Is.EqualTo("短标题"));
                Assert.That(fixture.Body.text, Is.EqualTo("短正文"));
                Assert.That(fixture.Title.rectTransform.rect.width, Is.GreaterThan(0));
                fixture.Bind("ChoiceExecute", null, null); fixture.Settle();
                Assert.That(fixture.Title.text, Is.Empty);
                Assert.That(fixture.Body.text, Is.Empty);
                Assert.That(fixture.Title.rectTransform.rect.width, Is.Zero.Within(.1f));
                Assert.That(fixture.Body.rectTransform.rect.width, Is.GreaterThan(0));
                fixture.Bind("ChoiceExecute", "仅标题", string.Empty); fixture.Settle();
                Assert.That(fixture.Body.text, Is.Empty);
                Assert.That(FitsPlain(fixture.Title, fixture.Title.fontSize), Is.True);
                fixture.Capture("empty-body");
            }
        }

        [TestCase(360f)] [TestCase(900f)]
        public void DocumentsKeepIconsWeightColorAndStopReflowingAfterTheLayoutSettles(float width)
        {
            using (var fixture = new RowFixture(width))
            {
                int bodyBaseSize = fixture.Body.fontSize;
                fixture.Bind("ChoiceExecute", string.Empty, "正文");
                var runStyle = new GameplayTextRunStyle("Regular", "Bold", 0xD95745FFu);
                var heading = new GameplayTextDocument(new GameplayTextRun[] {
                    new IconRun("core:resource.score", "分", 12), new LineBreak(), new TextRun("标题", runStyle) }, "横排标题");
                var body = new GameplayTextDocument(new GameplayTextRun[] {
                    new TextRun(new string('文', 90), runStyle), new IconRun("core:resource.score", "分", 123) }, "横排正文");
                Component titleDocument = fixture.BindDocument(fixture.Title, heading);
                Component bodyDocument = fixture.BindDocument(fixture.Body, body);
                fixture.Settle();
                Assert.That(fixture.Title.rectTransform.rect.width, Is.GreaterThan(0), "文档标题不能被空的纯文本回退值隐藏。");
                Assert.That(fixture.Title.GetComponentsInChildren<Image>().Any(image => image.gameObject.activeSelf && image.sprite != null), Is.True);
                Assert.That(fixture.Body.GetComponentsInChildren<Image>().Any(image => image.gameObject.activeSelf && image.sprite != null), Is.True);
                var fragments = fixture.Title.GetComponentsInChildren<Text>().Where(text => text != fixture.Title && text.gameObject.activeSelf).ToArray();
                Assert.That(fragments.Any(text => text.text.Contains("标题") && text.color == (Color)new Color32(0xD9, 0x57, 0x45, 0xFF)), Is.True);
                var roles = fixture.Style.GetType().GetProperty("FontRoles").GetValue(fixture.Style);
                var emphasis = (Font)roles.GetType().GetProperty("Emphasis").GetValue(roles);
                Assert.That(fragments.Any(text => text.text.Contains("标题") && text.font == emphasis), Is.True);
                Assert.That((bool)titleDocument.GetType().GetMethod("FitsAtFontSize").Invoke(titleDocument, new object[] { fixture.Title.fontSize }), Is.True);
                bool bodyFits = (bool)bodyDocument.GetType().GetMethod("FitsAtFontSize").Invoke(bodyDocument, new object[] { fixture.Body.fontSize });
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GAMEPLAY_CHOICE_ROW_EVIDENCE")))
                {
                    string candidates = string.Join(", ", Enumerable.Range(1, bodyBaseSize).Select(size => size + ":" +
                        bodyDocument.GetType().GetMethod("FitsAtFontSize").Invoke(bodyDocument, new object[] { size })));
                    Debug.Log("正式正文候选字号实测：宽=" + width + "，框体=" + fixture.Body.rectTransform.rect +
                        "，实际字号=" + fixture.Body.fontSize + "，候选=" + candidates);
                }
                if (!bodyFits)
                {
                    fixture.Capture("rich-failure-reproduction-" + width);
                }
                Assert.That(bodyFits, Is.True);
                int before = (int)bodyDocument.GetType().GetProperty("LayoutCount").GetValue(bodyDocument);
                fixture.Settle();
                Assert.That((int)bodyDocument.GetType().GetProperty("LayoutCount").GetValue(bodyDocument), Is.EqualTo(before), "稳定后不得逐次完整重排。");
                fixture.Capture("rich-" + width);
                fixture.Bind("ChoiceExecute", string.Empty, "普通正文"); fixture.Settle();
                Assert.That(fixture.Title.GetComponentsInChildren<Image>().Any(image => image.gameObject.activeSelf), Is.False);
                Assert.That(fixture.Title.rectTransform.rect.width, Is.Zero.Within(.1f));
            }
        }

        [Test]
        public void DialogsUseTheConfiguredSourceWhileQuickActionRowsRemainSeparate()
        {
            foreach (string path in new[] { "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EffectDialogShell.prefab",
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/FacilityBuildDialog.prefab" })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var shell = prefab.GetComponentsInChildren<Component>(true).First(component => component != null && component.GetType().Name == "EffectDialogShellView");
                Assert.That(AssetDatabase.GetAssetPath((UnityEngine.Object)Field(shell, "effectRowTemplate")), Is.EqualTo(SourcePath));
                var ordinary = (Component)Field(shell, "optionRowTemplate");
                Assert.That(ordinary.GetComponentsInChildren<Component>(true).Any(component => component != null && component.GetType().Name == "UiChoiceExecuteTextLayout"), Is.False);
            }
            var hud = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            var original = hud.GetComponentsInChildren<Component>(true).First(component => component != null && component.GetType().Name == "UiQuickActionRow");
            var instance = UnityEngine.Object.Instantiate(original.gameObject);
            try
            {
                var quick = instance.GetComponent(original.GetType());
                Assert.That(instance.GetComponentsInChildren<Component>(true).Any(component => component != null && component.GetType().Name == "UiChoiceExecuteTextLayout"), Is.False);
                int executed = 0;
                quick.GetType().GetMethod("Bind").Invoke(quick, new object[] { "快速行动", true, new Action(() => executed++) });
                ((Button)quick.GetType().GetProperty("Button").GetValue(quick)).onClick.Invoke();
                Assert.That(executed, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }

        private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);

        private static bool FitsPlain(Text text, int size)
        {
            var settings = text.GetGenerationSettings(text.rectTransform.rect.size);
            settings.fontSize = size; settings.resizeTextForBestFit = false;
            return text.cachedTextGeneratorForLayout.GetPreferredHeight(text.text, settings) / text.pixelsPerUnit <= text.rectTransform.rect.height + .1f &&
                (text.horizontalOverflow != HorizontalWrapMode.Overflow ||
                text.cachedTextGeneratorForLayout.GetPreferredWidth(text.text, settings) / text.pixelsPerUnit <= text.rectTransform.rect.width + .1f);
        }

        private sealed class RowFixture : IDisposable
        {
            private readonly GameObject canvas;
            private readonly GameObject instance;
            public readonly Component Row;
            public readonly Text Title, Body;
            public readonly Component ChoiceLayout;
            public UnityEngine.Object Style { get; private set; }
            public RowFixture(float width)
            {
                canvas = new GameObject("执行条目隔离布局", typeof(RectTransform), typeof(Canvas));
                canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                instance = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(SourcePath), canvas.transform, false);
                ((RectTransform)instance.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
                Row = instance.GetComponent(Type.GetType("YC.Presentation.UiEffectRowView, Assembly-CSharp", true));
                Title = (Text)Row.GetType().GetProperty("TitleText").GetValue(Row);
                Body = (Text)Row.GetType().GetProperty("DescriptionText").GetValue(Row);
                ChoiceLayout = (Component)Field(Row, "choiceTextLayout");
                Assert.That(ChoiceLayout, Is.Not.Null);
            }
            public void Bind(string modeName, string title, string body)
            {
                var type = Row.GetType();
                var bindingType = type.GetNestedType("Binding");
                var data = Activator.CreateInstance(bindingType);
                bindingType.GetField("StableItemId").SetValue(data, "layout-item");
                bindingType.GetField("RequestId").SetValue(data, "layout-request");
                bindingType.GetField("RowMode").SetValue(data, Enum.Parse(type.GetNestedType("Mode"), modeName));
                bindingType.GetField("RightStatus").SetValue(data, Enum.Parse(type.GetNestedType("Status"), "Use"));
                bindingType.GetField("Enabled").SetValue(data, true);
                bindingType.GetField("Title").SetValue(data, title);
                bindingType.GetField("Description").SetValue(data, body);
                type.GetMethod("Bind", new[] { bindingType, typeof(Func<string, int, bool>), typeof(Action<string>), typeof(Action<string>) })
                    .Invoke(Row, new object[] { data, new Func<string, int, bool>((_, __) => true), null, new Action<string>(_ => { }) });
            }
            public Component BindDocument(Text host, GameplayTextDocument document)
            {
                var type = Type.GetType("YC.Presentation.GameplayTextView, Assembly-CSharp", true);
                var view = host.GetComponent(type);
                var styleType = Type.GetType("YC.Presentation.GameplayTextStyle, Assembly-CSharp", true);
                var iconType = Type.GetType("YC.Presentation.GameplayIconCatalog, Assembly-CSharp", true);
                Style = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayTextStyle.asset", styleType);
                var icons = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayIconCatalog.asset", iconType);
                type.GetMethod("Configure").Invoke(view, new object[] { host, Style, icons, new ScoreCatalog() });
                type.GetMethod("Bind").Invoke(view, new object[] { document });
                return view;
            }
            public void Settle()
            {
                for (int i = 0; i < 4; i++)
                {
                    Canvas.ForceUpdateCanvases();
                    LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)instance.transform);
                    Row.GetType().GetMethod("FitText").Invoke(Row, null);
                }
                Canvas.ForceUpdateCanvases();
            }
            public void Capture(string name)
            {
                string requested = Environment.GetEnvironmentVariable("GAMEPLAY_CHOICE_ROW_EVIDENCE");
                if (string.IsNullOrEmpty(requested)) return;
                string directory = Path.GetFullPath(requested);
                string project = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..")) + Path.DirectorySeparatorChar;
                if (!directory.StartsWith(project, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("隔离证据目录必须在当前项目内。");
                Directory.CreateDirectory(directory);
                var rowRect = (RectTransform)instance.transform;
                var anchorMin = rowRect.anchorMin; var anchorMax = rowRect.anchorMax;
                var pivot = rowRect.pivot; var position = rowRect.anchoredPosition;
                var owner = canvas.GetComponent<Canvas>(); var mode = owner.renderMode; var previousCamera = owner.worldCamera;
                var cameraNode = new GameObject("执行条目隔离截图相机", typeof(Camera));
                var camera = cameraNode.GetComponent<Camera>();
                var target = new RenderTexture(Mathf.CeilToInt(rowRect.rect.width) + 48, Mathf.CeilToInt(rowRect.rect.height) + 48, 24);
                Texture2D pixels = null; var previousTarget = RenderTexture.active;
                try
                {
                    camera.transform.position = new Vector3(0, 0, -10); camera.orthographic = true;
                    camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.16f, .17f, .19f);
                    camera.targetTexture = target; target.Create();
                    owner.renderMode = RenderMode.ScreenSpaceCamera; owner.worldCamera = camera; owner.planeDistance = 1;
                    rowRect.anchorMin = rowRect.anchorMax = rowRect.pivot = new Vector2(.5f, .5f); rowRect.anchoredPosition = Vector2.zero;
                    Settle(); camera.Render();
                    RenderTexture.active = target;
                    pixels = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                    pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
                    File.WriteAllBytes(Path.Combine(directory, name + ".png"), pixels.EncodeToPNG());
                    File.WriteAllText(Path.Combine(directory, name + ".json"), JsonUtility.ToJson(new LayoutMeasurement {
                        sourcePrefab = SourcePath, sample = name, rowSize = rowRect.rect.size,
                        unityVersion = UnityEngine.Application.unityVersion,
                        loadedRowAssembly = Row.GetType().Assembly.Location,
                        loadedRowAssemblyMvid = Row.GetType().Assembly.ManifestModule.ModuleVersionId.ToString(),
                        loadedTestAssemblyMvid = GetType().Assembly.ManifestModule.ModuleVersionId.ToString(),
                        titleSize = Title.rectTransform.rect.size, bodySize = Body.rectTransform.rect.size,
                        titlePosition = Title.rectTransform.anchoredPosition, bodyPosition = Body.rectTransform.anchoredPosition,
                        titleFontSize = Title.fontSize, bodyFontSize = Body.fontSize,
                        titleText = Title.text, bodyText = Body.text,
                        iconSizes = instance.GetComponentsInChildren<Image>().Where(image => image.gameObject.activeInHierarchy && image.transform.IsChildOf(Body.transform))
                            .Select(image => image.rectTransform.rect.size).ToArray()
                    }, true));
                }
                finally
                {
                    RenderTexture.active = previousTarget;
                    owner.renderMode = mode; owner.worldCamera = previousCamera;
                    rowRect.anchorMin = anchorMin; rowRect.anchorMax = anchorMax; rowRect.pivot = pivot; rowRect.anchoredPosition = position;
                    camera.targetTexture = null; target.Release(); UnityEngine.Object.DestroyImmediate(target);
                    if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
                    UnityEngine.Object.DestroyImmediate(cameraNode);
                }
            }
            public float Left(Text text) => Left(text.rectTransform);
            public float Left(RectTransform rect) { var corners = new Vector3[4]; rect.GetWorldCorners(corners); return corners[0].x; }
            public float Right(Text text) { var corners = new Vector3[4]; text.rectTransform.GetWorldCorners(corners); return corners[2].x; }
            public void Dispose() => UnityEngine.Object.DestroyImmediate(canvas);
        }

        [Serializable]
        private sealed class LayoutMeasurement
        {
            public string sourcePrefab, sample, titleText, bodyText;
            public string unityVersion, loadedRowAssembly, loadedRowAssemblyMvid, loadedTestAssemblyMvid;
            public Vector2 rowSize, titleSize, bodySize, titlePosition, bodyPosition;
            public int titleFontSize, bodyFontSize;
            public Vector2[] iconSizes;
        }

        private sealed class ScoreCatalog : IGameplayTextCatalog
        {
            public string ContentHash => "choice-row-layout-isolation";
            public string Locale => "zh-CN";
            public bool TryGetTemplate(string key, out GameplayTextTemplateDefinition definition) { definition = null; return false; }
            public bool TryGetIcon(string key, out GameplayIconDefinition definition)
            {
                definition = key == "core:resource.score" ? new GameplayIconDefinition { Id = key, Usage = "inline_rule",
                    AssetKey = "core:resource.score.asset", RenderMode = "number_overlay", ValueRequired = true,
                    NumberOverlayStyleKey = "core:score_number_overlay" } : null;
                return definition != null;
            }
        }
    }
}
