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
using YC.Domain.Items;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    // R18专属原生组件复现。直接只读当前内容，不复制整包，不计正常菜单/对局验收。
    public sealed class EntrepreneurDetailStabilityR18Tests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const string HudPath = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        private const string RowPath = "Assets/YC/Presentation/Effects/Prefabs/EffectRow.prefab";
        private GameObject hudObject;
        private static Type Production(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object target, string name) => target.GetType().GetField(name, Flags)?.GetValue(target) ?? target.GetType().GetProperty(name, Flags)?.GetValue(target);
        private static object Call(object target, string name, params object[] args)
        {
            try { return target.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException ?? e).Throw(); throw; }
        }
        private static void Save(string name, object data)
        {
            string output = Environment.GetEnvironmentVariable("QY017_R18_OUTPUT") ?? throw new InvalidOperationException("缺少R18独占输出目录");
            using (var file = new FileStream(Path.Combine(output, name + ".json"), FileMode.CreateNew))
            using (var writer = new StreamWriter(file)) writer.Write(JsonConvert.SerializeObject(data, Formatting.Indented));
        }
        private static object Bounds(RectTransform rect) => new { x = rect.rect.x, y = rect.rect.y, width = rect.rect.width, height = rect.rect.height };
        private sealed class ReadLease : IDisposable
        {
            private Action close;
            public ReadLease(Action close) { this.close=close; }
            public void Dispose() { var action=close;close=null;action?.Invoke(); }
        }
        [SetUp] public void RecordNativeInputs()
        {
            var inputs = new[] { HudPath, RowPath, "Assets/YC/Presentation/Text/Content/GameplayTextStyle.asset",
                "Assets/YC/Presentation/Text/Content/GameplayIconCatalog.asset", "Assets/YC/Presentation/GameplayExpansionUi.cs",
                "Assets/YC/Presentation/Effects/UiEffectRowView.cs", "Assets/YC/Presentation/Text/GameplayTextView.cs",
                "Assets/YC/Tests/EditMode/EntrepreneurDetailStabilityR18Tests.cs", "Assets/StreamingAssets/Content/core/pack.json",
                "Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.ability.A01.json", "Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.ability.D09.json",
                "Assets/StreamingAssets/Content/core/presentation/entrepreneurs/A.zh-CN.json", "Assets/StreamingAssets/Content/core/presentation/entrepreneurs/D.zh-CN.json" };
            string Hash(string path)
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(path)) return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
            }
            Save(TestContext.CurrentContext.Test.Name + "-原生输入身份", new {
                files = inputs.Select(path => new { path, sha256 = Hash(path) }).ToArray(),
                loadedAssemblies = new[] { GetType().Assembly, Production("GameplayTextView").Assembly, typeof(GameStateView).Assembly }.Select(a =>
                    new { a.Location, mvid = a.ManifestModule.ModuleVersionId, sha256 = Hash(a.Location) }).ToArray(),
                note = "只读定向源身份与实际加载程序集；不复制core包；该SetUp只在真正执行测试时产生证据" });
        }
        private static void PointerClick(Button button)
        {
            Assert.That(button.gameObject.activeInHierarchy && button.interactable, Is.True, button.name + " 必须是真实可用原控件");
            var events = EventSystem.current ?? new GameObject("R18隔离指针", typeof(EventSystem)).GetComponent<EventSystem>();
            var pointer = new PointerEventData(events) { pointerId = 18, button = PointerEventData.InputButton.Left, position = Vector2.zero };
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerUpHandler);
            Assert.That(ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler), Is.True);
        }
        private object CreateHud(IGameplayTextCatalogSnapshot textCatalog)
        {
            Production("UiTheme").GetMethod("Initialize", Flags).Invoke(null, new object[] {
                AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", Production("UiThemeCatalog")) });
            hudObject = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(HudPath));
            var hud = hudObject.GetComponent(Production("GameplayInteractionHudView"));
            Call(hud, "ConfigureGameplayPresentation", textCatalog, new Dictionary<string, string>());
            return hud;
        }
        private static GameplayPresentationCatalog PresentationOnly()
        {
            string root = Path.GetFullPath(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var sources = new SortedDictionary<string,string>(StringComparer.Ordinal);
            byte[] ReadFile(string relative)
            {
                string path = Path.GetFullPath(Path.Combine(root, relative));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("显示输入越界");
                byte[] bytes = File.ReadAllBytes(path); string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create()) hash = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
                if (sources.TryGetValue(relative, out string previous) && previous != hash) throw new IOException("显示输入读取期间发生变化：" + relative);
                sources[relative] = hash; return bytes;
            }
            var manifest = JObject.Parse(System.Text.Encoding.UTF8.GetString(ReadFile("pack.json")));
            var catalog = (GameplayPresentationCatalog)typeof(GameplayPresentationCatalog).GetMethod("Load", Flags).Invoke(null,
                new object[] { manifest["presentation"], (string)manifest["packId"], new Func<string,JObject>(p => JObject.Parse(System.Text.Encoding.UTF8.GetString(ReadFile(p)))), new Func<string,byte[]>(ReadFile) });
            string identity;
            using (var sha = System.Security.Cryptography.SHA256.Create()) identity = string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                string.Join("\n", sources.Select(p => p.Key + "|" + p.Value)))).Select(b => b.ToString("x2")));
            Call(catalog, "BindContentHash", "r18-display-only-" + identity);
            Save(TestContext.CurrentContext.Test.Name + "-显示目录输入", new { catalog.ContentHash, sources,
                scope = "独立显示目录原装载器；仅定位绑定/布局。不装载规则模块、不替换pack、不绕过完整内容校验；不能宣称正式内容全链通过" });
            return catalog;
        }
        private static GameStateView BarePublicInput(IGameplayTextCatalogSnapshot catalog)
        {
            var view = new GameStateView { GameId = "R18.display-components", ContentPackHash = catalog.ContentHash,
                ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 1, HasAbilityActionCatalog = true, LaunchPlayerOrder = new List<int> { 1, 2 } };
            foreach (int id in new[] { 1, 2 }) view.Players.Add(new PlayerStateView { PlayerId = id, Name = "组件玩家" + id, Color = (PlayerColor)(id-1), Resources = new ResourceSet() });
            return view;
        }
        private sealed class EffectSample
        {
            public string Code, Definition, InputPath, Sha256, Kind;
            public GameplayTextRef Reference;
            public GameplayTextDocument Document;
        }
        private static EffectSample[] ReadEffectSamples(object frame)
        {
            var samples = new List<EffectSample>();
            foreach (string code in new[] { "A01", "D09" })
            {
                if(Environment.GetEnvironmentVariable("QY017_R18_EFFECT_SAMPLE_MODE")=="isolated")
                {
                    samples.Add(new EffectSample {Code=code,Definition="",InputPath="",Sha256="",Kind="合成生命周期/布局样本，不读取或验收作者未完成的效果正文",
                        Document=new GameplayTextDocument(new GameplayTextRun[]{new TextRun("仅用于绑定生命周期与容量测量的隔离正文。")})});
                    continue;
                }
                string relative = "entrepreneur/entrepreneur.ability." + code + ".json";
                byte[] bytes = File.ReadAllBytes(Path.Combine(UnityEngine.Application.streamingAssetsPath,"Content/core",relative));
                var definition = JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                JToken Get(JObject obj,string key) => obj.Properties().FirstOrDefault(p => string.Equals(p.Name,key,StringComparison.OrdinalIgnoreCase))?.Value;
                var detail = (definition["data"]?["playerDetails"] as JArray)?.OfType<JObject>()
                    .Select(d => Get(d,"EffectTextRef")).FirstOrDefault(r => r != null);
                GameplayTextRef reference = detail == null ? null : (GameplayTextRef)typeof(GameplayPresentationCatalog).GetMethod("ReadDto",Flags)
                    .MakeGenericMethod(typeof(GameplayTextRef)).Invoke(null,new object[]{detail});
                string hash; using(var sha=System.Security.Cryptography.SHA256.Create()) hash=string.Concat(sha.ComputeHash(bytes).Select(b=>b.ToString("x2")));
                samples.Add(new EffectSample { Code=code,Definition=(string)definition["definitionId"],InputPath=relative,Sha256=hash,Reference=reference,
                    Kind=reference == null ? "合成生命周期样本，待外部效果详情typed引用" : "当前外部data.playerDetails段落，仅生命周期取样，非语义验收",
                    Document=reference == null ? new GameplayTextDocument(new GameplayTextRun[]{new TextRun("仅用于绑定生命周期与容量测量的隔离正文。")}) :
                        (GameplayTextDocument)Call(frame,"ResolveGameplayText",reference) });
            }
            Save(TestContext.CurrentContext.Test.Name+"-效果文本样本",samples.Select(s=>new{s.Code,s.Definition,s.InputPath,s.Sha256,s.Kind,s.Reference,plainText=s.Document.ToPlainText()}).ToArray());
            return samples.ToArray();
        }
        private static void BindPersistentRow(Component row, string id, string title, Func<string,int,bool> current)
        {
            var type = row.GetType(); var bindingType = type.GetNestedType("Binding"); var binding = Activator.CreateInstance(bindingType);
            bindingType.GetField("StableItemId").SetValue(binding, id); bindingType.GetField("RequestId").SetValue(binding, "r18.request");
            bindingType.GetField("RowMode").SetValue(binding, Enum.Parse(type.GetNestedType("Mode"), "PersistentDetail"));
            bindingType.GetField("Title").SetValue(binding, title);
            type.GetMethod("Bind", new[] { bindingType, typeof(Func<string,int,bool>), typeof(Action<string>), typeof(Action<string>) })
                .Invoke(row, new object[] { binding, current, null, null });
        }
        private static GameStateView PublicComponentInput(ExternalContentPack pack)
        {
            var view = new GameStateView { GameId = "R18.component-only", ContentPackHash = pack.ContentHash,
                ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 1, StateRevision = 1, HasAbilityActionCatalog = true,
                LaunchPlayerOrder = new List<int> { 1, 2 } };
            foreach (int player in new[] { 1, 2 })
            {
                view.Players.Add(new PlayerStateView { PlayerId = player, Name = "组件玩家" + player, Color = (PlayerColor)(player - 1), Resources = new ResourceSet() });
                string definition = player == 1 ? "entrepreneur.ability.A01" : "entrepreneur.ability.D09", id = "r18.card." + player;
                var metadata = pack.Definitions.Single(d => d.DefinitionId == definition);
                string active = "r18.active." + player;
                view.ItemZones.Zones.Add(new ZoneStateView { Zone = new ZoneInstanceState { ZoneId = active, TemplateId = "core.zone.entrepreneur_active", OwnerPlayerId = player } });
                view.ItemZones.Items.Add(new ItemInstanceState { ItemId = id, DefinitionId = definition, TypeId = "entrepreneur.ability_card", ZoneId = active,
                    OwnerSubject = ItemSubjectRef.Player(player), ControllerSubject = ItemSubjectRef.Player(player) });
                view.Entrepreneurs.Players.Add(new EntrepreneurPlayerView { PlayerId = player, AbilityItemId = id });
                // 隔离投影输入按现有事实协议取激活类型，不能照抄待验展示映射而掩盖Held/Subscription错配。
                foreach(var source in metadata.AbilitySources)
                    view.PlayerEffects.Add(new PlayerEffectFactView { FactId="r18.fact."+player+"."+source.AbilityId,SourceItemId=id,
                        DefinitionId=definition,AbilityId=source.AbilityId,DetailPlayerId=player,
                        Activation=source.AuthorizationPolicy=="character_subscription" ? YC.Domain.Cards.PlayerDetailActivation.Subscription : YC.Domain.Cards.PlayerDetailActivation.Held });
                foreach (var zone in metadata.CardZones.Where(z => z.ZoneTemplateId == "core.zone.card_cube_storage" || z.ZoneTemplateId == "core.zone.card_facility_storage"))
                    view.ItemZones.Zones.Add(new ZoneStateView { Zone = new ZoneInstanceState { ZoneId = id + "." + zone.LocalZoneId,
                        LocalZoneId = zone.LocalZoneId, TemplateId = zone.ZoneTemplateId, OwnerPlayerId = player, ParentKind = ZoneParentKind.Item, ParentId = id } });
            }
            return view;
        }
        private static bool ProjectedDocumentsAreBound(object expansion, string[] ids)
        {
            var rows=(IDictionary)Read(expansion,"rows");
            return ids.All(id=>rows.Contains(id) && (bool)Read(((Text)Read(rows[id],"DescriptionText")).GetComponent(Production("GameplayTextView")),"HasDocument"));
        }
        private static object Snapshot(object expansion, string stage)
        {
            var rows = (IDictionary)Read(expansion, "rows"); var values = new List<object>();
            foreach (DictionaryEntry entry in rows)
            {
                var row = (Component)entry.Value; if (row == null) continue;
                var body = (Text)Read(row, "DescriptionText"); var host = body.GetComponent(Production("GameplayTextView"));
                var document = (GameplayTextDocument)Read(host, "document");
                var visual = body.GetComponentsInChildren<Text>(true).Where(t => t != body).ToArray();
                values.Add(new { id = (string)entry.Key, row.gameObject.activeSelf, row.gameObject.activeInHierarchy,
                    hasDocument = Read(host, "HasDocument"), plainText = document?.ToPlainText(), bindCount = Read(host, "BindCount"),
                    renderCount = Read(host, "RenderCount"), activeFragments = Read(host, "ActiveFragmentCount"),
                    fontSize = body.fontSize, effectiveFontSize = Read(host, "EffectiveFontSize"), bodyRect = Bounds(body.rectTransform),
                    rawText = body.text, visibleRuns = visual.Where(t => t.gameObject.activeInHierarchy && t.enabled && t.color.a > 0)
                        .Select(t => new { t.text, fontSize = t.fontSize, rendererAlpha = t.canvasRenderer.GetAlpha(), t.canvasRenderer.cull }).ToArray() });
            }
            return new { stage, frame = Time.frameCount, rows = values };
        }
        private static int CountAbilityDocuments(object expansion)
        {
            var rows = (IDictionary)Read(expansion, "rows"); int count = 0;
            foreach (DictionaryEntry entry in rows)
            {
                if (!((string)entry.Key).StartsWith("source-inventory:r18.card.", StringComparison.Ordinal)) continue;
                var row = (Component)entry.Value; var body = (Text)Read(row, "DescriptionText");
                var host = body.GetComponent(Production("GameplayTextView"));
                if ((bool)Read(host, "HasDocument")) count++;
            }
            return count;
        }
        private static bool HasBothDocuments(object expansion) => CountAbilityDocuments(expansion) == 2;
        [UnityTearDown] public IEnumerator LeavePlayMode()
        {
            if (hudObject != null) UnityEngine.Object.Destroy(hudObject);
            if (UnityEngine.Application.isPlaying) yield return new ExitPlayMode();
        }

        [UnityTest] public IEnumerator ActualEntrepreneurChooseFoldsAndExpandsSamePendingPageWithoutCommands()
        {
            yield return new EnterPlayMode();
            var pack = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var hud = CreateHud(pack.Presentation); var view = PublicComponentInput(pack);
            var frame = Read(hud, "Frame"); var expansion = Read(hud, "ExpansionUi");
            Call(frame, "BindGameplaySnapshot", view);
            Call(expansion, "Configure", new Func<GameStateView>(() => view), new Func<int>(() => 1), null,
                new Dictionary<string, YC.Domain.Cards.CharacterModeContract>(), new Action<YC.Domain.Commands.GameCommand>(_ => Assert.Fail("组件浏览不能派令")));
            var input = new InteractionView { InteractionId = "r36.choose.component", OwnerEffectId = "r36.choose.owner",
                Kind = EntrepreneurFlow.ChooseInteraction, AnsweringPlayerId = 1, Visibility = "owner", Status = "open",
                StateRevision = view.StateRevision, CandidateSetId = "r36.choose.candidates", CandidateSetVersion = 1,
                AnswerSchema = "candidate_ids", MinSelections = 1, MaxSelections = 1,
                CandidateIds = new List<string> { "entrepreneur.ability.A01", "entrepreneur.ability.D09" } };
            view.Interactions.Add(input);
            var projection = YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(
                YC.Presentation.Workflows.VisibleInteractionRequestSource.Read(null, view, 1).Single(), 1);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            var coordinator = Activator.CreateInstance(Production("EntrepreneurSelectionUiCoordinator"), Flags, null,
                new object[] { expansion, Read(hud, "DialogRegistry"), Read(frame, "ContentRect"),
                    new Func<int>(() => 1), new Action<YC.Domain.Commands.GameCommand>(commands.Add), new Func<bool>(() => true) }, null);
            Call(coordinator, "Render", projection); yield return null;
            var page = (Component)Read(Read(coordinator, "selection"), "view");
            Assert.That(page, Is.Not.Null); Assert.That(page.gameObject.activeInHierarchy, Is.True);
            var scroll = page.GetComponentsInChildren<ScrollRect>(true).First(s => s.gameObject.activeInHierarchy);
            scroll.verticalNormalizedPosition = .37f;
            var select = ((Component)hud).GetComponentsInChildren<MonoBehaviour>(true).Single(component => component != null &&
                component.GetType().FullName == "YC.PlayerJourney.PlayerAutomationId" &&
                (string)Read(component, "Id") == "selection.card.entrepreneur.ability.A01").GetComponent<Button>();
            select.onClick.Invoke(); yield return null;
            Assert.That(Read(coordinator, "selected"), Is.EqualTo("entrepreneur.ability.A01"));
            float position = scroll.verticalNormalizedPosition;
            var fold = (Button)Read(frame, "FoldButton"); Assert.That(fold.IsActive() && fold.IsInteractable(), Is.True);
            fold.onClick.Invoke(); yield return null;
            Assert.That(page.gameObject.activeInHierarchy, Is.False);
            fold.onClick.Invoke(); yield return null;
            Assert.That(Read(Read(coordinator, "selection"), "view"), Is.SameAs(page));
            Assert.That(page.gameObject.activeInHierarchy, Is.True);
            Assert.That(Read(coordinator, "selected"), Is.EqualTo("entrepreneur.ability.A01"));
            Assert.That(scroll.verticalNormalizedPosition, Is.EqualTo(position).Within(.001f));
            Assert.That(view.Interactions.Single(), Is.SameAs(input)); Assert.That(input.Status, Is.EqualTo("open"));
            Assert.That(commands, Is.Empty);
            Save("R36-企业家原Choose页自由折叠", new { IsolatedComponentOnly = true, NormalGameAcceptance = false,
                input.InteractionId, input.CandidateSetId, Selected = Read(coordinator, "selected"), ScrollPreserved = true,
                SamePage = true, CommandCount = commands.Count, RequiredNavigation = false });
            Call(coordinator, "Clear");
        }

        [UnityTest] public IEnumerator ActualAbilityDetailsSurviveFramesRefreshCityInspectionAndRefolding()
        {
            yield return new EnterPlayMode();
            var pack = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var hud = CreateHud(pack.Presentation); var view = PublicComponentInput(pack);
            var modules = Read(hud, "MainModules"); var expansion = Read(hud, "ExpansionUi");
            Call(modules, "Render", null, view, 1, Read(Read(hud, "DialogRegistry"), "CardVisualCatalog"));
            Call(expansion, "Configure", new Func<GameStateView>(() => view), new Func<int>(() => 1), null,
                new Dictionary<string, YC.Domain.Cards.CharacterModeContract>(), new Action<YC.Domain.Commands.GameCommand>(_ => Assert.Fail("只读详情不能提交")));
            var definitions=PlayerDetailDisplayCatalog.Read(pack.Definitions,pack.Presentation);
            Assert.That(definitions.Any(d=>d.DefinitionId=="entrepreneur.ability.A01" && d.EffectTextRef!=null) &&
                definitions.Any(d=>d.DefinitionId=="entrepreneur.ability.D09" && d.EffectTextRef!=null),Is.True,"正式效果段落映射必须先由内容任务交付；整卡description不能替代");
            expansion.GetType().GetProperty("DetailDefinitions").SetValue(expansion,definitions);
            Call(expansion, "Refresh"); var evidence = new List<object>();
            var expected=((IEnumerable<YC.Presentation.Workflows.PlayerDetailEntry>)Read(expansion,"DetailEntries"))
                .Where(e=>e.EffectTextRef!=null && e.SourceItemId.StartsWith("r18.card.",StringComparison.Ordinal)).Select(e=>e.EntryId).ToArray();
            Assert.That(expected.Length,Is.GreaterThanOrEqualTo(2));
            evidence.Add(Snapshot(expansion, "绑定后未展开"));
            PointerClick((Button)Read(modules, "localDetailToggle"));
            PointerClick(((Button[])Read(modules, "opponentDetailToggles"))[0]);
            evidence.Add(Snapshot(expansion, "首次展开即帧"));
            for (int frame = 1; frame <= 8; frame++)
            { yield return null; evidence.Add(Snapshot(expansion, "展开后帧" + frame)); }
            bool initialStable = ProjectedDocumentsAreBound(expansion,expected);
            for (int refresh = 0; refresh < 3; refresh++) { Call(expansion, "Refresh"); yield return null; }
            evidence.Add(Snapshot(expansion, "同快照三次原Refresh后"));
            Call(modules, "RenderCityInspection", view, 2); Call(expansion, "RefreshViewedEntrepreneurCards", view, 2);
            yield return null; evidence.Add(Snapshot(expansion, "查看另一城市后"));
            Call(modules, "RenderCityInspection", view, 1); Call(expansion, "RefreshViewedEntrepreneurCards", view, 1);
            PointerClick((Button)Read(modules, "localDetailToggle"));
            PointerClick(((Button[])Read(modules, "opponentDetailToggles"))[0]);
            yield return null; evidence.Add(Snapshot(expansion, "收回一帧后"));
            PointerClick((Button)Read(modules, "localDetailToggle"));
            PointerClick(((Button[])Read(modules, "opponentDetailToggles"))[0]);
            for (int frame = 1; frame <= 4; frame++) { yield return null; evidence.Add(Snapshot(expansion, "再次展开帧" + frame)); }
            bool reopened = ProjectedDocumentsAreBound(expansion,expected);
            Call(expansion, "Refresh"); yield return null; evidence.Add(Snapshot(expansion, "再次展开原Refresh后"));
            bool afterRefresh = ProjectedDocumentsAreBound(expansion,expected);
            Call(Read(hud, "Frame"), "BindGameplaySnapshot", new object[] { null }); yield return null;
            var currentRows=(IDictionary)Read(expansion,"rows");
            bool clearedAtScopeExit = expected.All(id=>!(bool)Read(((Text)Read(currentRows[id],"DescriptionText")).GetComponent(Production("GameplayTextView")),"HasDocument"));
            evidence.Add(Snapshot(expansion, "正式作用域撤回后"));
            Call(Read(hud, "Frame"), "BindGameplaySnapshot", view); Call(expansion, "Refresh"); yield return null;
            evidence.Add(Snapshot(expansion, "恢复同一公开快照后"));
            Save("真实详情跨帧与折叠", new { contentHash = pack.ContentHash, presentationHash = pack.PresentationContentHash, initialStable, reopened,
                afterRefresh, clearedAtScopeExit, restoredAfterScopeReturn = ProjectedDocumentsAreBound(expansion,expected), snapshots = evidence,
                scope = "正式A01/D09文本及原HUD消费者；公开组件输入隔离，不是合法对局/正常页面验收；未改资产" });
            Assert.That(clearedAtScopeExit, Is.True, "正式作用域撤回必须清除旧文档");
            Assert.That(initialStable && reopened && afterRefresh && ProjectedDocumentsAreBound(expansion,expected), Is.True, "正式效果正文不能在跨帧/收回重开后因绑定生命周期丢失");
        }

        [UnityTest] public IEnumerator ActualDetailPanelsRetainCurrentDocumentsAcrossVisibilityOnlyChanges()
        {
            yield return new EnterPlayMode(); var catalog = PresentationOnly(); var hud = CreateHud(catalog);
            var modules = Read(hud, "MainModules"); var frame = Read(hud, "Frame"); var view = BarePublicInput(catalog);
            Call(frame, "BindGameplaySnapshot", view);
            var samples=ReadEffectSamples(frame);
            Call(modules, "Render", null, view, 1, Read(Read(hud, "DialogRegistry"), "CardVisualCatalog"));
            var rows = new Dictionary<string,Component>(); bool current = true; int readDepth=0,readCount=0; var evidence = new List<object>();
            foreach (int player in new[] { 1, 2 })
            {
                string code = player == 1 ? "A01" : "D09", key = "source-inventory:r18.card." + player;
                var host = (RectTransform)Call(modules, player == 1 ? "GetLocalDetailHost" : "GetOpponentDetailHost", player == 1 ? 0 : 2);
                var row = (Component)Call(Read(hud, "DialogRegistry"), "InstantiateEffectRow", host); row.gameObject.SetActive(true); rows.Add(key,row);
                row.GetType().GetProperty("BeginInputRead").SetValue(row,new Func<IDisposable>(()=>{readDepth++;readCount++;return new ReadLease(()=>readDepth--);}));
                BindPersistentRow(row, key, code + "能力牌", (_,__) => {Assert.That(readDepth,Is.GreaterThan(0),"可见性守卫也须沿原读租约，不得从后台Host直接读状态");return current;});
                var document = samples[player-1].Document;
                Call(frame, "RenderGameplayText", ((Text)Read(row,"DescriptionText")).GetComponent(Production("GameplayTextView")), document, false);
            }
            var proxy = new { rows = (IDictionary)rows }; Call(modules,"RefreshDetailAvailability");
            var local = (Button)Read(modules,"localDetailToggle"); var opponent = ((Button[])Read(modules,"opponentDetailToggles"))[0];
            PointerClick(local); PointerClick(opponent);
            for(int i=0;i<6;i++) { yield return null; evidence.Add(Snapshot(proxy,"首次展开帧"+i)); }
            bool initial = HasBothDocuments(proxy);
            PointerClick(local); PointerClick(opponent); yield return null; evidence.Add(Snapshot(proxy,"收回后"));
            PointerClick(local); PointerClick(opponent);
            for(int i=0;i<4;i++) { yield return null; evidence.Add(Snapshot(proxy,"原控件重开帧"+i)); }
            bool reopened = HasBothDocuments(proxy);
            PointerClick(local); PointerClick(opponent); yield return null; current=false;
            PointerClick(local); PointerClick(opponent); yield return null; bool staleCleared=CountAbilityDocuments(proxy)==0;
            evidence.Add(Snapshot(proxy,"原绑定上下文已过期后"));
            Save("独立实际面板可见性复现",new { catalog.ContentHash,initial,reopened,staleCleared,readCount,readDepth,snapshots=evidence,
                scope="当前实际HUD面板/注册EffectRow/正式显示目录，仅隔离绑定生命周期；完整pack受外部Agnes登记阻断，未运行原ExpansionUI正式内容路径" });
            Assert.That(staleCleared,Is.True,"过期上下文不能在面板重开时重现旧正文");
            Assert.That(readCount,Is.GreaterThan(0));Assert.That(readDepth,Is.Zero,"可见性读租约必须释放");
            Assert.That(initial && reopened,Is.True,"仅收回和重开不能丢失仍属当前上下文的正式说明");
        }

        [UnityTest] public IEnumerator ActualRowMeasuresFormalTextAndIconCapacityWithoutCharacterTruncation()
        {
            yield return new EnterPlayMode();
            var catalog = PresentationOnly();
            var hud = CreateHud(catalog); var frame = Read(hud, "Frame"); Call(frame, "BindGameplaySnapshot", BarePublicInput(catalog));
            var samples=ReadEffectSamples(frame);
            var canvas = new GameObject("R18长度边界画布", typeof(RectTransform), typeof(Canvas));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var measures = new List<object>(); bool allRetained = true;
            try
            {
                foreach (float width in new[] { 360f, 520f, 900f })
                foreach (var sample in samples)
                foreach (int repeats in Enumerable.Range(1, 8))
                {
                    var source = sample.Document;
                    var runs = new List<GameplayTextRun>(); for (int i = 0; i < repeats; i++) { if (i > 0) runs.Add(new LineBreak()); runs.AddRange(source.Runs); }
                    var document = new GameplayTextDocument(runs);
                    var instance = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(RowPath), canvas.transform, false);
                    try
                    {
                        var row = instance.GetComponent(Production("UiEffectRowView")); var type = row.GetType(); var bindingType = type.GetNestedType("Binding");
                        var binding = Activator.CreateInstance(bindingType);
                        bindingType.GetField("StableItemId").SetValue(binding, "r18.length"); bindingType.GetField("RequestId").SetValue(binding, "r18.request");
                        bindingType.GetField("RowMode").SetValue(binding, Enum.Parse(type.GetNestedType("Mode"), "PersistentDetail"));
                        bindingType.GetField("Title").SetValue(binding, "长度边界样本");
                        type.GetMethod("Bind", new[] { bindingType, typeof(Func<string,int,bool>), typeof(Action<string>), typeof(Action<string>) })
                            .Invoke(row, new object[] { binding, new Func<string,int,bool>((_,__) => true), null, null });
                        ((RectTransform)instance.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
                        var body = (Text)Read(row, "DescriptionText"); int assetSize = body.fontSize;
                        var textView = body.GetComponent(Production("GameplayTextView")); Call(frame, "RenderGameplayText", textView, document, false);
                        yield return null; yield return null;
                        bool retained = (bool)Read(textView, "HasDocument"); allRetained &= retained;
                        measures.Add(new { width, key=sample.Reference?.Key, sample.Code, sample.Kind, repeats, characters = document.ToPlainText().Length, icons = document.Runs.OfType<IconRun>().Count(),
                            bodyRect = Bounds(body.rectTransform), assetSize, fittedSize = body.fontSize, effectiveSize = Read(textView, "EffectiveFontSize"),
                            hasDocument = retained, fragments = Read(textView, "ActiveFragmentCount"),
                            fitsAtAssetSize = Call(textView, "FitsAtFontSize", assetSize), fitsAtCurrentSize = Call(textView, "FitsAtFontSize", body.fontSize) });
                    }
                    finally { UnityEngine.Object.Destroy(instance); }
                }
            }
            finally { UnityEngine.Object.Destroy(canvas); }
            Save("正文及图标长度边界", new { contentHash = catalog.ContentHash, measures,
                note = "容量依当前容器/字体/换行/图标组合；此处测量不设为不可修改样式规范，不截短正式原文" });
            Assert.That(allRetained, Is.True, "增大内容长度不能清除正式文档");
        }

        [UnityTest] public IEnumerator QuickActionResearchMeasuresCurrentAndProposedParagraphsWithoutChangingAssets()
        {
            yield return new EnterPlayMode(); var catalog = PresentationOnly(); var hud = CreateHud(catalog);
            var frame = Read(hud,"Frame"); Call(frame,"BindGameplaySnapshot",BarePublicInput(catalog));
            var button = (Button)Read(Read(hud,"ActionPanelView"),"UseCharacterButton");
            var canvas = new GameObject("R18快速行研究画布",typeof(RectTransform),typeof(Canvas));
            canvas.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
            var measures=new List<object>(); string reason=YC.Domain.Effects.AbilityActionReasons.Display("style_action_used_this_round");
            try
            {
                foreach(float width in new[]{360f,530f,900f})
                foreach(string profile in new[]{"A09","P01"})
                foreach(bool available in new[]{true,false})
                foreach(bool includeBody in new[]{false,true})
                {
                    string relative="entrepreneur/entrepreneur.round."+profile[0]+".1.json";
                    var metadata=JObject.Parse(File.ReadAllText(Path.Combine(UnityEngine.Application.streamingAssetsPath,"Content/core",relative)));
                    var action=metadata.SelectTokens("$..actions[*]").OfType<JObject>().Single(a=>((string)a["actionId"]??"").Contains("."+profile+".") && ((string)a["actionId"]).EndsWith(".quick",StringComparison.Ordinal));
                    var presentation=(AbilityActionPresentationDefinition)typeof(GameplayPresentationCatalog).GetMethod("ReadDto",Flags)
                        .MakeGenericMethod(typeof(AbilityActionPresentationDefinition)).Invoke(null,new object[]{action["presentation"]});
                    var name=(GameplayTextDocument)Call(frame,"ResolveGameplayText",presentation.Name);
                    var body=(GameplayTextDocument)Call(frame,"ResolveGameplayText",presentation.Description);
                    var runs=new List<GameplayTextRun>(name.Runs);
                    if(!available) { runs.Add(new LineBreak());runs.Add(new TextRun(reason)); }
                    if(includeBody) { runs.Add(new LineBreak());runs.AddRange(body.Runs); }
                    var document=new GameplayTextDocument(runs);
                    var instance=UnityEngine.Object.Instantiate(button.gameObject,canvas.transform,false);
                    try
                    {
                        ((RectTransform)instance.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,width);
                        var row=instance.GetComponent(Production("UiQuickActionRow"));
                        Call(row,"Bind",document.ToPlainText(),available,new Action(()=>Assert.Fail("研究不提交行动")));
                        Call(row,"BindGameplayText",document,new Action<Component,GameplayTextDocument,bool>((host,doc,single)=>Call(frame,"RenderGameplayText",host,doc,single)));
                        instance.SetActive(true);yield return null;
                        var label=(Text)Read(row,"label");var view=label.GetComponent(Production("GameplayTextView"));
                        bool hasReason=available || document.ToPlainText().Contains(reason);
                        Assert.That((bool)Read(view,"HasDocument") && hasReason,Is.True);
                        measures.Add(new {width,profile,available,includeBody,sourceDefinition=relative,nameRef=presentation.Name,descriptionRef=presentation.Description,
                            paragraphs=1+(!available?1:0)+(includeBody?1:0),
                            plainText=document.ToPlainText(),icons=document.Runs.OfType<IconRun>().Count(),fontSize=label.fontSize,
                            rowRect=Bounds((RectTransform)instance.transform),labelRect=Bounds(label.rectTransform),
                            measuredHeight=Read(view,"measuredHeight"),fitsCurrentRect=Call(view,"FitsAtFontSize",label.fontSize),
                            activeFragments=Read(view,"ActiveFragmentCount"),reasonPreserved=hasReason});
                    }
                    finally {UnityEngine.Object.Destroy(instance);}
                }
            }
            finally {UnityEngine.Object.Destroy(canvas);}
            Save("快速行原生多行研究",new{catalog.ContentHash,measures,
                note="仅研究外部Name/效果段落/第二段原因承载；保留源字号/高度/结构，不实施第三行或改布局；不在C#补roundFace或拼行动名，不等同作者最终文案语义验收。"});
        }
    }
}
