using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Presentation;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>仅正式装配和动态消费者隔离验证；不计正常菜单、游戏画面或多局验收。</summary>
    public sealed class AbilityActionPresentationContractTests
    {
        [TestCase("A", "entrepreneur.ability.A01", "A09,A08,A06,A07,A05,A03", 11)]
        [TestCase("D", "entrepreneur.ability.D09", "D07,D08", 2)]
        [TestCase("P", "entrepreneur.ability.P09", "P01,P02,P03,P05,P06", 5)]
        [TestCase("M", "entrepreneur.ability.M13", "M05,M06,M09,M10,M11,M12", 6)]
        public void FormalSelectedFacesProjectEveryActionWithIndependentBodyAndWrapperText(string family, string entrepreneur, string faces, int count)
        {
            using var f = new PaulContentTestFixture(primaryEntrepreneur: entrepreneur, setupOnly: true,
                secondaryEntrepreneur: family == "D" ? "entrepreneur.ability.P09" : "entrepreneur.ability.D09");
            var initial = GameStateCloneService.DeepClone(f.State);
            var evidence = new List<object>();
            var seen = new HashSet<string>();
            foreach (string face in faces.Split(','))
            {
                // 每面在同一隔离起点的独立分支上走正式选择，不伪称同局同时选了互斥面。
                f.State = GameStateCloneService.DeepClone(initial);
                var declaration = f.Registry.AbilityDeclarations.First(s => s.ActiveFaceId == face && s.Actions.Count > 0);
                int round = f.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).RoundGroups
                    .Single(g => g.Candidates.Any(c => c.FaceId == face)).RoundNumber;
                f.Choose(round, face);
                var actions = f.Registry.QueryActions(f.State, 1);
                var visible = GameStateViewProjector.Project(f.State, GameStateViewer.Player(1), null, actions, f.Registry);
                foreach (var source in f.Registry.AbilityDeclarations.Where(s => s.ActiveFaceId == face))
                foreach (var action in source.Actions)
                {
                    var option = visible.AbilityActions.Single(o => o.ActionId == action.ActionId);
                    Assert.That(option.SourceRef.ActiveFaceId, Is.EqualTo(face));
                    Assert.That(option.DisplayName, Is.Not.Empty);
                    Assert.That(option.Description, Is.Not.Empty);
                    Assert.That(option.DisplayNameRef.Key, Is.EqualTo(action.Presentation.Name.Key));
                    Assert.That(option.DescriptionRef.Key, Is.EqualTo(action.Presentation.Description.Key));
                    var definition = f.Catalog.Definitions.Single(d => d.DefinitionId == source.DefinitionId);
                    Assert.That(option.DescriptionRef.Key, Is.Not.EqualTo(definition.Presentation?.Description?.Key), "动作正文独立于整卡详情；旧整卡可以仍是字面文本");
                    Assert.That(option.VersionRef.HandlerHash, Is.EqualTo(f.Registry.GetAbilityHandlerHash(action.BodyHandlerRef)));
                    Assert.That(option.EntryCostDescriptionRef != null, Is.EqualTo(action.Presentation.EntryCost != null));
                    Assert.That(option.EntryAfterEffectsDescriptionRef != null, Is.EqualTo(action.Presentation.EntryAfterEffects != null));
                    Assert.That(seen.Add(action.ActionId), Is.True);
                    evidence.Add(new { face, option, action.BodyHandlerRef, action.EntryCostRef, action.EntryAfterEffectsRef, action.UsageTrackId });
                }
            }
            Assert.That(seen.Count, Is.EqualTo(count));
            Assert.That(f.Registry.AbilityDeclarations.Where(s => s.AbilityId.StartsWith("entrepreneur.T", StringComparison.Ordinal))
                .SelectMany(s => s.Actions), Is.Empty, "T没有为展示凑出的主动入口");
            f.Artifacts.WriteLabeledText("正式选面到动作投影展示", JsonConvert.SerializeObject(evidence, Formatting.Indented));
        }

        [Test]
        public void CharacterAndUnlistedActionsResolveExternalTextAndReachBothExistingConsumers()
        {
            var artifacts = IsolatedTestArtifacts.BeginCase();
            GameObject hud = null, canvas = null;
            object dialog = null;
            try
            {
                string baseline = artifacts.CopyContent(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"), "动作展示完整私有基线");
                var formalPack = ExternalContentPack.Load(baseline);
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var loadedFiles = (IDictionary<string, byte[]>)typeof(ExternalContentPack).GetField("files", flags).GetValue(formalPack);
                artifacts.WriteLabeledText("正式包加载哈希依据", JsonConvert.SerializeObject(new { formalPack.ContentHash, formalPack.PresentationContentHash,
                    paths = loadedFiles.Keys.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
                    extensionIdentity = (string)typeof(ExternalContentPack).GetMethod("PresentationExtensionIdentity", flags).Invoke(formalPack, null) }, Formatting.Indented));
                const string sourceId = "texas", actionId = "qy002.presentation.unlisted";
                var sourceJson = JObject.Parse(File.ReadAllText(Path.Combine(baseline, "character/texas.json")));
                JObject Text(string key, int? amount = null) => JObject.FromObject(amount.HasValue ?
                    new { key, arguments = new[] { new { name = "amount", kind = "integer", value = amount.Value } } } : (object)new { key });
                var display = new JObject { ["name"] = Text("core:qy002.presentation.name"), ["description"] = Text("core:qy002.presentation.body", 7) };
                ((JArray)sourceJson["abilitySources"]).Add(new JObject {
                    ["protocolVersion"] = 1, ["abilityId"] = actionId, ["kind"] = "quick", ["handlerRef"] = actionId,
                    ["declarationVersion"] = "1.0.0", ["authorizationPolicy"] = "character_covered", ["effectiveZones"] = new JArray("core.zone.character_covered"),
                    ["presentationRef"] = sourceId, ["actions"] = new JArray(new[] { "quick", "special" }.Select(kind => new JObject {
                        ["actionId"] = actionId + "." + kind, ["entryKind"] = kind, ["bodyHandlerRef"] = actionId,
                        ["presentationRef"] = sourceId, ["presentation"] = display.DeepClone() })) });
                sourceJson["abilityHandlers"] = new JArray(new JObject { ["handlerId"] = actionId, ["script"] = "lua/qy002.presentation.lua", ["version"] = "1.0.0" });
                var textJson = JObject.Parse(File.ReadAllText(Path.Combine(baseline, "presentation/texts.zh-CN.json")));
                textJson["messages"]["core:qy002.presentation.name"] = new JObject { ["parameters"] = new JObject(), ["template"] = "非内置展示入口" };
                textJson["messages"]["core:qy002.presentation.body"] = new JObject { ["parameters"] = new JObject { ["amount"] = "integer" }, ["template"] = "[b]动态说明[/b]：{amount}" };
                string input = artifacts.CopyContent(baseline, "非内置动作通用展示派生输入", new Dictionary<string, string> {
                    ["character/texas.json"] = sourceJson.ToString(), ["presentation/texts.zh-CN.json"] = textJson.ToString(),
                    ["lua/qy002.presentation.lua"] = "return function(ctx) return {} end" });
                var pack = ExternalContentPack.Load(input);
                var configuration = new GameRuleConfiguration { MapId = "map-three-players", PlayerCount = 2,
                    EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
                var catalog = ActiveContentCatalogBuilder.Build(pack, configuration);
                configuration.ContentPackHash = catalog.ContentPackHash;
                var registry = new EffectRegistry(); ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); catalog.Register(registry);
                CharacterCoverEffectExecutor.Register(registry);
                var map = StaticMapDefinitions.Resolve(configuration.MapId);
                var initial = new GameState { GameId = "qy002.presentation." + Guid.NewGuid().ToString("N"), ContentPackHash = catalog.ContentPackHash,
                    MapId = map.MapId, ContentDefinitions = catalog.ContentDefinitions, RuleConfiguration = configuration,
                    Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "甲" }, new PlayerState { PlayerId = 2, Name = "乙" } } };
                var state = new ItemZoneStateFactory().CreateForSetup(initial, map, new[] {
                    new ItemRecipeEntry { EntryId = "character", DefinitionId = "texas", TypeId = "core.basic_character_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" } });
                state.Round = 1; state.ActionRound = 1; state.Phase = GamePhase.CharacterCover; state.CurrentPlayerId = 1; state.StartPlayerId = 1;
                var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1));
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
                string card = state.ItemZones.Items.Single(i => i.DefinitionId == "texas" && i.ControllerSubject.PlayerId == 1).ItemId;
                Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString(card), out var reason), Is.True, reason);
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                state.Phase = GamePhase.ActionRound1;
                var options = registry.QueryActions(state, 1);
                var role = options.Single(o => o.ActionId == "texas.use");
                string rawName = catalog.Definitions.Single(d => d.DefinitionId == "texas").DisplayName;
                Assert.That(role.DisplayName, Is.EqualTo("使用角色牌：" + rawName));
                Assert.That(state.ContentDefinitions.Characters.Single(c => c.TemplateId == "texas").Name, Is.EqualTo(rawName));
                var custom = options.Single(o => o.ActionId == actionId + ".quick");
                var special = options.Single(o => o.ActionId == actionId + ".special");
                Assert.That(custom.DisplayName, Is.EqualTo("非内置展示入口"));
                Assert.That(custom.Description, Is.EqualTo("动态说明：7"));
                var resolver = new GameplayTextResolver(catalog.TextCatalog);
                var context = new GameplayTextContext { Catalog = catalog.TextCatalog, ContentHash = catalog.ContentPackHash,
                    DefinitionNames = catalog.Definitions.ToDictionary(d => d.DefinitionId, d => d.DisplayName) };
                GameplayTextDocument Resolve(GameplayTextRef reference) => resolver.Resolve(reference, context);
                var bodyDocument = AbilityActionTextDocuments.Details(special, Resolve);
                Assert.That(bodyDocument.Runs.OfType<NumberRun>().Any(r => r.Value == 7), Is.True);
                Assert.That(bodyDocument.Runs.Any(r => r.Style.WeightMode == "Bold"), Is.True);

                hud = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
                var hudView = hud.GetComponentInChildren(Type.GetType("YC.Presentation.GameplayInteractionHudView, Assembly-CSharp", true), true);
                object Property(object target, string name) => target.GetType().GetProperty(name).GetValue(target);
                var panel = Property(hudView, "ActionPanelView"); var dialogs = Property(hudView, "DialogRegistry");
                var type = Type.GetType("YC.Presentation.ActionPanelController, Assembly-CSharp", true);
                var arguments = new object[11]; arguments[0] = panel; arguments[1] = Property(dialogs, "CardVisualCatalog");
                var controller = type.GetMethod("Bind").Invoke(null, arguments);
                var rendered = new List<string>();
                var styleType = Type.GetType("YC.Presentation.GameplayTextStyle, Assembly-CSharp", true);
                var iconsType = Type.GetType("YC.Presentation.GameplayIconCatalog, Assembly-CSharp", true);
                var style = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayTextStyle.asset", styleType);
                var icons = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayIconCatalog.asset", iconsType);
                void Render(Component host, GameplayTextDocument doc, bool singleLine)
                {
                    host.GetType().GetMethod("Configure").Invoke(host, new object[] { host.GetComponent<Text>(), style, icons, catalog.TextCatalog });
                    host.GetType().GetMethod("Bind").Invoke(host, new object[] { doc });
                    rendered.Add(doc.ToPlainText());
                }
                type.GetProperty("ResolveActionText").SetValue(controller, (Func<GameplayTextRef, GameplayTextDocument>)Resolve);
                type.GetProperty("RenderActionText").SetValue(controller, (Action<Component, GameplayTextDocument, bool>)Render);
                var visible = GameStateViewProjector.Project(state, GameStateViewer.Player(1), null, options, registry);
                var model = new YC.Presentation.Workflows.CharacterCardPanelPresenter().BuildProjectedView(state, 1, visible);
                type.GetMethod("RenderCharacterQuickAction").Invoke(controller, new object[] { model });
                Assert.That(rendered.Any(t => t.StartsWith(role.DisplayName, StringComparison.Ordinal)), Is.True);
                Assert.That(rendered.Any(t => t.StartsWith(custom.DisplayName, StringComparison.Ordinal)), Is.True);

                canvas = new GameObject("动作展示隔离画布", typeof(RectTransform), typeof(Canvas));
                var dialogType = Type.GetType("YC.Presentation.CharacterCardEffectChoiceDialog, Assembly-CSharp", true);
                dialog = Activator.CreateInstance(dialogType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, new object[] { dialogs, (RectTransform)canvas.transform }, null);
                dialogType.GetMethod("BindGameplayText").Invoke(dialog, new object[] { (Func<GameplayTextRef, GameplayTextDocument>)Resolve,
                    (Action<Component, GameplayTextDocument, bool>)Render });
                var optionType = Type.GetType("YC.Presentation.EffectDialogOption, Assembly-CSharp", true);
                var choice = Activator.CreateInstance(optionType, new object[] { special.DisplayName, (Action)(() => { }), true });
                optionType.GetField("StableId").SetValue(choice, special.OptionId);
                optionType.GetField("SourceLabel").SetValue(choice, special.DisplayName);
                optionType.GetField("DescriptionLabel").SetValue(choice, special.Description);
                optionType.GetField("LabelDocument").SetValue(choice, AbilityActionTextDocuments.Name(special, Resolve));
                optionType.GetField("DescriptionDocument").SetValue(choice, bodyDocument);
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(optionType)); list.Add(choice);
                rendered.Clear();
                dialogType.GetMethod("ShowExecutionChoices").Invoke(dialog, new object[] { "隔离动作", "仅验证已有列表消费", list, (Func<bool>)(() => true) });
                Assert.That(rendered, Does.Contain(special.DisplayName));
                Assert.That(rendered, Does.Contain(special.Description));
                artifacts.RecordContent("非内置声明实际生产装配", input, catalog.ContentPackHash);
                artifacts.WriteLabeledText("角色及非角色实际候选和动态消费者", JsonConvert.SerializeObject(new { role, custom, special, rendered, rawName, normalGameCoverage = false }, Formatting.Indented));
            }
            finally
            {
                dialog?.GetType().GetMethod("Hide")?.Invoke(dialog, null);
                if (hud != null) UnityEngine.Object.DestroyImmediate(hud);
                if (canvas != null) UnityEngine.Object.DestroyImmediate(canvas);
                artifacts.Finish();
            }
        }
    }
}
