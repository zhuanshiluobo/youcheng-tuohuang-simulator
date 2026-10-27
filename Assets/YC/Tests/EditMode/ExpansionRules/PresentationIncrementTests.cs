using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Presentation;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 仅隔离技术验证；不调用菜单/生成器，不计正常页面或多局验收。
    public sealed class PresentationIncrementTests
    {
        private IsolatedTestArtifacts artifacts;
        private static string Installed => Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
        private static Type Runtime => Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
        private static Type Bridge => Type.GetType("YC.Presentation.GameplayIconResourceResolver, Assembly-CSharp", true);
        [SetUp] public void SetUp() { artifacts = IsolatedTestArtifacts.BeginCase(); Reset(); }
        [TearDown] public void TearDown() { Reset(); artifacts.Finish(); }
        private static void Reset() => Runtime.GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        private static object Call(Type type, object instance, string method, params object[] arguments)
        {
            try { return type.GetMethod(method, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Invoke(instance, arguments); }
            catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private static JObject Manifest(string id, params string[] paths) => new JObject {
            ["schemaVersion"] = 1, ["packId"] = id, ["version"] = "1", ["locale"] = "zh-CN", ["texts"] = new JArray(paths) };
        private static JObject Message(string template, JObject parameters = null) => new JObject { ["parameters"] = parameters ?? new JObject(), ["template"] = template };
        private static JObject Document(string id, string extension = ".png") => new JObject {
            ["schemaVersion"] = 2, ["locale"] = "zh-CN",
            ["messages"] = new JObject {
                [id + ":alt"] = Message("技术夹具图标"),
                [id + ":value"] = Message("{value}单位", new JObject { ["value"] = "integer" }),
                [id + ":body"] = Message("[b]{name}[/b] [icon:" + id + ":probe|value={amount}]", new JObject { ["name"] = "text", ["amount"] = "integer" }) },
            ["assets"] = new JObject { [id + ":image"] = "icon" + extension },
            ["icons"] = new JArray(new JObject { ["id"] = id + ":probe", ["usage"] = "inline_rule",
                ["altText"] = new JObject { ["key"] = id + ":alt", ["arguments"] = new JArray() }, ["assetKey"] = id + ":image",
                ["monochrome"] = false, ["renderMode"] = "number_overlay", ["valueRequired"] = true,
                ["valueAltTextKey"] = id + ":value", ["numberOverlayStyleKey"] = "core:score_number_overlay" }) };
        private static byte[] Picture(int width, int height, bool jpeg = false)
        {
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                // 纯技术输入，不进入可用素材库或正式内容。
                image.SetPixels(Enumerable.Repeat(new Color(.2f, .6f, .8f), width * height).ToArray()); image.Apply();
                return jpeg ? image.EncodeToJPG() : image.EncodeToPNG();
            }
            finally { UnityEngine.Object.DestroyImmediate(image); }
        }
        private static Dictionary<string, string> Inputs(bool second = false)
        {
            var result = new Dictionary<string, string> {
                ["registry/sources.json"] = new JObject { ["schemaVersion"] = 1, ["sources"] = second ? new JArray("alpha", "beta") : new JArray("alpha") }.ToString(),
                ["registry/alpha/presentation.json"] = Manifest("alpha", "texts.json", "extra.json").ToString(),
                ["registry/alpha/texts.json"] = Document("alpha").ToString(),
                ["registry/alpha/extra.json"] = new JObject { ["schemaVersion"] = 2, ["locale"] = "zh-CN", ["messages"] = new JObject { ["alpha:extra"] = Message("[icon:beta:probe|value=7]") } }.ToString()
            };
            if (!second) result["registry/alpha/extra.json"] = new JObject { ["schemaVersion"] = 2, ["locale"] = "zh-CN", ["messages"] = new JObject { ["alpha:extra"] = Message("额外文件") } }.ToString();
            if (second)
            {
                result["registry/beta/presentation.json"] = Manifest("beta", "texts.json").ToString();
                var beta = Document("beta");
                beta["assets"]["beta:photo"] = "photo.jpg";
                ((JArray)beta["icons"]).Add(new JObject { ["id"] = "beta:photo", ["usage"] = "inline_rule", ["assetKey"] = "beta:photo",
                    ["altText"] = new JObject { ["key"] = "beta:alt", ["arguments"] = new JArray() } });
                result["registry/beta/texts.json"] = beta.ToString();
            }
            return result;
        }
        private string Copy(string label, Dictionary<string, string> text = null, bool second = false, byte[] image = null)
        {
            var binaries = new Dictionary<string, byte[]> { ["registry/alpha/icon.png"] = image ?? Picture(3, 5) };
            if (second) { binaries["registry/beta/icon.png"] = Picture(7, 9); binaries["registry/beta/photo.jpg"] = Picture(11, 13, true); }
            return artifacts.CopyContent(Installed, label, text ?? Inputs(second), binaries);
        }
        private ExternalContentPack Configure(string root)
        {
            Call(Runtime, null, "ConfigureRegistered", root, Path.Combine(root, "registry"));
            var pack = (ExternalContentPack)Runtime.GetProperty("Pack").GetValue(null);
            artifacts.RecordContent("正式JSON登记入口", root, pack.ContentHash); return pack;
        }
        private static GameRuleConfiguration Configuration() => new GameRuleConfiguration { PlayerCount = 2, MapId = StaticMapDefinitions.ThreePlayerMapId };
        private static GameplayTextDocument Resolve(IGameplayTextCatalog catalog, string id = "alpha") => new GameplayTextResolver(catalog).Resolve(
            new GameplayTextRef(id + ":body", new GameplayTextArgument { Name = "name", Kind = "text", Value = NormalizedValue.CreateString("[b]参数仍为字面[/b]") },
                new GameplayTextArgument { Name = "amount", Kind = "integer", Value = NormalizedValue.CreateInteger(12) }),
            new GameplayTextContext { Catalog = catalog, Locale = catalog.Locale, ContentHash = catalog.ContentHash });

        [Test] public void RegisteredSourcesReachActiveCatalogBodyAndHudWithFrozenSprites()
        {
            string root = Copy("两包完整输入", second: true); var pack = Configure(root);
            var active = ActiveContentCatalogBuilder.Build(pack, Configuration()); var catalog = active.TextCatalog;
            Assert.That(pack.PresentationSources.Select(s => s.PackId), Is.EqualTo(new[] { "alpha", "beta" }));
            Assert.That(catalog.TryGetTemplate("alpha:extra", out _), Is.True);
            Assert.That(catalog.TryGetTemplate("core:presentation.unavailable", out _), Is.True);
            var document = Resolve(catalog); Assert.That(document.ToPlainText(), Does.Contain("[b]参数仍为字面[/b]"));
            Assert.That(document.Runs.OfType<IconRun>().Single().NumberValue, Is.EqualTo(12));
            Assert.That(document.Runs.OfType<TextRun>().First().Style.WeightMode, Is.EqualTo("Bold"));
            var styleType = Type.GetType("YC.Presentation.GameplayTextStyle, Assembly-CSharp", true);
            var themeType = Type.GetType("YC.Presentation.GameplayIconCatalog, Assembly-CSharp", true);
            var style = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayTextStyle.asset", styleType);
            var theme = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayIconCatalog.asset", themeType);
            var roles = styleType.GetProperty("FontRoles").GetValue(style);
            var canvasObject = new GameObject("隔离正文", typeof(RectTransform), typeof(Canvas));
            var hostObject = new GameObject("正文", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var hudObject = new GameObject("隔离HUD"); hudObject.SetActive(false);
            hostObject.transform.SetParent(canvasObject.transform, false);
            var host = hostObject.GetComponent<Text>(); host.font = (Font)roles.GetType().GetProperty("Regular").GetValue(roles);
            host.fontSize = 32; host.rectTransform.sizeDelta = new Vector2(1000, 200);
            var viewType = Type.GetType("YC.Presentation.GameplayTextView, Assembly-CSharp", true);
            var hudType = Type.GetType("YC.Presentation.GameplayHudFrame, Assembly-CSharp", true);
            var view = hostObject.AddComponent(viewType); var hud = hudObject.AddComponent(hudType);
            try
            {
                hudType.GetField("gameplayTextStyle", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(hud, style);
                hudType.GetField("gameplayIconCatalog", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(hud, theme);
                Call(hudType, hud, "ConfigureGameplayPresentation", catalog, new Dictionary<string, string>());
                // 持有原输入文件的独占锁，清空缓存后仍能重建，证明渲染不延迟读盘。
                using (File.Open(Path.Combine(root, "registry/alpha/icon.png"), FileMode.Open, FileAccess.Read, FileShare.None))
                using (File.Open(Path.Combine(root, "registry/beta/icon.png"), FileMode.Open, FileAccess.Read, FileShare.None))
                using (File.Open(Path.Combine(root, "registry/beta/photo.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Call(viewType, view, "Configure", host, style, theme, catalog); Call(viewType, view, "Bind", document);
                    Canvas.ForceUpdateCanvases(); Canvas.ForceUpdateCanvases();
                    var bodySprite = host.GetComponentsInChildren<Image>().Single().sprite;
                    var hudSprite = (Sprite)Call(hudType, hud, "ResolveGameplayIcon", "alpha:probe", "dark");
                    var betaSprite = (Sprite)Call(hudType, hud, "ResolveGameplayIcon", "beta:probe", "dark");
                    Assert.That(bodySprite, Is.SameAs(hudSprite));
                    Assert.That(bodySprite.rect.size, Is.EqualTo(new Vector2(3, 5)));
                    Assert.That(betaSprite.rect.size, Is.EqualTo(new Vector2(7, 9)));
                    Assert.That(betaSprite, Is.Not.SameAs(bodySprite));
                    Assert.That(((Sprite)Call(hudType, hud, "ResolveGameplayIcon", "beta:photo", "dark")).rect.size, Is.EqualTo(new Vector2(11, 13)));
                    Assert.That(host.GetComponentsInChildren<Image>().Single().GetComponentsInChildren<Text>().Single().text, Is.EqualTo("12"));
                    var toneType = Type.GetType("YC.Presentation.GameplayIconTone, Assembly-CSharp", true);
                    foreach (var coreIcon in ((GameplayPresentationCatalog)catalog).Icons.Where(i => i.Id.StartsWith("core:", StringComparison.Ordinal)))
                    foreach (string tone in new[] { "Dark", "Light", "Muted" })
                    {
                        object[] args = { coreIcon, Enum.Parse(toneType, tone), null };
                        themeType.GetMethod("TryResolve").Invoke(theme, args);
                        Assert.That(Call(hudType, hud, "ResolveGameplayIcon", coreIcon.Id, tone.ToLowerInvariant()), Is.SameAs(args[2]));
                    }
                    string keyBefore = (string)viewType.GetProperty("MeasurementCacheKey").GetValue(view);
                    Call(Bridge, null, "Clear"); Assert.That(hudSprite == null, Is.True); Assert.That(bodySprite == null, Is.True);
                    Canvas.ForceUpdateCanvases(); Canvas.ForceUpdateCanvases();
                    Assert.That((string)viewType.GetProperty("MeasurementCacheKey").GetValue(view), Is.Not.EqualTo(keyBefore));
                    Assert.That(host.GetComponentsInChildren<Image>().Single().sprite.rect.size, Is.EqualTo(new Vector2(3, 5)));
                    artifacts.WriteLabeledText("真实Sprite证据", JsonConvert.SerializeObject(new { active.ContentPackHash, pack.PresentationContentHash,
                        png = "alpha/icon.png=3x5;beta/icon.png=7x9", jpeg = "11x13", sameBodyHudSprite = true, coreThemeTonesRetained = true, noLateDiskRead = true, cacheReleased = true }));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(hudObject); UnityEngine.Object.DestroyImmediate(canvasObject); }
        }

        [Test] public void OfficialIndependentTextsAndLegacySchemaRemainReadableWithoutChangingBaseBytes()
        {
            const string basePath = "presentation/texts.zh-CN.json";
            string sourcePath = Path.Combine(Installed, basePath);
            byte[] capturedSource = File.ReadAllBytes(sourcePath);
            var privateRoots = new List<string>();
            string Digest(byte[] bytes)
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
            try
            {
                string root = Copy("官方独立文件"); privateRoots.Add(root); Configure(root);
                var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
                var paths = ((JArray)manifest["presentation"]["texts"]).Values<string>().ToArray();
                foreach (string required in new[] { basePath, "presentation/entrepreneurs/T.zh-CN.json", "presentation/entrepreneurs/A.zh-CN.json",
                    "presentation/entrepreneurs/D.zh-CN.json", "presentation/entrepreneurs/P.zh-CN.json", "presentation/entrepreneurs/M.zh-CN.json" })
                    Assert.That(paths.Count(path => path == required), Is.EqualTo(1), "必要文案路径应各登记一次：" + required);
                var official = new JObject { ["schemaVersion"] = 2, ["locale"] = "zh-CN", ["messages"] = new JObject { ["core:official.probe"] = Message("独立技术验证") } };
                string changed = artifacts.CopyContent(root, "官方独立探针", new Dictionary<string, string> { ["presentation/entrepreneurs/T.zh-CN.json"] = official.ToString() });
                privateRoots.Add(changed);
                Assert.That(ExternalContentPack.Load(changed).Presentation.TryGetTemplate("core:official.probe", out _), Is.True);
                manifest["presentation"]["texts"] = new JArray(basePath);
                string legacy = artifacts.CopyContent(root, "旧schema1输入", new Dictionary<string, string> { ["pack.json"] = manifest.ToString() });
                privateRoots.Add(legacy);
                var old = ExternalContentPack.Load(legacy); new GameplayTextResolver(old.Presentation).Precompile();
                Assert.That(old.Presentation.HasExtensions, Is.False);
                artifacts.RecordContent("schema1保留", legacy, old.ContentHash);
            }
            finally
            {
                byte[] after = File.ReadAllBytes(sourcePath);
                var copies = privateRoots.Select(root => new { root, bytes = File.ReadAllBytes(Path.Combine(root, basePath)) }).ToArray();
                artifacts.WriteLabeledText("基础文案当次字节核验", JsonConvert.SerializeObject(new {
                    sourcePath, capturedSourceSha256 = Digest(capturedSource), afterSourceSha256 = Digest(after),
                    sourceUnchanged = capturedSource.SequenceEqual(after),
                    privateCopies = copies.Select(copy => new { copy.root, sha256 = Digest(copy.bytes), matchesCapturedSource = capturedSource.SequenceEqual(copy.bytes) }).ToArray()
                }));
                Assert.That(after, Is.EqualTo(capturedSource), "本次测试不得改写正式基础文案。");
                foreach (var copy in copies) Assert.That(copy.bytes, Is.EqualTo(capturedSource), "私有副本必须保持当次源字节：" + copy.root);
            }
        }

        [TestCase("text")] [TestCase("image")] [TestCase("declaration")] [TestCase("version")]
        [TestCase("set")] [TestCase("registration")] [TestCase("filename")]
        public void EveryLoadedInputChangesRealActiveIdentityAndRejectsPinnedConfiguration(string variation)
        {
            string original = Copy("原身份"); var before = Configure(original); var activeBefore = ActiveContentCatalogBuilder.Build(before, Configuration());
            var replacements = new Dictionary<string, string>(); var binary = new Dictionary<string, byte[]>();
            if (variation == "text" || variation == "declaration")
            { var doc = Document("alpha"); if (variation == "text") doc["messages"]["alpha:alt"]["template"] = "变化"; else doc["icons"][0]["monochrome"] = true; replacements["registry/alpha/texts.json"] = doc.ToString(); }
            if (variation == "image") binary["registry/alpha/icon.png"] = Picture(4, 6);
            if (variation == "version") { var source = Manifest("alpha", "texts.json", "extra.json"); source["version"] = "2"; replacements["registry/alpha/presentation.json"] = source.ToString(); }
            if (variation == "set") replacements["registry/sources.json"] = "{\"schemaVersion\":1,\"sources\":[]}";
            if (variation == "registration") replacements["registry/sources.json"] = "{\"schemaVersion\":1, \"sources\":[\"alpha\"]}";
            if (variation == "filename") { replacements["registry/alpha/presentation.json"] = Manifest("alpha", "renamed.json", "extra.json").ToString(); replacements["registry/alpha/renamed.json"] = Document("alpha").ToString(); }
            string next = artifacts.CopyContent(original, "改变后的完整输入", replacements, binary);
            var after = Configure(next); var activeAfter = ActiveContentCatalogBuilder.Build(after, Configuration());
            Assert.That(after.ContentHash, Is.Not.EqualTo(before.ContentHash)); Assert.That(after.PresentationContentHash, Is.Not.EqualTo(before.PresentationContentHash));
            Assert.That(activeAfter.ContentPackHash, Is.Not.EqualTo(activeBefore.ContentPackHash));
            Assert.That(after.Presentation.ContentHash, Is.EqualTo(after.ContentHash)); Assert.That(activeAfter.TextCatalog.ContentHash, Is.EqualTo(activeAfter.ContentPackHash));
            var pinned = Configuration(); pinned.ContentPackHash = activeBefore.ContentPackHash;
            Assert.That(Assert.Throws<InvalidOperationException>(() => ActiveContentCatalogBuilder.Build(after, pinned)).Message, Is.EqualTo("content_version_mismatch"));
            artifacts.WriteLabeledText("身份变化", JsonConvert.SerializeObject(new { variation, before = before.ContentHash, after = after.ContentHash,
                presentationBefore = before.PresentationContentHash, presentationAfter = after.PresentationContentHash, activeBefore = activeBefore.ContentPackHash, activeAfter = activeAfter.ContentPackHash }));
        }

        [Test] public void SourceOrderAndRootRelocationAreStableButResourcesCannotBeMutated()
        {
            string root = Copy("位置一", second: true); var first = ExternalContentPack.Load(root, new[] { Path.Combine(root, "registry/alpha"), Path.Combine(root, "registry/beta") });
            var reversed = ExternalContentPack.Load(root, new[] { Path.Combine(root, "registry/beta"), Path.Combine(root, "registry/alpha") });
            Assert.That(first.ContentHash, Is.EqualTo(reversed.ContentHash));
            string moved = artifacts.CopyContent(root, "位置二"); var relocated = ExternalContentPack.Load(moved, new[] { Path.Combine(moved, "registry/alpha"), Path.Combine(moved, "registry/beta") });
            Assert.That(relocated.ContentHash, Is.EqualTo(first.ContentHash));
            var resources = (IGameplayIconResourceCatalog)first.Presentation; resources.TryGetIconResource("alpha:image", out var image);
            byte firstByte = image.CopyBytes()[0]; var copy = image.CopyBytes(); copy[0] ^= 0xff; Assert.That(image.CopyBytes()[0], Is.EqualTo(firstByte));
            var snapshot = first.CreateActiveSnapshot(Configuration());
            Assert.That(((IGameplayIconResourceCatalog)snapshot.Presentation).TryGetIconResource("alpha:image", out _), Is.True);
            Assert.That(snapshot.PresentationSources.Count, Is.EqualTo(2));
            artifacts.RecordContent("位置重排稳定", moved, relocated.ContentHash);
        }

        [TestCase("duplicate_property")] [TestCase("duplicate_text")] [TestCase("duplicate_icon")] [TestCase("duplicate_asset")]
        [TestCase("core_override")] [TestCase("foreign_asset")] [TestCase("same_pack")] [TestCase("missing_text")]
        [TestCase("missing_image")] [TestCase("traversal")] [TestCase("absolute")] [TestCase("backslash")]
        [TestCase("schema")] [TestCase("type")] [TestCase("unknown_field")] [TestCase("unknown_icon_field")]
        [TestCase("locale")] [TestCase("manifest_locale")] [TestCase("webp")] [TestCase("invalid_image")]
        [TestCase("unknown_icon")] [TestCase("parameter")] [TestCase("parameter_type")] [TestCase("bold")] [TestCase("unknown_tag")]
        [TestCase("registry_duplicate")] [TestCase("registry_escape")] [TestCase("registry_type")]
        [TestCase("big_schema")] [TestCase("icon_type")] [TestCase("stray_bracket")] [TestCase("device_path")] [TestCase("big_icon_number")]
        public void InvalidInputsFailWithSourceDiagnosticAndDoNotReplaceCurrent(string failure)
        {
            var text = Inputs(); var doc = Document("alpha"); var source = Manifest("alpha", "texts.json", "extra.json");
            switch (failure)
            {
                case "duplicate_property": text["registry/alpha/texts.json"] = "{\"schemaVersion\":2,\"schemaVersion\":2}"; break;
                case "duplicate_text": text["registry/alpha/extra.json"] = doc.ToString(); break;
                case "duplicate_icon": var dupIcon = (JObject)doc.DeepClone(); dupIcon["messages"] = new JObject(); dupIcon["assets"] = new JObject(); text["registry/alpha/extra.json"] = dupIcon.ToString(); break;
                case "duplicate_asset": var dupAsset = (JObject)doc.DeepClone(); dupAsset["messages"] = new JObject(); dupAsset["icons"] = new JArray(); text["registry/alpha/extra.json"] = dupAsset.ToString(); break;
                case "core_override": ((JObject)doc["messages"]).Add("core:presentation.unavailable", Message("覆盖")); break;
                case "foreign_asset": doc["icons"][0]["assetKey"] = "core:resource.score.asset"; break;
                case "same_pack": text["registry/sources.json"] = "{\"schemaVersion\":1,\"sources\":[\"alpha\",\"alpha2\"]}"; text["registry/alpha2/presentation.json"] = source.ToString(); break;
                case "missing_text": source["texts"] = new JArray("missing.json"); break;
                case "missing_image": doc["assets"]["alpha:image"] = "missing.png"; break;
                case "traversal": doc["assets"]["alpha:image"] = "../icon.png"; break;
                case "absolute": doc["assets"]["alpha:image"] = "C:/icon.png"; break;
                case "backslash": doc["assets"]["alpha:image"] = "folder\\icon.png"; break;
                case "schema": doc["schemaVersion"] = 4; break;
                case "type": doc["icons"] = new JObject(); break;
                case "unknown_field": doc["accidental"] = true; break;
                case "unknown_icon_field": doc["icons"][0]["accidental"] = true; break;
                case "locale": doc["locale"] = "en"; break;
                case "manifest_locale": source["locale"] = "en"; break;
                case "webp": doc["assets"]["alpha:image"] = "icon.webp"; break;
                case "unknown_icon": doc["messages"]["alpha:body"]["template"] = "[icon:alpha:missing]"; break;
                case "parameter": doc["messages"]["alpha:body"]["template"] = "{missing}"; break;
                case "parameter_type": doc["messages"]["alpha:body"]["parameters"]["amount"] = "floating"; break;
                case "bold": doc["messages"]["alpha:body"]["template"] = "[b]未闭合"; break;
                case "unknown_tag": doc["messages"]["alpha:body"]["template"] = "[typo]"; break;
                case "registry_duplicate": text["registry/sources.json"] = "{\"schemaVersion\":1,\"sources\":[\"alpha\",\"alpha\"]}"; break;
                case "registry_escape": text["registry/sources.json"] = "{\"schemaVersion\":1,\"sources\":[\"../alpha\"]}"; break;
                case "registry_type": text["registry/sources.json"] = "{\"schemaVersion\":1,\"sources\":7}"; break;
                case "big_schema": doc["schemaVersion"] = long.MaxValue; break;
                case "icon_type": doc["icons"] = new JArray(7); break;
                case "stray_bracket": doc["messages"]["alpha:body"]["template"] = "错误]"; break;
                case "device_path": doc["assets"]["alpha:image"] = "NUL.png"; break;
                case "big_icon_number": doc["icons"][0]["monochrome"] = JToken.Parse("9223372036854775808"); break;
            }
            if (failure != "duplicate_property") text["registry/alpha/texts.json"] = doc.ToString();
            text["registry/alpha/presentation.json"] = source.ToString();
            string good = Copy("正确对照"); var previous = Configure(good);
            string root = Copy("错误完整输入", text, image: failure == "invalid_image" ? new byte[] { 1, 2, 3 } : null);
            var error = Assert.Throws<InvalidDataException>(() => Configure(root));
            Assert.That(error.Message, Does.Contain("presentation"));
            Assert.That(error.Message, Does.Contain(failure.StartsWith("registry", StringComparison.Ordinal) ? failure == "registry_escape" ? "../alpha" : "sources.json" : failure == "same_pack" ? "alpha" : failure == "missing_text" ? "missing.json" : failure == "manifest_locale" ? "alpha" : "alpha"));
            Assert.That(Runtime.GetProperty("Pack").GetValue(null), Is.SameAs(previous));
            artifacts.WriteLabeledText("拒绝诊断", JsonConvert.SerializeObject(new { failure, exception = error.ToString(), previousHash = previous.ContentHash, currentUnchanged = true }));
        }

        [Test] public void SingleLanguageCombinationSupportsExistingLocaleSelectionAndSchema1LiteralTags()
        {
            var inputs = Inputs(); var manifest = JObject.Parse(File.ReadAllText(Path.Combine(Installed, "pack.json")));
            foreach (string relative in ((JArray)manifest["presentation"]["texts"]).Values<string>())
            { var doc = JObject.Parse(File.ReadAllText(Path.Combine(Installed, relative))); doc["locale"] = "en"; inputs[relative] = doc.ToString(); }
            foreach (string path in new[] { "registry/alpha/presentation.json", "registry/alpha/texts.json", "registry/alpha/extra.json" })
            { var doc = JObject.Parse(inputs[path]); doc["locale"] = "en"; inputs[path] = doc.ToString(); }
            var legacy = JObject.Parse(inputs["registry/alpha/extra.json"]); legacy["schemaVersion"] = 1; legacy["messages"]["alpha:extra"]["template"] = "[unknown] [[b]]"; inputs["registry/alpha/extra.json"] = legacy.ToString();
            var pack = Configure(Copy("单语言en", inputs)); Assert.That(pack.Presentation.Locale, Is.EqualTo("en"));
            Assert.That(new GameplayTextResolver(pack.Presentation).Resolve(new GameplayTextRef("alpha:extra"), new GameplayTextContext { Locale = "en" }).ToPlainText(), Is.EqualTo("[unknown] [b]"));
            var strict = Resolve(pack.Presentation); Assert.That(strict.Runs.OfType<IconRun>().Count(), Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => new GameplayTextResolver(pack.Presentation).Resolve(new GameplayTextRef("alpha:extra"), new GameplayTextContext { Locale = "zh-CN" }));
        }

        [Test] public void ActualBootstrapAndHostRecoveryUseTheCombinedContentIdentity()
        {
            string root = Copy("正式启动输入"); var original = Configure(root);
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var bootstrapType = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var contextObject = new GameObject("隔离启动上下文"); contextObject.SetActive(false);
            var context = contextObject.AddComponent(contextType);
            try
            {
                var seats = new List<PlayerSeat> { new PlayerSeat { PlayerId = 1, PlayerName = "甲", Color = PlayerColor.Red }, new PlayerSeat { PlayerId = 2, PlayerName = "乙", Color = PlayerColor.Blue } };
                Call(contextType, context, "ConfigureNewLocal", 1, "", seats, Configuration(), null, null);
                object result = Call(bootstrapType, null, "Build", context, false, false);
                var session = (GameSession)result.GetType().GetField("Session").GetValue(result);
                var registry = (EffectRegistry)result.GetType().GetField("EffectRegistry").GetValue(result);
                var catalog = (IGameplayTextCatalogSnapshot)result.GetType().GetField("GameplayTextCatalog").GetValue(result);
                var providerType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider")).First(t => t != null);
                var provider = Activator.CreateInstance(providerType);
                Assert.That(Call(providerType, provider, "GetContentPackHash", Configuration()), Is.EqualTo(catalog.ContentHash));
                Assert.That(catalog.TryGetTemplate("alpha:body", out _), Is.True);
                Assert.That(catalog.ContentHash, Is.EqualTo(session.State.ContentPackHash));
                string archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(session.State));
                artifacts.WriteLabeledText("正式启动原包", archive);
                var control = HostRecoveryService.RecoverJson(archive, registry); Assert.That(control.Succeeded, Is.True, control.Diagnostic);
                var save = YC.Tests.EditMode.MatchSaveResumeTests.FromSession(session, LaunchMode.Local, 1); save.Validate();
                artifacts.WriteLabeledText("正式MatchSave输入", JsonConvert.SerializeObject(save));
                Assert.That(Call(bootstrapType, null, "Restore", null, save), Is.Not.Null);
                var replacement = Document("alpha"); replacement["messages"]["alpha:alt"]["template"] = "身份变化";
                string next = artifacts.CopyContent(root, "显示变更后的启动输入", new Dictionary<string, string> { ["registry/alpha/texts.json"] = replacement.ToString() });
                var after = Configure(next); var otherActive = ActiveContentCatalogBuilder.Build(after, Configuration());
                var otherRegistry = new EffectRegistry(); otherActive.Register(otherRegistry);
                var rejected = HostRecoveryService.RecoverJson(archive, otherRegistry);
                Assert.That(rejected.Succeeded, Is.False); Assert.That(rejected.FaultCode, Is.EqualTo(HostRecoveryService.ContentHashMismatch));
                Assert.That(Call(providerType, provider, "GetContentPackHash", Configuration()), Is.EqualTo(otherActive.ContentPackHash));
                var pinned = Configuration(); pinned.ContentPackHash = catalog.ContentHash;
                object[] validation = { pinned, null };
                Assert.That(Call(providerType, provider, "TryValidate", validation), Is.EqualTo(false));
                Assert.That(validation[1], Is.EqualTo("content_version_mismatch"));
                var saveError = Assert.Throws<InvalidOperationException>(() => Call(bootstrapType, null, "Restore", null, save));
                Assert.That(saveError.Message, Is.EqualTo("content_version_mismatch"));
                artifacts.WriteLabeledText("正式恢复身份门禁", JsonConvert.SerializeObject(new { gameId = session.State.GameId, originalHash = session.State.ContentPackHash,
                    changedHash = otherActive.ContentPackHash, controlSucceeded = control.Succeeded, rejected.Succeeded, rejected.FaultCode, rejected.Diagnostic,
                    matchSaveControlRestored = true, matchSaveMismatch = saveError.Message, networkProviderMismatch = validation[1] }));
            }
            finally { UnityEngine.Object.DestroyImmediate(contextObject); }
        }

        [Test] public void SamePackResourcesAndAltTextsCanBeDeclaredInLaterFile()
        {
            var inputs = Inputs(); var first = Document("alpha");
            var later = new JObject { ["schemaVersion"] = 2, ["locale"] = "zh-CN", ["messages"] = new JObject {
                ["alpha:alt"] = first["messages"]["alpha:alt"].DeepClone(), ["alpha:value"] = first["messages"]["alpha:value"].DeepClone() },
                ["assets"] = first["assets"].DeepClone() };
            ((JObject)first["messages"]).Remove("alpha:alt"); ((JObject)first["messages"]).Remove("alpha:value"); first.Remove("assets");
            first["messages"]["alpha:body"]["template"] = "[[b]] [b]{name}[/b][icon:alpha:probe|value={amount}]";
            inputs["registry/alpha/texts.json"] = first.ToString(); inputs["registry/alpha/extra.json"] = later.ToString();
            var pack = Configure(Copy("跨文件资源后声明", inputs)); Assert.That(Resolve(pack.Presentation).ToPlainText(), Does.StartWith("[b]"));
            var resolver = new GameplayTextResolver(pack.Presentation); var diagnostics = new List<GameplayTextFormatException>(); resolver.Diagnostic += diagnostics.Add;
            var fallback = resolver.Resolve(new GameplayTextRef("alpha:missing"), new GameplayTextContext());
            Assert.That(fallback.ToPlainText(), Is.Not.Empty); Assert.That(diagnostics.Count, Is.EqualTo(1));
            artifacts.WriteLabeledText("晚声明与兜底", JsonConvert.SerializeObject(new { valid = true, fallback = fallback.ToPlainText(), diagnostic = diagnostics[0].Message }));
        }

        [Test] public void UnregisteredInputsDoNotLoadAndReloadReleasesRuntimeImages()
        {
            string root = Copy("登记资源"); var first = Configure(root);
            first.Presentation.TryGetIcon("alpha:probe", out var icon);
            var toneType = Type.GetType("YC.Presentation.GameplayIconTone, Assembly-CSharp", true);
            object[] arguments = { first.Presentation, null, icon, Enum.Parse(toneType, "Dark"), null };
            Assert.That(Call(Bridge, null, "TryResolve", arguments), Is.EqualTo(true)); var sprite = (Sprite)arguments[4]; var texture = sprite.texture;
            string noise = artifacts.CopyContent(root, "未登记文件", new Dictionary<string, string> { ["unregistered/presentation.json"] = "这不是JSON，不应被扫描" });
            var second = Configure(noise); Assert.That(second.ContentHash, Is.EqualTo(first.ContentHash));
            Assert.That(sprite == null && texture == null, Is.True);
            arguments[0] = second.Presentation; Call(Bridge, null, "TryResolve", arguments); sprite = (Sprite)arguments[4]; texture = sprite.texture;
            Call(Runtime, null, "Reload"); Assert.That(sprite == null && texture == null, Is.True);
            artifacts.WriteLabeledText("重载和未登记输入", JsonConvert.SerializeObject(new { first.ContentHash, ignoredUnregistered = true, replacementReleased = true, reloadReleased = true }));
        }

        [Test] public void ReparseDirectoryIsRejectedBeforeAnyExternalFileIsRead()
        {
            string root = Copy("重解析点输入");
            string target = Path.GetFullPath(Path.Combine(root, "registry", "alpha")), link = Path.GetFullPath(Path.Combine(root, "registry", "junction"));
            Assert.That(Path.GetFullPath(link).StartsWith(Path.GetFullPath(artifacts.InputsDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);
            Assert.That(Directory.Exists(target) && !Directory.Exists(link) && !File.Exists(link), Is.True);
            // 只创建本用例明确私有junction；不删除，不枚举链接，不读外部目录。
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + target + "\"") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = System.Diagnostics.Process.Start(start))
            { string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd(); process.WaitForExit(); Assert.That(process.ExitCode, Is.Zero, output + error); }
            Assert.That(File.GetAttributes(link) & FileAttributes.ReparsePoint, Is.EqualTo(FileAttributes.ReparsePoint));
            artifacts.WriteLabeledText("故意非法输入拓扑", JsonConvert.SerializeObject(new { link, target, attributes = File.GetAttributes(link).ToString(), note = "封存普通文件后仅新增此非法junction，原字节未改；保留现场，不遍历链接" }));
            var rejected = Assert.Throws<InvalidDataException>(() => ExternalContentPack.Load(root, new[] { link }));
            Assert.That(rejected.Message, Does.Contain("presentation_reparse_point")); Assert.That(rejected.Message, Does.Contain(link));
            // 包内文件路径也沿同一祖先检查；不允许清单根安全而图像子目录越界。
            var safe = Configure(root);
            var storeType = typeof(ExternalContentPack).Assembly.GetType("YC.Infrastructure.Lua.PresentationFileStore", true);
            var method = storeType.GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic);
            var errorResult = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { root, "registry/junction/icon.png" }));
            Assert.That(errorResult.InnerException, Is.TypeOf<InvalidDataException>());
            artifacts.WriteLabeledText("重解析点拒绝", JsonConvert.SerializeObject(new { sourceError = rejected.Message, imageError = errorResult.InnerException.Message, safe.ContentHash }));
        }

        [Test] public void OfficialIncrementCannotShadowExistingCoreThemeAssetKey()
        {
            string good = Copy("核心主题正确对照"); var previous = Configure(good);
            previous.Presentation.TryGetIcon("core:resource.score", out var core);
            Assert.That(core.AssetKey, Is.Not.Empty);
            var inputs = Inputs();
            inputs["presentation/entrepreneurs/T.zh-CN.json"] = new JObject {
                ["schemaVersion"] = 2, ["locale"] = "zh-CN", ["messages"] = new JObject(),
                ["assets"] = new JObject { [core.AssetKey] = "registry/alpha/icon.png" } }.ToString();
            string root = Copy("官方增量企图重映主题", inputs);
            var rejected = Assert.Throws<InvalidDataException>(() => Configure(root));
            Assert.That(rejected.Message, Does.Contain("presentation_theme_asset_shadow"));
            Assert.That(rejected.Message, Does.Contain("presentation/entrepreneurs/T.zh-CN.json"));
            Assert.That(rejected.Message, Does.Contain(core.AssetKey));
            Assert.That(Runtime.GetProperty("Pack").GetValue(null), Is.SameAs(previous));
            artifacts.WriteLabeledText("核心主题资源键保护", JsonConvert.SerializeObject(new { assetKey = core.AssetKey, rejected = rejected.Message, currentUnchanged = true }));
        }
    }
}
