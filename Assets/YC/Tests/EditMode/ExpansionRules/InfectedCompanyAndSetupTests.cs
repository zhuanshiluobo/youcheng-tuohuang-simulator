using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Facilities;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class InfectedCompanyAndSetupTests
    {
        private const string Rhodes = "infected.enterprise.rhodes_island", Company = "infected.enterprise.stonehoove_security", Foundation = "infected.enterprise.maylander_foundation";
        private static readonly string Content = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
        private ExternalContentPack pack;
        private EffectRegistry registry;
        private GameMapDefinition map;
        private ActiveContentCatalog catalog;
        private readonly Dictionary<int, ActiveContentCatalog> catalogs = new Dictionary<int, ActiveContentCatalog>();
        private string fixtureRoot;

        [OneTimeSetUp]
        public void LoadActualCompaniesAndRecipeWithExplicitMissingContentFixtures()
        {
            fixtureRoot = Path.Combine(Directory.GetCurrentDirectory(), "Logs/ExpansionValidation/InfectedCompanyAndSetup/content", Guid.NewGuid().ToString("N"));
            foreach (string file in Directory.GetFiles(Content, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".meta")))
                Copy(file, Path.Combine(fixtureRoot, file.Substring(Content.Length + 1)));
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "pack.json")));
            var paths = (JArray)manifest["definitions"];
            var inventoryDefinitions = new HashSet<string>(StringComparer.Ordinal);
            if (manifest["facilityInventory"] != null)
            {
                var inventory = JObject.Parse(File.ReadAllText(Path.Combine(fixtureRoot, (string)manifest["facilityInventory"])));
                foreach (string path in (inventory["definitionFiles"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>()) inventoryDefinitions.Add(path);
            }
            foreach (string category in new[] { "enterprise", "department", "component", "contract", "facility", "city_style", "event", "character" })
            foreach (string file in Directory.GetFiles(Path.Combine(Content, category), "*.json"))
            {
                var json = JObject.Parse(File.ReadAllText(file)); string id = (string)json["definitionId"];
                // 此夹具独立验证企业/公司合同；后继真实设施、样式和角色由各自及集成夹具加载。
                if ((string)json["expansionId"] == "enterprise" || new[] { Rhodes, Company, Foundation, "infected.component.company_marker", "infected.contract.lifetime_security" }.Contains(id))
                {
                    string path = category + "/" + Path.GetFileName(file);
                    if (!paths.Values<string>().Contains(path) && !inventoryDefinitions.Contains(path)) paths.Add(path);
                }
            }
            string originals = Path.Combine(Directory.GetCurrentDirectory(), "游城拓荒/企业扩");
            foreach (var pair in new[] { ("企业合作板 2行4列.jpg", "sheets/enterprise_boards.jpg"), ("罗德岛干员牌.jpg", "sheets/operator_faces.jpg"),
                ("工程科.png", "departments/engineering.png"), ("防卫科.png", "departments/defense.png"), ("结构科.png", "departments/structural.png"), ("公路.jpg", "enterprise_road.jpg") })
                Copy(Path.Combine(originals, pair.Item1), Path.Combine(fixtureRoot, "artwork", pair.Item2));
            string infected = Path.Combine(Directory.GetCurrentDirectory(), "游城拓荒/感染者扩");
            foreach (var pair in new[] {
                ("2410077427276854351", "enterprises/infected_stonehoove_security.jpg"), ("2410077427276855138", "enterprises/infected_maylander_foundation.jpg"),
                ("2411202693612143350", "enterprises/infected_rhodes_island.jpg"), ("2411202693612147496", "components/infected_company_marker.png"),
                ("2411202693633912505", "components/infected_lifetime_security.png") })
                Copy(Directory.GetFiles(infected).Single(f => Path.GetFileName(f).Contains(pair.Item1)), Path.Combine(fixtureRoot, "artwork", pair.Item2));

            // 后继尚未交付的内容只在隔离测试包内生成。每个所测能力真实返回并执行Effect，不写生产占位。
            var recipe = JObject.Parse(File.ReadAllText(Path.Combine(Content, "recipes/infected.json")));
            foreach (var entry in recipe["entries"])
            {
                string id = (string)entry["definitionId"], role = (string)entry["initialZoneRole"];
                string category = id.Split('.')[1]; string path = category + "/" + id + ".json";
                if (paths.Values<string>().Contains(path)) continue;
                JObject definition;
                if (category == "character")
                {
                    definition = JObject.Parse(File.ReadAllText(Path.Combine(Content, "character/liskarm.json")));
                    definition["itemTypeId"] = role == "hand" ? "core.basic_character_card" : role == "flowing_shapes" ? "core.generated_character_card" : "core.operator_card";
                    definition["data"]["acquisitionRole"] = role == "hand" ? "initial" : role == "flowing_shapes" ? "generated" : "operator_deck";
                    definition["data"]["instanceTypeId"] = definition["itemTypeId"].DeepClone();
                    definition["abilities"] = new JArray(new[] { "strategy", "tactic" }.Select(mode => FixtureAbility(id, mode,
                        role == "hand" && mode == "strategy" ? "return { Flow.DispatchOperator({ apiVersion=1, player=ctx.playerId, drawCount=3, excludedReturnItemIds={ctx.payload.cardInstanceRef} }) }" : "return { Effect.GainScore({recipient=ctx.playerId, amount=1}) }")));
                }
                else if (category == "award")
                {
                    definition = new JObject { ["itemTypeId"] = "core.award_card", ["data"] = new JObject { ["instanceTypeId"] = "core.award_card", ["acquisitionRole"] = "reward_pool", ["lifecycle"] = "immediate_retired" },
                        ["abilities"] = new JArray(FixtureAbility(id, "activate", "return { Effect.GainScore({recipient=ctx.playerId, amount=1}) }")) };
                }
                else if (category == "facility")
                {
                    definition = JObject.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(Content, "facility"), "facility_enterprise_office_red.json").Single()));
                    definition["abilities"] = new JArray();
                }
                else
                {
                    string source = id.EndsWith("enterprise_offices") ? "city_style_security_district" : "city_style_city_construction_frontier";
                    definition = JObject.Parse(File.ReadAllText(Path.Combine(Content, "city_style/" + source + ".json")));
                }
                definition["definitionId"] = id; definition["contentType"] = category; definition["version"] = "1.0.0";
                definition["displayName"] = "接口测试定义：" + id; definition["expansionId"] = "infected"; definition["replaces"] = null;
                definition["artwork"] = "artwork/sheets/character_faces.jpg"; definition["artworkSpriteId"] = ""; definition["playerMarkerZones"] = new JArray(); definition["cardZones"] = new JArray();
                if (category == "city_style") definition["abilities"] = new JArray();
                string destination = Path.Combine(fixtureRoot, path); Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.WriteAllText(destination, definition.ToString()); paths.Add(path);
            }
            manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected");
            manifest["setupRecipes"] = new JArray("recipes/standard.json", "recipes/enterprise.json", "recipes/infected.json");
            manifest["expansionDependencies"] = new JArray(
                new JObject { ["expansionId"] = "enterprise", ["requires"] = new JArray("core"), ["requiredCapabilities"] = new JArray("enterprise_rules.v1") },
                new JObject { ["expansionId"] = "infected", ["requires"] = new JArray("enterprise"), ["requiredCapabilities"] = new JArray("infected_rules.v1") });
            File.WriteAllText(Path.Combine(fixtureRoot, "pack.json"), manifest.ToString());
            pack = ExternalContentPack.Load(fixtureRoot);
        }
        private JObject FixtureAbility(string id, string mode, string body)
        {
            string path = "lua/test-fixtures/" + id + "." + mode + ".lua";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixtureRoot, path)));
            File.WriteAllText(Path.Combine(fixtureRoot, path), "return function(ctx) " + body + " end");
            return new JObject { ["abilityId"] = id + "." + mode, ["mode"] = mode, ["skillType"] = "normal", ["subscriptionId"] = "fixture." + id + "." + mode,
                ["script"] = path, ["version"] = "1.0.0", ["completionHandlerId"] = "", ["cleanupScript"] = "", ["hasSpecialZone"] = false, ["specialZoneId"] = "" };
        }
        private static void Copy(string source, string destination) { Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(source, destination, true); }
        private GameState State(int players = 3, bool prepareFixtureBoards = true)
        {
            map = StaticMapDefinitions.CreateFourPlayerMap();
            var config = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = players, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
            if (!catalogs.TryGetValue(players, out catalog)) catalogs[players] = catalog = ActiveContentCatalogBuilder.Build(pack, config); config.ContentPackHash = catalog.ContentPackHash;
            registry = new EffectRegistry(); catalog.Register(registry); ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, new InfluenceService(new MapQueryService(map))); CharacterCoverEffectExecutor.Register(registry);
            var seats = Enumerable.Range(1, players).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "玩家" + id }).ToList();
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, seats, config, 452, catalog.SetupRecipe, catalog.ContentDefinitions);
            state.Phase = GamePhase.ActionRound1; state.Round = 1; state.ActionRound = 1; state.CurrentPlayerId = state.StartPlayerId = 1;
            state.EffectRuntime.PlayerOrderSnapshot = seats.Select(s => s.PlayerId).ToList();
            state.Map.OpenLocationIds = map.Locations.Where(l => !l.IsRedZone).Select(l => l.LocationId).ToList();
            foreach (var player in state.Players) player.Resources = new ResourceSet { GoldVoucher = 80, Originium = 20, OriginiumShard = 20, Iron = 20 };
            if (prepareFixtureBoards)
            {
                foreach (string definition in new[] { Rhodes, Company, Foundation, "enterprise.fussen_bank" })
                {
                    var board = Item(state, definition); Position(state, board, FindZone(state, "enterprises").ZoneId);
                    foreach (int player in seats.Select(s => s.PlayerId))
                    {
                        var declaration = new CardZoneDefinition { LocalZoneId = "cooperation_" + player, ZoneTemplateId = "core.zone.board_marker_track", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
                            AcceptedTypeIds = new List<string> { "core.player_marker" }, CapacityPolicy = "bounded", Capacity = 1, OrderingKind = ZoneOrderingKind.Track,
                            VisibilityPolicyId = ItemZoneVisibility.Public, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
                        Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, board.ItemId, declaration, out string zone, out string reason), Is.True, reason);
                        Position(state, state.ItemZones.Items.First(i => i.ZoneId == FindZone(state, "supply", player).ZoneId), zone, "level_0");
                    }
                }
                foreach (var entry in state.SetupRecipe.Entries.Where(e => e.Condition.Kind == "enterprise_selected" && new[] { Company, Foundation }.Contains(e.Condition.RuntimeId)))
                    foreach (var item in state.ItemZones.Items.Where(i => i.DefinitionId == entry.DefinitionId))
                    { Position(state, item, state.ItemZones.Zones.Single(z => z.LocalZoneId == entry.InitialZoneRole && z.OwnerPlayerId == -1).ZoneId); if (item.PhysicalKind == ItemPhysicalKind.Cube) item.OriginalSupplyZoneId = item.ZoneId; }
            }
            Valid(state); return state;
        }
        private static ItemInstanceState Item(GameState state, string definition, int player = -1) => state.ItemZones.Items.First(i => i.DefinitionId == definition && (player < 0 || i.ControllerSubject.PlayerId == player));
        private static void Position(GameState state, ItemInstanceState item, string zone, string slot = "")
        {
            foreach (var order in state.ItemZones.Orders) if (order.ItemIds.Remove(item.ItemId)) order.OrderRevision++;
            item.ZoneId = zone; item.SlotId = slot;
            var destination = state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == zone); if (destination != null) { destination.ItemIds.Add(item.ItemId); destination.OrderRevision++; }
        }
        private static void Valid(GameState state) => Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
        private static InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open");
        private EffectNodeRuntimeState Run(GameState state, EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state); return engine.GetNode(id);
        }
        private void Answer(GameState state, params string[] ids)
        {
            var request = Open(state); Assert.That(request, Is.Not.Null);
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision, ids.Length == 1 ? NormalizedValue.CreateString(ids[0]) : Strings(ids), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state);
        }
        private void Drain(GameState state)
        {
            for (int index = 0; index < 100 && Open(state) != null; index++)
            { var request = Open(state); Answer(state, request.CandidateIds.Contains("choice.skip") ? new[] { "choice.skip" } : request.CandidateIds.Take(Math.Max(1, request.MinSelections)).ToArray()); }
            Assert.That(Open(state), Is.Null);
        }
        private GameState Restore(GameState state)
        {
            var result = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic); Valid(result.State); return result.State;
        }
        private static List<string> In(GameState state, string role, int player = -1) => new ItemZoneQuery().GetItemIds(state, state.ItemZones.Zones.Single(z => z.LocalZoneId == role && z.OwnerPlayerId == player).ZoneId).ToList();
        private static void SetLevel(GameState state, string definition, int player, int level)
        { var board = Item(state, definition); var zone = state.ItemZones.Zones.Single(z => z.ParentId == board.ItemId && z.LocalZoneId == "cooperation_" + player); state.ItemZones.Items.Single(i => i.ZoneId == zone.ZoneId).SlotId = "level_" + level; }
        private EffectNodeRuntimeState Activate(GameState state, string definition, int level, int player = 1)
        {
            SetLevel(state, definition, player, level); var board = Item(state, definition);
            return Run(state, EffectSpec.Create(LuaDomainEffectTypeIds.ActivateEnterpriseSpecial, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(player.ToString()),
                "unlockedByPlayer", NormalizedValue.CreateString(player.ToString()), "enterpriseItemIds", Strings(new[] { board.ItemId }), "specialId", NormalizedValue.CreateString(definition + ".level_" + level + ".special")), player));
        }
        private void Reward(GameState state, string definition, int level, int player = 1)
        { SetLevel(state, definition, player, level - 1); Run(state, EnterpriseEffectExecutor.UpgradeSpec(player, Item(state, definition).ItemId, 1)); }
        private void AddResourcePoints(GameState state)
        {
            registry.Register(new EffectRegistration("fixture.resource_points", context =>
            {
            foreach (var location in map.Locations.Where(l => !l.IsRedZone))
            {
                var parent = context.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == location.LocationId);
                var zone = context.State.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.resource_point_markers");
                if (context.State.ItemZones.Items.Any(i => i.ZoneId == zone.ZoneId)) continue;
                Assert.That(new ItemZoneStateFactory().TryCreateGrantedItem(context.State, context.Node.EffectId, location.LocationId, "core.resource_point", "core.resource_point_token", zone.ZoneId, new ItemSubjectRef(), out string id, out string reason), Is.True, reason);
                context.State.ItemZones.Items.Single(i => i.ItemId == id).LocalState = Object("resourceType", NormalizedValue.CreateInteger((int)ResourceType.Originium), "amount", NormalizedValue.CreateInteger(2));
            }
                return EffectStepResult.Completed();
            }));
            Run(state, EffectSpec.Create("fixture.resource_points", playerId: 1));
        }
        private void DockCity(GameState state, int player)
        {
            var location = map.Locations.First(l => !l.IsRedZone && l.CanDockCity && !state.Players.Any(p => p.CityLocationId == l.LocationId));
            var parent = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == location.LocationId);
            Position(state, state.ItemZones.Items.Single(i => i.TypeId == "core.mobile_city" && i.OwnerSubject.PlayerId == player), state.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.city_dock").ZoneId);
        }
        private void PlaceCompany(GameState state, string slot = null, int player = 1)
        { Run(state, InfluenceEffectSpecFactory.PlaceInfluence(player, slot ?? new CompanyInfluenceRules().GetPlacementSlotIds(state)[0], ownerSubject: new RuleSubjectReference { SubjectType = "company", DefinitionId = CompanyInfluenceRules.CompanyId, InstanceId = CompanyInfluenceRules.CompanyId })); }
        private void Acquire(GameState state, string role, int player = 1) => Run(state, EffectSpec.Create(RewardAcquisitionFlow.TypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(player.ToString()), "poolRole", NormalizedValue.CreateString(role)), player));

        private static IEnumerable<TestCaseData> EnterpriseLevels()
        {
            foreach (string definition in new[] { Rhodes, Company, Foundation })
            foreach (var level in JObject.Parse(File.ReadAllText(Path.Combine(Content, "enterprise/" + definition + ".json")))["data"]["levels"].Where(l => (int)l["level"] > 0))
                yield return new TestCaseData(definition, (int)level["level"], (string)level["kind"]);
        }
        [TestCaseSource(nameof(EnterpriseLevels))]
        public void EveryNonemptyLevelExecutesThroughRegisteredEnterpriseDefinition(string definition, int level, string kind)
        {
            var state = State(); AddResourcePoints(state); DockCity(state, 1);
            if (definition == Rhodes && level == 3) Position(state, Item(state, "texas", 1), FindZone(state, "discard", 1).ZoneId);
            if (kind == "special") Activate(state, definition, level); else Reward(state, definition, level);
            Drain(state);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.Status == EffectNodeStatus.Faulted || n.EffectTypeId == ContentPrimitiveEffectExecutor.IncompleteEffect), Is.False);
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == (kind == "special" ? "EnterpriseSpecialEffectActivated" : EnterpriseEffectExecutor.LevelChangedEvent)), Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.First(n => n.ParentEffectId.Length == 0).Status, Is.EqualTo(EffectNodeStatus.Completed));
        }

        [Test]
        public void ContractNormalCollectionDeduplicatesAndRecoveryCannotCollectTwice()
        {
            var state = State(); AddResourcePoints(state); DockCity(state, 1); Acquire(state, "contracts");
            string slot = new CompanyInfluenceRules().GetPlacementSlotIds(state)[0]; PlaceCompany(state, slot);
            string companyLocation = state.Map.Influences.Single().LocationId;
            var query = new InfluenceSubjectQuery(); Assert.That(query.GetCollectionLocationIds(state, 1, "normal", CompanyInfluenceRules.HasContract(state, 1)), Does.Contain(companyLocation));
            Assert.That(query.GetCollectionLocationIds(state, 1, "extra", true), Does.Not.Contain(companyLocation));
            var collection = new ResourceCollectionService(new MapQueryService(map)); state.Phase = GamePhase.ResourceCollection;
            Assert.That(collection.Collect(state, 1, new[] { companyLocation, companyLocation }, null).Succeeded, Is.False);
            Run(state, InfluenceEffectSpecFactory.PlaceInfluence(1, InfluenceService.GetLocationSlotId(companyLocation, 1)));
            Assert.That(query.GetCollectionLocationIds(state, 1, "normal", true).Count(id => id == companyLocation), Is.EqualTo(1));
            var result = collection.Collect(state, 1, new[] { companyLocation }, null);
            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(22)); Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(80));
            int paid = state.FindPlayer(1).Resources.GoldVoucher; Assert.That(paid, Is.LessThan(80));
            state = Restore(state); Assert.That(collection.Collect(state, 1, new[] { companyLocation }, null).Succeeded, Is.False);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(22)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(paid));
            state.Phase = GamePhase.ActionRound2;
            Run(state, InfluenceEffectSpecFactory.RemoveInfluence(1, state.ItemZones.Items.Single(i => i.TypeId == "core.player_marker" && i.SlotId == InfluenceService.GetLocationSlotId(companyLocation, 1)).ItemId));
            Run(state, EffectSpec.Create(ResourceCollectionEffectExecutor.TypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "scope", NormalizedValue.CreateString("extra")), 1));
            Assert.That(Open(state).CandidateIds, Does.Not.Contain(companyLocation));
            string city = state.FindPlayer(1).CityLocationId; Assert.That(Open(state).CandidateIds, Does.Contain(city)); state = Restore(state); Answer(state, city);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(24)); int nodes = state.EffectRuntime.EffectNodes.Count;
            state = Restore(state); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(24)); Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(nodes));
        }

        [Test]
        public void CompanyTollRecipientIsBankAndNeverAContractPlayer()
        {
            var state = State(); AddResourcePoints(state); Acquire(state, "contracts", 1); Acquire(state, "contracts", 2); PlaceCompany(state);
            // 只验证既有路费接口的主体归约；生产放置候选另测，不能由此允许公司放在航道。
            var company = state.ItemZones.Items.Single(i => i.TypeId == "infected.company_marker" && i.SlotId.Length > 0);
            var route = map.Routes.First(r => r.InfluenceSlotCount > 0); string slot = InfluenceService.GetRouteSlotId(route.RouteId, 0);
            Position(state, company, state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.influence_slots" && z.LegalSlotIds.Contains(slot)).ZoneId, slot);
            var toll = new RouteTollService(new MapQueryService(map)); Assert.That(toll.GetOpponentInfluenceOwnersOnPaymentKey(state, route.RouteId, 3, RouteTollPaymentKeyMode.RouteId), Is.EqualTo(new[] { -1 }));
            Assert.That(toll.TryBuildPaymentPlan(state, 3, new[] { route.RouteId, route.RouteId }, new Dictionary<string, int> { [route.RouteId] = -1 }, RouteTollPaymentKeyMode.RouteId, out var payments).IsValid, Is.True);
            Assert.That(payments.Count, Is.EqualTo(1)); Assert.That(payments[0].PaidToSupply, Is.True); toll.ApplyPayments(state, state.FindPlayer(3), payments);
            Assert.That(state.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(78)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(80)); Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(80));
        }

        [Test]
        public void ActualWAndSilverashDefinitionsRespectCompanySubjectFilter()
        {
            var state = State(); AddResourcePoints(state); PlaceCompany(state); string company = state.ItemZones.Items.Single(i => i.TypeId == "infected.company_marker" && i.SlotId.Length > 0).ItemId;
            UseOperator(state, "operator.w", "tactic"); Assert.That(Open(state).CandidateIds, Does.Contain(company)); Answer(state, company); Drain(state);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == company).ZoneId, Is.EqualTo(FindZone(state, "company_markers").ZoneId));
            state = State(); AddResourcePoints(state); PlaceCompany(state); company = state.ItemZones.Items.Single(i => i.TypeId == "infected.company_marker" && i.SlotId.Length > 0).ItemId;
            string playerSlot = new CompanyInfluenceRules().GetPlacementSlotIds(state)[0]; Run(state, InfluenceEffectSpecFactory.PlaceInfluence(2, playerSlot));
            UseOperator(state, "operator.silverash", "tactic"); Assert.That(Open(state).CandidateIds, Does.Not.Contain(company));
            Answer(state, Open(state).CandidateIds[0]); Drain(state); Assert.That(new ItemZoneQuery().IsInfluence(state, company), Is.True);
        }
        private void UseOperator(GameState state, string definition, string mode)
        {
            var card = Item(state, definition); Run(state, MoveSpec(1, new[] { card.ItemId }, card.ZoneId, FindZone(state, "hand", 1).ZoneId, "unordered", 1, "core.character_card_transfer"));
            Run(state, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(state, card.ItemId); Run(state, CharacterLifecycleService.CreateUseSpec(1, card.ItemId, mode));
        }

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void RecipeKeepsSingleRhodesAndCreatesExactPhysicalCounts(int players)
        {
            var state = State(players);
            Assert.That(catalog.Definitions.Count(d => d.ContentType == "enterprise"), Is.EqualTo(10));
            Assert.That(catalog.Definitions.Single(d => d.RuntimeId == "enterprise.rhodes_island").DefinitionId, Is.EqualTo(Rhodes));
            Assert.That(In(state, "enterprises").Count, Is.EqualTo(4)); Assert.That(In(state, "operator_deck").Count, Is.EqualTo(16)); Assert.That(In(state, "flowing_shapes").Count, Is.EqualTo(3));
            Assert.That(In(state, "company_markers").Count, Is.EqualTo(8)); Assert.That(In(state, "contracts").Count, Is.EqualTo(2)); Assert.That(In(state, "awards").Count, Is.EqualTo(4));
            Assert.That(catalog.SetupRecipe.Entries.Where(e => e.InitialZoneRole == "facility_deck" && e.DefinitionId.StartsWith("infected.")).Sum(e => e.Quantity), Is.EqualTo(6));
            Assert.That(catalog.ContentDefinitions.CityStyles.Count(s => s.Level == 1), Is.EqualTo(10)); Assert.That(catalog.ContentDefinitions.CityStyles.Count(s => s.Level == 2), Is.EqualTo(5));
            Assert.That(In(state, "city_styles").Count, Is.EqualTo(6)); Assert.That(state.SetupRecipe.OperatorDrawCount, Is.EqualTo(3));
            Assert.That(In(state, "hand", 1).Count, Is.EqualTo(8)); Assert.That(state.ItemZones.Items.Any(i => i.DefinitionId.Contains("entrepreneur")), Is.False);
        }
        [TestCase(true)] [TestCase(false)]
        public void RetentionKeepsFiveAndSecretlyRetiresThreeAcrossRecovery(bool keepAmiya)
        {
            var state = State(); string amiya = Item(state, "infected.character.amiya", 1).ItemId;
            Run(state, ExpansionSetupFlow.RetentionSpec(1)); var request = Open(state); Assert.That(request.MinSelections, Is.EqualTo(5));
            var keep = request.CandidateIds.Where(id => id != amiya).Take(keepAmiya ? 4 : 5).ToList(); if (keepAmiya) keep.Add(amiya);
            state = Restore(state); Answer(state, keep.ToArray()); state = Restore(state);
            Assert.That(In(state, "hand", 1), Is.EquivalentTo(keep)); var retired = In(state, "secret_retired"); Assert.That(retired.Count, Is.EqualTo(3));
            foreach (var player in state.Players) Assert.That(new ItemZoneQuery().Project(state, GameStateViewer.Player(player.PlayerId)).Items.Any(i => retired.Contains(i.ItemId)), Is.False);
            Assert.That(CharacterLifecycleService.GetRetainedBasicCharacterCount(state, 1), Is.EqualTo(5));
            foreach (string role in new[] { "operator_deck", "flowing_shapes" })
            { var card = state.ItemZones.Items.First(i => i.ZoneId == FindZone(state, role).ZoneId); Run(state, MoveSpec(1, new[] { card.ItemId }, card.ZoneId, FindZone(state, "hand", 1).ZoneId, "unordered", 1, "core.character_card_transfer")); }
            Assert.That(In(state, "hand", 1).Count, Is.EqualTo(7)); Assert.That(CharacterLifecycleService.GetRetainedBasicCharacterCount(state, 1), Is.EqualTo(5));
        }
        [Test]
        public void RealPreparationWithoutRhodesStillLetsFixtureAmiyaDispatchFromSameSixteen()
        {
            var state = State(4, false); Run(state, ExpansionSetupFlow.PreparationSpec(1));
            while (Open(state) != null)
            {
                var request = Open(state); var id = request.CandidateIds.FirstOrDefault(c => !state.ItemZones.Items.Any(i => i.ItemId == c && i.DefinitionId == Rhodes)) ?? request.CandidateIds[0]; Answer(state, id);
            }
            Assert.That(In(state, "enterprises").Select(id => state.ItemZones.Items.Single(i => i.ItemId == id).DefinitionId), Does.Not.Contain(Rhodes));
            Assert.That(In(state, "operator_deck").Count, Is.EqualTo(16));
            string amiya = Item(state, "infected.character.amiya", 1).ItemId;
            Run(state, ExpansionSetupFlow.RetentionSpec(1)); Answer(state, Open(state).CandidateIds.Where(id => id != amiya).Take(4).Concat(new[] { amiya }).ToArray());
            Run(state, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(state, amiya);
            Run(state, CharacterLifecycleService.CreateUseSpec(1, amiya, "strategy"));
            Assert.That(state.ItemZones.Items.Count(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.LocalZoneId == "dispatch_candidates")), Is.EqualTo(3));
            state = Restore(state); Answer(state, Open(state).CandidateIds.First(id => id != "decline"));
            Assert.That(Open(state).CandidateIds, Does.Not.Contain(amiya)); Answer(state, Open(state).CandidateIds[0]);
            Assert.That(In(state, "operator_deck").Count, Is.EqualTo(15)); Assert.That(state.ItemZones.Zones.Count(z => z.LocalZoneId == "operator_deck"), Is.EqualTo(1));
        }
        [Test]
        public void CompanyPlacementRejectsRoutesUnexploredOccupiedCityClosedAndLockedPoints()
        {
            var state = State(); Assert.That(new CompanyInfluenceRules().GetPlacementSlotIds(state), Is.Empty); AddResourcePoints(state); DockCity(state, 1);
            var rules = new CompanyInfluenceRules(); var candidates = rules.GetPlacementSlotIds(state); Assert.That(candidates, Is.Not.Empty);
            Assert.That(candidates.Any(id => id.Contains("route")), Is.False);
            string cityLocation = state.FindPlayer(1).CityLocationId;
            Assert.That(candidates, Does.Not.Contain(InfluenceService.GetLocationSlotId(cityLocation, 0)));
            string closedLocation = map.Locations.First(l => !l.IsRedZone && l.LocationId != cityLocation).LocationId;
            state.Map.OpenLocationIds.Remove(closedLocation);
            Assert.That(rules.GetPlacementSlotIds(state), Does.Not.Contain(InfluenceService.GetLocationSlotId(closedLocation, 0)));
            state.Map.OpenLocationIds.Add(closedLocation);
            string slot = candidates[0]; PlaceCompany(state, slot); Assert.That(rules.GetPlacementSlotIds(state), Does.Not.Contain(slot));
            var item = state.ItemZones.Items.Single(i => i.TypeId == "infected.company_marker" && i.SlotId == slot);
            Assert.That(item.OwnerSubject.Kind, Is.EqualTo(ItemSubjectKind.Company)); Assert.That(item.OwnerSubject.PlayerId, Is.EqualTo(-1));
            Assert.That(new InfluenceSubjectQuery().GetCandidateItemIds(state, 1, "opponent"), Does.Contain(item.ItemId));
            Assert.That(new InfluenceSubjectQuery().GetCandidateItemIds(state, 1, "player"), Does.Not.Contain(item.ItemId));
            string locked = rules.GetPlacementSlotIds(state)[0]; registry.InfluenceService.PlacementAllowed = (s, target) => target != locked;
            Assert.That(new CompanyInfluenceRules(new MapQueryService(map), registry.InfluenceService.PlacementAllowed).GetPlacementSlotIds(state), Does.Not.Contain(locked));
            var node = Run(state, InfluenceEffectSpecFactory.MoveInfluence(1, item.ItemId, locked)); Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed));
            var route = map.Routes.First(r => r.InfluenceSlotCount > 0); Assert.That(registry.InfluenceService.ValidateItemPlacement(state, item.OwnerSubject, InfluenceService.GetRouteSlotId(route.RouteId, 0)).Succeeded, Is.False);
            Assert.That(registry.InfluenceService.GetLegalMovementSlotIds(state, item.ItemId), Does.Not.Contain(locked));
            Run(state, EffectSpec.Create(ContentPrimitiveEffectExecutor.SelectInfluence, Object("player", NormalizedValue.CreateString("p1"), "operation", NormalizedValue.CreateString("move"), "subjectFilter", NormalizedValue.CreateString("opponent")), 1));
            Assert.That(Open(state).CandidateIds.Any(id => id.EndsWith("|" + locked)), Is.False);
            Answer(state, Open(state).CandidateIds[0]);
        }
        [TestCase(3, 1)] [TestCase(5, 2)]
        public void BothContractHoldersReceiveSameFlooredRegionContribution(int count, int expected)
        {
            var state = State(); AddResourcePoints(state); Acquire(state, "contracts", 1); Acquire(state, "contracts", 2);
            string region = "A";
            foreach (var location in map.Locations.Where(l => l.RegionId == region && !l.IsRedZone && l.InfluenceSlotCount > 0).Take(count)) PlaceCompany(state, InfluenceService.GetLocationSlotId(location.LocationId, 0));
            // 当前地图每区资源点最多4处。5枚用归约专用位置夹具填入同区其它合法槽，只验计数，不放宽公司放置规则。
            int placed = Math.Min(count, map.Locations.Count(l => l.RegionId == region && !l.IsRedZone));
            foreach (var location in map.Locations.Where(l => l.RegionId == region).Take(count - placed))
            {
                string slot = InfluenceService.GetLocationSlotId(location.LocationId, 1);
                Position(state, state.ItemZones.Items.First(i => In(state, "company_markers").Contains(i.ItemId)), state.ItemZones.Zones.Single(z => z.LegalSlotIds.Contains(slot)).ZoneId, slot);
            }
            var rules = new CompanyInfluenceRules(); Assert.That(rules.GetControlContribution(state, region, 1), Is.EqualTo(expected)); Assert.That(rules.GetControlContribution(state, region, 2), Is.EqualTo(expected)); Assert.That(rules.GetControlContribution(state, region, 3), Is.Zero);
            state.Phase = GamePhase.FinalScoring;
            registry.Register(new EffectRegistration("fixture.final_scoring", context => { Assert.That(new FinalScoringService(new MapQueryService(map)).Resolve(context.State).Succeeded, Is.True); return EffectStepResult.Completed(); }));
            Run(state, EffectSpec.Create("fixture.final_scoring", playerId: 1));
            var scored = state.FinalScoring.RegionScores.Single(r => r.RegionId == region);
            Assert.That(scored.InfluenceCounts.Single(i => i.PlayerId == 1).Count, Is.EqualTo(expected)); Assert.That(scored.InfluenceCounts.Single(i => i.PlayerId == 2).Count, Is.EqualTo(expected));
            state = Restore(state); Assert.That(new FinalScoringService(new MapQueryService(map)).Resolve(state).Scoring.RegionScores.Single(r => r.RegionId == region).InfluenceCounts.Count, Is.EqualTo(scored.InfluenceCounts.Count));
            Assert.That(state.ItemZones.Items.Where(i => i.TypeId == "infected.company_marker").All(i => i.OwnerSubject.Kind == ItemSubjectKind.Company && i.OwnerSubject.PlayerId == -1), Is.True);
        }
        [Test]
        public void StonehooveMissingRemovalStillPlacesAndPaidRemovalUpgradesOnlyWhenPresent()
        {
            var state = State(); AddResourcePoints(state); Activate(state, Company, 1); Assert.That(Open(state).CandidateIds, Does.Contain("place"));
            state = Restore(state); Answer(state, "place"); Answer(state, Open(state).CandidateIds[0]); Assert.That(In(state, "company_markers").Count, Is.EqualTo(7));
            Activate(state, Company, 2); Drain(state); Assert.That(new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetLevel(state, 1, Item(state, Company).ItemId), Is.EqualTo(2));
            string slot = new CompanyInfluenceRules().GetPlacementSlotIds(state)[0]; Run(state, InfluenceEffectSpecFactory.PlaceInfluence(1, slot));
            Activate(state, Company, 2); Drain(state); Assert.That(new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetLevel(state, 1, Item(state, Company).ItemId), Is.EqualTo(3)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
        }
        [Test]
        public void StonehooveReplacementAndFollowingCompanyMoveBelongToActivator()
        {
            var state = State(); AddResourcePoints(state); PlaceCompany(state); var companyItem = state.ItemZones.Items.Single(i => i.TypeId == "infected.company_marker" && i.SlotId.Length > 0);
            string original = companyItem.SlotId; string playerSlot = new CompanyInfluenceRules().GetPlacementSlotIds(state)[0]; Run(state, InfluenceEffectSpecFactory.PlaceInfluence(1, playerSlot));
            string target = state.ItemZones.Items.Single(i => i.SlotId == playerSlot && i.TypeId == "core.player_marker").ItemId;
            Activate(state, Company, 4, 2); Answer(state, target); Assert.That(state.Map.Influences.Single(i => i.SlotId == playerSlot).PlayerId, Is.EqualTo(2));
            Assert.That(Open(state).AnsweringPlayerId, Is.EqualTo(2)); Assert.That(Open(state).CandidateIds.All(id => id.StartsWith("move|" + companyItem.ItemId + "|")), Is.True);
            state = Restore(state); Answer(state, Open(state).CandidateIds[0]); Assert.That(state.ItemZones.Items.Single(i => i.ItemId == companyItem.ItemId).SlotId, Is.Not.EqualTo(original));
        }
        [Test]
        public void ArrivalContractReceiptSurvivesRecoveryAndHoldingLimit()
        {
            var state = State(); AddResourcePoints(state); Reward(state, Company, 5); state = Restore(state); Drain(state);
            Assert.That(CompanyInfluenceRules.HasContract(state, 1), Is.True); Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(1));
            int events = state.EffectRuntime.RuleEvents.Count(e => e.EventType == RewardAcquisitionFlow.AcquiredEventTypeId);
            state = Restore(state); Run(state, EnterpriseEffectExecutor.UpgradeSpec(1, Item(state, Company).ItemId, 1)); Acquire(state, "contracts");
            Assert.That(In(state, "contracts").Count, Is.EqualTo(1)); Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == RewardAcquisitionFlow.AcquiredEventTypeId), Is.EqualTo(events));
            Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(1)); Acquire(state, "contracts", 2); Acquire(state, "contracts", 3);
            Assert.That(CompanyInfluenceRules.HasContract(state, 2), Is.True); Assert.That(CompanyInfluenceRules.HasContract(state, 3), Is.False);
        }
        [Test]
        public void FoundationResourceChoicesRepeatIndependentlyAndCanChooseSameResource()
        {
            var state = State(); Activate(state, Foundation, 1); Answer(state, "iron"); state = Restore(state); Answer(state, "iron");
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(22)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(84));
            Activate(state, Foundation, 3); Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(18)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2));
            state.FindPlayer(1).Resources.Iron = 3; Activate(state, Foundation, 3); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2));
            Reward(state, Foundation, 5); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(4));
        }
        [Test]
        public void FoundationChoosesOnlyActualRemainingAwardAndPublishesLifecycleRequestOnce()
        {
            var state = State(); Reward(state, Foundation, 4); string chosen = Open(state).CandidateIds[0]; int total = state.ItemZones.Items.Count;
            state = Restore(state); Answer(state, chosen); Assert.That(In(state, "awards", 1), Does.Contain(chosen));
            var acquired = state.EffectRuntime.RuleEvents.Single(e => e.EventType == RewardAcquisitionFlow.AcquiredEventTypeId);
            Assert.That(Text(acquired.Payload, "rewardItemId"), Is.EqualTo(chosen)); Assert.That(acquired.Visibility, Is.EqualTo("public"));
            state = Restore(state); Reward(state, Foundation, 4, 2); Assert.That(Open(state).CandidateIds, Does.Not.Contain(chosen)); Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(3));
            Answer(state, Open(state).CandidateIds[0]); Assert.That(state.ItemZones.Items.Count, Is.EqualTo(total));
        }
        [Test]
        public void RhodesRecoveryAndDirectReplacementUseSeparateActualAbilities()
        {
            var state = State(); var recovered = Item(state, "texas", 1); Position(state, recovered, FindZone(state, "discard", 1).ZoneId);
            Activate(state, Rhodes, 3); state = Restore(state); Answer(state, recovered.ItemId);
            Assert.That(In(state, "hand", 1), Is.EqualTo(new[] { recovered.ItemId })); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(90));
            int count = In(state, "operator_deck").Count; Reward(state, Rhodes, 4); Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(count));
            Assert.That(Open(state).CandidateIds, Does.Not.Contain("decline")); string selected = Open(state).CandidateIds[0]; state = Restore(state); Answer(state, selected); Answer(state, recovered.ItemId);
            Assert.That(In(state, "hand", 1), Is.EqualTo(new[] { selected })); Assert.That(In(state, "retired"), Does.Contain(recovered.ItemId));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == OperatorDispatchFlow.TypeId || n.EffectTypeId == DeckShuffleEffectExecutor.TypeId), Is.False);
            Assert.That(state.FindPlayer(1).Score, Is.Zero); Reward(state, Rhodes, 5); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2));
        }
        [TestCase(2, 2)] [TestCase(3, 1)] [TestCase(4, 0)]
        public void SupplyRetirementUsesPlayerCountAndRefillsOriginalSlotBeforeCover(int players, int expected)
        {
            var state = State(players); Run(state, ExpansionSetupFlow.RoundPreparationSpec(1)); int count = 0;
            while (Open(state) != null)
            {
                var request = Open(state); Assert.That(request.InteractionTypeId, Is.EqualTo(ExpansionSetupFlow.SupplyRetirementInteraction)); Assert.That(request.AnsweringPlayerId, Is.EqualTo(count + 1));
                string itemId = request.CandidateIds[0], zone = state.ItemZones.Items.Single(i => i.ItemId == itemId).ZoneId;
                state = Restore(state); Answer(state, itemId); Assert.That(state.ItemZones.Items.Single(i => i.ItemId == itemId).ZoneId, Is.EqualTo(FindZone(state, "retired").ZoneId));
                Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == zone), Is.EqualTo(1)); count++;
            }
            Assert.That(count, Is.EqualTo(expected));
        }
        [Test]
        public void RedOpeningUpgradesFinishBeforeThreePlayerSupplyRetirement()
        {
            var state = State(); state.Round = 5; Run(state, ExpansionSetupFlow.RoundPreparationSpec(1));
            for (int player = 1; player <= 3; player++) { Assert.That(Open(state).InteractionTypeId, Is.EqualTo("round.enterprise.upgrade")); Assert.That(Open(state).AnsweringPlayerId, Is.EqualTo(player)); Answer(state, Item(state, Foundation).ItemId); }
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(ExpansionSetupFlow.SupplyRetirementInteraction));
            state = Restore(state); Answer(state, Open(state).CandidateIds[0]); Assert.That(Open(state), Is.Null);
        }
        [TestCase(true)] [TestCase(false)]
        public void CleanupUsesCoveredPresenceAndLeavesPersistentCards(bool covered)
        {
            var state = State(); var discard = Item(state, "texas", 1); Position(state, discard, FindZone(state, "discard", 1).ZoneId);
            var persistent = Item(state, "elysium", 1); Position(state, persistent, FindZone(state, "persistent", 1).ZoneId);
            if (covered) { Run(state, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(state, Item(state, "liskarm", 1).ItemId); }
            state.FindPlayer(1).UsedCharacterThisRound = true;
            new RoundExecutionService(registry); Run(state, EffectSpec.Create(RoundExecutionService.PlayerCharacterRecyclingTypeId, playerId: 1));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == discard.ItemId).ZoneId, Is.EqualTo(FindZone(state, covered ? "hand" : "discard", 1).ZoneId));
            Assert.That(In(state, "persistent", 1), Does.Contain(persistent.ItemId)); state = Restore(state);
            Assert.That(In(state, "covered", 1), Is.Empty);
        }
        [Test]
        public void InfectedOrdinaryDispatchOverridesInheritedTwoCardRequest()
        {
            var state = State(); Run(state, OperatorDispatchFlow.CreateSpec(1, 2));
            Assert.That(state.ItemZones.Items.Count(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.LocalZoneId == "dispatch_candidates")), Is.EqualTo(3));
            state = Restore(state); Answer(state, Open(state).CandidateIds[0]); Answer(state, Open(state).CandidateIds[0]);
            Assert.That(In(state, "operator_deck").Count, Is.EqualTo(15));
        }
        [Test]
        public void CorruptedRewardReceiptAndDuplicateContractHoldingAreRejectedOnRecovery()
        {
            var state = State(); Acquire(state, "contracts");
            var archive = HostRecoveryService.CreateArchive(state);
            var node = archive.Snapshot.State.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == RewardAcquisitionFlow.TypeId);
            Set(node, "acquisitionCompleted", NormalizedValue.CreateBoolean(false));
            Assert.That(HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), registry).Succeeded, Is.False);
            Position(state, state.ItemZones.Items.Single(i => In(state, "contracts").Contains(i.ItemId)), state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.player_rewards" && z.OwnerPlayerId == 1).ZoneId);
            Assert.That(new ItemZoneQuery().TryValidate(state, out _), Is.False);
        }
        [Test]
        public void AwardLifecycleSubscriberUsesRealInstanceAndDoesNotRepeatAfterRecovery()
        {
            var state = State();
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("fixture.award_lifecycle", RewardAcquisitionFlow.AcquiredEventTypeId, "*", "fixture.award_lifecycle", context =>
            {
                if (Text(context.Event.Payload, "poolRole") != "awards") return new List<EffectSpec>();
                string id = Text(context.Event.Payload, "rewardItemId"); int player = context.Event.PlayerId;
                var item = context.State.ItemZones.Items.Single(i => i.ItemId == id);
                return new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(player, 1),
                    MoveSpec(player, new[] { id }, item.ZoneId, FindZone(context.State, "retired").ZoneId, "unordered", 1, "core.reward_card_transfer") };
            }));
            Reward(state, Foundation, 4); string chosen = Open(state).CandidateIds[0]; state = Restore(state); Answer(state, chosen);
            Assert.That(In(state, "retired"), Does.Contain(chosen)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            state = Restore(state); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(In(state, "awards").Count, Is.EqualTo(3));
        }
        [Test]
        public void FormalMissingDefinitionsAndEntrepreneurRemainBlocked()
        {
            var config = new GameRuleConfiguration { PlayerCount = 3, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
            var installed = ExternalContentPack.Load(Content);
            Assert.That(() => ActiveContentCatalogBuilder.Build(installed, config), Throws.InvalidOperationException.With.Message.EqualTo("content_not_ready"));
            config.EnabledExpansionIds.Remove("enterprise"); Assert.That(() => ActiveContentCatalogBuilder.Build(pack, config), Throws.InvalidOperationException.With.Message.EqualTo("missing_enterprise_dependency"));
            config.EnabledExpansionIds.Add("enterprise"); config.RuleModeId = "entrepreneur"; Assert.That(() => ActiveContentCatalogBuilder.Build(pack, config), Throws.InvalidOperationException.With.Message.EqualTo("rule_mode_not_implemented"));
        }
    }
}
