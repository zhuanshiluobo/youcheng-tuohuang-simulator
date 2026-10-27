using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class EnterpriseFacilityStyleEventTests
    {
        private const string Business = "test.enterprise.habitat";
        private const string Logistics = "test.enterprise.logistics";
        private static readonly string[] StyleIds = {
            "city_style_city_construction_frontier", "city_style_pioneer_outpost", "city_style_core_commercial_area",
            "city_style_large_enterprise_park", "city_style_security_district", "city_style_pioneer_command_hub" };
        private static readonly string[] OfficeIds = { "facility_enterprise_office_red", "facility_enterprise_office_blue", "facility_enterprise_office_yellow" };
        private static readonly string[] EventIds = { "enterprise.event.collection_platform", "enterprise.event.cave_ruins" };
        private ExternalContentPack pack;
        private GameSetupRecipe coreRecipe;

        [OneTimeSetUp]
        public void LoadRealDefinitionsThroughProductionLoader()
        {
            string source = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            string destination = Path.Combine(Directory.GetCurrentDirectory(), "Logs/ExpansionValidation/EnterpriseFacilityStyleEvent/content-fixture");
            // 测试装配显式列举真实文件；正式 pack 保持由内容集成任务登记。
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(file => !file.EndsWith(".meta", StringComparison.Ordinal)))
            {
                string target = Path.Combine(destination, file.Substring(source.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target, true);
            }
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(destination, "pack.json")));
            foreach (string path in StyleIds.Select(id => "city_style/" + id + ".json").Concat(EventIds.Select(id => "event/" + id + ".json")))
                if (!((JArray)manifest["definitions"]).Values<string>().Contains(path)) ((JArray)manifest["definitions"]).Add(path);
            manifest["enabledExpansionIds"] = new JArray("core", "enterprise");
            File.WriteAllText(Path.Combine(destination, "pack.json"), manifest.ToString());
            pack = ExternalContentPack.Load(destination);
            typeof(ExternalContentPack).GetField("UseDefinitionIds", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(pack, true);
            coreRecipe = ActiveContentCatalogBuilder.Build(pack, new GameRuleConfiguration {
                PlayerCount = 3, MapId = StaticMapDefinitions.FourPlayerMapId }).SetupRecipe;
        }

        private GameState State(out EffectRegistry registry, int level = 0, bool allStyles = true, bool nestedBuild = false)
        {
            var definitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(), Facilities = pack.CreateFacilities().ToList(),
                Events = pack.CreateEvents().ToList(), CityStyles = pack.CreateCityStyles().ToList(), SpecialActions = pack.CreateSpecialActions().ToList() };
            var recipe = GameStateCloneService.DeepClone(coreRecipe);
            foreach (var definition in pack.ActiveDefinitions.Where(value => StyleIds.Contains(value.DefinitionId)))
                recipe.CardZoneDeclarations.Add(new ItemCardZoneDeclaration { DefinitionId = definition.DefinitionId,
                    CardZones = GameStateCloneService.DeepClone(definition.CardZones) });
            foreach (var entry in recipe.Entries)
                if (entry.DefinitionId == "event_red_03") entry.DefinitionId = EventIds[0];
                else if (entry.DefinitionId == "event_yellow_02") entry.DefinitionId = EventIds[1];
            foreach (string id in OfficeIds) recipe.Add(id, id, "core.facility_card", "facility_deck");
            foreach (string id in StyleIds) recipe.Add(id, id, "core.board_card", "city_styles");
            if (allStyles) { recipe.CityStyleLevelOneDrawCount = 8; recipe.CityStyleLevelTwoDrawCount = 4; }
            foreach (string id in new[] { Business, Logistics })
            {
                recipe.Add(id, id, "core.board_card", "enterprises");
                recipe.CardZoneDeclarations.Add(new ItemCardZoneDeclaration { DefinitionId = id, CardZones = Enumerable.Range(1, 3).Select(player => new CardZoneDefinition {
                    LocalZoneId = "cooperation_" + player, ZoneTemplateId = "core.zone.board_marker_track", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
                    AcceptedTypeIds = new List<string> { "core.player_marker" }, CapacityPolicy = "bounded", Capacity = 1, OrderingKind = ZoneOrderingKind.Track,
                    EntryPolicyId = "core.owned_player_marker", ExitPolicyId = "core.explicit_transfer", VisibilityPolicyId = ItemZoneVisibility.Public,
                    CarrierExitPolicyId = "core.preserve_contents" }).ToList() });
            }
            var enterprise = new EnterpriseDefinitions();
            foreach (string id in new[] { Business, Logistics })
            {
                enterprise.Register(new EnterpriseDefinition { DefinitionId = id, RuntimeId = id, Levels = Enumerable.Range(0, 6).Select(current => new EnterpriseLevelDefinition {
                    Level = current, Kind = current == 0 ? "empty" : current == 1 || current == 3 ? "special" : "reward",
                    AbilityId = current == 0 ? "" : id + ".level_" + current }).ToList() });
                foreach (int current in Enumerable.Range(1, 5))
                {
                    int captured = current;
                    enterprise.BindAbility(id + ".level_" + current, context => nestedBuild && id == Business && captured == 1
                        ? new[] { EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, Object("sourceZone", NormalizedValue.CreateString("supply"), "paymentMode", NormalizedValue.CreateString("free"), "allowDecline", NormalizedValue.CreateBoolean(true)), context.Event.PlayerId) }
                        : new[] { ResourceEffectSpecFactory.Gain(context.Event.PlayerId, ResourceType.GoldVoucher, captured == 1 ? 4 : captured) });
                }
            }
            enterprise.Freeze();
            registry = new EffectRegistry { ContentPackHash = "test.enterprise.facility-style-event", SetupRecipe = recipe, ContentDefinitions = definitions };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); EnterpriseEffectExecutor.Register(registry, enterprise);
            FacilityBuildEffectExecutor.Register(registry); FacilitySelectionEffectExecutor.Register(registry);
            FacilityEntryEffectExecutor.Register(registry);
            FacilityLuaCatalog.Register(registry, pack); CityStyleLuaCatalog.Register(registry, pack); EventCardLuaCatalog.Register(registry, pack);
            var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.FourPlayerMapId, PlayerCount = 3, ContentPackHash = registry.ContentPackHash };
            var seats = Enumerable.Range(1, 3).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "玩家" + id }).ToList();
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, seats, config, 197, recipe, definitions);
            state.Phase = GamePhase.ActionRound1; state.Round = 1; state.ActionRound = 1; state.CurrentPlayerId = 1;
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2, 3 };
            foreach (int player in Enumerable.Range(1, 3))
            {
                var tower = state.ItemZones.Items.Single(item => item.ZoneId == FindZone(state, "city_cell_7", player).ZoneId);
                Position(state, tower.ItemId, "city_cell_11", player);
            }
            foreach (string id in new[] { Business, Logistics }) foreach (int player in Enumerable.Range(1, 3))
            {
                var track = state.ItemZones.Zones.Single(zone => zone.ParentId == Item(state, id) && zone.LocalZoneId == "cooperation_" + player);
                var marker = state.ItemZones.Items.First(value => value.TypeId == "core.player_marker" && value.OwnerSubject.PlayerId == player && value.ZoneId == FindZone(state, "supply", player).ZoneId);
                SetPosition(state, marker, track.ZoneId); marker.SlotId = "level_" + level;
            }
            var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)); var influence = new InfluenceService(map);
            state.Map.OpenLocationIds.AddRange(map.Map.Locations.Select(location => location.LocationId));
            InfluenceEffectExecutor.Register(registry, influence);
            EventCardEffectExecutor.Register(registry, map, influence, new EventDeckService(), new ResourceTokenService());
            CityMoveEffectExecutor.Register(registry, map, influence, new CityMovementService(map, influence, new TravelCostService(map)), new TravelCostService(map), new ResourceTokenService());
            var query = new SpecialActionOptionQueryService(map, influence, new CityMovementService(map, influence, new TravelCostService(map)), new SpecialActionLifecycleService(), new MainActionBudgetService());
            CityStyleSpecialActionEffectExecutor.Register(registry, query, null, map, influence, new CityMovementService(map, influence, new TravelCostService(map)));
            Valid(state); return state;
        }
        private static string Item(GameState state, string id) => state.ItemZones.Items.Single(value => value.DefinitionId == id).ItemId;
        private static void Valid(GameState state) { Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason); }
        private static EffectNodeRuntimeState Run(GameState state, EffectRegistry registry, EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state); return engine.GetNode(id);
        }
        private static InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.LastOrDefault(request => request.Status == "open");
        private static void Answer(GameState state, EffectRegistry registry, params string[] ids)
        {
            var request = Open(state); Assert.That(request, Is.Not.Null);
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                ids.Length == 1 ? NormalizedValue.CreateString(ids[0]) : Strings(ids), out string reason), Is.True,
                reason + "；交互=" + request.InteractionTypeId + "；候选=" + string.Join(",", request.CandidateIds));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state);
        }
        private static GameState Restore(GameState state, EffectRegistry registry)
        {
            var result = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic); Valid(result.State); return result.State;
        }
        private static void SetPosition(GameState state, ItemInstanceState item, string zoneId)
        {
            state.ItemZones.Orders.Find(order => order.ZoneId == item.ZoneId)?.ItemIds.Remove(item.ItemId);
            item.ZoneId = zoneId; item.SlotId = "";
            var order = state.ItemZones.Orders.Find(value => value.ZoneId == zoneId);
            if (order != null && !order.ItemIds.Contains(item.ItemId)) order.ItemIds.Add(item.ItemId);
        }
        private static void Position(GameState state, string itemId, string role, int player = -1)
        {
            var item = state.ItemZones.Items.Single(value => value.ItemId == itemId);
            string zoneId = FindZone(state, role, player).ZoneId;
            if (role.StartsWith("facility_supply_", StringComparison.Ordinal))
                foreach (var previous in state.ItemZones.Items.Where(value => value.ZoneId == zoneId && value.ItemId != itemId).ToList())
                    SetPosition(state, previous, FindZone(state, "facility_deck").ZoneId);
            SetPosition(state, item, zoneId);
        }
        private static readonly string[][] ExpectedPatterns = {
            new[] { "1,0:yellow", "0,1:blue", "1,1:blue", "2,1:red" }, new[] { "1,0:yellow", "0,1:blue" },
            new[] { "0,0:blue", "1,0:blue", "2,0:red" }, new[] { "0,0:blue", "0,1:red", "1,1:blue" },
            new[] { "0,0:blue", "0,1:red", "1,1:blue", "0,2:yellow", "1,2:yellow", "2,2:red" },
            new[] { "0,0:yellow", "0,1:red", "1,1:yellow", "0,2:blue", "1,2:blue", "2,2:red" } };
        private List<int> Layout(GameState state, string id, int rotation = 0, bool mirror = false, bool wrongColor = false)
        {
            int index = Array.IndexOf(StyleIds, id);
            var cells = ExpectedPatterns[index].Select(cell => { var parts = cell.Split(':'); var xy = parts[0].Split(',');
                int x = int.Parse(xy[0]), y = int.Parse(xy[1]); if (mirror) x = -x;
                for (int step = 0; step < rotation; step++) { int saved = x; x = -y; y = saved; }
                return Tuple.Create(x, y, parts[1]); }).ToList();
            int minX = cells.Min(cell => cell.Item1), minY = cells.Min(cell => cell.Item2);
            var used = new HashSet<string>(state.FindPlayer(1).DeclaredCityStyles.SelectMany(declaration => declaration.UsedFacilityIds)); var slots = new List<int>();
            foreach (var cell in cells)
            {
                int slot = (cell.Item2 - minY) * BuildFacilityService.CityBoardSlotCountPerRow + cell.Item1 - minX;
                string color = wrongColor && slots.Count == 0 ? (cell.Item3 == "blue" ? "yellow" : "blue") : cell.Item3;
                var item = state.ItemZones.Items.First(value => !used.Contains(value.ItemId) && value.TypeId == "core.facility_card" &&
                    FacilityCardDatabase.Get(state, value.DefinitionId).Color == color && !OfficeIds.Contains(value.DefinitionId));
                Position(state, item.ItemId, "city_cell_" + slot, 1); used.Add(item.ItemId); slots.Add(slot);
            }
            Valid(state); return slots;
        }
        private CityStyleDeclarationState Declare(GameState state, EffectRegistry registry, string id)
        {
            var node = Run(state, registry, CityStyleDeclarationEffectExecutor.Create(1, id, Layout(state, id)));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed)); return state.FindPlayer(1).DeclaredCityStyles.Last();
        }
        private static EffectNodeRuntimeState Activate(GameState state, EffectRegistry registry, CityStyleDeclarationState marker) => Run(state, registry,
            CityStyleSpecialActionEffectSpecFactory.Activate(1, marker.UnlockedSpecialActionId, marker.InfluenceMarkerId));
        private static int Level(GameState state, string id = Business) => new EnterpriseStateQuery().GetLevel(state, 1, Item(state, id));

        [Test]
        public void OfficesHaveDistinctColorsCorrectCostsAndOnePhysicalCopyEach()
        {
            var state = State(out _);
            for (int i = 0; i < OfficeIds.Length; i++)
            {
                var card = FacilityCardDatabase.Get(state, OfficeIds[i]);
                Assert.That(card.Color, Is.EqualTo(new[] { "red", "blue", "yellow" }[i])); Assert.That(card.Score, Is.EqualTo(1));
                Assert.That(card.ResourceCost.Originium, Is.EqualTo(3)); Assert.That(card.ResourceCost.OriginiumShard, Is.EqualTo(1)); Assert.That(card.ResourceCost.Iron, Is.EqualTo(3));
                Assert.That(card.GoldVoucherCost, Is.EqualTo(23)); Assert.That(card.Unique, Is.False); Assert.That(card.HasEntryEffect, Is.True);
                Assert.That(state.ItemZones.Items.Count(item => item.DefinitionId == OfficeIds[i]), Is.EqualTo(1));
                Assert.That(pack.ActiveDefinitions.Single(def => def.DefinitionId == OfficeIds[i]).ArtworkSpriteId, Is.EqualTo("facility_0" + (49 + i)));
            }
            Assert.That(pack.Definitions.Single(def => def.DefinitionId == "facility_enterprise_office").Enabled, Is.False);
            Assert.That(state.ItemZones.Items.Any(item => item.DefinitionId == "facility_enterprise_office"), Is.False);
        }
        [TestCase("resources")][TestCase("gold")][TestCase("free")]
        public void BuildRestoresAfterPaymentAndRefillCompletesScoreAndEmptyEntryOnce(string payment)
        {
            var state = State(out var registry); var player = state.FindPlayer(1); player.Resources = new ResourceSet { Originium = 3, OriginiumShard = 1, Iron = 3, GoldVoucher = 23 };
            string item = Item(state, OfficeIds[0]); Position(state, item, "facility_supply_0");
            int score = player.Score; int deckCount = state.Decks.FacilityDeck.Count;
            Run(state, registry, FacilityBuildEffectSpecFactory.Build(1, item, -1, payment, false, "test.office", false));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("facility.build.placement"));
            Assert.That(state.Decks.FacilityDeck.Count, Is.EqualTo(deckCount - 1)); Assert.That(state.Decks.FacilitySupply[0], Is.Not.EqualTo(item));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(payment == "gold" ? 0 : 23)); Assert.That(player.Resources.Iron, Is.EqualTo(payment == "resources" ? 0 : 3));
            state = Restore(state, registry); Answer(state, registry, Open(state).CandidateIds.First());
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 1)); Assert.That(Level(state), Is.Zero); Assert.That(Open(state), Is.Null);
            Assert.That(state.Map.Facilities.Count(facility => facility.ContentInstanceId == item), Is.EqualTo(1));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 1));
        }

        [TestCaseSource(nameof(OfficeIds))]
        public void EveryOfficeBuildRunsItsRegisteredEntryAndRecoversSpecialExactlyOnce(string definitionId)
        {
            var state = State(out var registry, level: 1);
            string item = Item(state, definitionId); Position(state, item, "facility_supply_0");
            int gold = state.FindPlayer(1).Resources.GoldVoucher, score = state.FindPlayer(1).Score;
            Run(state, registry, FacilityBuildEffectSpecFactory.Build(1, item, -1, "free", false, "test.office", false));
            Answer(state, registry, Open(state).CandidateIds.First());
            var request = Open(state); Assert.That(request, Is.Not.Null, definitionId);
            string requestId = request.GetStableInteractionId(), candidate = request.CandidateIds.First();
            int revision = request.StateRevision;
            state = Restore(state, registry);
            Assert.That(Open(state).GetStableInteractionId(), Is.EqualTo(requestId));
            Answer(state, registry, candidate);
            Assert.That(Open(state), Is.Null);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 4));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "EnterpriseSpecialEffectActivated"), Is.EqualTo(1));
            state = Restore(state, registry);
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(requestId, 1, revision, NormalizedValue.CreateString(candidate), out _), Is.False);
            engine.RunUntilQuiescent();
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 4));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 1));
            Valid(state);
        }
        [Test]
        public void PoolDrawsFourLevelOneAndTwoLevelTwoWithoutPublishingAllStyles()
        {
            var state = State(out _, allStyles: false);
            var styles = state.Decks.CityStyleSupply.Select(id => CityStyleDatabase.Get(state, id)).ToList();
            Assert.That(styles.Count(style => style.Level == 1), Is.EqualTo(4)); Assert.That(styles.Count(style => style.Level == 2), Is.EqualTo(2));
            Assert.That(pack.CreateCityStyles().Count(style => style.Level == 1), Is.EqualTo(8)); Assert.That(pack.CreateCityStyles().Count(style => style.Level == 2), Is.EqualTo(4));
            Assert.That(state.ItemZones.Items.Count(item => StyleIds.Contains(item.DefinitionId)), Is.EqualTo(6));
        }
        public static IEnumerable<TestCaseData> RotatedPatterns()
        {
            foreach (string id in StyleIds) foreach (int rotation in Enumerable.Range(0, 4)) yield return new TestCaseData(id, rotation);
        }
        [TestCaseSource(nameof(RotatedPatterns))]
        public void PatternAcceptsExactCoordinatesUnderRotation(string id, int rotation)
        {
            var state = State(out _); var slots = Layout(state, id, rotation);
            Assert.That(new CityStylePatternMatcher().MatchSelected(state, 1, CityStyleDatabase.Get(state, id), slots).Succeeded, Is.True);
        }
        [TestCaseSource(nameof(StyleIds))]
        public void PatternRejectsWrongColor(string id)
        {
            var state = State(out _); var slots = Layout(state, id, wrongColor: true);
            Assert.That(new CityStylePatternMatcher().MatchSelected(state, 1, CityStyleDatabase.Get(state, id), slots).Succeeded, Is.False);
        }
        [TestCase("city_style_city_construction_frontier")][TestCase("city_style_security_district")][TestCase("city_style_pioneer_command_hub")]
        public void ChiralPatternRejectsMirror(string id)
        {
            var state = State(out _); var slots = Layout(state, id, mirror: true);
            Assert.That(new CityStylePatternMatcher().MatchSelected(state, 1, CityStyleDatabase.Get(state, id), slots).Succeeded, Is.False);
        }
        [Test]
        public void PatternRejectsDisplacedCellWithUnchangedColorTotals()
        {
            var state = State(out _); string id = StyleIds[4]; var slots = Layout(state, id);
            var item = state.ItemZones.Items.Single(value => value.ZoneId == FindZone(state, "city_cell_8", 1).ZoneId);
            Position(state, item.ItemId, "city_cell_9", 1); slots[slots.IndexOf(8)] = 9;
            Assert.That(new CityStylePatternMatcher().MatchSelected(state, 1, CityStyleDatabase.Get(state, id), slots).Succeeded, Is.False);
        }
        [Test]
        public void CommercialResourceChoicePrecedesRestrictedImmediateActionAndConsumesOnlyOuterBudget()
        {
            var state = State(out var registry); var marker = Declare(state, registry, StyleIds[2]);
            state.FindPlayer(1).Resources = new ResourceSet { GoldVoucher = 100, Originium = 100, Iron = 100, OriginiumShard = 100, PureOriginium = 100 };
            int gold = state.FindPlayer(1).Resources.GoldVoucher; Activate(state, registry, marker);
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { "GoldVoucher", "Originium", "Iron", "OriginiumShard" }));
            Answer(state, registry, "GoldVoucher"); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 4));
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { "build", "explore", "move_city" }));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            state = Restore(state, registry); Answer(state, registry, "build");
            while (Open(state) != null) Answer(state, registry, Open(state).CandidateIds.First());
            Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == FacilityEntryEffectTypeIds.Build && node.Status == EffectNodeStatus.Completed), Is.True);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero); Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1));
        }
        [Test]
        public void ImmediateMainActionRejectsSpecialActionInjection()
        {
            var state = State(out var registry);
            var marker = Declare(state, registry, StyleIds[2]);
            state.FindPlayer(1).Resources = new ResourceSet { GoldVoucher = 100, Originium = 100, Iron = 100, OriginiumShard = 100, PureOriginium = 100 };
            Activate(state, registry, marker); Answer(state, registry, "GoldVoucher");
            var request = Open(state);
            Assert.That(request.CandidateIds, Is.EquivalentTo(new[] { "build", "explore", "move_city" }));
            var command = YC.Application.Interactions.EffectInteractionCommands.Answer(
                YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1), 1, new[] { "special" });
            var result = new YC.Application.Interactions.AnswerInteractionCommandHandler(registry).Handle(state, command);
            Assert.That(result.Succeeded, Is.False);
            Assert.That(Open(state).GetStableInteractionId(), Is.EqualTo(request.GetStableInteractionId()));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
        }
        [Test]
        public void SecurityAllowsSameBranchTwiceAndLocksCharactersForWholeActionRound()
        {
            var state = State(out var registry); var marker = Declare(state, registry, StyleIds[4]); Activate(state, registry, marker);
            Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.True); int iron = state.FindPlayer(1).Resources.Iron;
            Answer(state, registry, "remove"); Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { "replace", "place", "remove" }));
            state = Restore(state, registry); Answer(state, registry, "remove");
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(iron + 4)); Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.True);
            new MainActionBudgetService().GrantAdditionalMainActions(state, 1, 1); Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.True);
            new MainActionBudgetService().ResetForNewActionTurn(state, 1); Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.False);
        }
        [Test]
        public void SecurityCannotActivateAfterCharacterWasUsed()
        {
            var state = State(out var registry); var marker = Declare(state, registry, StyleIds[4]); state.FindPlayer(1).UsedCharacterThisTurn = true;
            var node = Activate(state, registry, marker); Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(marker.MarkerArea, Is.EqualTo(CityStyleMarkerAreas.UsesTwo)); Assert.That(Open(state), Is.Null);
        }
        [Test]
        public void CompositeSecurityCostIsAtomicWhenIronIsInsufficient()
        {
            var state = State(out var registry); var marker = Declare(state, registry, StyleIds[4]); state.FindPlayer(1).Resources = new ResourceSet { Originium = 2 };
            Activate(state, registry, marker); Answer(state, registry, "replace");
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2)); Answer(state, registry, "remove");
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(2));
        }
        [Test]
        public void CompositeSecurityCostPaysAllResourcesOnceBeforeReplacement()
        {
            var state = State(out var registry); var marker = Declare(state, registry, StyleIds[4]);
            state.FindPlayer(1).Resources = new ResourceSet { Originium = 2, Iron = 1 };
            Activate(state, registry, marker); Answer(state, registry, "replace");
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.Zero); Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
            state = Restore(state, registry); Answer(state, registry, "remove");
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.Zero); Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(2));
        }
        [Test]
        public void SecurityCharacterLockIsEnforcedByActualCoveredCardAvailability()
        {
            var state = State(out var registry); var marker = Declare(state, registry, StyleIds[4]);
            CharacterCoverEffectExecutor.Register(registry);
            string card = state.FindPlayer(1).HandCardIds.First();
            Run(state, registry, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(state, registry, card);
            Assert.That(CharacterLifecycleService.GetAvailability(state, 1, card).CanUse, Is.True);
            Activate(state, registry, marker);
            Assert.That(CharacterLifecycleService.GetAvailability(state, 1, card).DisabledReasonCode, Is.EqualTo("character_action_round_locked"));
        }
        [Test]
        public void CommandHubGrantsRestrictedBudgetAndDoubleModePermissionWithoutUnlockingDiscardedCharacter()
        {
            var state = State(out var registry); var marker = Declare(state, registry, StyleIds[5]); Activate(state, registry, marker);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1)); Assert.That(state.FindPlayer(1).RestrictedMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(new MainActionBudgetService().ValidateCanSpendSpecial(state, 1).IsValid, Is.False);
            Assert.That(state.CharacterActionRoundPermissions.Single().ExpiresActionRoundSequence, Is.EqualTo(1));
            state = Restore(state, registry); Assert.That(state.FindPlayer(1).RestrictedMainActionsThisTurn, Is.EqualTo(1));
            var view = GameStateViewProjector.ProjectForPlayer(state, 1); Assert.That(view.Players.Single(player => player.PlayerId == 1).RestrictedMainActionsThisTurn, Is.EqualTo(1));
            string character = state.FindPlayer(1).HandCardIds.First(); Position(state, character, "discard", 1);
            Assert.That(CharacterLifecycleService.GetAvailability(state, 1, character).CanUse, Is.False);
            new MainActionBudgetService().SpendCompletedMainAction(state, 1); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
            new MainActionBudgetService().ResetForNewActionTurn(state, 1); state.ActionRound = 2; Assert.That(state.FindPlayer(1).RestrictedMainActionsThisTurn, Is.Zero);
            Assert.That(state.CharacterActionRoundPermissions.Any(permission => permission.ExpiresActionRoundSequence == CharacterLifecycleService.GetActionRoundSequence(state)), Is.False);
        }
        [TestCase("strategy-first")][TestCase("tactic-first")]
        public void CommandHubExecutesBothModesAndConsumesCoveredCharacterOnlyOnce(string order)
        {
            var state = State(out var registry); var marker = Declare(state, registry, StyleIds[5]);
            CharacterCoverEffectExecutor.Register(registry); CharacterAbilityEffectExecutor.Register(registry);
            string card = state.FindPlayer(1).HandCardIds.Single(id => CharacterCardDatabase.Get(state, id).TemplateId == "texas");
            var definition = CharacterCardDatabase.Get(state, card);
            foreach (string mode in new[] { "strategy", "tactic" })
            {
                string ability = mode == "strategy" ? definition.StrategyAbilityId : definition.TacticAbilityId;
                string subscription = "test.hub.character." + mode;
                registry.CharacterActivationSubscriptions[ability] = subscription;
                registry.CharacterAbilityContracts[ability] = new CharacterModeContract { Mode = mode, AbilityId = ability, SkillType = "normal" };
                registry.RegisterEventHandler(new EffectEventHandlerRegistration(subscription, CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType, ability, subscription,
                    context => new[] { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 1) }, EffectHandlerRole.Primary));
            }
            Run(state, registry, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(state, registry, card);
            Activate(state, registry, marker); int score = state.FindPlayer(1).Score;
            Run(state, registry, CharacterLifecycleService.CreateUseSpec(1, card, "both", order));
            Assert.That(state.CharacterUses.Last().CompletedModes, Is.EqualTo(order == "strategy-first" ? new[] { "strategy", "tactic" } : new[] { "tactic", "strategy" }));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 2)); Assert.That(state.FindPlayer(1).DiscardCardIds.Count(id => id == card), Is.EqualTo(1));
            Assert.That(CharacterLifecycleService.GetAvailability(state, 1, card).CanUse, Is.False);
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 2));
        }
        [Test]
        public void RestrictedBudgetCannotBeRestoredAsUnrestrictedWhenFieldIsRemoved()
        {
            var state = State(out var registry); Activate(state, registry, Declare(state, registry, StyleIds[5]));
            var archive = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            foreach (var value in archive.Descendants().OfType<JObject>()) value.Remove("RestrictedMainActionsThisTurn");
            Assert.That(HostRecoveryService.RecoverJson(archive.ToString(), registry).Succeeded, Is.False);
        }
        [Test]
        public void LargeEnterpriseParkChoosesOneEnterpriseAndResumesTwoArrivals()
        {
            var state = State(out var registry); var marker = Declare(state, registry, StyleIds[3]); Activate(state, registry, marker);
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { Item(state, Business), Item(state, Logistics) }));
            state = Restore(state, registry); Answer(state, registry, Item(state, Business));
            Assert.That(Level(state), Is.EqualTo(2)); Assert.That(Level(state, Logistics), Is.Zero);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
        }
        [TestCase(false)][TestCase(true)]
        public void FrontierLosesScoreAndShufflesDeckAfterDeclinedBuild(bool restoreBeforeDecline)
        {
            var state = State(out var registry); var marker = Declare(state, registry, StyleIds[0]); int score = state.FindPlayer(1).Score;
            var order = state.ItemZones.Orders.Single(value => value.ZoneId == FindZone(state, "facility_deck").ZoneId); int revision = order.OrderRevision;
            Activate(state, registry, marker); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score - 1));
            Assert.That(Open(state).CandidateIds, Does.Contain("choice.skip"));
            if (restoreBeforeDecline) state = Restore(state, registry);
            Answer(state, registry, "choice.skip");
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score - 1));
            Assert.That(state.ItemZones.Orders.Single(value => value.ZoneId == order.ZoneId).OrderRevision, Is.EqualTo(revision + 1));
        }
        [Test]
        public void FrontierStillShufflesWhenSelectedBuildFailsPaymentRevalidation()
        {
            var state = State(out var registry); var marker = Declare(state, registry, StyleIds[0]); int score = state.FindPlayer(1).Score;
            state.FindPlayer(1).Resources = new ResourceSet { Originium = 3, OriginiumShard = 1, Iron = 3 };
            Position(state, Item(state, OfficeIds[0]), "facility_deck");
            Activate(state, registry, marker); Answer(state, registry, Item(state, OfficeIds[0]));
            state.FindPlayer(1).Resources = new ResourceSet();
            var order = state.ItemZones.Orders.Single(value => value.ZoneId == FindZone(state, "facility_deck").ZoneId); int revision = order.OrderRevision;
            Answer(state, registry, "resources");
            Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == FacilityEntryEffectTypeIds.Build && node.Status == EffectNodeStatus.Failed), Is.True);
            Assert.That(state.ItemZones.Orders.Single(value => value.ZoneId == order.ZoneId).OrderRevision, Is.EqualTo(revision + 1));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score - 1));
        }
        [Test]
        public void PioneerPlacesOnlyAtCurrentCityAndUsesAllOwnMarkersOnThisStyle()
        {
            var state = State(out var registry); string id = StyleIds[1];
            var first = Declare(state, registry, id);
            foreach (string item in first.UsedFacilityIds) Position(state, item, "facility_deck");
            Declare(state, registry, id);
            var map = StaticMapDefinitions.Resolve(state.MapId);
            var location = map.Locations.First(value => value.CanDockCity && value.InfluenceSlotCount >= 2 && value.EventColor == EventColor.Yellow);
            var locationZone = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == location.LocationId);
            var dock = state.ItemZones.Zones.Single(zone => zone.ParentId == locationZone.ZoneId && zone.LocalZoneId == "cities");
            var city = state.ItemZones.Items.Single(item => item.TypeId == "core.mobile_city" && item.OwnerSubject.PlayerId == 1);
            SetPosition(state, city, dock.ZoneId);
            // 沿真实事件揭示链创建资源指示物，使用完事件后再发动据点。
            Reveal(state, registry, EventIds[1], EventColor.Yellow, location.LocationId);
            Answer(state, registry, EventCardOptionIdFactory.Create(EventIds[1], 0));
            var slots = Enumerable.Range(0, location.InfluenceSlotCount).Select(index => InfluenceService.GetLocationSlotId(location.LocationId, index)).ToList();
            Activate(state, registry, first);
            Assert.That(Open(state), Is.Not.Null, string.Join(";", state.EffectRuntime.EffectNodes.Select(node =>
                node.EffectTypeId + ":" + node.Status + ":" + node.FailureReason + ":" + node.NormalizedArguments)));
            Assert.That(Open(state).CandidateIds.All(slots.Contains), Is.True);
            Answer(state, registry, Open(state).CandidateIds.First());
            Assert.That(Open(state).CandidateIds.All(slots.Contains), Is.True);
            Answer(state, registry, Open(state).CandidateIds.First());
            Assert.That(state.Map.Influences.Count(value => value.LocationId == location.LocationId && value.PlayerId == 1), Is.EqualTo(2));
            state = Restore(state, registry);
            for (int moved = 0; moved < 2; moved++)
            {
                Assert.That(Open(state).CandidateIds.All(value => value.StartsWith("move|", StringComparison.Ordinal)), Is.True);
                Answer(state, registry, Open(state).CandidateIds.First());
            }
            Assert.That(Open(state), Is.Null);
            var repetitions = state.EffectRuntime.EffectNodes.Where(node => node.EffectTypeId == LuaDomainEffectTypeIds.Repeat).ToList();
            Assert.That(repetitions.Count, Is.EqualTo(2));
            Assert.That(repetitions.All(node => Number(node.NormalizedResult, "completedCount") == 2), Is.True);
        }
        [Test]
        public void DeclarationResponseCanNestFreeOfficeBuildAndEnterpriseSpecialAcrossRestore()
        {
            var state = State(out var registry, level: 1, nestedBuild: true); string office = Item(state, OfficeIds[2]); Position(state, office, "facility_supply_0");
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.style.declared.enterprise", "CityStyleDeclared", "", "test.style.declared.enterprise",
                context => new[] { EffectSpec.Create(LuaDomainEffectTypeIds.ActivateEnterpriseSpecial,
                    Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "unlockedByPlayer", NormalizedValue.CreateString("1")), 1) }, EffectHandlerRole.Observer));
            var declaration = Run(state, registry, CityStyleDeclarationEffectExecutor.Create(1, StyleIds[1], Layout(state, StyleIds[1])));
            Assert.That(declaration.Status, Is.EqualTo(EffectNodeStatus.Blocked));
            string special = state.EffectRuntime.EffectNodes.Last(value => value.EffectTypeId == LuaDomainEffectTypeIds.ActivateEnterpriseSpecial).FrozenEnterpriseSpecials.Single(value => value.Candidate.EnterpriseItemId == Item(state, Business)).Candidate.CandidateId;
            // 候选 ID 为稳定派生键，企业绑定事实从冻结候选读取。
            Answer(state, registry, special); Answer(state, registry, office);
            state = Restore(state, registry); Answer(state, registry, Open(state).CandidateIds.First());
            var node = state.EffectRuntime.EffectNodes.Last(value => value.EffectTypeId == LuaDomainEffectTypeIds.ActivateEnterpriseSpecial);
            string logistics = node.FrozenEnterpriseSpecials.Single(value => value.Candidate.EnterpriseItemId == Item(state, Logistics)).Candidate.CandidateId;
            Answer(state, registry, logistics);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(3)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
            Assert.That(state.EffectRuntime.RuleEvents.Count(evt => evt.EventType == "CityStyleDeclared"), Is.EqualTo(1));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(3));
        }
        [Test]
        public void RepeatedDeclarationDoesNotRefillLevelTwoUsageAndFacilitiesCannotBeReused()
        {
            var state = State(out var registry); var marker = Declare(state, registry, StyleIds[5]); var oldFacilities = marker.UsedFacilityIds.ToList();
            Activate(state, registry, marker); Run(state, registry, CityStyleSpecialActionEffectSpecFactory.Cleanup(1));
            Assert.That(marker.RemainingSpecialActionUses, Is.EqualTo(1));
            Assert.That(new DeclareCityStyleService().Validate(state, 1, StyleIds[5], marker.UsedCityBoardSlotIndexes).IsValid, Is.False);
            // 测试第二组真实设施，旧组实例仍留在历史记录，不能用重宣告恢复次数。
            foreach (var id in oldFacilities) Position(state, id, "facility_deck");
            var slots = Layout(state, StyleIds[5]); Run(state, registry, CityStyleDeclarationEffectExecutor.Create(1, StyleIds[5], slots));
            var declarations = state.FindPlayer(1).DeclaredCityStyles; Assert.That(declarations.Count, Is.EqualTo(2));
            Assert.That(declarations[0].RemainingSpecialActionUses, Is.EqualTo(1)); Assert.That(declarations[1].RemainingSpecialActionUses, Is.Zero);
            Assert.That(declarations[1].UnlockedSpecialActionId, Is.Empty);
        }
        [Test]
        public void ReplacementSlotsHaveOneEffectiveVersionAndKeepRuntimeIds()
        {
            foreach (var pair in new[] { Tuple.Create(EventIds[0], "event_red_03", "event_red_007", "采集平台残骸"), Tuple.Create(EventIds[1], "event_yellow_02", "event_yellow_012", "洞穴遗迹") })
            {
                var definition = pack.ActiveDefinitions.Single(value => value.RuntimeId == pair.Item2);
                Assert.That(definition.DefinitionId, Is.EqualTo(pair.Item1)); Assert.That(definition.ArtworkSpriteId, Is.EqualTo(pair.Item3)); Assert.That(definition.DisplayName, Is.EqualTo(pair.Item4));
                Assert.That(pack.ActiveDefinitions.Any(value => value.DefinitionId == pair.Item2), Is.False);
            }
        }
        private void Reveal(GameState state, EffectRegistry registry, string id, EventColor color, string locationOverride = null)
        {
            var deck = FindZone(state, color == EventColor.Red ? "event_red" : "event_yellow");
            var order = state.ItemZones.Orders.Single(value => value.ZoneId == deck.ZoneId); string card = Item(state, id); order.ItemIds.Remove(card); order.ItemIds.Insert(0, card); order.OrderRevision++;
            string location = locationOverride ?? StaticMapDefinitions.Resolve(state.MapId).Locations.First(value => value.EventColor == color).LocationId;
            Run(state, registry, EventCardEffectSpecFactory.Resolve(1, color, location));
        }
        [Test]
        public void CollectionPlatformLosesScoreBelowZeroAndActivatesSavedSpecialOnSameEnterprise()
        {
            var state = State(out var registry); Reveal(state, registry, EventIds[0], EventColor.Red);
            Answer(state, registry, EventCardOptionIdFactory.Create(EventIds[0], 0)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(-1));
            var candidate = Open(state).CandidateIds.First(); var frozen = state.EffectRuntime.EffectNodes.Last(node => node.EffectTypeId == EnterpriseSpecialSelectionExecutor.UpgradeThenActivateTypeId).FrozenEnterpriseSpecials.Single(special => special.Candidate.CandidateId == candidate);
            string selectedEnterprise = frozen.Candidate.EnterpriseItemId;
            state = Restore(state, registry); Answer(state, registry, candidate);
            Assert.That(new EnterpriseStateQuery().GetLevel(state, 1, selectedEnterprise), Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(-1)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
        }
        [Test]
        public void CaveSponsorOnlyUpgradesAndDoesNotTriggerUnlockedSpecial()
        {
            var state = State(out var registry); state.FindPlayer(1).Resources.Iron = 1; Reveal(state, registry, EventIds[1], EventColor.Yellow);
            Answer(state, registry, EventCardOptionIdFactory.Create(EventIds[1], 2)); state = Restore(state, registry); Answer(state, registry, Item(state, Business));
            Assert.That(Level(state), Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            Assert.That(state.EffectRuntime.RuleEvents.Any(evt => evt.EventType == "EnterpriseSpecialEffectActivated"), Is.False);
        }
        [TestCase(0, 5, 0)][TestCase(1, 0, 10)]
        public void CaveOtherOptionsApplyOnlySelectedReward(int option, int rock, int gold)
        {
            var state = State(out var registry); Reveal(state, registry, EventIds[1], EventColor.Yellow); Answer(state, registry, EventCardOptionIdFactory.Create(EventIds[1], option));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(rock)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold)); Assert.That(Level(state), Is.Zero);
        }
        [TestCase(1, 4, 3, 0, 0)][TestCase(2, 0, 0, 4, 5)]
        public void CollectionOtherOptionsApplyOnlySelectedReward(int option, int rock, int shards, int iron, int gold)
        {
            var state = State(out var registry); Reveal(state, registry, EventIds[0], EventColor.Red); Answer(state, registry, EventCardOptionIdFactory.Create(EventIds[0], option));
            var resources = state.FindPlayer(1).Resources; Assert.That(resources.Originium, Is.EqualTo(rock)); Assert.That(resources.OriginiumShard, Is.EqualTo(shards));
            Assert.That(resources.Iron, Is.EqualTo(iron)); Assert.That(resources.GoldVoucher, Is.EqualTo(gold)); Assert.That(Level(state), Is.Zero);
        }
    }
}
