using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class EnterpriseDetailTypographyTests
    {
        private static Type Runtime(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static T Field<T>(object value, string name) => (T)value.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);

        [TestCase("梅兰德基金会 5 级特效", "5")]
        [TestCase("名称 <b>原文</b> 005 / 12", "005|12")]
        public void TitleDigitsKeepTheirLiteralTextAndUseTheExistingNumberRole(string caption, string digits)
        {
            var document = (GameplayTextDocument)Runtime("EnterpriseSelectionDialogView")
                .GetMethod("CreateDetailTitleDocument", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { caption });
            Assert.That(document.ToPlainText(), Is.EqualTo(caption));
            Assert.That(string.Join("|", document.Runs.OfType<TextRun>()
                .Where(run => run.Style.SemanticRole == "EffectNumber").Select(run => run.Text)), Is.EqualTo(digits));
            Assert.That(document.Runs.OfType<TextRun>().Where(run => run.Style.SemanticRole == "Regular")
                .All(run => run.Style.WeightMode == "Inherit"), Is.True, "企业名沿原宿主真实字体，不把字面标签当样式。");
        }

        [Test] public void EnterpriseTitleUsesNumberFontAndKeepsFullLongTitleAboveDescription()
        {
            var viewType = Runtime("EnterpriseSelectionDialogView");
            var textViewType = Runtime("GameplayTextView");
            var styleType = Runtime("GameplayTextStyle");
            var style = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayTextStyle.asset", styleType);
            var icons = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayIconCatalog.asset", Runtime("GameplayIconCatalog"));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/Enterprise/EnterpriseSelectionDialog.prefab");
            Assert.That(prefab, Is.Not.Null); Assert.That(style, Is.Not.Null); Assert.That(icons, Is.Not.Null);
            var root = new GameObject("企业详情字号与长标题检查", typeof(RectTransform), typeof(Canvas));
            root.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            ((RectTransform)root.transform).sizeDelta = new Vector2(1920, 900);
            try
            {
                var page = UnityEngine.Object.Instantiate(prefab, root.transform, false).GetComponent(viewType);
                var title = Field<Text>(page, "detailTitle");
                var body = Field<Text>(page, "description");
                var titleView = Field<Component>(page, "detailTitleDocument");
                Assert.That(title.fontSize, Is.EqualTo(48), "本批用户明确要求企业名称48号。");
                Assert.That(body.fontSize, Is.EqualTo(28), "本批用户明确要求描述28号。");
                Assert.That(titleView.GetComponent<Text>(), Is.SameAs(title));
                var configured = new HashSet<Component>();
                bool titlePreservesFullText = false;
                Action<Component, GameplayTextDocument, bool> render = (host, document, singleLine) =>
                {
                    if (document == null) { textViewType.GetMethod("ClearBinding").Invoke(host, null); return; }
                    if (configured.Add(host))
                        textViewType.GetMethod("Configure").Invoke(host, new object[] { host.GetComponent<Text>(), style, icons, new CaptionCatalog() });
                    if (host == titleView) titlePreservesFullText = !singleLine;
                    if (singleLine) textViewType.GetMethod("BindSingleLineEllipsized").Invoke(host, new object[] { document, float.MaxValue });
                    else textViewType.GetMethod("Bind").Invoke(host, new object[] { document });
                };
                viewType.GetMethod("BindGameplayText").Invoke(page, new object[] {
                    new Func<IReadOnlyList<GameplayTextRef>, GameplayTextDocument>(_ => new GameplayTextDocument(new[] { new TextRun("正文内容") })), render });
                var projection = new EnterpriseSelectionProjection {
                    RequestId = "enterprise-typography", Revision = 1, Mode = EnterpriseSelectionMode.Effect,
                    Boards = new[] { new EnterpriseBoardProjection { Id = "board", VisualKey = "enterprise-0" } },
                    Targets = new[] { new EnterpriseSelectionTarget { Id = "target", BoardId = "board",
                        CompanyId = "board", EffectId = "typography-special", Tier = 5, Label = "企业 5", Available = true } }
                };
                viewType.GetMethod("Present").Invoke(page, new object[] { projection, false });
                viewType.GetMethod("Choose").Invoke(page, new object[] { "target" });
                Flush(page);
                Assert.That(titlePreservesFullText, Is.True);
                var roles = styleType.GetProperty("FontRoles").GetValue(style);
                var numberFont = (Font)roles.GetType().GetProperty("EffectNumber").GetValue(roles);
                Assert.That(title.GetComponentsInChildren<Text>().Any(fragment => fragment != title &&
                    fragment.text == "5" && fragment.font == numberFont && fragment.fontStyle == FontStyle.Normal), Is.True);
                var fullTitle = "梅兰德基金会 · 5级特效";
                projection.Targets[0].Label = fullTitle;
                projection.Revision++;
                viewType.GetMethod("Present").Invoke(page, new object[] { projection, false });
                Flush(page);
                var fragments = title.GetComponentsInChildren<Text>().Where(fragment => fragment != title).ToArray();
                Assert.That(string.Concat(fragments.Select(fragment => fragment.text)), Is.EqualTo(fullTitle));
                Assert.That(fragments.All(fragment => fragment.fontSize == title.fontSize), Is.True);
                Assert.That((float)textViewType.GetProperty("preferredWidth").GetValue(titleView),
                    Is.LessThanOrEqualTo(title.rectTransform.rect.width + .01f));
                var titleCorners = new Vector3[4]; var descriptionCorners = new Vector3[4];
                title.rectTransform.GetWorldCorners(titleCorners);
                Field<ScrollRect>(page, "descriptionScroll").GetComponent<RectTransform>().GetWorldCorners(descriptionCorners);
                Assert.That(titleCorners.Min(point => point.y), Is.GreaterThanOrEqualTo(descriptionCorners.Max(point => point.y) - .01f));
                Assert.That(title.rectTransform.rect.height + .01f,
                    Is.GreaterThanOrEqualTo((float)textViewType.GetProperty("preferredHeight").GetValue(titleView)));
                var fullDocument = (GameplayTextDocument)viewType.GetMethod("CreateDetailTitleDocument",
                    BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { fullTitle });
                float fullWidth = (float)textViewType.GetMethod("MeasureSingleLineWidth").Invoke(titleView, new object[] { fullDocument });
                Debug.Log($"企业详情标题原生尺寸：标题={fullTitle}，字号={title.fontSize}，完整单行宽={fullWidth:R}，宿主={title.rectTransform.rect.ToString("R")}，实测标题高={textViewType.GetProperty("preferredHeight").GetValue(titleView)}，描述字号={body.fontSize}。");
                viewType.GetMethod("ClearPresentation").Invoke(page, null);
                Assert.That((int)textViewType.GetProperty("ActiveFragmentCount").GetValue(titleView), Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void Flush(Component page)
        {
            for (int i = 0; i < 3; i++)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)page.transform);
                Canvas.ForceUpdateCanvases();
            }
        }

        private sealed class CaptionCatalog : IGameplayTextCatalog
        {
            public string ContentHash => "enterprise-typography";
            public string Locale => "zh-CN";
            public bool TryGetTemplate(string key, out GameplayTextTemplateDefinition definition) { definition = null; return false; }
            public bool TryGetIcon(string key, out GameplayIconDefinition definition) { definition = null; return false; }
        }
    }
}
