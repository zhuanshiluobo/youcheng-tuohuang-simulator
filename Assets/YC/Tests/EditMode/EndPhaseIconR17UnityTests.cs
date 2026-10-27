using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Tests.EditMode.ExpansionRules;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    // R17专有隔离组件验证。只实例化实际Prefab，不保存资产；不计正常菜单/对局验收。
    public sealed class EndPhaseIconR17UnityTests
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const string HudPath = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        private const string ChipPath = "Assets/YC/Presentation/Settlement/Prefabs/SettlementEffectChip.prefab";
        private SceneRuntimeCatalogScope catalogs;
        private static Type Production(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value) ?? value.GetType().GetProperty(name, Flags)?.GetValue(value);
        private static void Set(object value, string name, object field) => value.GetType().GetField(name, Flags).SetValue(value, field);
        private static object Call(object value, string name, params object[] args)
        {
            try { return value.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException ?? e).Throw(); throw; }
        }
        private static string Output => Environment.GetEnvironmentVariable("QY017_R17_OUTPUT") ?? throw new InvalidOperationException("R17输出目录未指定");
        private static void Save(string name, object value)
        {
            using (var file = new FileStream(Path.Combine(Output, name + ".json"), FileMode.CreateNew))
            using (var writer = new StreamWriter(file)) writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented, new UnityValueConverter()));
        }
        private sealed class UnityValueConverter : JsonConverter
        {
            public override bool CanRead => false;
            public override bool CanConvert(Type type) => type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Rect) || type == typeof(Color);
            public override object ReadJson(JsonReader r, Type t, object value, JsonSerializer s) => throw new NotSupportedException();
            public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
            {
                float[] fields = value is Vector2 v2 ? new[] { v2.x, v2.y } : value is Vector3 v3 ? new[] { v3.x, v3.y, v3.z } :
                    value is Vector4 v4 ? new[] { v4.x, v4.y, v4.z, v4.w } : value is Rect rect ? new[] { rect.x, rect.y, rect.width, rect.height } :
                    value is Color color ? new[] { color.r, color.g, color.b, color.a } : throw new NotSupportedException();
                s.Serialize(w, fields);
            }
        }
        private static object AssemblyIdentity(Assembly assembly)
        {
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(assembly.Location)) hash = string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
            return new { assembly.FullName, assembly.Location, mvid = assembly.ManifestModule.ModuleVersionId, sha256 = hash };
        }
        [UnityTearDown] public IEnumerator LeavePlayMode()
        {
            if (UnityEngine.Application.isPlaying) yield return new ExitPlayMode();
            catalogs?.Dispose(); catalogs = null;
        }

        [Test] public void ImportedSpritesUseActualUnityResourcesAndSettings()
        {
            var actual = JObject.Parse(File.ReadAllText("游城拓荒/可用UI素材/参数/项目实际用法/收尾阶段结算图标参数.json"));
            var evidence = new List<object>();
            foreach (var entry in actual["assets"])
            {
                string path = (string)entry["unityAsset"];
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                Assert.That(importer, Is.Not.Null, path);
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                Assert.That(sprite, Is.Not.Null, path);
                var settings = new TextureImporterSettings(); importer.ReadTextureSettings(settings);
                var platform = importer.GetDefaultPlatformTextureSettings();
                evidence.Add(new { path, guid = AssetDatabase.AssetPathToGUID(path), sprite.rect, sprite.pixelsPerUnit, sprite.border,
                    importer.textureType, importer.spriteImportMode, importer.spritePixelsPerUnit, importer.sRGBTexture,
                    importer.alphaIsTransparency, importer.mipmapEnabled, importer.filterMode, importer.wrapMode,
                    importer.textureCompression, settings.spriteMeshType, platform });
                Assert.That(importer.textureType, Is.EqualTo(TextureImporterType.Sprite));
                Assert.That(importer.spriteImportMode, Is.EqualTo(SpriteImportMode.Single));
                Assert.That(sprite.rect.size, Is.EqualTo(new Vector2(48, 48))); Assert.That(sprite.pixelsPerUnit, Is.EqualTo(200));
                Assert.That(sprite.border, Is.EqualTo(Vector4.zero)); Assert.That(importer.mipmapEnabled, Is.False);
                Assert.That(importer.alphaIsTransparency && importer.sRGBTexture, Is.True);
                Assert.That(settings.spriteMeshType, Is.EqualTo(SpriteMeshType.FullRect));
                Assert.That(importer.filterMode, Is.EqualTo(FilterMode.Bilinear)); Assert.That(importer.wrapMode, Is.EqualTo(TextureWrapMode.Clamp));
                Assert.That(importer.textureCompression, Is.EqualTo(TextureImporterCompression.Uncompressed));
            }
            Assert.That(evidence.Count, Is.EqualTo(15));
            Save("Unity实际导入", new { resources = evidence, production = AssemblyIdentity(Production("SettlementEffectChipView").Assembly), tests = AssemblyIdentity(GetType().Assembly), domain = AssemblyIdentity(typeof(GameState).Assembly) });
        }

        [UnityTest] public IEnumerator P03ActualChip() => CheckSource("P03");
        [UnityTest] public IEnumerator P04ActualChip() => CheckSource("P04");
        [UnityTest] public IEnumerator T09ActualChip() => CheckSource("T09");
        [UnityTest] public IEnumerator D09ActualChip() => CheckSource("D09");
        [UnityTest] public IEnumerator D01ActualChip() => CheckSource("D01");
        [UnityTest] public IEnumerator D02ActualChip() => CheckSource("D02");
        [UnityTest] public IEnumerator A05ActualChip() => CheckSource("A05");
        [UnityTest] public IEnumerator A08UsesOriginalRequiredEntryWithoutM04Icon() => CheckSource("A08");

        private sealed class Scenario : IDisposable
        {
            internal GameState State; internal EffectRegistry Registry; internal ExternalContentPack Pack;
            internal IGameplayTextCatalogSnapshot Text; internal Action Pump, Close; internal CharacterSettlementOption Option;
            public void Dispose() => Close?.Invoke();
        }
        private static Scenario Prepare(string source)
        {
            Scenario scenario;
            if (source.StartsWith("P", StringComparison.Ordinal))
            {
                var f = new PaulContentTestFixture();
                scenario = new Scenario { Close = f.Dispose };
                try
                {
                    f.Choose(3, source); f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker");
                    // 与现有U10夹具相同的隔离收尾前提，停在登记后；受测入口不由夹具提交。
                    f.FinishPreparation(); f.State.Round = 3; f.State.Phase = GamePhase.Cleanup; f.State.ActionRound = 1;
                    OpenPaulWindow(f);
                    scenario.State = f.State; scenario.Registry = f.Registry; scenario.Pack = f.Pack;
                    scenario.Text = f.Catalog.TextCatalog; scenario.Pump = f.Pump;
                }
                catch { scenario.Dispose(); throw; }
            }
            else if (source == "T09")
            {
                var artifacts = IsolatedTestArtifacts.BeginCase(); var f = new FifthNumberContentTestFixture(artifacts);
                scenario = new Scenario { Close = () => { f.Snapshot("R17最终隔离状态"); artifacts.Finish(); } };
                try { f.CollectOne(); f.OpenCleanup(); scenario.State = f.State; scenario.Registry = f.Registry; scenario.Pack = f.Pack; scenario.Pump = f.Pump; }
                catch { scenario.Dispose(); throw; }
            }
            else if (source.StartsWith("D", StringComparison.Ordinal))
            {
                var f = new TiagoInvestmentTestFixture(); scenario = new Scenario { Close = f.Dispose };
                try
                {
                    f.CreateSelectedHost(new[] { source == "D09" ? "D01" : source }, initialReputation: 6); f.OpenSelectedWindow(true);
                    scenario.State = f.State; scenario.Registry = f.Registry; scenario.Pack = f.Pack; scenario.Pump = f.Drain;
                }
                catch { scenario.Dispose(); throw; }
            }
            else
            {
                var f = new AgnesContentTestFixture(); scenario = new Scenario { Close = f.Dispose };
                try
                {
                    f.Formal(); f.Choose(1, "A08", source == "A08" ? "2" : "1");
                    if (source == "A08") { f.Invoke("entrepreneur.A08.tactic.quick"); f.ResolveMapRequests(); }
                    else f.Choose(5, "A05");
                    f.Cleanup(); scenario.State = f.State; scenario.Registry = f.Registry; scenario.Pack = f.Pack; scenario.Pump = f.Pump;
                }
                catch { scenario.Dispose(); throw; }
            }
            // 现有T夹具保存的是装配前pack。只给同一文本目录绑定真实活动哈希，不更改游戏状态/规则。
            if (scenario.Text == null) scenario.Text = (IGameplayTextCatalogSnapshot)Call(scenario.Pack.Presentation, "WithContentHash", scenario.State.ContentPackHash);
            var options = scenario.Registry.GetSettlementOptions(scenario.State, 1);
            scenario.Option = options.Single(o => source == "A08" ? o.LocalId == CleanupObligation.SettlementLocalId :
                o.Presentation?.IconKey == Key(source) && (source == "D09" || source == "T09" || o.LocalId.Contains(source)));
            return scenario;
        }
        private static string Key(string source) => "core:settlement." + (source.StartsWith("P", StringComparison.Ordinal) ? "outpost_payout" :
            source == "T09" ? "marker_redeem" : source == "D09" ? "fund_invest" : source.StartsWith("D", StringComparison.Ordinal) ? "reputation_score" : "tactical_dispatch");
        private static void OpenPaulWindow(PaulContentTestFixture f)
        {
            var chain = new RoundMainChainBuilder().Build(f.State.GameId, "", 3, f.State.LaunchPlayerOrder, 1);
            var main = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && n.PlayerId == 1);
            f.State.EffectRuntime.MainNodes.AddRange(chain.Nodes);
            foreach (var previous in chain.Nodes.Where(n => n.MainlineIndex < main.MainlineIndex)) previous.Status = EffectNodeStatus.Completed;
            f.State.EffectRuntime.ActiveMainNodeId = main.NodeId; f.State.EffectRuntime.FirstMainNodeId = chain.FirstNodeId;
            f.State.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId; f.State.EffectRuntime.CurrentRoundNumber = 3;
            f.State.EffectRuntime.CurrentRoundStartPlayerId = 1; f.State.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            main.ExecutionEffectId = f.Engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId, Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1));
            main.Status = EffectNodeStatus.Running; f.Pump();
        }
        private IEnumerator CheckSource(string source)
        {
            catalogs = new SceneRuntimeCatalogScope(); yield return new EnterPlayMode();
            // 组件场景不含正式启动器；按这些启动器的原入口装载目录，而不是依赖EditMode兼容静态表。
            var installed = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            CharacterCardDatabase.Initialize(installed.CreateCharacters()); EventCardDatabase.InitializeExternal(installed.CreateEvents());
            FacilityCardDatabase.InitializeExternal(installed.CreateFacilities()); CityStyleDatabase.InitializeExternal(installed.CreateCityStyles());
            SpecialActionDatabase.InitializeExternal(installed.CreateSpecialActions());
            Production("UiTheme").GetMethod("Initialize", Flags).Invoke(null, new object[] {
                AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", Production("UiThemeCatalog")) });
            using (var scenario = Prepare(source))
            {
                var session = new GameSession(scenario.State); session.RegisterHandler(new InvokeSettlementOptionCommandHandler(scenario.Registry));
                var view = session.ProjectFor(GameStateViewer.Player(1));
                // 实际查询注册器选项再投影，保证HasAbilityActionCatalog由正式provider提供。
                var projected = view.CharacterSettlementOptions.Single(o => o.OptionId == scenario.Option.OptionId);
                Assert.That(projected.IsEnabled && projected.VisibleToViewer, Is.True, projected.DisabledReasonCode);
                var option = SettlementOptionView.FromOption(projected);
                Save(source + "-点击前", new { state = scenario.State, option, catalogHash = scenario.Text.ContentHash,
                    boundary = "现有规则夹具准备收尾窗；不计正常开局/真实屏幕命中" });
                var hudObject = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(HudPath));
                try
                {
                    var hud = hudObject.GetComponent(Production("GameplayInteractionHudView"));
                    var frame = Read(hud, "Frame"); var expansion = Read(hud, "ExpansionUi");
                    Call(hud, "ConfigureGameplayPresentation", scenario.Text, new Dictionary<string, string>());
                    var commands = new List<GameCommand>();
                    Call(expansion, "Configure", new Func<GameStateView>(() => view), new Func<int>(() => 1), null,
                        new Dictionary<string, CharacterModeContract>(), new Action<GameCommand>(commands.Add));
                    // 正常消费者先Refresh记录当前观察者；只调用失效通知会被LateUpdate按旧观察者清空。
                    Call(expansion, "Refresh");
                    Call(expansion, "InvalidateSettlementAvailability");
                    var strip = Read(expansion, "settlementStrip"); Set(strip, "reducedMotion", true);
                    // 实际HUD所用模板来源，不用另一份预制体替代现场覆盖。
                    var template = (Component)Read(strip, "chipTemplate");
                    var templateAsset = PrefabUtility.GetCorrespondingObjectFromSource(template.gameObject) ?? template.gameObject;
                    string templatePath = AssetDatabase.GetAssetPath(templateAsset);
                    Save(source + "-实际模板引用", new { templatePath, guid = AssetDatabase.AssetPathToGUID(templatePath),
                        templateIsAsset = !template.gameObject.scene.IsValid(), hudPath = HudPath, hudGuid = AssetDatabase.AssetPathToGUID(HudPath) });
                    Assert.That(templatePath, Is.EqualTo(ChipPath));
                    var chips = (IDictionary)Read(strip, "chips"); var chip = (Component)chips[option.OptionId];
                    Assert.That(chip, Is.Not.Null); Call(chip, "PrepareArrival", true);
                    yield return null; Canvas.ForceUpdateCanvases();
                    if (source != "A08")
                    {
                        Assert.That(option.Presentation.IconKey, Is.EqualTo(Key(source)));
                        Assert.That(option.Presentation.ContentMode, Is.EqualTo("icon_text"));
                        yield return CaptureStates(source, option, frame, template);
                    }
                    else
                    {
                        Assert.That(option.CleanupRequest, Is.Null); Assert.That(option.CanDeclineWholeEffect, Is.False);
                        Assert.That(option.Presentation.IconKey, Is.Not.EqualTo(Key("A05")));
                    }
                    var events = EventSystem.current ?? new GameObject("R17隔离指针系统", typeof(EventSystem)).GetComponent<EventSystem>();
                    var pointer = new PointerEventData(events) { pointerId = 17, button = PointerEventData.InputButton.Left, position = Vector2.zero };
                    var ancestors = new List<object>(); for (var parent = chip.transform; parent != null; parent = parent.parent)
                        ancestors.Add(new { parent.name, parent.gameObject.activeSelf, parent.gameObject.activeInHierarchy });
                    bool down = ExecuteEvents.Execute(chip.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                    bool up = ExecuteEvents.Execute(chip.gameObject, pointer, ExecuteEvents.pointerUpHandler);
                    bool click = ExecuteEvents.Execute(chip.gameObject, pointer, ExecuteEvents.pointerClickHandler);
                    Save(source + "-指针诊断", new { down, up, click, ancestors, chipEnabled = ((Behaviour)chip).enabled,
                        allowed = Read(chip, "executionAllowed"), pending = Read(chip, "pending"),
                        suspended = Production("GameplayHudFrame").GetProperty("EffectInputSuspended", Flags).GetValue(null),
                        currentGuard = Call(expansion, "SettlementCurrent", option, true), view.StateRevision, optionRevision = option.StateRevision,
                        commandCount = commands.Count });
                    Assert.That(commands.Count, Is.EqualTo(1), "实际芯片→条目租约→原InvokeSettlement只提交一次");
                    var command = commands.Single();
                    Assert.That(command.Kind, Is.EqualTo(GameCommandKind.InvokeSettlementOption));
                    if (option.CleanupRequest != null) Assert.That(command.Parameters["intent"], Is.EqualTo(CleanupSourceCommand.Intent));
                    var result = session.Submit(command); Assert.That(result.Succeeded, Is.True, result.Validation?.Reason); scenario.Pump();
                    ExecuteEvents.Execute(chip.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                    ExecuteEvents.Execute(chip.gameObject, pointer, ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.Execute(chip.gameObject, pointer, ExecuteEvents.pointerClickHandler);
                    Assert.That(commands.Count, Is.EqualTo(1), "处理中重复点击不得再次提交");
                    var open = scenario.State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open").ToArray();
                    if (source == "A08")
                    {
                        Assert.That(open, Is.Not.Empty); Assert.That(open.All(r => !r.AllowDecline), Is.True);
                    }
                    Save(source + "-点击结果", new { command, result, open, state = scenario.State,
                        chain = "真实投影→实际HUD/Strip/Chip→PointerDown/Up/Click→InvokeSettlement→原GameSession.Submit；隔离事件派发，非正常屏幕命中",
                        production = AssemblyIdentity(Production("SettlementEffectChipView").Assembly), tests = AssemblyIdentity(GetType().Assembly) });
                }
                finally { UnityEngine.Object.Destroy(hudObject); }
            }
            yield return null;
        }

        private static IEnumerator CaptureStates(string source, SettlementOptionView option, object frame, Component template)
        {
            var root = new GameObject("R17实际组件画布", typeof(RectTransform), typeof(Canvas));
            root.layer = 30;
            var cameraObject = new GameObject("R17组件相机", typeof(Camera));
            var canvas = root.GetComponent<Canvas>(); var camera = cameraObject.GetComponent<Camera>();
            var rt = new RenderTexture(3000, 280, 24, RenderTextureFormat.ARGB32); rt.Create();
            try
            {
                camera.orthographic = true; camera.orthographicSize = 70; camera.transform.position = new Vector3(0, 0, -100);
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.11f, .14f, .17f, 1);
                camera.cullingMask = 1 << 30; camera.targetTexture = rt;
                canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = camera;
                var rect = (RectTransform)root.transform; rect.sizeDelta = new Vector2(1500, 140);
                var resolver = new Func<GameplayTextRef, GameplayTextDocument>(r => (GameplayTextDocument)Call(frame, "ResolveGameplayText", r));
                var renderer = new Action<Component, GameplayTextDocument, bool>((c, d, single) => Call(frame, "RenderGameplayText", c, d, single));
                var measurement = new Func<Component, GameplayTextDocument, float>((c, d) => (float)Call(frame, "MeasureGameplayText", c, d));
                var icons = new Func<string, string, Sprite>((key, tone) => (Sprite)Call(frame, "ResolveGameplayIcon", key, tone));
                var states = new[] { "available", "hover", "pressed", "pending", "disabled" }; var evidence = new List<object>();
                var events = EventSystem.current ?? new GameObject("R17隔离指针系统", typeof(EventSystem)).GetComponent<EventSystem>();
                foreach (var state in states)
                {
                    int i = Array.IndexOf(states, state);
                    var chip = UnityEngine.Object.Instantiate(template, root.transform, false); chip.name = source + "-" + state;
                    Call(chip, "Bind", option, state == "pending", state != "disabled", 1L, 1L, 280f, resolver, renderer, measurement, icons,
                        new Action<long, long, int, string>((a, b, c, d) => Assert.Fail("状态截图不得提交")), null, null, null);
                    Call(chip, "PrepareArrival", true); chip.gameObject.SetActive(true);
                    var chipRect = (RectTransform)chip.transform;
                    chipRect.anchorMin = chipRect.anchorMax = chipRect.pivot = new Vector2(.5f, .5f);
                    chipRect.sizeDelta = new Vector2(((LayoutElement)Read(chip, "layout")).preferredWidth, 48);
                    chipRect.anchoredPosition = new Vector2(-600 + i * 300, 0);
                    var pointer = new PointerEventData(events) { pointerId = i + 1, position = Vector2.zero, button = PointerEventData.InputButton.Left };
                    if (state == "hover") ExecuteEvents.Execute(chip.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
                    if (state == "pressed") ExecuteEvents.Execute(chip.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                    foreach (var child in chip.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 30;
                    var icon = (Image)Read(chip, "icon"); var background = (Image)Read(chip, "background");
                    string tone = state == "pending" ? "light" : state == "disabled" ? "muted" : "dark";
                    Assert.That(icon.sprite, Is.SameAs(icons(option.Presentation.IconKey, tone)));
                    Assert.That(icon.preserveAspect && icon.type == Image.Type.Simple, Is.True);
                    Assert.That(icon.color, Is.EqualTo(Color.white));
                    string expectedBackground = state == "available" ? "defaultBackground" : state + "Background";
                    Assert.That(background.sprite, Is.SameAs(Read(chip, expectedBackground)));
                    evidence.Add(new { state, tone, spritePath = AssetDatabase.GetAssetPath(icon.sprite), spriteGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(icon.sprite)),
                        sourcePixels = icon.sprite.rect.size, iconRect = icon.rectTransform.rect.size, icon.type, icon.preserveAspect, tint = icon.color,
                        backgroundPath = AssetDatabase.GetAssetPath(background.sprite), chipWidth = chipRect.rect.width, chipHeight = chipRect.rect.height,
                        text = ((Component)Read(chip, "labelView")).GetComponent<Text>().text });
                }
                yield return null; Canvas.ForceUpdateCanvases();
                // 正式文字视图在绑定时生成子网格；统一设置截图专用层，不改资产。
                foreach (var child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 30;
                camera.Render();
                var old = RenderTexture.active; RenderTexture.active = rt;
                var png = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                try
                {
                    png.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); png.Apply();
                    using (var file = new FileStream(Path.Combine(Output, source + "-五态.png"), FileMode.CreateNew))
                    { var bytes = png.EncodeToPNG(); file.Write(bytes, 0, bytes.Length); }
                }
                finally { RenderTexture.active = old; UnityEngine.Object.Destroy(png); }
                Save(source + "-五态测量", new { option, order = states, screenshot = source + "-五态.png", canvasSize = rect.rect.size,
                    imageSize = new[] { rt.width, rt.height }, pixelsPerLogicalUnit = 2, states = evidence,
                    renderedTextRuns = root.GetComponentsInChildren<Text>(true).Where(t => !string.IsNullOrEmpty(t.text)).Select(t => new { t.name, t.text }).ToArray(),
                    note = "实际HUD引用的芯片模板内存实例；五态为组件隔离展示；未保存Prefab/Scene，未计正常游戏画面验收" });
            }
            finally { camera.targetTexture = null; rt.Release(); UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(root); UnityEngine.Object.Destroy(cameraObject); }
        }
    }
}
