using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Presentation;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    // 只读展示消费者隔离；不代替规则Host、正常菜单或合法对局验收。
    public sealed class EffectDetailPresentationConsumerTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private GameObject hudObject;
        private static Type Production(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object target, string name) => target.GetType().GetField(name, Flags)?.GetValue(target) ?? target.GetType().GetProperty(name, Flags)?.GetValue(target);
        private static object Call(object target, string name, params object[] args)
        {
            try { return target.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException ?? e).Throw(); throw; }
        }
        private static string Hash(string path)
        {
            using var algorithm = SHA256.Create(); using var stream = File.OpenRead(path);
            return string.Concat(algorithm.ComputeHash(stream).Select(b => b.ToString("x2")));
        }
        private static void Save(string name, object data)
        {
            string output = Environment.GetEnvironmentVariable("QY017_R18_OUTPUT") ?? throw new InvalidOperationException("缺少续验独占输出目录");
            using var file = new FileStream(Path.Combine(output, name + ".json"), FileMode.CreateNew);
            using var writer = new StreamWriter(file); writer.Write(JsonConvert.SerializeObject(data, Formatting.Indented));
        }
        [SetUp] public void RecordNativeIdentity()
        {
            var sourcePaths = new[] { "Assets/YC/Infrastructure/Lua/AbilitySourceLuaCatalog.cs", "Assets/YC/Tests/EditMode/EffectDetailPresentationConsumerTests.cs",
                "Assets/YC/Presentation/GameplayExpansionUi.cs", "Assets/YC/Presentation/Effects/UiEffectRowView.cs", "Assets/YC/Presentation/Text/GameplayTextView.cs",
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab", "Assets/YC/Presentation/Effects/Prefabs/EffectRow.prefab",
                "Assets/YC/Presentation/Text/Content/GameplayTextStyle.asset", "Assets/YC/Presentation/Text/Content/GameplayIconCatalog.asset" };
            Save(TestContext.CurrentContext.Test.Name + "-实际身份", new { files = sourcePaths.Select(p => new { path = p, sha256 = Hash(p) }).ToArray(),
                assemblies = new[] { GetType().Assembly, typeof(AbilitySourceLuaCatalog).Assembly, typeof(GameplayTextResolver).Assembly,
                    typeof(GameStateView).Assembly, typeof(PlayerDetailEntryProjector).Assembly, Production("GameplayTextView").Assembly }
                    .Select(a => new { a.Location, mvid = a.ManifestModule.ModuleVersionId, sha256 = Hash(a.Location) }).ToArray(),
                scope = "原生只读展示隔离；不修改资产、规则状态或正式内容。" });
        }
        private static void RegisterDisplay(EffectRegistry registry, ExternalContentDefinition content, IGameplayTextCatalog catalog, Func<GameplayTextRef,string> resolve)
        {
            try { typeof(AbilitySourceLuaCatalog).GetMethod("RegisterPresentation", Flags).Invoke(null, new object[] { registry, content, catalog, resolve }); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException ?? e).Throw(); throw; }
        }
        private static ExternalContentDefinition AnonymousDefinition(params PlayerDetailDisplayDefinition[] rows)
        {
            var content = new ExternalContentDefinition { DefinitionId = "fixture.display", DisplayName = "匿名来源",
                Presentation = new ContentPresentationDefinition { Description = new GameplayTextRef("core:whole") } };
            foreach (string id in rows.Select(r => r.AbilityId).Where(id => !string.IsNullOrEmpty(id)).Distinct())
                content.Abilities.Add(new ExternalAbilityDefinition { AbilityId = id });
            content.Data["playerDetails"] = JArray.FromObject(rows); return content;
        }
        [Test] public void TypedAndLegacyParagraphsStayWithTheirDeclaredSources()
        {
            var catalog = new GameplayTextResolverTests.TestCatalog();
            catalog.Add("core:typed", "分数结算时：获得{amount}分。", "amount", "integer");
            var reference = new GameplayTextRef("core:typed", new GameplayTextArgument { Name = "amount", Kind = "integer", Value = NormalizedValue.CreateInteger(3) });
            var content = AnonymousDefinition(new PlayerDetailDisplayDefinition { AbilityId = "fixture.scoring", Activation = PlayerDetailActivation.Held,
                    EffectTextRef = reference, EffectText = "被替代的旧摘要", DisplayName = "计分来源" },
                new PlayerDetailDisplayDefinition { AbilityId = "fixture.legacy", Activation = PlayerDetailActivation.Held, EffectText = "剩余游戏：保留本段。", DisplayName = "旧来源" },
                new PlayerDetailDisplayDefinition { Activation = PlayerDetailActivation.Held, EffectText = "纯展示段不能借给定义" });
            var resolver = new GameplayTextResolver(catalog); var context = new GameplayTextContext { Catalog = catalog, ContentHash = catalog.ContentHash, Locale = catalog.Locale };
            var registry = new EffectRegistry(); RegisterDisplay(registry, content, catalog, r => resolver.Resolve(r, context).ToPlainText());
            Assert.That(registry.AbilityPresentationDescriptions["fixture.scoring"], Is.EqualTo("分数结算时：获得3分。"));
            Assert.That(registry.AbilityPresentationDescriptions["fixture.legacy"], Is.EqualTo("剩余游戏：保留本段。"));
            Assert.That(registry.AbilityPresentationDescriptions[content.DefinitionId], Is.Empty);
            Assert.That(registry.AbilityPresentationNames["fixture.scoring"], Is.EqualTo("计分来源"));
            content.Data["specialAction"] = new JObject { ["description"] = "明确的旧特殊行动说明" };
            RegisterDisplay(registry, content, catalog, r => resolver.Resolve(r, context).ToPlainText());
            Assert.That(registry.AbilityPresentationDescriptions[content.DefinitionId], Is.EqualTo("明确的旧特殊行动说明"));
            Save("匿名对应来源登记", registry.AbilityPresentationDescriptions);
        }
        [Test] public void DistinctTimingParagraphsAreNotArbitrarilyCollapsedIntoLegacyActionFallback()
        {
            var content = AnonymousDefinition(new PlayerDetailDisplayDefinition { AbilityId = "fixture.shared", Activation = PlayerDetailActivation.Held, EffectText = "持有时的段落" },
                new PlayerDetailDisplayDefinition { AbilityId = "fixture.shared", Activation = PlayerDetailActivation.Delayed, EffectText = "延后义务的段落" });
            var registry = new EffectRegistry(); RegisterDisplay(registry, content, null, _ => throw new InvalidOperationException("本例没有typed引用"));
            Assert.That(registry.AbilityPresentationDescriptions.ContainsKey("fixture.shared"), Is.False);
            Assert.That(PlayerDetailDisplayCatalog.Read(new[] { content }), Has.Count.EqualTo(2), "正式详情仍保留两个时机，不裁去其元数据");
        }
        [Test] public void InvalidTypedReferenceIsRejectedInsteadOfBorrowingAnotherParagraph()
        {
            var content = AnonymousDefinition(new PlayerDetailDisplayDefinition { AbilityId = "fixture.invalid", Activation = PlayerDetailActivation.Held,
                EffectTextRef = new GameplayTextRef("core:missing"), EffectText = "不得掩盖缺失引用" });
            var registry = new EffectRegistry();
            var error = Assert.Throws<InvalidOperationException>(() => RegisterDisplay(registry, content, new GameplayTextResolverTests.TestCatalog(), _ => "不应解析"));
            Assert.That(error.Message, Does.StartWith("player_detail_text_ref_missing_or_mismatched:"));
            Assert.That(registry.AbilityPresentationDescriptions.ContainsKey("fixture.invalid"), Is.False);
        }
        private static GameplayPresentationCatalog LoadDisplayCatalog()
        {
            // 沿R18已验证的正式显示目录装载器，只读冻结内容身份；不加载正在更新的规则模块。
            return (GameplayPresentationCatalog)typeof(EntrepreneurDetailStabilityR18Tests).GetMethod("PresentationOnly", Flags).Invoke(null, null);
        }
        private static ExternalContentDefinition[] ReadCurrentDefinitions()
        {
            string folder = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core/entrepreneur");
            var files = Directory.GetFiles(folder, "*.json").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var identities = files.Select(p => new { path = p, sha256 = Hash(p) }).ToArray();
            var definitions = files.Select(p =>
            {
                var json = JObject.Parse(File.ReadAllText(p)); var carrierJson = (JObject)json.DeepClone();
                // 与生产加载器相同：表现参数先由专用DTO读取器解释，不能直接反序列化为NormalizedValue。
                carrierJson.Remove("presentation");
                foreach (var ability in (carrierJson["abilities"] as JArray ?? new JArray()).OfType<JObject>())
                { ability.Remove("presentation"); (ability["settlement"] as JObject)?.Remove("presentation"); }
                foreach (var source in (carrierJson["abilitySources"] as JArray ?? new JArray()).OfType<JObject>())
                foreach (var action in (source.GetValue("actions", StringComparison.OrdinalIgnoreCase) as JArray ?? new JArray()).OfType<JObject>())
                    action.Property("presentation", StringComparison.OrdinalIgnoreCase)?.Remove();
                var definition = carrierJson.ToObject<ExternalContentDefinition>();
                definition.Presentation = GameplayPresentationCatalog.ReadDto<ContentPresentationDefinition>(json["presentation"]);
                return definition;
            }).ToArray();
            Assert.That(identities.All(p => p.sha256 == Hash(p.path)), Is.True, "读取中的作者输入必须稳定");
            Save(TestContext.CurrentContext.Test.Name + "-作者定义输入", identities); return definitions;
        }
        [Test] public void CurrentAuthorTypedRowsResolveAndRemainBoundToExplicitEffectFacts()
        {
            var catalog = LoadDisplayCatalog(); var sources = ReadCurrentDefinitions();
            var metadata = PlayerDetailDisplayCatalog.Read(sources, catalog);
            var resolver = new GameplayTextResolver(catalog); var context = new GameplayTextContext { Catalog = catalog, ContentHash = catalog.ContentHash, Locale = catalog.Locale };
            var evidence = new List<object>();
            foreach (var source in sources.Where(s => metadata.Any(d => d.DefinitionId == s.DefinitionId)))
            {
                var registry = new EffectRegistry(); RegisterDisplay(registry, source, catalog, r => resolver.Resolve(r, context).ToPlainText());
                foreach (var detail in metadata.Where(d => d.DefinitionId == source.DefinitionId))
                {
                    Assert.That(detail.EffectTextRef, Is.Not.Null, detail.DefinitionId + ":" + detail.AbilityId);
                    var document = resolver.Resolve(detail.EffectTextRef, context);
                    Assert.That(document.ToPlainText(), Is.Not.Empty); Assert.That(document.ToPlainText(), Is.Not.EqualTo("此项说明暂不可用"));
                    var declaration = source.AbilitySources.Single(d => d.AbilityId == detail.AbilityId);
                    var activation = declaration.AuthorizationPolicy == "character_subscription" ? PlayerDetailActivation.Subscription : PlayerDetailActivation.Held;
                    var view = BareView(catalog); var item = AddCarrier(view, source, 1);
                    view.PlayerEffects.Add(new PlayerEffectFactView { FactId = "isolated:" + detail.AbilityId, SourceItemId = item.ItemId, DefinitionId = source.DefinitionId,
                        AbilityId = declaration.AbilityId, Activation = activation, DetailPlayerId = 1 });
                    var projected = PlayerDetailEntryProjector.Project(view, new[] { detail });
                    bool isMainDetail = detail.DisplayCategory == PlayerDetailDisplayCategory.PlayerEffect || detail.DisplayCategory == PlayerDetailDisplayCategory.StorageArea;
                    Assert.That(projected, Has.Count.EqualTo(isMainDetail ? 1 : 0), detail.DefinitionId + ":" + detail.AbilityId + " 与分类/事实协议不匹配");
                    if (isMainDetail) Assert.That(projected[0].EffectTextRef.Key, Is.EqualTo(detail.EffectTextRef.Key));
                    Assert.That(registry.AbilityPresentationDescriptions[detail.AbilityId], Is.EqualTo(document.ToPlainText()));
                    view.PlayerEffects.Clear(); Assert.That(PlayerDetailEntryProjector.Project(view, new[] { detail }), Is.Empty);
                    evidence.Add(new { source.DefinitionId, detail.AbilityId, activation, detail.DisplayCategory, isMainDetail, key = detail.EffectTextRef.Key, plainText = document.ToPlainText(), icons = document.Runs.OfType<IconRun>().Count() });
                }
            }
            Assert.That(evidence.Count, Is.GreaterThan(0));
            Save("当前作者typed详情消费", new { catalog.ContentHash, rows = evidence,
                scope = "元数据到正式投影/登记/解释器；显式公开效果事实隔离，不验证rule/grant准入、规则时序或合法开局。" });
        }
        private static GameStateView BareView(IGameplayTextCatalogSnapshot catalog)
        {
            var view = new GameStateView { GameId = "R18.author-display-only", ContentPackHash = catalog.ContentHash, ViewerRole = GameStateViewerRole.Player,
                ViewerPlayerId = 1, HasAbilityActionCatalog = true, LaunchPlayerOrder = new List<int> { 1, 2, 3 } };
            foreach (int player in view.LaunchPlayerOrder) view.Players.Add(new PlayerStateView { PlayerId = player, Name = "组件玩家" + player, Color = (PlayerColor)(player - 1), Resources = new ResourceSet() });
            return view;
        }
        private static ItemInstanceState AddCarrier(GameStateView view, ExternalContentDefinition source, int player)
        {
            string id = "display:" + source.DefinitionId; string active = "display-active:" + player;
            if (view.ItemZones.Zones.All(z => z.Zone.ZoneId != active)) view.ItemZones.Zones.Add(new ZoneStateView { Zone = new ZoneInstanceState { ZoneId = active, TemplateId = "core.zone.entrepreneur_active", OwnerPlayerId = player } });
            var item = new ItemInstanceState { ItemId = id, DefinitionId = source.DefinitionId, TypeId = source.ItemTypeId, ZoneId = active,
                ControllerSubject = ItemSubjectRef.Player(player), OwnerSubject = ItemSubjectRef.Player(player) };
            view.ItemZones.Items.Add(item);
            foreach (var zone in source.CardZones) view.ItemZones.Zones.Add(new ZoneStateView { Zone = new ZoneInstanceState {
                ZoneId = id + ":" + zone.LocalZoneId, LocalZoneId = zone.LocalZoneId, TemplateId = zone.ZoneTemplateId, OwnerPlayerId = player, ParentKind = ZoneParentKind.Item, ParentId = id } });
            return item;
        }
        private static void Click(Button button)
        {
            Assert.That(button.gameObject.activeInHierarchy && button.interactable, Is.True);
            var pointer = new PointerEventData(EventSystem.current ?? new GameObject("续验隔离指针", typeof(EventSystem)).GetComponent<EventSystem>()) { button = PointerEventData.InputButton.Left };
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerDownHandler); ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerUpHandler);
            Assert.That(ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler), Is.True);
        }
        [UnityTest] public IEnumerator CurrentDtmEffectParagraphsSurviveOriginalHudVisibilityAndScopeChanges()
        {
            yield return new EnterPlayMode();
            var catalog = LoadDisplayCatalog(); var sources = ReadCurrentDefinitions(); var view = BareView(catalog);
            var selected = new[] { "entrepreneur.ability.T09", "entrepreneur.ability.D09", "entrepreneur.ability.M13" };
            var metadata = PlayerDetailDisplayCatalog.Read(sources.Where(s => selected.Contains(s.DefinitionId)), catalog);
            foreach (string definitionId in selected)
            {
                int player = Array.IndexOf(selected, definitionId) + 1; var source = sources.Single(s => s.DefinitionId == definitionId); var item = AddCarrier(view, source, player);
                foreach (var declaration in source.AbilitySources) view.PlayerEffects.Add(new PlayerEffectFactView { FactId = "display:" + declaration.AbilityId,
                    SourceItemId = item.ItemId, DefinitionId = item.DefinitionId, AbilityId = declaration.AbilityId, DetailPlayerId = player,
                    Activation = declaration.AuthorizationPolicy == "character_subscription" ? PlayerDetailActivation.Subscription : PlayerDetailActivation.Held });
            }
            Production("UiTheme").GetMethod("Initialize", Flags).Invoke(null, new object[] { AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", Production("UiThemeCatalog")) });
            hudObject = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            var hud = hudObject.GetComponent(Production("GameplayInteractionHudView")); Call(hud, "ConfigureGameplayPresentation", catalog, new Dictionary<string,string>());
            var modules = Read(hud, "MainModules"); var frame = Read(hud, "Frame");
            Call(frame, "BindGameplaySnapshot", view);
            Call(modules, "Render", null, view, 1, Read(Read(hud, "DialogRegistry"), "CardVisualCatalog"));
            // 完整ExpansionUi.Refresh会经库存读取正式pack；其独立失败保存在a003，待作者/005最终交接后再验。
            // 这里仅用正式投影结果和实际HUD登记的行，验证原文字承载及生命周期，不替完整刷新报通过。
            var entries = PlayerDetailEntryProjector.Project(view, metadata).Where(e => e.EffectTextRef != null).ToArray();
            Assert.That(entries, Has.Length.EqualTo(6), "T09两段、D09一段、M13三段");
            Assert.That(entries.Single(e => e.AbilityId == "entrepreneur.T09.departure").StorageZoneIds, Has.Count.EqualTo(1), "零库存仍保实际子区和正文");
            var rows = new Dictionary<string, Component>();
            foreach (var entry in entries)
            {
                var host = (RectTransform)Call(modules, entry.DetailPlayerId == 1 ? "GetLocalDetailHost" : "GetOpponentDetailHost", entry.DetailPlayerId == 1 ? 0 : entry.DetailPlayerId);
                var row = (Component)Call(Read(hud, "DialogRegistry"), "InstantiateEffectRow", host); row.gameObject.SetActive(true); rows.Add(entry.EntryId, row);
                typeof(EntrepreneurDetailStabilityR18Tests).GetMethod("BindPersistentRow", Flags).Invoke(null,
                    new object[] { row, entry.EntryId, entry.DisplayName, new Func<string,int,bool>((_,__) => true) });
                Call(frame, "RenderGameplayText", ((Text)Read(row, "DescriptionText")).GetComponent(Production("GameplayTextView")),
                    Call(frame, "ResolveGameplayText", entry.EffectTextRef), false);
            }
            Call(modules, "RefreshDetailAvailability");
            var buttons = new[] { (Button)Read(modules, "localDetailToggle") }.Concat(((Button[])Read(modules, "opponentDetailToggles")).Take(2)).ToArray();
            Click(buttons[0]);
            for (int i = 0; i < 8; i++) yield return null;
            var resolver = new GameplayTextResolver(catalog); var context = new GameplayTextContext { Catalog = catalog, ContentHash = catalog.ContentHash, Locale = catalog.Locale };
            object[] Check(string stage, int expectedVisiblePlayer)
            {
                var measurements = new List<object>();
                foreach (var entry in entries)
                {
                    var row = (Component)rows[entry.EntryId]; var text = (Text)Read(row, "DescriptionText"); var host = text.GetComponent(Production("GameplayTextView"));
                    Assert.That(Read(host, "HasDocument"), Is.EqualTo(true), entry.AbilityId + ":" + stage);
                    var document = (GameplayTextDocument)Read(host, "document");
                    Assert.That(document.ToPlainText(), Is.EqualTo(resolver.Resolve(entry.EffectTextRef, context).ToPlainText()));
                    int effectiveSize = (int)Read(host, "EffectiveFontSize");
                    if (entry.DetailPlayerId == expectedVisiblePlayer)
                    {
                        Assert.That(row.gameObject.activeInHierarchy, Is.True, stage + ":" + entry.AbilityId);
                        Assert.That(effectiveSize, Is.GreaterThan(0), "已展开段落必须实际完成文字测量");
                        Assert.That((int)Read(host, "ActiveFragmentCount"), Is.GreaterThan(0), "已展开段落必须实际生成片段");
                    }
                    measurements.Add(new { stage, entry.AbilityId, key = entry.EffectTextRef.Key, text = document.ToPlainText(), active = row.gameObject.activeInHierarchy,
                        bodyActive = text.gameObject.activeInHierarchy, hostActive = host.gameObject.activeInHierarchy,
                        effectiveFontSize = effectiveSize, fragments = Read(host, "ActiveFragmentCount"), width = text.rectTransform.rect.width, height = text.rectTransform.rect.height,
                        fitsAtSourceSize = text.fontSize > 0 ? (bool?)Call(host, "FitsAtFontSize", text.fontSize) : null,
                        fitsAtEffectiveSize = effectiveSize > 0 ? (bool?)Call(host, "FitsAtFontSize", effectiveSize) : null });
                }
                return measurements.ToArray();
            }
            var snapshots = new List<object> { Check("本机T09原控件展开8帧", 1) };
            // 原UI只同时展开一个对手；逐位打开后检查，不能把已被另一位收回的D09当作可见量尺。
            for (int player = 2; player <= 3; player++)
            {
                Click(buttons[player - 1]); for (int i = 0; i < 8; i++) yield return null;
                snapshots.Add(Check("对手" + player + "原控件单独展开8帧", player));
            }
            for (int i = 0; i < 3; i++) { Call(modules, "Render", null, view, 1, Read(Read(hud, "DialogRegistry"), "CardVisualCatalog")); yield return null; }
            Click(buttons[0]); yield return null; Click(buttons[0]); for (int i = 0; i < 4; i++) yield return null;
            snapshots.Add(Check("本机原控件收回重开", 1));
            for (int player = 2; player <= 3; player++)
            {
                Click(buttons[player - 1]); yield return null; Click(buttons[player - 1]); yield return null;
                Click(buttons[player - 1]); for (int i = 0; i < 4; i++) yield return null;
                snapshots.Add(Check("对手" + player + "原控件收回重开", player));
            }
            Call(frame, "BindGameplaySnapshot", new object[] { null }); yield return null;
            Assert.That(entries.All(e => !(bool)Read(((Text)Read(rows[e.EntryId], "DescriptionText")).GetComponent(Production("GameplayTextView")), "HasDocument")), Is.True);
            Call(frame, "BindGameplaySnapshot", view);
            foreach (var entry in entries) Call(frame, "RenderGameplayText", ((Text)Read(rows[entry.EntryId], "DescriptionText")).GetComponent(Production("GameplayTextView")),
                Call(frame, "ResolveGameplayText", entry.EffectTextRef), false);
            for (int i = 0; i < 4; i++) yield return null; snapshots.Add(Check("作用域返回并重新消费正式引用", 3));
            Save("当前TDM原HUD显示消费", new { catalog.ContentHash, snapshots,
                scope = "正式作者六段+实际HUD面板/登记行/展开控件+原Frame；完整ExpansionUi.Refresh的a003失败单列待办，不计该刷新、实际库存计数、正常局或rule/grant准入通过。" });
        }
        [UnityTearDown] public IEnumerator LeavePlayMode()
        {
            if (hudObject != null) UnityEngine.Object.Destroy(hudObject);
            if (UnityEngine.Application.isPlaying) yield return new ExitPlayMode();
        }
    }
}
