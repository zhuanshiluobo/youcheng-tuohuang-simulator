using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.CityStyles;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation;

namespace YC.Tests.EditMode
{
    public sealed class CityStyleSelectionControllerTests
    {
        private static readonly string[] EnterpriseStyleIds =
        {
            "city_style_city_construction_frontier", "city_style_pioneer_outpost",
            "city_style_core_commercial_area", "city_style_large_enterprise_park",
            "city_style_security_district", "city_style_pioneer_command_hub"
        };
        // 独立转录卡面布局，不从受测定义的图案生成正例。
        private static readonly string[][] Layouts =
        {
            new[] { "1,0:yellow", "0,1:blue", "1,1:blue", "2,1:red" },
            new[] { "1,0:yellow", "0,1:blue" },
            new[] { "0,0:blue", "1,0:blue", "2,0:red" },
            new[] { "0,0:blue", "0,1:red", "1,1:blue" },
            new[] { "0,0:blue", "0,1:red", "1,1:blue", "0,2:yellow", "1,2:yellow", "2,2:red" },
            new[] { "0,0:yellow", "0,1:red", "1,1:yellow", "0,2:blue", "1,2:blue", "2,2:red" }
        };
        private GameSetupRecipe coreRecipe;
        private GameContentDefinitions frozenDefinitions;
        private readonly CityStyleSelectionController controller = new CityStyleSelectionController();

        [OneTimeSetUp]
        public void LoadRealFrozenDefinitionsWithoutChangingGlobalCatalog()
        {
            AssertCoreGlobalCatalog();
            string root = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var installed = ExternalContentPack.Load(root);
            var catalog = ActiveContentCatalogBuilder.Build(installed, Configuration());
            coreRecipe = catalog.SetupRecipe;
            frozenDefinitions = catalog.ContentDefinitions;
            foreach (string id in EnterpriseStyleIds)
            {
                var json = JObject.Parse(File.ReadAllText(Path.Combine(root, "city_style", id + ".json")));
                var definition = json["data"].ToObject<CityStyleDefinition>();
                Assert.That(definition.CityStyleId, Is.EqualTo(id));
                Assert.That(definition.Name, Is.EqualTo((string)json["displayName"]));
                frozenDefinitions.CityStyles.Add(definition);
            }
            AssertCoreGlobalCatalog();
        }

        [TearDown]
        public void GlobalCatalogRemainsCoreOnly() => AssertCoreGlobalCatalog();

        [Test]
        public void MixedSupplyPreservesOrderAndUsesFrozenDefinitions()
        {
            var state = State();
            var requested = new[] { EnterpriseStyleIds[4], CityStyleDatabase.CompositePowerSystem,
                EnterpriseStyleIds[1], CityStyleDatabase.MaterialRelayStation };
            // 公共样式区无牌序；实际投影沿实体集合的装配顺序，不沿本次移入顺序。
            var expected = new[] { CityStyleDatabase.CompositePowerSystem, CityStyleDatabase.MaterialRelayStation,
                EnterpriseStyleIds[1], EnterpriseStyleIds[4] };
            Supply(state, requested);
            Assert.That(state.Decks.CityStyleSupply.ToArray(), Is.EqualTo(expected));
            var options = controller.BuildOptions(state, 1);
            Assert.That(options.Select(option => option.CityStyleId), Is.EqualTo(expected));
            foreach (var option in options)
            {
                var definition = CityStyleDatabase.Get(state, option.CityStyleId);
                Assert.That(option.Name, Is.EqualTo(definition.Name));
                Assert.That(option.Description, Is.EqualTo(definition.Description));
                Assert.That(option.Score, Is.EqualTo(definition.Score));
            }
        }

        [TestCase(0, 4)]
        [TestCase(1, 2)]
        [TestCase(2, 3)]
        [TestCase(3, 3)]
        [TestCase(4, 6)]
        [TestCase(5, 6)]
        public void EnterpriseLayoutCanBeBrowsedAndConfirmedWithCoreOnlyGlobalCatalog(int styleIndex, int count)
        {
            var state = State();
            string id = EnterpriseStyleIds[styleIndex];
            Supply(state, id);
            var slots = Layout(state, styleIndex);
            var option = controller.BuildOptions(state, 1).Single();
            Assert.That(option.CityStyleId, Is.EqualTo(id));
            Assert.That(option.CanDeclare, Is.True, option.Reason);
            var result = controller.ValidateSelection(state, 1, id, slots);
            Assert.That(result.CanConfirm, Is.True, result.Reason);
            Assert.That(result.RequiredFacilityCount, Is.EqualTo(count));
            Assert.That(result.SelectedFacilityCount, Is.EqualTo(count));
            Assert.That(result.RotationDegrees, Is.Zero);
        }

