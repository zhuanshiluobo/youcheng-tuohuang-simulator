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
    public sealed class InfectedFacilityStyleAwardTests
    {
        private const string Business = "test.enterprise.habitat";
        private const string Logistics = "test.enterprise.logistics";
        private const string Foundation = "infected.enterprise.maylander_foundation";
        private const string Settlement = "infected.facility.temporary_settlement";
        private static readonly string[] StyleIds = { "infected.city_style.emerging_buildings", "infected.city_style.district_management", "infected.city_style.enterprise_offices" };
        private static readonly string[] OfficeIds = { "infected.facility.rhodes_office_red", "infected.facility.rhodes_office_blue", "infected.facility.rhodes_office_yellow" };
        private static readonly string[] AwardIds = { "infected.award.enterprise_special", "infected.award.score", "infected.award.roads", "infected.award.permanent_support" };
        private ExternalContentPack pack;
        private GameSetupRecipe coreRecipe;

        [OneTimeSetUp]
        public void LoadRealDefinitionsThroughProductionLoader()
        {
            string source = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            string destination = Path.Combine(Directory.GetCurrentDirectory(), "Logs/ExpansionValidation/InfectedFacilityStyleAward/content-fixture");
            // 测试装配显式列举真实文件；正式 pack 保持由内容集成任务登记。
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(file => !file.EndsWith(".meta", StringComparison.Ordinal)))
            {
                string target = Path.Combine(destination, file.Substring(source.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target, true);
            }
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(destination, "pack.json")));
            foreach (string path in StyleIds.Select(id => "city_style/" + id + ".json").Concat(OfficeIds.Concat(new[] { Settlement }).Select(id => "facility/" + id + ".json")).Concat(AwardIds.Select(id => "award/" + id + ".json")))
                if (!((JArray)manifest["definitions"]).Values<string>().Contains(path)) ((JArray)manifest["definitions"]).Add(path);
            ((JArray)manifest["definitions"]).Add("enterprise/" + Foundation + ".json");
            manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected");
            var originals = Directory.GetFiles(Path.Combine(Directory.GetCurrentDirectory(), "游城拓荒/感染者扩"));
            string foundationArt = Path.Combine(destination, "artwork/enterprises/infected_maylander_foundation.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(foundationArt)); File.Copy(originals.Single(p => Path.GetFileName(p).Contains("2410077427276855138")), foundationArt, true);
            var imageKeys = new[] { "2411202693639238062", "2411202693639238822", "2411202693639239637", "2655937845512670668", "26559378455128280", "26559378455128593", "2411202693633901188", "2411202693639234383", "2411202693639235372", "2411202693639236068", "2411202693639236804" };
            var contentPaths = StyleIds.Select(id => "city_style/" + id + ".json").Concat(OfficeIds.Concat(new[] { Settlement }).Select(id => "facility/" + id + ".json")).Concat(AwardIds.Select(id => "award/" + id + ".json")).ToList();
            var slices = JObject.Parse(File.ReadAllText(Path.Combine(destination, "artwork_slices.json")));
            for (int index = 0; index < contentPaths.Count; index++)
            {
                var json = JObject.Parse(File.ReadAllText(Path.Combine(source, contentPaths[index])));
                string artwork = Path.Combine(destination, (string)json["artwork"]);
                Directory.CreateDirectory(Path.GetDirectoryName(artwork)); string original = originals.Single(p => Path.GetFileName(p).Contains(imageKeys[index])); File.Copy(original, artwork, true);
                int width = index < 3 ? 930 : 600, height = index < 3 ? 600 : 850; string sprite = (string)json["artworkSpriteId"];
                ((JArray)slices["sheets"]).Add(new JObject { ["id"] = sprite, ["artwork"] = json["artwork"], ["width"] = width, ["height"] = height, ["rows"] = 1, ["columns"] = 1, ["cellWidth"] = width, ["cellHeight"] = height, ["skippedCells"] = new JArray() });
                ((JArray)slices["slices"]).Add(new JObject { ["id"] = sprite, ["sheetId"] = sprite, ["cell"] = 1, ["row"] = 1, ["column"] = 1, ["name"] = json["displayName"], ["contentType"] = json["contentType"], ["definitionIds"] = new JArray((string)json["definitionId"]), ["expansionId"] = "infected", ["rect"] = new JObject { ["x"] = 0, ["y"] = 0, ["width"] = width, ["height"] = height } });
            }
            File.WriteAllText(Path.Combine(destination, "artwork_slices.json"), slices.ToString());
            File.WriteAllText(Path.Combine(destination, "pack.json"), manifest.ToString());
            pack = ExternalContentPack.Load(destination);
            typeof(ExternalContentPack).GetField("UseDefinitionIds", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(pack, true);
            coreRecipe = ActiveContentCatalogBuilder.Build(pack, new GameRuleConfiguration {
                PlayerCount = 3, MapId = StaticMapDefinitions.FourPlayerMapId }).SetupRecipe;
        }

        private GameState State(out EffectRegistry registry, int level = 0, bool allStyles = true, bool nestedBuild = false, bool foundation = false, int enterpriseCount = 2)
        {
            var definitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(), Facilities = pack.CreateFacilities().ToList(),
                Events = pack.CreateEvents().ToList(), CityStyles = pack.CreateCityStyles().ToList(), SpecialActions = pack.CreateSpecialActions().ToList(), Awards = ContentLifecycleLuaCatalog.CreateAwards(pack) };
            var recipe = GameStateCloneService.DeepClone(coreRecipe);
            foreach (var definition in pack.ActiveDefinitions.Where(value => StyleIds.Contains(value.DefinitionId)))
                recipe.CardZoneDeclarations.Add(new ItemCardZoneDeclaration { DefinitionId = definition.DefinitionId,
                    CardZones = GameStateCloneService.DeepClone(definition.CardZones) });
            foreach (string id in OfficeIds) recipe.Add(id, id, "core.facility_card", "facility_deck");
            recipe.Add("temporary_settlements", Settlement, "core.facility_card", "facility_deck", quantity: 3);
            foreach (string id in StyleIds) recipe.Add(id, id, "core.board_card", "city_styles");
            foreach (string id in AwardIds) recipe.Add(id, id, "core.award_card", "awards");
            foreach (var character in pack.CreateCharacters().Where(c => c.CardId.StartsWith("operator."))) recipe.Add(character.CardId, character.CardId, "core.operator_card", "operator_deck");
            if (allStyles) { recipe.CityStyleLevelOneDrawCount = 6; recipe.CityStyleLevelTwoDrawCount = 3; }
            var enterpriseIds = foundation ? new[] { Foundation } : new[] { Business, Logistics, "test.enterprise.transit", "test.enterprise.trade" }.Take(enterpriseCount).ToArray();
            foreach (string id in enterpriseIds)
            {
                recipe.Add(id, id, "core.board_card", "enterprises");
                recipe.CardZoneDeclarations.Add(new ItemCardZoneDeclaration { DefinitionId = id, CardZones = Enumerable.Range(1, 3).Select(player => new CardZoneDefinition {
                    LocalZoneId = "cooperation_" + player, ZoneTemplateId = "core.zone.board_marker_track", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
                    AcceptedTypeIds = new List<string> { "core.player_marker" }, CapacityPolicy = "bounded", Capacity = 1, OrderingKind = ZoneOrderingKind.Track,
                    EntryPolicyId = "core.owned_player_marker", ExitPolicyId = "core.explicit_transfer", VisibilityPolicyId = ItemZoneVisibility.Public,
                    CarrierExitPolicyId = "core.preserve_contents" }).ToList() });
            }
            registry = new EffectRegistry { ContentPackHash = "test.infected.facility-style-award", SetupRecipe = recipe, ContentDefinitions = definitions };
            var enterprise = foundation ? LuaContentCatalog.CreateEnterpriseDefinitions(registry, pack.ActiveDefinitions.Where(d => d.DefinitionId == Foundation), pack.GetScript) : new EnterpriseDefinitions();
            foreach (string id in foundation ? Array.Empty<string>() : enterpriseIds)
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
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); EnterpriseEffectExecutor.Register(registry, enterprise);
            FacilityBuildEffectExecutor.Register(registry); FacilitySelectionEffectExecutor.Register(registry);
            FacilityEntryEffectExecutor.Register(registry);
            FacilityLuaCatalog.Register(registry, pack); CityStyleLuaCatalog.Register(registry, pack); ContentLifecycleLuaCatalog.Register(registry, pack);
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
            foreach (string id in enterpriseIds) foreach (int player in Enumerable.Range(1, 3))
            {
                var track = state.ItemZones.Zones.Single(zone => zone.ParentId == Item(state, id) && zone.LocalZoneId == "cooperation_" + player);
                var marker = state.ItemZones.Items.First(value => value.TypeId == "core.player_marker" && value.OwnerSubject.PlayerId == player && value.ZoneId == FindZone(state, "player_markers", player).ZoneId);
                SetPosition(state, marker, track.ZoneId); marker.SlotId = "level_" + level;
            }
            var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)); var influence = new InfluenceService(map);
            state.Map.OpenLocationIds.AddRange(map.Map.Locations.Select(location => location.LocationId));
            InfluenceEffectExecutor.Register(registry, influence);
            EventCardEffectExecutor.Register(registry, map, influence, new EventDeckService(), new ResourceTokenService());
            CityMoveEffectExecutor.Register(registry, map, influence, new CityMovementService(map, influence, new TravelCostService(map)), new TravelCostService(map), new ResourceTokenService());
            var query = new SpecialActionOptionQueryService(map, influence, new CityMovementService(map, influence, new TravelCostService(map)), new SpecialActionLifecycleService(), new MainActionBudgetService());
            CityStyleSpecialActionEffectExecutor.Register(registry, query, null, map, influence, new CityMovementService(map, influence, new TravelCostService(map)));
            foreach (var player in state.Players) player.Resources = new ResourceSet { GoldVoucher = 80, Originium = 20, OriginiumShard = 20, Iron = 20 };
            Valid(state); return state;
        }
        private static string Item(GameState state, string id) => state.ItemZones.Items.First(value => value.DefinitionId == id).ItemId;
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
        private static readonly string[][] Patterns = {
            new[] { "0,0:yellow", "1,0:yellow", "2,0:red", "1,1:blue" },
            new[] { "0,0:yellow", "1,0:red", "0,1:blue" },
            new[] { "0,0:blue", "1,0:blue", "2,0:red", "0,1:red", "1,1:yellow", "0,2:yellow" } };
        private List<int> Layout(GameState state, int style, int rotation = 0, bool mirror = false, bool settlement = false)
        {
            foreach (var placed in state.Map.Facilities.Where(f => f.PlayerId == 1 && f.CityBoardSlotIndex != 11).ToList())
                Position(state, placed.ContentInstanceId, "facility_deck");
            var cells = Patterns[style].Select(c => { var p = c.Split(':'); var xy = p[0].Split(','); int x = int.Parse(xy[0]), y = int.Parse(xy[1]);
                if (mirror) x = -x; for (int k = 0; k < rotation; k++) { int prior = x; x = -y; y = prior; } return Tuple.Create(x, y, p[1]); }).ToList();
            int minX = cells.Min(c => c.Item1), minY = cells.Min(c => c.Item2);
            var used = new HashSet<string>(state.FindPlayer(1).DeclaredCityStyles.SelectMany(d => d.UsedFacilityIds)); var slots = new List<int>();
            foreach (var cell in cells)
            {
                int slot = (cell.Item2 - minY) * BuildFacilityService.CityBoardSlotCountPerRow + cell.Item1 - minX;
                var item = state.ItemZones.Items.First(i => !used.Contains(i.ItemId) && i.TypeId == "core.facility_card" &&
                    (settlement && slots.Count == 0 ? i.DefinitionId == Settlement : FacilityCardDatabase.Get(state, i.DefinitionId).Color == cell.Item3 && !OfficeIds.Contains(i.DefinitionId)));
                Position(state, item.ItemId, "city_cell_" + slot, 1); item.ControllerSubject = ItemSubjectRef.Player(1); used.Add(item.ItemId); slots.Add(slot);
            }
            Valid(state); return slots;
        }
        private static void Drain(GameState state, EffectRegistry registry)
        {
            for (int i = 0; i < 40 && Open(state) != null; i++) Answer(state, registry, Open(state).CandidateIds.First());
            Assert.That(Open(state), Is.Null);
        }
        private static void Acquire(GameState state, EffectRegistry registry, string id)
        {
            Run(state, registry, EffectSpec.Create(RewardAcquisitionFlow.TypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "poolRole", NormalizedValue.CreateString("awards")), 1));
            Answer(state, registry, Item(state, id));
        }
        private static void Cleanup(GameState state, EffectRegistry registry)
        {
            if (!registry.TryGet("test.player_cleanup", out _)) registry.Register("test.player_cleanup", c => EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType = "PlayerCleanupStarted", PlayerId = c.Node.PlayerId }));
            Run(state, registry, EffectSpec.Create("test.player_cleanup", playerId: 1));
        }
        private static int Level(GameState state, string id) => new EnterpriseStateQuery().GetLevel(state, 1, Item(state, id));


        [TestCaseSource(nameof(AwardIds))]
        public void FoundationLevelFourGrantsEachActualAwardAndPublicSupplyCannotDuplicateIt(string award)
        {
            var state = State(out var registry, level: 3, foundation: true);
            int count = state.ItemZones.Items.Count;
            Run(state, registry, EnterpriseEffectExecutor.UpgradeSpec(1, Item(state, Foundation), 1));
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(AwardIds.Select(id => Item(state, id))));
            state = Restore(state, registry); Answer(state, registry, Item(state, award));
            var receipt = ContentLifecycleEffectExecutor.Acquisition(state, Item(state, award));
            Assert.That(receipt, Is.Not.Null); Assert.That(Level(state, Foundation), Is.EqualTo(4));
            if (award == AwardIds[2]) { Answer(state, registry, Open(state).CandidateIds.First()); state = Restore(state, registry); Answer(state, registry, Open(state).CandidateIds.First()); }
            state = Restore(state, registry);
            Assert.That(state.ItemZones.Items.Count, Is.EqualTo(count + (award == AwardIds[2] ? 2 : 0)));
            Assert.That(state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.reward_pool" && z.LocalZoneId == "awards").ZoneId, Is.Not.EqualTo(state.ItemZones.Items.Single(i => i.ItemId == Item(state, award)).ZoneId));
            Run(state, registry, EffectSpec.Create(RewardAcquisitionFlow.TypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "poolRole", NormalizedValue.CreateString("awards")), 1));
            Assert.That(Open(state).CandidateIds, Does.Not.Contain(Item(state, award)));
        }

        [Test]
        public void FourEnterpriseChoiceKeepsFirstUpgradeReceiptAndExcludesItAfterRestore()
        {
            var state = State(out var registry, enterpriseCount: 4);
            string office = Item(state, OfficeIds[0]); Position(state, office, "city_cell_0", 1);
            Run(state, registry, FacilityEntryEffectSpecFactory.Activate(1, OfficeIds[0], office, 0)); Answer(state, registry, "upgrade_two");
            Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(4)); string first = Open(state).CandidateIds.First(); Answer(state, registry, first);
            state = Restore(state, registry); Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(3)); Assert.That(Open(state).CandidateIds, Does.Not.Contain(first));
            Answer(state, registry, Open(state).CandidateIds.First());
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == EnterpriseEffectExecutor.LevelChangedEvent), Is.EqualTo(2));
        }

        [Test]
        public void DistrictManagementUsesActualDeclarationAndDoesNotRewardAnUnavailableMarker()
        {
            var state = State(out var registry); Run(state, registry, CityStyleDeclarationEffectExecutor.Create(1, StyleIds[2], Layout(state, 2)));
            string marker = state.FindPlayer(1).DeclaredCityStyles.Single().InfluenceMarkerId;
            int pure = state.FindPlayer(1).Resources.PureOriginium;
            Run(state, registry, CityStyleDeclarationEffectExecutor.Create(1, StyleIds[1], Layout(state, 1)));
            Answer(state, registry, "return"); state = Restore(state, registry); Answer(state, registry, marker); state = Restore(state, registry);
            Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure + 1)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(9));
            Assert.That(state.FindPlayer(1).DeclaredCityStyles[0].MarkerArea, Is.Empty);
            // 历史宣告不能凭相同格位或旧设施再次支付图案。
            Assert.That(new CityStylePatternMatcher().MatchSelected(state, 1, CityStyleDatabase.Get(state, StyleIds[1]), state.FindPlayer(1).DeclaredCityStyles[1].UsedCityBoardSlotIndexes).Succeeded, Is.False);
            var before = state.FindPlayer(1).Resources.PureOriginium;
            var node = Run(state, registry, EffectSpec.Create(ContentLifecycleEffectExecutor.ReturnMarkerTypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1")), 1));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(before));
        }

        [Test]
        public void PersistentAwardMovesOnlyOwnInfluenceAndKeepsItsRealInstance()
        {
            var state = State(out var registry); var route = StaticMapDefinitions.Resolve(state.MapId).Routes.First(r => r.InfluenceSlotCount >= 2);
            Run(state, registry, InfluenceEffectSpecFactory.PlaceInfluence(1, InfluenceService.GetRouteSlotId(route.RouteId, 0)));
            Run(state, registry, InfluenceEffectSpecFactory.PlaceInfluence(2, InfluenceService.GetRouteSlotId(route.RouteId, 1)));
            string own = state.Map.Influences.Single(i => i.PlayerId == 1).InfluenceId, other = state.Map.Influences.Single(i => i.PlayerId == 2).InfluenceId;
            string oldSlot = state.Map.Influences.Single(i => i.InfluenceId == own).SlotId;
            Acquire(state, registry, AwardIds[3]); Cleanup(state, registry); Answer(state, registry, "originium_shard");
            Assert.That(Open(state).CandidateIds.All(id => id.Contains("|" + own + "|")), Is.True); Assert.That(Open(state).CandidateIds.Any(id => id.Contains(other)), Is.False);
            string chosen = Open(state).CandidateIds.First(); state = Restore(state, registry); Answer(state, registry, chosen);
            Assert.That(state.Map.Influences.Single(i => i.InfluenceId == own).SlotId, Is.Not.EqualTo(oldSlot));
            Assert.That(ContentLifecycleEffectExecutor.IsHeld(state, state.ItemZones.Items.Single(i => i.ItemId == Item(state, AwardIds[3])), 1), Is.True);
        }

        [Test]
        public void HostRejectsForgedMarkerReturnAndDuplicateEnterpriseSelection()
        {
            var state = State(out var registry); Run(state, registry, CityStyleDeclarationEffectExecutor.Create(1, StyleIds[2], Layout(state, 2)));
            string marker = state.FindPlayer(1).DeclaredCityStyles.Single().InfluenceMarkerId;
            Position(state, marker, "player_markers", 1);
            Assert.Throws<InvalidOperationException>(() => HostRecoveryService.CreateArchive(state));
            state = State(out registry); string office = Item(state, OfficeIds[0]); Position(state, office, "city_cell_0", 1);
            Run(state, registry, FacilityEntryEffectSpecFactory.Activate(1, OfficeIds[0], office, 0)); Answer(state, registry, "upgrade_two"); Answer(state, registry, Open(state).CandidateIds.First());
            var flow = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == EnterpriseDistinctUpgradeFlow.TypeId);
            var selected = Value(flow.ContinuationData, "selectedEnterpriseItemIds"); selected.Items.Add(selected.Items[0].Clone());
            Assert.Throws<InvalidOperationException>(() => HostRecoveryService.CreateArchive(state));
        }

        [Test]
        public void RealDefinitionsMatchCountsCostsLifecycleAndUniqueGroups()
        {
            var state = State(out _);
            Assert.That(state.ItemZones.Items.Count(i => i.DefinitionId == Settlement), Is.EqualTo(3));
            var settlement = FacilityCardDatabase.Get(state, Settlement);
            Assert.That(settlement.GoldVoucherCost, Is.EqualTo(11)); Assert.That(settlement.ResourceCost.OriginiumShard, Is.EqualTo(1)); Assert.That(settlement.ResourceCost.Iron, Is.EqualTo(2));
            Assert.That(settlement.Score, Is.Zero); Assert.That(settlement.UniqueGroupId, Is.EqualTo(Settlement));
            for (int index = 0; index < OfficeIds.Length; index++)
            {
                var office = FacilityCardDatabase.Get(state, OfficeIds[index]);
                Assert.That(office.Color, Is.EqualTo(new[] { "red", "blue", "yellow" }[index])); Assert.That(office.Score, Is.Zero);
                Assert.That(office.GoldVoucherCost, Is.EqualTo(14)); Assert.That(office.ResourceCost.Originium, Is.EqualTo(1)); Assert.That(office.ResourceCost.OriginiumShard, Is.EqualTo(1)); Assert.That(office.ResourceCost.Iron, Is.EqualTo(2));
                Assert.That(office.UniqueGroupId, Is.EqualTo("infected.facility.rhodes_office")); Assert.That(state.ItemZones.Items.Count(i => i.DefinitionId == office.FacilityId), Is.EqualTo(1));
            }
            foreach (var id in AwardIds) Assert.That(state.ItemZones.Items.Count(i => i.DefinitionId == id && i.TypeId == "core.award_card"), Is.EqualTo(1));
            Assert.That(state.ContentDefinitions.Awards.Select(d => d.Lifecycle), Is.EquivalentTo(new[] { "held_quick_action", "immediate_retired", "immediate_retired", "persistent" }));
            Assert.That(StyleIds.Select(id => CityStyleDatabase.Get(state, id).Level), Is.EqualTo(new[] { 1, 1, 2 }));
            Assert.That(StyleIds.Select(id => CityStyleDatabase.Get(state, id).Score), Is.EqualTo(new[] { 4, 3, 6 }));
            Assert.That(state.Players.All(p => p.HandCardIds.All(id => !AwardIds.Contains(GameContentDefinitions.ResolveDefinitionId(state, id)))), Is.True);
        }
        [TestCase(0, 0)][TestCase(0, 1)][TestCase(0, 2)][TestCase(0, 3)]
        [TestCase(1, 0)][TestCase(1, 1)][TestCase(1, 2)][TestCase(1, 3)]
        [TestCase(2, 0)][TestCase(2, 1)][TestCase(2, 2)][TestCase(2, 3)]
        public void ActualPatternsRotateWithoutMirroring(int style, int rotation)
        {
            var state = State(out _); var slots = Layout(state, style, rotation);
            Assert.That(new CityStylePatternMatcher().MatchSelected(state, 1, CityStyleDatabase.Get(state, StyleIds[style]), slots).Succeeded, Is.True);
        }
        [TestCase(0)][TestCase(1)][TestCase(2)]
        public void MirroredPatternIsRejected(int style)
        {
            var state = State(out _); var slots = Layout(state, style, mirror: true);
            Assert.That(new CityStylePatternMatcher().MatchSelected(state, 1, CityStyleDatabase.Get(state, StyleIds[style]), slots).Succeeded, Is.False);
        }
        [TestCaseSource(nameof(OfficeIds))]
        public void OfficeBuildPaysNormalCostAndUpgradesTwoDifferentEnterprises(string definition)
        {
            var state = State(out var registry); string item = Item(state, definition); Position(state, item, "facility_supply_0");
            int gold = state.FindPlayer(1).Resources.GoldVoucher;
            Run(state, registry, FacilityBuildEffectSpecFactory.Build(1, item, -1, "gold", false, "test.office", false));
            Answer(state, registry, Open(state).CandidateIds.First()); Answer(state, registry, "upgrade_two");
            Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(2));
            state = Restore(state, registry); string first = Open(state).CandidateIds.First(); Answer(state, registry, first);
            Assert.That(Open(state).CandidateIds, Does.Not.Contain(first)); state = Restore(state, registry); Answer(state, registry, Open(state).CandidateIds.Single());
            Assert.That(Level(state, Business), Is.EqualTo(1)); Assert.That(Level(state, Logistics), Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - 14));
            foreach (var id in OfficeIds) Assert.That(FacilityCardDatabase.PlayerHasBuiltUniqueFacility(state, state.FindPlayer(1), FacilityCardDatabase.Get(state, id)), Is.True);
        }
        [Test]
        public void OfficeWithOnlyOneEnterpriseDoesNotOfferDoubleUpgradeOfSameEnterprise()
        {
            var state = State(out var registry); Position(state, Item(state, Logistics), "reserve");
            string id = Item(state, OfficeIds[0]); Position(state, id, "city_cell_0", 1);
            Run(state, registry, FacilityEntryEffectSpecFactory.Activate(1, OfficeIds[0], id, 0));
            Assert.That(Open(state).CandidateIds, Is.EqualTo(new[] { "dispatch" }));
        }
        [TestCase(true)][TestCase(false)]
        public void OfficeDispatchUsesThreeActualOperatorsAndRealReturnOrDecline(bool accept)
        {
            var state = State(out var registry); string id = Item(state, OfficeIds[0]); Position(state, id, "city_cell_0", 1);
            int score = state.FindPlayer(1).Score; int hand = state.FindPlayer(1).HandCardIds.Count;
            Run(state, registry, FacilityEntryEffectSpecFactory.Activate(1, OfficeIds[0], id, 0)); Answer(state, registry, "dispatch");
            Assert.That(state.ItemZones.Items.Count(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.LocalZoneId == "dispatch_candidates")), Is.EqualTo(3));
            state = Restore(state, registry);
            var candidates = Open(state).CandidateIds;
            if (accept) Answer(state, registry, candidates.First());
            else
            {
                var request = Open(state); var executor = new EffectTreeExecutor(state, registry);
                Assert.That(request.AllowDecline, Is.True);
                Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateBoolean(false), out var reason), Is.True, reason);
                Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            }
            if (accept) { Assert.That(Open(state), Is.Not.Null); Answer(state, registry, Open(state).CandidateIds.First(id => state.ItemZones.Items.Any(i => i.ItemId == id && i.TypeId == "core.basic_character_card"))); }
            Assert.That(state.FindPlayer(1).HandCardIds.Count, Is.EqualTo(hand)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + (accept ? 0 : 1)));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == FindZone(state, "retired").ZoneId && i.TypeId == "core.basic_character_card"), Is.EqualTo(accept ? 1 : 0));
        }
        [TestCase(false)][TestCase(true)]
        public void DeclarationRetiresOnlyUsedSettlementAndBindingWithEveryCommitRestored(bool retire)
        {
            var state = State(out var registry); var slots = Layout(state, 1, settlement: true);
            string facility = state.Map.Facilities.Single(f => f.PlayerId == 1 && f.CityBoardSlotIndex == slots[0]).ContentInstanceId;
            var others = state.ItemZones.Items.Where(i => i.DefinitionId == Settlement && i.ItemId != facility).ToDictionary(i => i.ItemId, i => i.ZoneId);
            string character = state.ItemZones.Items.First(i => i.ZoneId == FindZone(state, "hand", 1).ZoneId).ItemId;
            CharacterCoverEffectExecutor.Register(registry); CharacterLifecycleService.Register(registry);
            var definition = CharacterCardDatabase.Get(state, GameContentDefinitions.ResolveDefinitionId(state, character));
            foreach (string mode in new[] { "strategy", "tactic" })
            {
                string ability = mode == "strategy" ? definition.StrategyAbilityId : definition.TacticAbilityId;
                registry.CharacterActivationSubscriptions[ability] = "test.bound." + ability;
                registry.CharacterAbilityContracts[ability] = new CharacterModeContract { Mode = mode, AbilityId = ability, SkillType = "persistent" };
                registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.bound." + ability, CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType, ability, "test.bound." + ability,
                    c => new List<EffectSpec> { ResourceEffectSpecFactory.Gain(c.Event.PlayerId, ResourceType.GoldVoucher, 1) }, EffectHandlerRole.Primary));
            }
            Run(state, registry, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(state, registry, character);
            Run(state, registry, CharacterLifecycleService.CreateUseSpec(1, character, "tactic"));
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, character, definition.TacticAbilityId), Is.True);
            registry.Register("test.bind_facility", c => CharacterLifecycleService.BindFacility(c, character, facility));
            Run(state, registry, EffectSpec.Create("test.bind_facility", playerId: 1));
            int score = state.FindPlayer(1).Score, gold = state.FindPlayer(1).Resources.GoldVoucher;
            Run(state, registry, CityStyleDeclarationEffectExecutor.Create(1, StyleIds[1], slots));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 3)); Assert.That(state.FindPlayer(1).DeclaredCityStyles.Single().MarkerArea, Is.EqualTo("declared"));
            // 同一宣告的管理中心选择和设施选择都必须有；不假定订阅显示顺序。
            var observed = new HashSet<string>();
            for (int steps = 0; steps < 150; steps++)
            {
                state = Restore(state, registry); var engine = new EffectTreeExecutor(state, registry); var request = Open(state);
                if (request != null)
                {
                    string choice = request.CandidateIds.Contains("retire") ? (retire ? "retire" : "keep") : "keep";
                    Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString(choice), out var reason), Is.True, reason);
                }
                var current = state.ItemZones.Items.Single(i => i.ItemId == facility);
                var bound = state.ItemZones.Items.Single(i => i.ItemId == character);
                observed.Add(current.ZoneId == FindZone(state, "retired").ZoneId ? bound.ZoneId == current.ZoneId ? "both_retired" : "facility_retired" : "declared");
                if (!engine.Advance()) break;
            }
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + (retire ? 12 : 0))); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 3));
            Assert.That(state.FacilityCharacterBindings.Single().Active, Is.EqualTo(!retire));
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, character, definition.TacticAbilityId), Is.EqualTo(!retire));
            if (retire) Assert.That(observed, Is.SupersetOf(new[] { "declared", "facility_retired", "both_retired" }));
            foreach (var pair in others) Assert.That(state.ItemZones.Items.Single(i => i.ItemId == pair.Key).ZoneId, Is.EqualTo(pair.Value));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "CityStyleDeclared"), Is.EqualTo(1));
        }
        [Test]
        public void EmergingBuildingsExcludesRainbowAndStillPaysNormalBuildCost()
        {
            var state = State(out var registry); var slots = Layout(state, 0);
            Position(state, Item(state, Settlement), "facility_supply_0"); Position(state, Item(state, OfficeIds[0]), "facility_supply_1");
            Run(state, registry, CityStyleDeclarationEffectExecutor.Create(1, StyleIds[0], slots));
            Assert.That(Open(state).CandidateIds, Does.Not.Contain(Item(state, Settlement))); Assert.That(Open(state).CandidateIds, Does.Contain(Item(state, OfficeIds[0])));
            Answer(state, registry, Item(state, OfficeIds[0])); Answer(state, registry, "gold");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(66));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("facility.build.placement"));
            Assert.That(Open(state).CandidateIds.Count, Is.GreaterThan(0));
        }
        [Test]
        public void ReturnedLevelTwoMarkerPreservesScoreHistoryAndRoundUseRecordThenAllowsRedeclaration()
        {
            var state = State(out var registry); Run(state, registry, CityStyleDeclarationEffectExecutor.Create(1, StyleIds[2], Layout(state, 2)));
            var original = state.FindPlayer(1).DeclaredCityStyles.Single(); string marker = original.InfluenceMarkerId;
            state.FindPlayer(1).UsedSpecialActionIdsThisRound.Add(original.UnlockedSpecialActionId);
            Run(state, registry, EffectSpec.Create(ContentLifecycleEffectExecutor.ReturnMarkerTypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1")), 1));
            Answer(state, registry, marker); state = Restore(state, registry);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(6)); Assert.That(state.FindPlayer(1).DeclaredCityStyles.Single().MarkerArea, Is.Empty);
            Assert.That(state.FindPlayer(1).UsedSpecialActionIdsThisRound, Does.Contain(original.UnlockedSpecialActionId));
            Run(state, registry, CityStyleDeclarationEffectExecutor.Create(1, StyleIds[2], Layout(state, 2)));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(12)); Assert.That(state.FindPlayer(1).DeclaredCityStyles.Count, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).DeclaredCityStyles[0].MarkerArea, Is.Empty); Assert.That(state.FindPlayer(1).DeclaredCityStyles[1].MarkerArea, Is.EqualTo("2"));
            Assert.That(state.FindPlayer(1).UsedSpecialActionIdsThisRound.Count, Is.EqualTo(1));
        }
        [TestCase(true)][TestCase(false)]
        public void EnterpriseOfficesUpgradesSameCompanyTwiceAndLocksCharactersForWholeActionRound(bool usedCharacter)
        {
            var state = State(out var registry, level: 1); Run(state, registry, CityStyleDeclarationEffectExecutor.Create(1, StyleIds[2], Layout(state, 2)));
            var declaration = state.FindPlayer(1).DeclaredCityStyles.Single(); state.FindPlayer(1).UsedCharacterThisTurn = usedCharacter;
            var node = Run(state, registry, CityStyleSpecialActionEffectSpecFactory.Activate(1, declaration.UnlockedSpecialActionId, declaration.InfluenceMarkerId));
            if (usedCharacter) { Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); return; }
            Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.True); state = Restore(state, registry); Answer(state, registry, Open(state).CandidateIds.First());
            Assert.That(new[] { Level(state, Business), Level(state, Logistics) }.OrderBy(x => x), Is.EqualTo(new[] { 1, 3 }));
            Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.True); Assert.That(state.FindPlayer(1).DeclaredCityStyles.Single().MarkerArea, Is.EqualTo("used_from_2"));
        }
        [Test]
        public void ImmediateScoreAwardMovesRealInstanceOnceAndDoesNotDuplicatePublicSupply()
        {
            var state = State(out var registry); Acquire(state, registry, AwardIds[1]);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(state.ItemZones.Items.Single(i => i.ItemId == Item(state, AwardIds[1])).ZoneId, Is.EqualTo(FindZone(state, "retired").ZoneId));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "reward.acquired"), Is.EqualTo(1));
            Assert.That(state.ItemZones.Items.Count(i => i.TypeId == "core.award_card"), Is.EqualTo(4));
        }
        [TestCase(false)][TestCase(true)]
        public void QuickAwardWorksBeforeOrAfterMainActionAndIgnoresCharacterUsage(bool afterMain)
        {
            var state = State(out var registry, level: 1); Acquire(state, registry, AwardIds[0]);
            var round = new RoundExecutionService(registry); var prepared = round.PrepareActionWindowForSmoke(state, 1);
            Assert.That(prepared.IsValid, Is.True, prepared.Reason); string windowId = state.EffectRuntime.ActiveMainNodeId;
            Assert.That(Open(state).InteractionTypeId, Does.StartWith(RoundExecutionService.MainlineCompletionInteractionTypeId));
            state.FindPlayer(1).ActedMainActionThisTurn = afterMain; state.FindPlayer(1).RemainingMainActionsThisTurn = afterMain ? 0 : 1;
            state.FindPlayer(1).UsedCharacterThisTurn = state.FindPlayer(1).CharacterCardLockedThisTurn = true;
            state = Restore(state, registry); var option = CharacterSettlementOptionQuery.GetOptions(state, 1, "").Single(); Assert.That(option.IsEnabled, Is.True);
            Assert.That(CharacterSettlementOptionQuery.GetOptions(state, 2, ""), Is.Empty);
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1).CharacterSettlementOptions.Any(o => o.OptionId == option.OptionId), Is.True);
            int gold = state.FindPlayer(1).Resources.GoldVoucher;
            var handler = new YC.Application.Interactions.InvokeSettlementOptionCommandHandler(registry, round);
            var command = YC.Application.Interactions.EffectInteractionCommands.InvokeSettlementOption(1, option.OptionId, "", option.StateRevision);
            var result = handler.Handle(state, command); Assert.That(result.Succeeded, Is.True, result.LogMessage + ";" + state.EffectRuntime.LastFaultMessage); state = Restore(state, registry); Answer(state, registry, Open(state).CandidateIds.First());
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 4)); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(afterMain ? 0 : 1));
            Assert.That(state.EffectRuntime.ActiveMainNodeId, Is.EqualTo(windowId));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ContentLifecycleEffectExecutor.AwardActivateTypeId).ParentEffectId, Is.EqualTo(state.EffectRuntime.MainNodes.Single(n => n.NodeId == windowId).ExecutionEffectId));
            Assert.That(handler.Handle(state, command).Succeeded, Is.True); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 4));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == Item(state, AwardIds[0])).ZoneId, Is.EqualTo(FindZone(state, "retired").ZoneId));
        }
        [Test]
        public void PersistentAwardWaitsForCleanupChoosesOneResourceAndSurvivesRecoveryAndRecycling()
        {
            var state = State(out var registry); Acquire(state, registry, AwardIds[3]); int gold = state.FindPlayer(1).Resources.GoldVoucher;
            Assert.That(Open(state), Is.Null); Cleanup(state, registry); state = Restore(state, registry); Answer(state, registry, "iron");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 3)); Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(21));
            Cleanup(state, registry); Assert.That(Open(state), Is.Null); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 3));
            state.Round++; Cleanup(state, registry); Answer(state, registry, "originium");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 6));
            Assert.That(ContentLifecycleEffectExecutor.IsHeld(state, state.ItemZones.Items.Single(i => i.ItemId == Item(state, AwardIds[3])), 1), Is.True);
        }
        [Test]
        public void RoadAwardRestoresBetweenRoutesKeepsRemovalOrderAndAllowsEmptyRoutes()
        {
            var state = State(out var registry); var map = StaticMapDefinitions.Resolve(state.MapId); string route = map.Routes.First(r => r.InfluenceSlotCount >= 2).RouteId;
            Run(state, registry, InfluenceEffectSpecFactory.PlaceInfluence(2, InfluenceService.GetRouteSlotId(route, 0)));
            Run(state, registry, InfluenceEffectSpecFactory.PlaceInfluence(1, InfluenceService.GetRouteSlotId(route, 1)));
            int gold = state.FindPlayer(1).Resources.GoldVoucher; Acquire(state, registry, AwardIds[2]);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("effect.route.choose")); Answer(state, registry, route);
            Assert.That(state.Map.RoadRouteIds, Does.Contain(route)); Assert.That(Open(state).CandidateIds, Does.Not.Contain(route));
            var events = state.EffectRuntime.RuleEvents.Where(e => e.EventType == "InfluenceRemoved").ToList(); Assert.That(events.Count, Is.EqualTo(2));
            var departures = state.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence).ToList();
            Assert.That(Value(departures[0].NormalizedResult, "originalSubjectKeys").Items[0].StringValue, Is.EqualTo("player:1"));
            Assert.That(Value(departures[1].NormalizedResult, "originalSubjectKeys").Items[0].StringValue, Is.EqualTo("player:2"));
            string firstRoad = state.ItemZones.Items.Single(i => i.TypeId == "core.road_token").ItemId;
            state = Restore(state, registry); Assert.That(state.ItemZones.Items.Any(i => i.ItemId == firstRoad && i.TypeId == "core.road_token"), Is.True);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("effect.route.choose"));
            string empty = Open(state).CandidateIds.First(r => !state.Map.Influences.Any(i => i.RouteId == r)); Answer(state, registry, empty);
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(state.Map.RoadRouteIds.Count, Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 20));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "InfluenceRemoved"), Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Score, Is.Zero);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == Item(state, AwardIds[2])).ZoneId, Is.EqualTo(FindZone(state, "retired").ZoneId));
            var roads = state.ItemZones.Items.Where(i => i.TypeId == "core.road_token").ToArray(); Assert.That(roads.Length, Is.EqualTo(2));
            Assert.That(roads.Select(i => i.ItemId).Distinct().Count(), Is.EqualTo(2)); Assert.That(roads.Any(i => i.ItemId == firstRoad), Is.True);
            foreach (var road in roads)
            {
                var zone = state.ItemZones.Zones.Single(z => z.ZoneId == road.ZoneId); Assert.That(zone.TemplateId, Is.EqualTo("core.zone.route_road"));
                Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == zone.ZoneId), Is.EqualTo(1)); Assert.That(new ItemZoneQuery().IsInfluence(state, road.ItemId), Is.False);
                Assert.That(state.Map.RoadRouteIds, Does.Contain(state.ItemZones.Zones.Single(z => z.ZoneId == zone.ParentId).LocalZoneId));
            }
        }
    }
}
