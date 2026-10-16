using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Infrastructure.Multiplayer;
using YC.Presentation.Workflows;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.PlayMode.ExpansionRules
{
    public sealed class PlayerDetailSceneTests
    {
        private const BindingFlags Access = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private EnterpriseGameplayUiTests scene;
        private IReadOnlyList<PlayerDetailDisplayDefinition> display;
        private FieldInfo artworkPackField;
        private object previousArtworkPack;
        private static object Get(object obj, string name) => obj.GetType().GetField(name, Access).GetValue(obj);
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Access).SetValue(obj, value);
        private static object Prop(object obj, string name) => obj.GetType().GetProperty(name, Access).GetValue(obj);
        private static void Property(object obj, string name, object value) => obj.GetType().GetProperty(name, Access).SetValue(obj, value);
        private static object Call(object obj, string name, params object[] args) => obj.GetType().GetMethods(Access)
            .Single(method => method.Name == name && method.GetParameters().Length == args.Length && method.GetParameters()
                .Select((parameter, i) => args[i] == null || parameter.ParameterType.IsByRef || parameter.ParameterType.IsInstanceOfType(args[i])).All(value => value)).Invoke(obj, args);
        private GameState State => (GameState)Get(scene, "state");
        private GameStateView View => (GameStateView)Get(scene, "view");
        private Component Ui => (Component)Get(scene, "adapter");
        private Component Hud => (Component)Get(scene, "hud");
        private EffectRegistry Registry => (EffectRegistry)Get(scene, "registry");
        private IReadOnlyList<PlayerDetailEntry> Entries => (IReadOnlyList<PlayerDetailEntry>)Prop(Ui, "DetailEntries");
        private List<Component> Rows => ((IEnumerable)Prop(Ui, "Rows")).Cast<Component>().ToList();
        private void Refresh()
        {
            Call(scene, "RefreshView"); Call(Ui, "NotifyCommandSettled"); Call(scene, "RenderDetails");
            var model = new CharacterCardPanelPresenter().BuildProjectedView(State, View.ViewerPlayerId, View);
            var panel = Prop(Hud, "CharacterHandPanel");
            var catalog = Prop(Prop(Hud, "DialogRegistry"), "CardVisualCatalog");
            foreach (var card in model.HandCards.Concat(model.CoveredCards).Where(card => model.ResolveDefinitionId(card.CardId).StartsWith("infected.character.", StringComparison.Ordinal)))
                Assert.That(Call(catalog, "GetCharacterFront", model.ResolveDefinitionId(card.CardId)), Is.Not.Null, "受控组合必须使用真实卡面素材。");
            Call(panel, "Render", View.ViewerPlayerId, model);
            Assert.That(((IEnumerable)Prop(panel, "OrderedHand")).Cast<CharacterCardHandItemViewModel>().Select(card => card.CardId),
                Is.EquivalentTo(model.HandCards.Select(card => card.CardId)));
        }
        private void Run(EffectSpec spec) => Call(scene, "Run", spec);
        private ItemInstanceState Item(string definition) => State.ItemZones.Items.Single(item => item.DefinitionId == definition);
        private ItemInstanceState Add(ExternalContentDefinition source, int player, string role)
        {
            var item = new ItemInstanceState { ItemId = StableIdFactory.Create("detail_fixture", State.GameId, source.DefinitionId, player.ToString()),
                DefinitionId = source.DefinitionId, TypeId = source.ItemTypeId, PhysicalKind = ItemPhysicalKind.Card,
                OwnerSubject = ItemSubjectRef.Player(player), ControllerSubject = ItemSubjectRef.Player(player), ZoneId = role == "player_awards" ? State.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.player_awards" && zone.OwnerPlayerId == player).ZoneId : role == "reward_pool" ? State.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.reward_pool" && zone.LocalZoneId == "awards").ZoneId : FindZone(State, role, role == "flowing_shapes" ? -1 : player).ZoneId };
            State.ItemZones.Items.Add(item); State.ItemZones.Orders.Find(order => order.ZoneId == item.ZoneId)?.ItemIds.Add(item.ItemId);
            foreach (var zone in source.CardZones) Assert.That(new ItemZoneStateFactory().TryCreateCardZone(State, item.ItemId, zone, out _, out var reason), Is.True, reason);
            return item;
        }
        private void Drain()
        {
            for (int i = 0; i < 100; i++)
            {
                var request = State.EffectRuntime.InteractionRequests.LastOrDefault(value => value.Status == "open");
                if (request == null) return;
                var projection = YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, request.AnsweringPlayerId);
                var eligible = request.CandidateIds.Where(id => id != "decline" && id != "choice.skip" && id != "stop").ToList();
                var candidates = request.CandidateIds.Contains("hand") ? new[] { "hand" } : (eligible.Count > 0 ? eligible : request.CandidateIds).Take(Math.Max(1, request.MinSelections)).ToArray();
                var result = new AnswerInteractionCommandHandler(Registry).Handle(State, EffectInteractionCommands.Answer(projection, request.AnsweringPlayerId, candidates));
                Assert.That(result.Succeeded, Is.True, result.LogMessage);
            }
            Assert.Fail("角色真实流程未完成。");
        }
        private void Use(string key)
        {
            var card = Item("infected.character." + key);
            Run(EffectSpec.Create("detail.fixture.cover", Object("cardId", NormalizedValue.CreateString(card.ItemId)), 1));
            Run(CharacterLifecycleService.CreateUseSpec(1, card.ItemId, "tactic")); Drain();
        }
        private void ConfigureRealContent()
        {
            foreach (var player in State.Players) player.Color = (PlayerColor)(player.PlayerId - 1);
            var root = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core");
            var sources = new List<ExternalContentDefinition>();
            foreach (var dir in new[] { "character", "award", "contract" })
            foreach (var file in Directory.GetFiles(Path.Combine(root, dir), "*.json"))
                sources.Add(JsonConvert.DeserializeObject<ExternalContentDefinition>(File.ReadAllText(file)));
            display = PlayerDetailDisplayCatalog.Read(sources); Property(Ui, "DetailDefinitions", display);
            var keys = new[] { "amiya", "muelsyse", "swire", "flowing_shape_alpha", "flowing_shape_beta", "flowing_shape_gamma" };
            var originals = new[] { "2491142855109755480", "2410077611324906006", "2491142855115552708", "2411202693614542236", "2411202693614543214", "2411202693614543818" };
            var staged = Path.GetFullPath(Path.Combine(root, "../../../../Logs/ExpansionValidation/PlayerDetails/content", Guid.NewGuid().ToString("N")));
            void Copy(string source, string relative) { string target = Path.Combine(staged, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(source, target, true); }
            var paths = new List<string>();
            for (int i = 0; i < keys.Length; i++)
            {
                string path = "character/infected.character." + keys[i] + ".json"; paths.Add(path);
                Copy(Path.Combine(root, path), path);
                var source = sources.Single(value => value.DefinitionId == "infected.character." + keys[i]);
                foreach (var ability in source.Abilities)
                    foreach (var script in new[] { ability.Script, ability.CleanupScript }.Where(value => !string.IsNullOrEmpty(value))) Copy(Path.Combine(root, script), script);
                var art = (string)JObject.Parse(File.ReadAllText(Path.Combine(root, path)))["artwork"];
                Copy(Directory.GetFiles(Path.Combine(Directory.GetCurrentDirectory(), "游城拓荒/感染者扩")).Single(file => Path.GetFileName(file).Contains(originals[i])), art);
            }
            var award = sources.Single(value => value.DefinitionId == "infected.award.enterprise_special");
            string awardPath = "award/" + award.DefinitionId + ".json"; paths.Add(awardPath); Copy(Path.Combine(root, awardPath), awardPath);
            foreach (var ability in award.Abilities) Copy(Path.Combine(root, ability.Script), ability.Script);
            Copy(Directory.GetFiles(Path.Combine(Directory.GetCurrentDirectory(), "游城拓荒/感染者扩")).Single(file => Path.GetFileName(file).Contains("2411202693639234383")), award.Artwork);
            File.WriteAllText(Path.Combine(staged, "artwork_slices.json"), new JObject {
                ["schemaVersion"] = 1, ["coordinateOrigin"] = "bottom-left",
                ["sheets"] = new JArray(new JObject { ["id"] = award.ArtworkSpriteId, ["artwork"] = award.Artwork,
                    ["width"] = 600, ["height"] = 850, ["rows"] = 1, ["columns"] = 1, ["cellWidth"] = 600, ["cellHeight"] = 850, ["skippedCells"] = new JArray() }),
                ["slices"] = new JArray(new JObject { ["id"] = award.ArtworkSpriteId, ["sheetId"] = award.ArtworkSpriteId,
                    ["cell"] = 1, ["row"] = 1, ["column"] = 1, ["name"] = award.DisplayName, ["contentType"] = "award",
                    ["definitionIds"] = new JArray(award.DefinitionId), ["expansionId"] = "infected",
                    ["rect"] = new JObject { ["x"] = 0, ["y"] = 0, ["width"] = 600, ["height"] = 850 } }) }.ToString());
            File.WriteAllText(Path.Combine(staged, "pack.json"), new JObject { ["schemaVersion"] = 3, ["packId"] = "core", ["version"] = "1.0.0",
                ["definitions"] = new JArray(paths), ["enabledExpansionIds"] = new JArray("infected"),
                ["artworkSlices"] = "artwork_slices.json", ["requiredCapabilities"] = new JArray(), ["setupRecipes"] = new JArray(), ["expansionDependencies"] = new JArray() }.ToString());
            var pack = ExternalContentPack.Load(staged);
            // 只切换当前测试的素材读取目录；规则仍由上面的显式内容装配，正式pack文件不变。
            artworkPackField = Ui.GetType().Assembly.GetType("YC.Presentation.ExternalContentRuntime").GetField("pack", BindingFlags.Static | BindingFlags.NonPublic);
            previousArtworkPack = artworkPackField.GetValue(null);
            artworkPackField.SetValue(null, pack);
            State.ContentDefinitions.Characters.AddRange(pack.CreateCharacters()); Registry.ContentPackHash = State.ContentPackHash = pack.ContentHash;
            State.RuleConfiguration.ContentPackHash = pack.ContentHash;
            CharacterCardLuaCatalog.Register(Registry, pack); ItemTransferEffectExecutor.Register(Registry);
            State.ContentDefinitions.Awards.AddRange(ContentLifecycleLuaCatalog.CreateAwards(pack)); ContentLifecycleLuaCatalog.Register(Registry, pack);
            var map = new MapQueryService(StaticMapDefinitions.Resolve(State.MapId)); InfluenceEffectExecutor.Register(Registry, new InfluenceService(map));
            Registry.Register("detail.fixture.cover", context => CharacterLifecycleService.Cover(context, Text(context.Node.NormalizedArguments, "cardId"), true));
            foreach (var source in pack.ActiveDefinitions.Where(value => value.ContentType == "character")) Add(source, 1, source.DefinitionId.Contains("flowing_shape") ? "flowing_shapes" : "hand");
            Property(Ui, "CharacterDefinition", (Func<string, CharacterCardDefinition>)(id => CharacterCardDatabase.Get(State, id)));
            Use("amiya"); Use("muelsyse"); Use("swire");
            Add(sources.Single(value => value.DefinitionId == "infected.award.permanent_support"), 1, "player_awards");
            Add(sources.Single(value => value.DefinitionId == "infected.award.permanent_support"), 2, "player_awards");
            Add(sources.Single(value => value.DefinitionId == "infected.contract.lifetime_security"), 1, "player_awards");
            Add(sources.Single(value => value.DefinitionId == "infected.award.enterprise_special"), 1, "player_awards");
            Add(sources.Single(value => value.DefinitionId == "infected.award.score"), 1, "reward_pool");
            Refresh();
        }
        [UnityTearDown] public IEnumerator Release()
        {
            try { if (scene != null) yield return scene.ReleaseUi(); }
            finally { artworkPackField?.SetValue(null, previousArtworkPack); artworkPackField = null; previousArtworkPack = null; scene = null; }
        }
        [UnityTest] public IEnumerator RealContentsInThreePlayerScene() { yield return ValidateScene(3); }
        [UnityTest] public IEnumerator RealContentsInFourPlayerScene() { yield return ValidateScene(4); }
        private IEnumerator ValidateScene(int count)
        {
            scene = new EnterpriseGameplayUiTests(); yield return scene.LoadSceneWithPlayers(count); ConfigureRealContent();
            Assert.That(Entries.Count, Is.EqualTo(6));
            Assert.That(Entries.Count(entry => entry.DetailPlayerId == 2), Is.EqualTo(1));
            Assert.That(Entries.Single(entry => entry.DisplayName == "缪尔赛思").CoverSlotIds.Count, Is.EqualTo(1));
            Assert.That(Entries.Single(entry => entry.DisplayName == "缪尔赛思").CanInspectCoverContents, Is.True);
            Assert.That(Entries.Single(entry => entry.DisplayName == "诗怀雅").StorageZoneIds.Count, Is.EqualTo(1));
            var restored = JsonConvert.DeserializeObject<InitialGameStateViewDto>(JsonConvert.SerializeObject(new InitialGameStateViewDto { View = View })).View;
            Assert.That(PlayerDetailEntryProjector.Project(restored, display).Select(entry => entry.EntryId), Is.EqualTo(Entries.Select(entry => entry.EntryId)));
            yield return Capture(count + "人-明细收起", new Vector2Int(1920, 1080));
            Call(Prop(Hud, "MainModules"), "ToggleLocalDetail");
            foreach (var size in new[] { new Vector2Int(1920, 1080), new Vector2Int(1024, 768) })
            {
                yield return Capture(count + "人-完整文案-" + size.x, size);
                foreach (var row in Rows.Where(row => row.gameObject.activeInHierarchy)) CheckGeometryAndText(row);
            }
            var storage = Entries.Single(entry => entry.DisplayName == "诗怀雅");
            Call(Ui, "InspectDetail", storage);
            Assert.That(((IReadOnlyList<string>)Prop(Ui, "InspectedItemIds")).Count, Is.GreaterThan(0));
            Assert.That(((IList)Get(scene, "commands")).Count, Is.Zero);
            yield return Capture(count + "人-真实储物区域", new Vector2Int(1920, 1080));
            Set(scene, "viewer", 2); Refresh();
            Assert.That(Entries.Count(entry => entry.DetailPlayerId == 2), Is.EqualTo(1));
            Assert.That(View.CharacterUses, Is.Empty);
            var privateArea = Entries.Single(entry => entry.DisplayName == "缪尔赛思");
            Assert.That(privateArea.HasCoverArea, Is.True);
            Assert.That(privateArea.CanInspectCoverContents, Is.False);
            Assert.That(privateArea.CoverSlotIds, Is.Empty);
            Call(Ui, "InspectDetail", privateArea);
            Assert.That((IReadOnlyList<string>)Prop(Ui, "InspectedItemIds"), Is.Empty);
            Assert.That(Hud.GetComponentsInChildren<Text>().Any(text => text.text == (string)Get(Ui, "privateContentsText")), Is.True);
            Refresh();
            Call(Prop(Hud, "MainModules"), "ToggleLocalDetail");
            yield return Capture(count + "人-热座持有者", new Vector2Int(1024, 768));
        }
        [UnityTest] public IEnumerator RealQuickAwardUsesOnlyQuickListAndOneHostCommand()
        {
            scene = new EnterpriseGameplayUiTests(); yield return scene.LoadSceneWithPlayers(3); ConfigureRealContent();
            Call(scene, "PublishFourBoards");
            var award = Item("infected.award.enterprise_special");
            Call(scene, "Place", award, State.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.reward_pool" && zone.LocalZoneId == "awards").ZoneId);
            Run(EffectSpec.Create(RewardAcquisitionFlow.TypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "poolRole", NormalizedValue.CreateString("awards")), 1));
            var request = State.EffectRuntime.InteractionRequests.Last(value => value.Status == "open");
            var result = new AnswerInteractionCommandHandler(Registry).Handle(State,
                EffectInteractionCommands.Answer(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1), 1, new[] { award.ItemId }));
            Assert.That(result.Succeeded, Is.True, result.LogMessage); Refresh();
            var option = View.CharacterSettlementOptions.Single(value => value.SourceItemId == award.ItemId);
            var cooperationNames = (Text[])Get(Ui, "cooperationNames");
            Assert.That(cooperationNames.Count(text => text.gameObject.activeInHierarchy && !string.IsNullOrEmpty(text.text)), Is.EqualTo(4));
            Assert.That(((Image[])Get(Ui, "cooperationGrades")).Count(image => image.gameObject.activeInHierarchy && image.enabled && image.sprite != null), Is.GreaterThan(0));
            ((Button)Get(Ui, "cooperationOpenButton")).onClick.Invoke();
            Assert.That(((Component)Get(Ui, "publicBoards")).gameObject.activeInHierarchy, Is.True);
            Assert.That(((IList)Get(scene, "commands")).Count, Is.Zero);
            ((Component)Get(Ui, "publicBoards")).gameObject.SetActive(false);
            Assert.That(option.IsEnabled, Is.True, option.DisabledReasonCode);
            Assert.That(Entries.Any(entry => entry.SourceItemId == award.ItemId), Is.False);
            Assert.That(((IDictionary)Get(Ui, "settlementRows")).Contains(option.OptionId), Is.False);
            var quick = ((IDictionary)Get(Ui, "quickRows"))[option.OptionId]; Assert.That(quick, Is.Not.Null);
            var tabs = Hud.GetComponentsInChildren<MonoBehaviour>(true).Single(value => value.GetType().Name == "UiMainActionTabs");
            ((Button[])Get(tabs, "tabs"))[1].onClick.Invoke();
            yield return Capture("真实快速授勋入口与合作区", new Vector2Int(1920, 1080));
            var button = (Button)Prop(quick, "Button"); button.onClick.Invoke(); button.onClick.Invoke();
            var commands = (List<GameCommand>)Get(scene, "commands"); Assert.That(commands.Count, Is.EqualTo(1));
            Assert.That(commands[0].Kind, Is.EqualTo(GameCommandKind.InvokeSettlementOption));
            result = new InvokeSettlementOptionCommandHandler(Registry).Handle(State, commands[0]); Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Drain(); Refresh();
            Assert.That(award.ZoneId, Is.EqualTo(FindZone(State, "retired").ZoneId));
            Assert.That(((IDictionary)Get(Ui, "quickRows")).Count, Is.Zero);
            Assert.That(((IDictionary)Get(Ui, "settlementRows")).Contains(option.OptionId), Is.False);
            int score = State.FindPlayer(1).Resources.GoldVoucher;
            result = new InvokeSettlementOptionCommandHandler(Registry).Handle(State, commands[0]); Assert.That(result.Succeeded, Is.True);
            Assert.That(State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(score));
        }
        [UnityTest] public IEnumerator CooperationThreePlayersUsesSuppliedCompositeMarkers() => CheckCooperation(3);
        [UnityTest] public IEnumerator CooperationFourPlayersUsesSuppliedCompositeMarkers() => CheckCooperation(4);
        private IEnumerator CheckCooperation(int count)
        {
            scene = new EnterpriseGameplayUiTests(); yield return scene.LoadSceneWithPlayers(count);
            foreach (var player in State.Players) player.Color = (PlayerColor)(player.PlayerId - 1);
            Assert.That(State.Players.Select(player => player.Color), Is.Unique);
            Call(scene, "PublishFourBoards"); Refresh();
            var grades = (Image[])Get(Ui, "cooperationGrades");
            var emptySlots = (GameObject[])Get(Ui, "cooperationEmptySlots");
            var logos = (Image[])Get(Ui, "cooperationLogos");
            var headers = (GameObject)Get(Ui, "cooperationLevelHeaders");
            Assert.That(headers.GetComponentsInChildren<Text>().Select(text => text.text), Is.EqualTo(new[] { "0", "1", "2", "3", "4", "5" }));
            Assert.That(logos.All(image => image.enabled && image.sprite != null), Is.True);
            var atlas = Get(Ui, "enterpriseArtwork");
            for (int i = 0; i < 8; i++)
            {
                var logo = (Sprite)Call(atlas, "Logo", "enterprise-" + i);
                Assert.That(logo, Is.Not.Null);
                Assert.That(logo.texture, Is.SameAs(logos[0].sprite.texture));
                Assert.That(logo.rect.width, Is.LessThan(logo.texture.width / 4f));
            }
            var board = State.ItemZones.Items.First(item => State.ItemZones.Zones.Any(zone => zone.ZoneId == item.ZoneId && zone.TemplateId == "core.zone.enterprise_display"));
            var tracks = State.ItemZones.Zones.Where(zone => zone.ParentId == board.ItemId && zone.LocalZoneId.StartsWith("cooperation_", StringComparison.Ordinal)).Select(zone => zone.ZoneId).ToArray();
            var markers = State.ItemZones.Items.Where(item => tracks.Contains(item.ZoneId)).ToArray();
            Assert.That(markers.Length, Is.EqualTo(count));
            int ColorBit(int player) => State.FindPlayer(player).Color == PlayerColor.Green ? 1 : State.FindPlayer(player).Color == PlayerColor.Yellow ? 2 : State.FindPlayer(player).Color == PlayerColor.Blue ? 4 : 8;
            int allColors = markers.Aggregate(0, (mask, marker) => mask | ColorBit(marker.OwnerSubject.PlayerId));
            // 覆盖每种实际玩家颜色组合；刷新只迁移显示，不能在两个等级残留同一玩家。
            for (int mask = 0; mask < 16; mask++)
            {
                foreach (var marker in markers) marker.SlotId = (mask & ColorBit(marker.OwnerSubject.PlayerId)) != 0 ? "level_0" : "level_5";
                Refresh();
                for (int level = 0; level < 6; level++)
                {
                    int expected = level == 0 ? mask & allColors : level == 5 ? allColors & ~mask : 0;
                    Assert.That(grades[level].enabled, Is.EqualTo(expected != 0));
                    Assert.That(emptySlots[level].activeSelf, Is.EqualTo(expected == 0));
                    if (expected != 0) Assert.That(grades[level].sprite.name, Is.EqualTo("cooperation-" + expected.ToString("x2") + "-64"));
                    else Assert.That(grades[level].sprite, Is.Null);
                }
                if (mask == 5) yield return Capture(count + "人-企业合作双色同级", new Vector2Int(1920, 1080));
            }
            yield return Capture(count + "人-企业合作素材规范-1920", new Vector2Int(1920, 1080));
            CheckCooperationViewport();
            yield return Capture(count + "人-企业合作素材规范-1024", new Vector2Int(1024, 768));
            CheckCooperationViewport();
            foreach (var button in new[] { (Button)Get(Ui, "cooperationOpenButton") })
            {
                button.onClick.Invoke();
                Assert.That(((Component)Get(Ui, "publicBoards")).gameObject.activeInHierarchy, Is.True);
                ((Component)Get(Ui, "publicBoards")).gameObject.SetActive(false);
            }
            Assert.That(((IList)Get(scene, "commands")).Count, Is.Zero);
        }
        private void CheckCooperationViewport()
        {
            var headers = (GameObject)Get(Ui, "cooperationLevelHeaders");
            var viewport = (RectTransform)headers.transform.parent.parent;
            var contents = ((Image[])Get(Ui, "cooperationGrades")).Select(image => image.rectTransform)
                .Concat(headers.GetComponentsInChildren<Text>().Select(text => text.rectTransform));
            foreach (var rect in contents)
            {
                var corners = new Vector3[4]; rect.GetWorldCorners(corners);
                foreach (var point in corners)
                {
                    var local = viewport.InverseTransformPoint(point);
                    Assert.That(local.x, Is.InRange(viewport.rect.xMin - .5f, viewport.rect.xMax + .5f));
                    Assert.That(local.y, Is.InRange(viewport.rect.yMin - .5f, viewport.rect.yMax + .5f));
                }
            }
        }
        private static void CheckGeometryAndText(Component row)
        {
            var title = (Text)Prop(row, "TitleText"); var body = (Text)Prop(row, "DescriptionText");
            var a = new Vector3[4]; var b = new Vector3[4]; title.rectTransform.GetWorldCorners(a); body.rectTransform.GetWorldCorners(b);
            Assert.That(a[0].x, Is.EqualTo(b[0].x).Within(.2f)); Assert.That(a[0].y, Is.GreaterThanOrEqualTo(b[1].y - .2f));
            Assert.That((int)title.alignment % 3, Is.Zero); Assert.That((int)body.alignment % 3, Is.Zero);
            foreach (var text in new[] { title, body })
            {
                var settings = text.GetGenerationSettings(text.rectTransform.rect.size);
                var height = text.cachedTextGeneratorForLayout.GetPreferredHeight(text.text, settings) / text.pixelsPerUnit;
                Assert.That(height, Is.LessThanOrEqualTo(text.rectTransform.rect.height + .2f), text.text);
            }
        }
        [UnityTest] public IEnumerator AllOfficialTextsFitFixedFramesAndReusedRowsRecoverBaseSizes()
        {
            scene = new EnterpriseGameplayUiTests(); yield return scene.LoadSceneWithPlayers(3); ConfigureRealContent();
            Call(Prop(Hud, "MainModules"), "ToggleLocalDetail"); yield return Capture("缩字前", new Vector2Int(1024, 768));
            var row = Rows.First(value => value.gameObject.activeInHierarchy);
            var title = (Text)Prop(row, "TitleText"); var body = (Text)Prop(row, "DescriptionText");
            var bindingType = row.GetType().GetNestedType("Binding");
            void Bind(string name, string text)
            {
                var data = Activator.CreateInstance(bindingType); Set(data, "StableItemId", "text-reuse");
                Set(data, "RowMode", Enum.Parse(row.GetType().GetNestedType("Mode"), "PersistentDetail")); Set(data, "Title", name); Set(data, "Description", text);
                Set(data, "Source", "不能拼入正文"); Set(data, "CostImpact", "不能拼入正文");
                Call(row, "Bind", data, (Func<string, int, bool>)((_, __) => true), (Action<string>)(_ => { }), (Action<string>)(_ => Assert.Fail("明细不能执行")));
            }
            Bind("名称", "短文"); yield return null; Canvas.ForceUpdateCanvases(); Call(row, "FitText");
            var size = ((RectTransform)row.transform).rect.size; var titleSize = title.rectTransform.rect.size; var bodySize = body.rectTransform.rect.size;
            int titleFont = title.fontSize, bodyFont = body.fontSize;
            foreach (var text in display)
            {
                Bind(text.DisplayName, text.EffectText); yield return null; Canvas.ForceUpdateCanvases(); Call(row, "FitText");
                Assert.That(((RectTransform)row.transform).rect.size, Is.EqualTo(size));
                Assert.That(title.rectTransform.rect.size, Is.EqualTo(titleSize)); Assert.That(body.rectTransform.rect.size, Is.EqualTo(bodySize));
                Assert.That(body.text, Is.EqualTo(text.EffectText)); CheckGeometryAndText(row);
            }
            var longest = display.OrderByDescending(value => value.EffectText.Length).First();
            Bind(longest.DisplayName, string.Join("", Enumerable.Repeat(longest.EffectText, 4))); yield return null; Canvas.ForceUpdateCanvases(); Call(row, "FitText");
            Assert.That(title.fontSize, Is.LessThan(titleFont)); Assert.That(body.fontSize, Is.LessThan(bodyFont)); CheckGeometryAndText(row);
            int fitted = body.fontSize; for (int i = 0; i < 8; i++) Call(row, "FitText"); Assert.That(body.fontSize, Is.EqualTo(fitted));
            yield return Capture("长文加宽重算", new Vector2Int(1920, 1080)); Call(row, "FitText");
            Assert.That(body.fontSize, Is.GreaterThanOrEqualTo(fitted)); CheckGeometryAndText(row);
            Bind(longest.DisplayName, longest.EffectText); yield return Capture("最长正式卡文", new Vector2Int(1024, 768)); CheckGeometryAndText(row);
            Bind("名称", "短文"); yield return null; Canvas.ForceUpdateCanvases(); Call(row, "FitText");
            Assert.That(title.fontSize, Is.EqualTo(titleFont)); Assert.That(body.fontSize, Is.EqualTo(bodyFont));
            yield return Capture("长短切换恢复", new Vector2Int(1920, 1080)); CheckGeometryAndText(row);
        }
        private static IEnumerator Capture(string name, Vector2Int size)
        {
            var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
            capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { size });
            for (int i = 0; i < 8; i++) yield return null;
            Canvas.ForceUpdateCanvases();
            var folder = Path.GetFullPath("Logs/ExpansionValidation/PlayerDetails/real-content-screenshots"); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name + ".png"); ScreenCapture.CaptureScreenshot(path);
            for (int i = 0; i < 12; i++) yield return null; Assert.That(File.Exists(path), Is.True);
        }
    }
}