        [Test]
        public void EmptySupplyDoesNotRestoreGlobalDefaults()
        {
            var state = State();
            Supply(state);
            Assert.That(state.Decks.CityStyleSupply, Is.Empty);
            Assert.That(controller.BuildOptions(state, 1), Is.Empty);
        }

        [Test]
        public void MissingFrozenDefinitionFailsExplicitlyEvenWhenGlobalDefinitionExists()
        {
            var state = State();
            string id = CityStyleDatabase.CompositePowerSystem;
            Supply(state, id);
            state.ContentDefinitions.CityStyles.RemoveAll(style => style.CityStyleId == id);
            Assert.That(CityStyleDatabase.TryGet(id, out _), Is.True);
            var error = Assert.Throws<InvalidOperationException>(() => controller.BuildOptions(state, 1));
            Assert.That(error.Message, Does.Contain(id));
            Assert.That(controller.ValidateSelection(state, 1, id, new[] { 0, 1, 2 }).CanConfirm, Is.False);
        }

        [Test]
        public void UnavailableStyleRemainsBrowsableWithDeclarationReason()
        {
            var state = State();
            string id = EnterpriseStyleIds[2];
            Supply(state, id);
            var expected = new DeclareCityStyleService().Validate(state, 1, id);
            Assert.That(expected.IsValid, Is.False);
            var option = controller.BuildOptions(state, 1).Single();
            Assert.That(option.CityStyleId, Is.EqualTo(id));
            Assert.That(option.CanDeclare, Is.False);
            Assert.That(option.Reason, Is.EqualTo(expected.Reason));
            Assert.That(option.Reason, Is.Not.Empty);
        }

        [Test]
        public void IncorrectSelectedCountCannotConfirmAndKeepsRealRequiredCount()
        {
            var state = State();
            string id = EnterpriseStyleIds[2];
            Supply(state, id);
            var slots = Layout(state, 2);
            var result = controller.ValidateSelection(state, 1, id, slots.Take(2));
            Assert.That(result.CanConfirm, Is.False);
            Assert.That(result.RequiredFacilityCount, Is.EqualTo(3));
            Assert.That(result.SelectedFacilityCount, Is.EqualTo(2));
            Assert.That(result.Reason, Is.Not.Empty);
        }

        [Test]
        public void WrongColorCannotConfirm()
        {
            var state = State();
            string id = EnterpriseStyleIds[2];
            Supply(state, id);
            var slots = Layout(state, 2, wrongColor: true);
            Assert.That(controller.ValidateSelection(state, 1, id, slots).CanConfirm, Is.False);
        }

        [Test]
        public void RepeatedSelectedSlotCannotConfirm()
        {
            var state = State();
            string id = EnterpriseStyleIds[2];
            Supply(state, id);
            Layout(state, 2);
            Assert.That(controller.ValidateSelection(state, 1, id, new[] { 0, 0, 2 }).CanConfirm, Is.False);
        }

        [Test]
        public void PreviouslyDeclaredPhysicalInstancesCannotBeReused()
        {
            var state = State();
            string id = EnterpriseStyleIds[2];
            Supply(state, id);
            var slots = Layout(state, 2);
            Assert.That(controller.ValidateSelection(state, 1, id, slots).CanConfirm, Is.True);
            Declare(state, id, slots);
            var declaration = state.FindPlayer(1).DeclaredCityStyles.Single();
            var actualIds = state.Map.Facilities.Where(facility => slots.Contains(facility.CityBoardSlotIndex))
                .Select(facility => facility.ContentInstanceId);
            Assert.That(declaration.UsedFacilityIds, Is.EquivalentTo(actualIds));
            Assert.That(controller.ValidateSelection(state, 1, id, slots).CanConfirm, Is.False);
            Assert.That(controller.BuildOptions(state, 1).Single().CanDeclare, Is.False);
        }

        [Test]
        public void FrozenButUnpublishedStyleCannotConfirm()
        {
            var state = State();
            string id = EnterpriseStyleIds[2];
            Supply(state, EnterpriseStyleIds[1]);
            var slots = Layout(state, 2);
            var result = controller.ValidateSelection(state, 1, id, slots);
            Assert.That(result.CanConfirm, Is.False);
            Assert.That(result.Reason, Is.EqualTo(new DeclareCityStyleService().Validate(state, 1, id, slots).Reason));
            Assert.That(controller.BuildOptions(state, 1).Select(option => option.CityStyleId), Does.Not.Contain(id));
        }

        [TestCase("effect")]
        [TestCase("resource")]
        [TestCase("slot")]
        [TestCase("row")]
        public void AdditionalFrozenDeclarationRequirementsStillApply(string condition)
        {
            var state = State();
            int index = condition == "row" ? 0 : 2;
            string id = EnterpriseStyleIds[index];
            Supply(state, id);
            var slots = Layout(state, index, facilityFilter: condition == "resource"
                ? (Func<FacilityCardDefinition, bool>)(facility => facility.ResourceCost.Originium > 0) : null);
            var initial = controller.ValidateSelection(state, 1, id, slots);
            Assert.That(initial.CanConfirm, Is.True, initial.Reason);
            var style = state.ContentDefinitions.CityStyles.Single(value => value.CityStyleId == id);
            var requirement = style.DeclarationRequirement;
            if (condition == "effect") requirement.RequiredEffectTypes.Add("test.unavailable_facility_effect");
            else if (condition == "resource")
            {
                // RequiredResourceTypes 检查所选设施的建造成本，不检查玩家钱包。
                requirement.RequiredResourceTypes.Add(ResourceType.Originium);
                var eligible = controller.ValidateSelection(state, 1, id, slots);
                Assert.That(eligible.CanConfirm, Is.True, eligible.Reason);
                Assert.That(controller.BuildOptions(state, 1).Single().CanDeclare, Is.True);
                var placement = state.Map.Facilities.Single(facility => facility.PlayerId == 1 && facility.CityBoardSlotIndex == slots[0]);
                var previous = state.ItemZones.Items.Single(item => item.ItemId == placement.ContentInstanceId);
                var color = FacilityCardDatabase.Get(state, previous.DefinitionId).Color;
                var availableZones = AvailableFacilityZones(state);
                var replacement = state.ItemZones.Items.First(item => item.TypeId == "core.facility_card" &&
                    availableZones.Contains(item.ZoneId) &&
                    FacilityCardDatabase.Get(state, item.DefinitionId).Color == color &&
                    FacilityCardDatabase.Get(state, item.DefinitionId).ResourceCost.Originium == 0);
                Move(state, previous, Zone(state, "facility_deck").ZoneId);
                Move(state, replacement, Zone(state, "city_cell_" + slots[0], 1).ZoneId);
                AssertValidItems(state);
                // 更换实际实例只使资源成本条件失效，颜色和布局仍然成立。
                var patternOnly = GameStateCloneService.DeepClone(style);
                patternOnly.DeclarationRequirement.RequiredResourceTypes.Clear();
                Assert.That(new CityStylePatternMatcher().MatchSelected(state, 1, patternOnly, slots).Succeeded, Is.True);
            }
            else if (condition == "slot") requirement.RequiredCityBoardSlotIndexes.Add(11);
            else requirement.RequireSameCityBoardRow = true;
            var result = controller.ValidateSelection(state, 1, id, slots);
            Assert.That(result.CanConfirm, Is.False);
            var option = controller.BuildOptions(state, 1).Single();
            Assert.That(option.CanDeclare, Is.False);
            Assert.That(option.Reason, Is.Not.Empty);
        }

        private static GameRuleConfiguration Configuration() => new GameRuleConfiguration
        { MapId = StaticMapDefinitions.FourPlayerMapId, PlayerCount = 3 };

        private GameState State()
        {
            AssertCoreGlobalCatalog();
            var recipe = GameStateCloneService.DeepClone(coreRecipe);
            foreach (string id in EnterpriseStyleIds) recipe.Add(id, id, "core.board_card", "city_styles");
            recipe.CityStyleLevelOneDrawCount = 8;
            recipe.CityStyleLevelTwoDrawCount = 4;
            var config = Configuration();
            config.ContentPackHash = "test.city-style-selection";
            var seats = Enumerable.Range(1, 3).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "玩家" + id }).ToList();
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, seats, config, 127, recipe, frozenDefinitions);
            state.Phase = GamePhase.ActionRound1;
            state.Round = 1;
            state.ActionRound = 1;
            state.CurrentPlayerId = 1;
            var tower = state.ItemZones.Items.Single(item => item.ZoneId == Zone(state, "city_cell_7", 1).ZoneId);
            Move(state, tower, Zone(state, "city_cell_11", 1).ZoneId);
            AssertValidItems(state);
            return state;
        }

        private static void AssertCoreGlobalCatalog()
        {
            Assert.That(CityStyleDatabase.All.Count, Is.EqualTo(6));
            foreach (string id in EnterpriseStyleIds) Assert.That(CityStyleDatabase.TryGet(id, out _), Is.False);
        }

        private static ZoneInstanceState Zone(GameState state, string local, int player = -1) =>
            state.ItemZones.Zones.Single(zone => zone.LocalZoneId == local && (player < 0 || zone.OwnerPlayerId == player));

        private static void Move(GameState state, ItemInstanceState item, string zoneId)
        {
            state.ItemZones.Orders.Find(order => order.ZoneId == item.ZoneId)?.ItemIds.Remove(item.ItemId);
            item.ZoneId = zoneId;
            item.SlotId = string.Empty;
            state.ItemZones.Orders.Find(order => order.ZoneId == zoneId)?.ItemIds.Add(item.ItemId);
        }

        private static void Supply(GameState state, params string[] ids)
        {
            var zone = Zone(state, "city_styles");
            Assert.That(state.ItemZones.Orders.Any(order => order.ZoneId == zone.ZoneId), Is.False,
                "无序公共样式区不应有人工插入的牌序。");
            var expectedProjection = state.ItemZones.Items.Where(item => item.TypeId == "core.board_card" && ids.Contains(item.DefinitionId))
                .Select(item => item.DefinitionId).ToArray();
            Assert.That(expectedProjection.Length, Is.EqualTo(ids.Length));
            foreach (var item in state.ItemZones.Items.Where(item => item.ZoneId == zone.ZoneId).ToArray())
                Move(state, item, Zone(state, "reserve").ZoneId);
            foreach (string id in ids)
                Move(state, state.ItemZones.Items.Single(item => item.TypeId == "core.board_card" && item.DefinitionId == id), zone.ZoneId);
            Assert.That(state.Decks.CityStyleSupply.ToArray(), Is.EqualTo(expectedProjection));
            AssertValidItems(state);
        }

        private static HashSet<string> AvailableFacilityZones(GameState state) =>
            new HashSet<string>(state.ItemZones.Zones.Where(zone => zone.LocalZoneId == "facility_deck" ||
                zone.LocalZoneId.StartsWith("facility_supply_", StringComparison.Ordinal)).Select(zone => zone.ZoneId));

        private static List<int> Layout(GameState state, int index, bool wrongColor = false,
            Func<FacilityCardDefinition, bool> facilityFilter = null)
        {
            var selected = new HashSet<string>();
            var slots = new List<int>();
            var availableZones = AvailableFacilityZones(state);
            foreach (string cell in Layouts[index])
            {
                var parts = cell.Split(':');
                var xy = parts[0].Split(',');
                int slot = int.Parse(xy[1]) * BuildFacilityService.CityBoardSlotCountPerRow + int.Parse(xy[0]);
                string color = wrongColor && slots.Count == 0 ? "yellow" : parts[1];
                var item = state.ItemZones.Items.First(candidate => candidate.TypeId == "core.facility_card" &&
                    !selected.Contains(candidate.ItemId) && availableZones.Contains(candidate.ZoneId) &&
                    FacilityCardDatabase.Get(state, candidate.DefinitionId).Color == color &&
                    (facilityFilter == null || facilityFilter(FacilityCardDatabase.Get(state, candidate.DefinitionId))));
                selected.Add(item.ItemId);
                Move(state, item, Zone(state, "city_cell_" + slot, 1).ZoneId);
                slots.Add(slot);
            }
            AssertValidItems(state);
            return slots;
        }

        private static void Declare(GameState state, string id, List<int> slots)
        {
            var registry = new EffectRegistry();
            CityStyleDeclarationEffectExecutor.Register(registry);
            var executor = new EffectTreeExecutor(state, registry);
            string root = executor.CreateRoot(CityStyleDeclarationEffectExecutor.Create(1, id, slots));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            Assert.That(executor.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed), executor.LastDiagnostic);
            AssertValidItems(state);
        }

        private static void AssertValidItems(GameState state) =>
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
    }
}
