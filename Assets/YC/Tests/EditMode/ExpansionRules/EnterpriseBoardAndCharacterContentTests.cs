using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 仅装配显式完成的真实定义；不修改正式pack、不生成/覆盖界面资产。
    public sealed class EnterpriseBoardAndCharacterContentTests
    {
        private static readonly string ContentRoot = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
        private static readonly string[] DefinitionPaths = {
            "enterprise/enterprise.rhine_lab.json", "enterprise/enterprise.fussen_bank.json",
            "enterprise/enterprise.raythean.json", "enterprise/enterprise.mama_johns.json", "enterprise/enterprise.blacksteel.json",
            "department/enterprise.department.defense.json", "department/enterprise.department.structural.json",
            "character/operator.mudrock.json", "character/operator.w.json", "character/operator.kaltsit.json",
            "character/mlynar.json", "component/enterprise.component.road.json",
            "enterprise/enterprise.rhodes_island.json", "enterprise/enterprise.hyde_brothers.json", "enterprise/enterprise.rim_billiton.json",
            "department/enterprise.department.engineering.json", "character/operator.silverash.json", "character/operator.eunectes.json",
            "character/operator.closure.json", "character/operator.dorothy.json", "character/operator.passenger.json", "character/mountain.json"
        };
        private static readonly string[] ScriptCategories = { "enterprises", "departments", "characters" };
        private ExternalContentPack pack;
        private ExternalContentPack core;
        private EffectRegistry registry;
        private GameMapDefinition map;

        [OneTimeSetUp]
        public void LoadExplicitRealDefinitionsAndStageOriginalArtwork()
        {
            core = ExternalContentPack.Load(ContentRoot);
            string root = Path.Combine(Directory.GetCurrentDirectory(), "Logs/ExpansionValidation/EnterpriseBoardAndCharacterContent/content", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (string path in DefinitionPaths)
            {
                Copy(Path.Combine(ContentRoot, path), Path.Combine(root, path));
                var json = JObject.Parse(File.ReadAllText(Path.Combine(ContentRoot, path)));
                foreach (var ability in (JArray)json["abilities"])
                foreach (string key in new[] { "script", "cleanupScript" })
                    if (!string.IsNullOrEmpty((string)ability[key])) Copy(Path.Combine(ContentRoot, (string)ability[key]), Path.Combine(root, (string)ability[key]));
            }
            string originals = Path.Combine(Directory.GetCurrentDirectory(), "游城拓荒/企业扩");
            Copy(Path.Combine(originals, "企业合作板 2行4列.jpg"), Path.Combine(root, "artwork/sheets/enterprise_boards.jpg"));
            Copy(Path.Combine(originals, "罗德岛干员牌.jpg"), Path.Combine(root, "artwork/sheets/operator_faces.jpg"));
            Copy(Path.Combine(originals, "工程科.png"), Path.Combine(root, "artwork/departments/engineering.png"));
            Copy(Path.Combine(originals, "防卫科.png"), Path.Combine(root, "artwork/departments/defense.png"));
            Copy(Path.Combine(originals, "结构科.png"), Path.Combine(root, "artwork/departments/structural.png"));
            Copy(Path.Combine(originals, "公路.jpg"), Path.Combine(root, "artwork/enterprise_road.jpg"));
            Copy(Path.Combine(Directory.GetCurrentDirectory(), "游城拓荒/素材/角色卡.jpg"), Path.Combine(root, "artwork/sheets/character_faces.jpg"));
            var originalSlices = JObject.Parse(File.ReadAllText(Path.Combine(ContentRoot, "artwork_slices.json")));
            var spriteIds = DefinitionPaths.Select(path => (string)JObject.Parse(File.ReadAllText(Path.Combine(ContentRoot, path)))["artworkSpriteId"]).ToHashSet();
            var slices = ((JArray)originalSlices["slices"]).Where(s => spriteIds.Contains((string)s["id"])).ToList();
            var sheets = ((JArray)originalSlices["sheets"]).Where(s => slices.Any(slice => (string)slice["sheetId"] == (string)s["id"])).ToList();
            foreach (var sheet in sheets) Copy(Path.Combine(ContentRoot, (string)sheet["artwork"]), Path.Combine(root, (string)sheet["artwork"]));
            File.WriteAllText(Path.Combine(root, "artwork_slices.json"), new JObject {
                ["schemaVersion"] = originalSlices["schemaVersion"].DeepClone(), ["coordinateOrigin"] = originalSlices["coordinateOrigin"].DeepClone(),
                ["sheets"] = new JArray(sheets.Select(s => s.DeepClone())),
                ["slices"] = new JArray(slices.Select(s => s.DeepClone()))
            }.ToString());
            File.WriteAllText(Path.Combine(root, "pack.json"), new JObject {
                ["schemaVersion"] = 3, ["packId"] = "core", ["version"] = "1.0.0", ["definitions"] = new JArray(DefinitionPaths),
                ["enabledExpansionIds"] = new JArray("enterprise"), ["requiredCapabilities"] = new JArray(),
                ["expansionDependencies"] = new JArray(new JObject { ["expansionId"] = "enterprise", ["requires"] = new JArray("core") }),
                ["setupRecipes"] = new JArray(), ["artworkSlices"] = "artwork_slices.json"
            }.ToString());
            pack = ExternalContentPack.Load(root);
        }
        private static void Copy(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, true);
        }
        private GameState State(int players = 2)
        {
            map = StaticMapDefinitions.CreateThreePlayerMap();
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            ItemTransferEffectExecutor.Register(registry); DeckShuffleEffectExecutor.Register(registry); RepeatEffectExecutor.Register(registry);
            var query = new MapQueryService(map); var influence = new InfluenceService(query);
            InfluenceEffectExecutor.Register(registry, influence); MapOperationEffectExecutor.Register(registry, query);
            ContentPrimitiveEffectExecutor.Register(registry, query, influence); FacilityEntryEffectExecutor.Register(registry);
            var travel = new TravelCostService(query);
            CityMoveEffectExecutor.Register(registry, query, influence, new CityMovementService(query, influence, travel), travel, new ResourceTokenService());
            ResourceCollectionEffectExecutor.Register(registry);
            CharacterCoverEffectExecutor.Register(registry); CharacterLifecycleService.Register(registry);
            CharacterCardLuaCatalog.Register(registry, pack);
            EnterpriseEffectExecutor.Register(registry, LuaContentCatalog.CreateEnterpriseDefinitions(registry, pack.ActiveDefinitions, pack.GetScript));
            var definitions = new GameContentDefinitions {
                Components = pack.ActiveDefinitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition { DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"] }).ToList(),
                Characters = core.CreateCharacters().Concat(pack.CreateCharacters()).ToList(), Facilities = core.CreateFacilities().ToList(),
                Events = core.CreateEvents().ToList(), CityStyles = core.CreateCityStyles().ToList()
            };
            var source = new GameState { GameId = "game.enterprise_content", MapId = map.MapId, ContentPackHash = pack.ContentHash,
                Round = 1, ActionRound = 1, StartPlayerId = 1, CurrentPlayerId = 1, Phase = GamePhase.ActionRound1,
                ContentDefinitions = definitions, RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = players, ContentPackHash = pack.ContentHash } };
            for (int player = 1; player <= players; player++) source.Players.Add(new PlayerState { PlayerId = player, Resources = new ResourceSet { GoldVoucher = 80, Originium = 20, OriginiumShard = 20, Iron = 20, PureOriginium = 3 } });
            var entries = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "player_markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 29, PerPlayer = true, InitialZoneRole = "player_markers" },
                new ItemRecipeEntry { EntryId = "mobile_cities", DefinitionId = "core.mobile_city", TypeId = "core.mobile_city", Quantity = 1, PerPlayer = true, InitialZoneRole = "city_reserve" }
            };
            foreach (var definition in pack.ActiveDefinitions.Where(d => d.ContentType != "component")) entries.Add(new ItemRecipeEntry {
                EntryId = definition.DefinitionId, DefinitionId = definition.DefinitionId, TypeId = definition.ItemTypeId, Quantity = 1,
                PerPlayer = definition.ContentType == "character" && (string)definition.Data["acquisitionRole"] == "initial",
                InitialZoneRole = definition.ContentType == "character" ? (string)definition.Data["acquisitionRole"] == "initial" ? "hand" : "operator_deck" : definition.ContentType == "department" ? "departments" : "enterprises"
            });
            foreach (string id in new[] { "liskarm", "elysium", "texas", "cannot", "tin-man" }) entries.Add(new ItemRecipeEntry { EntryId = "initial_" + id, DefinitionId = id, TypeId = "core.basic_character_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" });
            foreach (var location in map.Locations.Where(l => !l.IsRedZone)) entries.Add(new ItemRecipeEntry { EntryId = "resource_point_" + location.LocationId, DefinitionId = "core.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + location.LocationId });
            foreach (var facility in definitions.Facilities.Where(d => d.GoldVoucherCost > 0).Take(9)) entries.Add(new ItemRecipeEntry { EntryId = facility.FacilityId, DefinitionId = facility.FacilityId, TypeId = "core.facility_card", Quantity = 1, InitialZoneRole = "facility_deck" });
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, entries, pack.ActiveDefinitions.Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = d.CardZones }).ToList());
            foreach (var token in state.ItemZones.Items.Where(i => i.TypeId == "core.resource_point_token"))
                token.LocalState = Object("resourceType", NormalizedValue.CreateInteger((int)ResourceType.Originium), "amount", NormalizedValue.CreateInteger(2));
            state.EffectRuntime.PlayerOrderSnapshot = Enumerable.Range(1, players).ToList();
            state.Map.OpenLocationIds.AddRange(map.Locations.Where(l => !l.IsRedZone).Select(l => l.LocationId));
            foreach (var board in pack.ActiveDefinitions.Where(d => d.ContentType == "enterprise"))
            foreach (int player in Enumerable.Range(1, players))
            {
                var declaration = new CardZoneDefinition { LocalZoneId = "cooperation_" + player, ZoneTemplateId = "core.zone.board_marker_track",
                    AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" },
                    CapacityPolicy = "bounded", Capacity = 1, OrderingKind = ZoneOrderingKind.Track, VisibilityPolicyId = ItemZoneVisibility.Public,
                    EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
                Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, Item(state, board.DefinitionId).ItemId, declaration, out string zone, out string reason), Is.True, reason);
                var marker = state.ItemZones.Items.First(i => i.ZoneId == FindZone(state, "player_markers", player).ZoneId);
                SetFixturePosition(state, marker, zone); marker.SlotId = "level_1";
            }
            state.ItemZones.Relations.Add(new ItemRelationState { RelationId = StableIdFactory.Create("relation", state.GameId, "active_department"), RelationTypeId = EnterpriseStateQuery.ActiveDepartmentRelation,
                SourceItemId = Item(state, "enterprise.rhine_lab").ItemId, TargetItemId = Item(state, "enterprise.department.structural").ItemId });
            Valid(state); return state;
        }
        private static ItemInstanceState Item(GameState state, string definition) => state.ItemZones.Items.Single(i => i.DefinitionId == definition);
        private static void SetFixturePosition(GameState state, ItemInstanceState item, string zone)
        {
            foreach (var order in state.ItemZones.Orders) if (order.ItemIds.Remove(item.ItemId)) order.OrderRevision++;
            item.ZoneId = zone; item.SlotId = "";
            var destination = state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == zone);
            if (destination != null) { destination.ItemIds.Add(item.ItemId); destination.OrderRevision++; }
        }
        private static void Valid(GameState state) => Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
        private string DockFixtureCity(GameState state, int player)
        {
            var location = map.Locations.First(l => !l.IsRedZone && l.CanDockCity && !state.Players.Any(p => p.CityLocationId == l.LocationId));
            var parent = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == location.LocationId);
            var dock = state.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.city_dock");
            var city = state.ItemZones.Items.Single(i => i.TypeId == "core.mobile_city" && i.OwnerSubject.PlayerId == player);
            SetFixturePosition(state, city, dock.ZoneId); Valid(state);
            Assert.That(state.FindPlayer(player).CityLocationId, Is.EqualTo(location.LocationId));
            return location.LocationId;
        }
        private EffectNodeRuntimeState Run(GameState state, EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state); return engine.GetNode(root);
        }
        private void Answer(GameState state, params string[] values)
        {
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open"); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                values.Length == 1 ? NormalizedValue.CreateString(values[0]) : Strings(values), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state);
        }
        private void Drain(GameState state)
        {
            for (int step = 0; step < 200; step++)
            {
                var request = state.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open"); if (request == null) return;
                string choice = request.CandidateIds.Contains("choice.skip") ? "choice.skip" : request.CandidateIds.Contains("decline") ? "decline" : request.CandidateIds[0];
                if (request.MinSelections > 1) Answer(state, request.CandidateIds.Take(request.MinSelections).ToArray()); else Answer(state, choice);
            }
            Assert.Fail("内容交互未在业务步数上限内完成。");
        }
        private GameState Restore(GameState state)
        {
            var recovery = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(recovery.Succeeded, Is.True, recovery.Diagnostic); Valid(recovery.State); return recovery.State;
        }
        private EffectNodeRuntimeState Invoke(GameState state, string path, NormalizedValue payload = null, int player = 1)
        {
            string ability = Path.GetFileNameWithoutExtension(path); string source = File.ReadAllText(Path.Combine(ContentRoot, path));
            string subscription = "content.test.invoke." + ability; string type = "content.test.invoke";
            if (!registry.TryGet(type, out _)) registry.Register(new EffectRegistration(type, context => EffectStepResult.Completed().AddEvent(new EffectEventRequest {
                EventType = "ContentAbilityInvoked", RouteKey = Text(context.Node.NormalizedArguments, "abilityId"), PlayerId = context.Node.PlayerId,
                Payload = Value(context.Node.NormalizedArguments, "payload").Clone()
            })));
            if (!registry.HasEventHandler(subscription))
            {
                var dispatcher = new LuaEffectEventDispatcher(registry); string completion = ability == "enterprise.rhodes_island.level_2.special" ? subscription + ".gold" : "";
                var lua = new LuaScriptDefinition(ability, ability, subscription, "1.0.0", source, LuaContentHasher.ComputeSha256(source));
                if (completion.Length > 0) dispatcher.RegisterContinuation(new LuaHandlerDefinition(completion, "EffectCompleted", "", new LuaScriptDefinition(ability, ability, completion, "1.0.0", source, LuaContentHasher.ComputeSha256(source)), EffectHandlerRole.Continuation, 0, "", ability));
                dispatcher.Register(new LuaHandlerDefinition(subscription, "ContentAbilityInvoked", ability, lua, EffectHandlerRole.Primary, 0, "", ability, completion));
            }
            return Run(state, EffectSpec.Create(type, Object("abilityId", NormalizedValue.CreateString(ability), "payload", payload ?? Object("enterpriseItemId", NormalizedValue.CreateString(Item(state, "enterprise.rhine_lab").ItemId))), player));
        }
        private static IEnumerable<string> ReadyScripts()
        {
            // 路径清单来自本任务交付，避免把其他任务脚本计入覆盖。
            foreach (string category in ScriptCategories)
            foreach (string path in Directory.GetFiles(Path.Combine(ContentRoot, "lua", category), "*.lua"))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("enterprise.", StringComparison.Ordinal) && !name.StartsWith("enterprise.event.", StringComparison.Ordinal) ||
                    name.StartsWith("operator.", StringComparison.Ordinal) || name.StartsWith("mlynar.", StringComparison.Ordinal) || name.StartsWith("mountain.", StringComparison.Ordinal)) yield return "lua/" + category + "/" + name;
            }
        }
        [TestCaseSource(nameof(ReadyScripts))]
        public void DeliveredAbilityRunsThroughLuaHostAndRealEffects(string script)
        {
            var state = State();
            if (script == "lua/characters/operator.eunectes.strategy.lua") DockFixtureCity(state, 1);
            if (script == "lua/characters/operator.closure.tactic.lua" || script == "lua/characters/operator.dorothy.tactic.lua" || script == "lua/characters/operator.passenger.tactic.lua") UseCard(state, Path.GetFileName(script).Replace(".tactic.lua", ""), "tactic");
            else
            {
                if (script == "lua/enterprises/enterprise.rhodes_island.level_4.special.lua")
                { var card = state.ItemZones.Items.First(i => i.DefinitionId == "texas" && i.ControllerSubject.PlayerId == 1); SetFixturePosition(state, card, FindZone(state, "discard", 1).ZoneId); }
                if (script == "lua/characters/operator.dorothy.strategy.lua")
                { var l = map.Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount >= 2); Place(state, 1, InfluenceService.GetLocationSlotId(l.LocationId, 0)); Place(state, 1, InfluenceService.GetLocationSlotId(l.LocationId, 1)); }
                Invoke(state, script); Drain(state);
            }
            Assert.That(state.Logs.Any(l => l.Message.Contains("incomplete_effect_skipped")), Is.False, script);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.Status == EffectNodeStatus.Faulted), Is.False, script);
            Assert.That(state.EffectRuntime.EffectNodes.Where(n => n.Status == EffectNodeStatus.Failed && n.FailureReason != "player_declined")
                .Select(n => n.EffectTypeId + ":" + n.FailureReason), Is.Empty, script);
        }
        [Test] public void ExplicitDefinitionsLoadWithExactTypesAndNoOperatorsInInitialHands()
        {
            var state = State(); Assert.That(pack.Definitions.Count, Is.EqualTo(22)); Assert.That(pack.ActiveDefinitions.Count, Is.EqualTo(22));
            foreach (var player in state.Players)
            {
                var hand = new ItemZoneQuery().GetItemIds(state, FindZone(state, "hand", player.PlayerId).ZoneId);
                Assert.That(hand.Count, Is.EqualTo(7));
                Assert.That(hand.All(id => state.ItemZones.Items.Single(i => i.ItemId == id).TypeId == "core.basic_character_card"), Is.True);
                Assert.That(hand.Count(id => state.ItemZones.Items.Single(i => i.ItemId == id).DefinitionId == "mlynar"), Is.EqualTo(1));
            }
            Assert.That(state.ItemZones.Items.Count(i => i.TypeId == "core.operator_card"), Is.EqualTo(8));
            Assert.That(pack.Definitions.Where(d => d.ItemTypeId == "core.operator_card").All(d => (string)d.Data["acquisitionRole"] == "operator_deck"), Is.True);
        }
        [Test] public void MamaJohnsUnlockAndBlacksteelArrivalHaveDifferentScoreBehavior()
        {
            var state = State(); Run(state, EnterpriseEffectExecutor.UpgradeSpec(1, Item(state, "enterprise.mama_johns").ItemId, 2));
            Assert.That(state.FindPlayer(1).Score, Is.Zero);
            Run(state, EnterpriseEffectExecutor.UpgradeSpec(1, Item(state, "enterprise.blacksteel").ItemId, 2)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
        }
        [Test] public void ZeroScorePaysOneAndStillReceivesTwentyGoldVouchers()
        {
            var state = State(); state.FindPlayer(1).Resources.GoldVoucher = 0;
            Invoke(state, "lua/enterprises/enterprise.mama_johns.level_2.special.lua");
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(-1)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(20));
            state = Restore(state); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(-1));
        }
        [Test] public void InsufficientEnterprisePaymentDoesNotCreateReward()
        {
            var state = State(); state.FindPlayer(1).Resources.GoldVoucher = 24;
            Invoke(state, "lua/enterprises/enterprise.mama_johns.level_4.special.lua");
            Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(24));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ResourceEffectTypeIds.GainScore), Is.False);
        }
        private static void SetLevel(GameState state, string definition, int player, int level)
        {
            var board = Item(state, definition); var zone = state.ItemZones.Zones.Single(z => z.ParentId == board.ItemId && z.LocalZoneId == "cooperation_" + player);
            state.ItemZones.Items.Single(i => i.ZoneId == zone.ZoneId).SlotId = "level_" + level;
        }
        [Test] public void StructuralFiveToFourPaysCurrentRewardAgainAfterFutureArrival()
        {
            var state = State(); SetLevel(state, "enterprise.rhine_lab", 1, 4); state.FindPlayer(1).Resources.OriginiumShard = 6;
            state.FindPlayer(1).Resources.PureOriginium = 0;
            Run(state, EnterpriseEffectExecutor.UpgradeSpec(1, Item(state, "enterprise.rhine_lab").ItemId, 1));
            Assert.That(new EnterpriseStateQuery().GetLevel(state, 1, Item(state, "enterprise.rhine_lab").ItemId), Is.EqualTo(4));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(3)); Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(1));
            state = Restore(state); Run(state, EnterpriseEffectExecutor.UpgradeSpec(1, Item(state, "enterprise.rhine_lab").ItemId, 1));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.Zero); Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(2));
        }
        [Test] public void DepartmentSwitchAtOneHasNoRewardAndAtTwoOnlyPaysExecutor()
        {
            var state = State(); int before = state.FindPlayer(1).Resources.GoldVoucher;
            Invoke(state, "lua/enterprises/enterprise.rhine_lab.level_1.special.lua"); Drain(state);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(before));
            SetLevel(state, "enterprise.rhine_lab", 1, 2); SetLevel(state, "enterprise.rhine_lab", 2, 2);
            state.ItemZones.Relations.Single(r => r.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation).TargetItemId = Item(state, "enterprise.department.structural").ItemId;
            Invoke(state, "lua/enterprises/enterprise.rhine_lab.level_1.special.lua"); Drain(state);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(before + 7)); Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(80));
        }
        [Test] public void RaytheanDeclinedDeckBuildStillShufflesOnceAcrossRecovery()
        {
            var state = State(); var order = state.ItemZones.Orders.Single(o => o.ZoneId == FindZone(state, "facility_deck").ZoneId); int revision = order.OrderRevision;
            Invoke(state, "lua/enterprises/enterprise.raythean.level_5.special.lua"); Answer(state, "choice.skip");
            Assert.That(state.ItemZones.Orders.Single(o => o.ZoneId == order.ZoneId).OrderRevision, Is.EqualTo(revision + 1));
            var shuffled = state.ItemZones.Orders.Single(o => o.ZoneId == order.ZoneId).ItemIds.ToArray(); state = Restore(state);
            new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.ItemZones.Orders.Single(o => o.ZoneId == order.ZoneId).ItemIds, Is.EqualTo(shuffled));
        }
        private void Place(GameState state, int player, string slot)
        {
            var result = Run(state, InfluenceEffectSpecFactory.PlaceInfluence(player, slot));
            Assert.That(result.Status, Is.EqualTo(EffectNodeStatus.Completed), result.FailureReason);
            Assert.That(Flag(result.NormalizedResult, "changed"), Is.True, JsonConvert.SerializeObject(result.NormalizedResult));
        }
        [Test] public void WTwoPlayersPaysFourAndOnlyReplacesUniqueOpponentOnce()
        {
            var state = State(); string location = map.Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount >= 2).LocationId;
            Place(state, 2, InfluenceService.GetLocationSlotId(location, 0)); Place(state, 2, InfluenceService.GetLocationSlotId(location, 1));
            Invoke(state, "lua/characters/operator.w.tactic.lua"); Drain(state);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(76));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceReplaced), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.Zero);
        }
        [Test] public void SilverashDirectSelectionExcludesAlreadyHandledPlayersAndContinuesPlacement()
        {
            var state = State(); var locations = map.Locations.Where(l => !l.IsRedZone && l.InfluenceSlotCount >= 2).Take(2).ToArray();
            Place(state, 2, InfluenceService.GetLocationSlotId(locations[0].LocationId, 0)); Place(state, 2, InfluenceService.GetLocationSlotId(locations[0].LocationId, 1));
            Place(state, 1, InfluenceService.GetLocationSlotId(locations[1].LocationId, 0));
            Invoke(state, "lua/characters/operator.silverash.tactic.lua");
            var selected = state.ItemZones.Items.First(i => new ItemZoneQuery().IsInfluence(state, i.ItemId) && i.OwnerSubject.PlayerId == 2).ItemId;
            Answer(state, selected); state = Restore(state);
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            Assert.That(request.CandidateIds.All(id => state.ItemZones.Items.Single(i => i.ItemId == id).OwnerSubject.PlayerId == 1), Is.True);
            Drain(state); Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.EqualTo(2));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluencePlaced), Is.EqualTo(5));
        }
        private void UseCard(GameState state, string definition, string mode)
        {
            var card = state.ItemZones.Items.Single(i => i.DefinitionId == definition && (i.ControllerSubject.PlayerId == 1 || i.TypeId == "core.operator_card")); Run(state, MoveSpec(1, new[] { card.ItemId }, card.ZoneId, FindZone(state, "hand", 1).ZoneId, "unordered", 1, "core.character_card_transfer"));
            Run(state, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(state, card.ItemId);
            Run(state, CharacterLifecycleService.CreateUseSpec(1, card.ItemId, mode)); Drain(state);
        }
        [Test] public void MudrockPersistentCleanupCanActivateOnceAndStopsAfterRealExit()
        {
            var state = State(); UseCard(state, "operator.mudrock", "tactic");
            Assert.That(Item(state, "operator.mudrock").ZoneId, Is.EqualTo(FindZone(state, "persistent", 1).ZoneId));
            var cleanup = EventSpec("PlayerCleanupStarted"); Run(state, cleanup); Answer(state, "activate"); Drain(state);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(18));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluencePlaced), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            var card = Item(state, "operator.mudrock");
            Run(state, MoveSpec(1, new[] { card.ItemId }, card.ZoneId, FindZone(state, "discard", 1).ZoneId, "unordered", 1, "core.character_card_transfer"));
            state = Restore(state); Run(state, cleanup);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluencePlaced), Is.EqualTo(1));
        }
        [Test] public void WRegisteredCleanupSurvivesOperatorReplacementAndRecovery()
        {
            var state = State(); UseCard(state, "operator.w", "strategy");
            Run(state, OperatorDispatchFlow.CreateSpec(state, 1));
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            Answer(state, Item(state, "operator.mudrock").ItemId);
            Assert.That(state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open").CandidateIds, Does.Contain(Item(state, "operator.w").ItemId));
            Answer(state, Item(state, "operator.w").ItemId);
            Assert.That(Item(state, "operator.w").ZoneId, Is.EqualTo(FindZone(state, "retired").ZoneId));
            state = Restore(state); Run(state, EventSpec("PlayerCleanupStarted")); Drain(state);
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.EqualTo(2));
            state = Restore(state); Run(state, EventSpec("PlayerCleanupStarted")); Drain(state);
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.EqualTo(2));
        }
        [Test] public void MlynarAllPlayerUpgradeStartsWithExecutorAndSpecialUsesIndependentEnterprise()
        {
            var state = State(3); state.CurrentPlayerId = 2;
            Invoke(state, "lua/characters/mlynar.strategy.lua", player: 2);
            Assert.That(CurrentRequest(state).InteractionTypeId, Is.EqualTo("enterprise.upgrade")); state = Restore(state);
            Answer(state, Item(state, "enterprise.fussen_bank").ItemId);
            var arrivals = state.EffectRuntime.RuleEvents.Where(e => e.EventType == EnterpriseEffectExecutor.LevelChangedEvent).ToList();
            Assert.That(arrivals.Select(e => e.PlayerId), Is.EqualTo(new[] { 2, 3, 1 }));
            Assert.That(state.Players.All(p => new EnterpriseStateQuery().GetLevel(state, p.PlayerId, Item(state, "enterprise.fussen_bank").ItemId) == 2), Is.True);
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            var specialIds = Value(request.PromptParameters, "candidateSpecialIds").Items.Select(value => value.StringValue).ToList();
            Assert.That(specialIds, Does.Contain("enterprise.raythean.level_1.special"));
            string raythean = request.CandidateIds[specialIds.IndexOf("enterprise.raythean.level_1.special")];
            state = Restore(state); Answer(state, raythean);
            Assert.That(state.FindPlayer(2).Resources.OriginiumShard, Is.EqualTo(23));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(20));
        }
        [Test] public void MlynarActualCoveredCardUsesAuthorizedFlowAndReturnsToItsHoldersDiscard()
        {
            var state = State(3); state.CurrentPlayerId = 2;
            var card = state.ItemZones.Items.Single(item => item.DefinitionId == "mlynar" && item.ControllerSubject.PlayerId == 2);
            Run(state, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 2)); Answer(state, card.ItemId);
            Run(state, CharacterLifecycleService.CreateUseSpec(2, card.ItemId, "strategy"));
            Answer(state, Item(state, "enterprise.fussen_bank").ItemId);
            var request = state.EffectRuntime.InteractionRequests.Last(item => item.Status == "open");
            var specials = Value(request.PromptParameters, "candidateSpecialIds").Items.Select(value => value.StringValue).ToList();
            Assert.That(request.AnsweringPlayerId, Is.EqualTo(2));
            string candidate = request.CandidateIds[specials.IndexOf("enterprise.raythean.level_1.special")];
            state = Restore(state); Answer(state, candidate);
            Assert.That(state.EffectRuntime.RuleEvents.Where(ruleEvent => ruleEvent.EventType == EnterpriseEffectExecutor.LevelChangedEvent).Select(ruleEvent => ruleEvent.PlayerId), Is.EqualTo(new[] { 2, 3, 1 }));
            Assert.That(state.CharacterUses.Single(use => use.CardInstanceId == card.ItemId).Finished, Is.True);
            Assert.That(state.ItemZones.Items.Single(item => item.ItemId == card.ItemId).ZoneId, Is.EqualTo(FindZone(state, "discard", 2).ZoneId));
            Assert.That(state.FindPlayer(2).Resources.OriginiumShard, Is.EqualTo(23)); Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(20));
        }
        [Test] public void EunectesFreeCityMoveConsumesNoMainActionAndEmitsMovementInsteadOfRemoval()
        {
            var state = State(); string source = DockFixtureCity(state, 1);
            var targets = new MapQueryService(map).GetAdjacentLocations(source).Where(l => !l.IsRedZone).OrderBy(l => l.LocationId).ToList();
            string destination = targets.First().LocationId;
            Place(state, 2, InfluenceService.GetLocationSlotId(destination, 0));
            string displaced = state.ItemZones.Items.Single(i => new ItemZoneQuery().IsInfluence(state, i.ItemId) && i.OwnerSubject.PlayerId == 2).ItemId;
            int gold = state.FindPlayer(1).Resources.GoldVoucher;
            int actions = state.FindPlayer(1).RemainingMainActionsThisTurn;
            Invoke(state, "lua/characters/operator.eunectes.strategy.lua"); Answer(state, destination); Drain(state);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.Not.EqualTo(source));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(actions));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == CityMoveEventTypeIds.CityMoveCompleted), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceMoved), Is.EqualTo(1));
            Assert.That(new ItemZoneQuery().IsInfluence(state, displaced), Is.True);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == displaced).OwnerSubject.PlayerId, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.False);
        }
        [Test] public void KaltsitFreezesUnlockedSpecialSetBeforeNewUnlockAndRecoversChosenOrder()
        {
            var state = State(); SetLevel(state, "enterprise.fussen_bank", 1, 3);
            string board = Item(state, "enterprise.fussen_bank").ItemId;
            // 通用测试观察者在第一项真实特效中升级局面；能力本体仍由真实Lua执行。
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("content.test.advance_during_special", "EnterpriseSpecialEffectActivated", "",
                "content.test.advance_during_special", context => new EnterpriseStateQuery().GetLevel(context.State, 1, board) == 3
                    ? new[] { EnterpriseEffectExecutor.UpgradeSpec(1, board, 1) } : new EffectSpec[0]));
            Invoke(state, "lua/characters/operator.kaltsit.tactic.lua"); Answer(state, Item(state, "enterprise.fussen_bank").ItemId);
            var order = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open").CandidateIds.ToArray();
            Assert.That(order.Length, Is.EqualTo(2));
            state = Restore(state); Answer(state, order); Drain(state);
            Assert.That(new EnterpriseStateQuery().GetLevel(state, 1, board), Is.EqualTo(4));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "EnterpriseSpecialEffectActivated"), Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Score, Is.Zero, "中途解锁的4级特效没有加入本次集合。");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(108));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(84));
        }
        [Test] public void RimBillitonExtraCollectionPaysScoreWithoutPhaseGoldOrConsumingNormalEligibility()
        {
            var state = State(); string location = DockFixtureCity(state, 1);
            var resources = state.FindPlayer(1).Resources.Clone();
            Invoke(state, "lua/enterprises/enterprise.rim_billiton.level_4.special.lua"); Answer(state, location); Drain(state);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(-1));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(resources.GoldVoucher));
            Assert.That(state.FindPlayer(1).Resources.Originium + state.FindPlayer(1).Resources.OriginiumShard + state.FindPlayer(1).Resources.Iron,
                Is.GreaterThan(resources.Originium + resources.OriginiumShard + resources.Iron));
            Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.False);
            state = Restore(state); Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.False);
        }
        private InteractionRequest CurrentRequest(GameState state) => state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
        private void FillSupply(GameState state)
        { for (int i = 0; i < 6; i++) Run(state, RefillSpec(1, FindZone(state, "facility_supply_" + i).ZoneId)); }
        private void StartCard(GameState state, string definition, string mode)
        {
            var card = state.ItemZones.Items.Single(i => i.DefinitionId == definition && (i.ControllerSubject.PlayerId == 1 || i.TypeId == "core.operator_card"));
            if (card.ZoneId != FindZone(state, "hand", 1).ZoneId) Run(state, MoveSpec(1, new[] { card.ItemId }, card.ZoneId, FindZone(state, "hand", 1).ZoneId, "unordered", 1, "core.character_card_transfer"));
            Run(state, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(state, card.ItemId);
            Run(state, CharacterLifecycleService.CreateUseSpec(1, card.ItemId, mode));
        }
        private static ZoneInstanceState CarrierZone(GameState state, string definition) => state.ItemZones.Zones.Single(z => z.ParentId == Item(state, definition).ItemId && z.ParentKind == ZoneParentKind.Item);
        private void LoadMarkers(GameState state, string definition, int count)
        { StartCard(state, definition, "tactic"); Answer(state, count.ToString()); Drain(state); }
        private static EffectSpec CompositePay(int player, params object[] fields) => EffectSpec.Create(ResourceEffectTypeIds.Pay, Object("player", NormalizedValue.CreateString(player.ToString()), "cost", Object(fields)), player);

        [Test] public void RhodesRouteIncomeReadsCommittedMapIncludingNewMarkerAfterRecovery()
        {
            var state = State(); var routes = map.Routes.Where(r => r.InfluenceSlotCount > 0).Take(3).ToArray();
            Place(state, 1, InfluenceService.GetRouteSlotId(routes[0].RouteId, 0)); Place(state, 2, InfluenceService.GetRouteSlotId(routes[1].RouteId, 0));
            Invoke(state, "lua/enterprises/enterprise.rhodes_island.level_2.special.lua");
            Assert.That(CurrentRequest(state).CandidateIds.All(id => map.Routes.Any(r => Enumerable.Range(0, r.InfluenceSlotCount).Any(i => InfluenceService.GetRouteSlotId(r.RouteId, i) == id))), Is.True);
            state = Restore(state); Answer(state, InfluenceService.GetRouteSlotId(routes[2].RouteId, 0)); Drain(state);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(82));
        }
        [Test] public void RhodesRecoversExactDiscardInstanceAndPubliclyDiscardsOtherHands()
        {
            var state = State(); var card = state.ItemZones.Items.Single(i => i.DefinitionId == "texas" && i.ControllerSubject.PlayerId == 1);
            SetFixturePosition(state, card, FindZone(state, "discard", 1).ZoneId); Invoke(state, "lua/enterprises/enterprise.rhodes_island.level_4.special.lua");
            state = Restore(state); Answer(state, card.ItemId); Drain(state);
            Assert.That(new ItemZoneQuery().GetItemIds(state, FindZone(state, "hand", 1).ZoneId), Is.EqualTo(new[] { card.ItemId }));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(90));
            Assert.That(state.EffectRuntime.RuleEvents.Single(e => e.EventType == "character.cards.revealed").Visibility, Is.EqualTo("public"));
            var empty = State(); Invoke(empty, "lua/enterprises/enterprise.rhodes_island.level_4.special.lua");
            Assert.That(empty.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(80)); Assert.That(empty.FindPlayer(1).HandCardIds.Count, Is.EqualTo(7));
        }
        [Test] public void HydeReturnsOnlyFrozenOldSupplyThenShufflesRefillsAndCanDeclineBuild()
        {
            var state = State(); FillSupply(state); var old = state.ItemZones.Items.Where(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot")).Select(i => i.ItemId).ToArray();
            Invoke(state, "lua/enterprises/enterprise.hyde_brothers.level_4.special.lua");
            var refresh = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == RefreshTypeId);
            Assert.That(Value(refresh.ContinuationData, "selectedItemIds").Items.Select(v => v.StringValue), Is.EquivalentTo(old));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.ParentEffectId == refresh.EffectId && n.EffectTypeId == TypeId), Is.EqualTo(6));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.ParentEffectId == refresh.EffectId && n.EffectTypeId == RefillTypeId), Is.EqualTo(6));
            state = Restore(state); Answer(state, "choice.skip"); Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(14));
            Assert.That(state.Decks.FacilitySupply.Count, Is.EqualTo(6)); Assert.That(state.Map.Facilities, Is.Empty);
        }
        [Test] public void RimReplacementUsesActualResourcePointScopeAndEngineeringEnforcesPrintedLimit()
        {
            var state = State(); var point = map.Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount > 0); var route = map.Routes.First(r => r.InfluenceSlotCount > 0);
            Place(state, 2, InfluenceService.GetLocationSlotId(point.LocationId, 0)); Place(state, 2, InfluenceService.GetRouteSlotId(route.RouteId, 0));
            string id = state.Map.Influences.Single(i => i.LocationId == point.LocationId).InfluenceId;
            Invoke(state, "lua/enterprises/enterprise.rim_billiton.level_2.special.lua"); Assert.That(CurrentRequest(state).CandidateIds, Is.EqualTo(new[] { id })); Answer(state, id); Drain(state);
            Assert.That(state.Map.Influences.Single(i => i.LocationId == point.LocationId).PlayerId, Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(76));
            FillSupply(state); Invoke(state, "lua/departments/enterprise.department.engineering.level_5.reward.lua");
            Assert.That(CurrentRequest(state).CandidateIds.All(item => FacilityCardDatabase.Get(state, new ItemZoneQuery().FindItem(state, item).DefinitionId).GoldVoucherCost <= 23), Is.True);
            Answer(state, CurrentRequest(state).CandidateIds[0]); Drain(state);
            Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(76));
            Assert.That(state.Map.Facilities.Count, Is.EqualTo(1));
        }
        [TestCase(1, 0)] [TestCase(1, 7)] [TestCase(2, 0)] [TestCase(2, 7)]
        public void SilverashSellsAllOfChosenPlayersResourceAtFourAndScoresActor(int target, int stock)
        {
            var state = State(); state.FindPlayer(target).Resources.Iron = stock; Invoke(state, "lua/characters/operator.silverash.strategy.lua");
            state = Restore(state); Answer(state, "iron|p" + target); Drain(state);
            Assert.That(state.FindPlayer(target).Resources.Iron, Is.Zero); Assert.That(state.FindPlayer(target).Resources.GoldVoucher, Is.EqualTo(80 + stock * 4));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(state.FindPlayer(2).Score, Is.Zero);
        }
        [Test] public void SilverashTacticUsesOneShotLifecycle()
        {
            var state = State(); UseCard(state, "operator.silverash", "tactic");
            Assert.That(Item(state, "operator.silverash").ZoneId, Is.EqualTo(FindZone(state, "retired").ZoneId));
        }
        [Test] public void EunectesRefillsFirstSlotBeforeSecondDifferentNameChoiceAndRewardsOnZero()
        {
            var state = State(); FillSupply(state); Invoke(state, "lua/characters/operator.eunectes.tactic.lua");
            string first = CurrentRequest(state).CandidateIds.First(id => id != "choice.skip"), slot = new ItemZoneQuery().FindItem(state, first).ZoneId;
            string name = FacilityCardDatabase.Get(state, new ItemZoneQuery().FindItem(state, first).DefinitionId).Name; Answer(state, first);
            Assert.That(state.ItemZones.Items.Any(i => i.ZoneId == slot), Is.True); state = Restore(state);
            Assert.That(CurrentRequest(state).CandidateIds.Where(id => id != "choice.skip").All(id => FacilityCardDatabase.Get(state, new ItemZoneQuery().FindItem(state, id).DefinitionId).Name != name), Is.True);
            Answer(state, "choice.skip"); Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(23)); Assert.That(state.ItemZones.Items.Single(i => i.ItemId == first).ZoneId, Is.EqualTo(FindZone(state, "retired").ZoneId));
            var zero = State(); Invoke(zero, "lua/characters/operator.eunectes.tactic.lua"); Answer(zero, "choice.skip"); Assert.That(zero.FindPlayer(1).Resources.Iron, Is.EqualTo(23));
        }
        [Test] public void ClosureStoredFacilityIsOwnedCandidatePaysNormalCostAndEmptyCarrierDiscards()
        {
            var state = State(); FillSupply(state); StartCard(state, "operator.closure", "tactic");
            string selected = CurrentRequest(state).CandidateIds.First(id => id != "choice.skip"); Answer(state, selected); Answer(state, "choice.skip");
            var zone = CarrierZone(state, "operator.closure"); Assert.That(state.ItemZones.Items.Single(i => i.ItemId == selected).ZoneId, Is.EqualTo(zone.ZoneId));
            state = Restore(state); Run(state, EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, Object("sourceZone", NormalizedValue.CreateString("supply")), 1));
            Assert.That(CurrentRequest(state).CandidateIds, Does.Contain(selected)); var facility = FacilityCardDatabase.Get(state, new ItemZoneQuery().FindItem(state, selected).DefinitionId);
            var cost = new FacilityBuildCostService().GetEffectiveResourceCost(state, state.FindPlayer(1), facility); Answer(state, selected); Answer(state, "resources");
            Assert.That(Item(state, "operator.closure").ZoneId, Is.EqualTo(FindZone(state, "discard", 1).ZoneId));
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, Item(state, "operator.closure").ItemId, "operator.closure.tactic"), Is.False);
            state = Restore(state); Drain(state); Assert.That(state.Map.Facilities.Any(f => f.ContentInstanceId == selected), Is.True);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(20 - cost.Originium));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(80));
        }
        [Test] public void DorothyStrategyPreflightsBothInstancesAndRecoversCompleteSelection()
        {
            var state = State(); var location = map.Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount >= 2); Place(state, 1, InfluenceService.GetLocationSlotId(location.LocationId, 0));
            Invoke(state, "lua/characters/operator.dorothy.strategy.lua"); Assert.That(state.Map.Influences.Count, Is.EqualTo(1)); Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.False);
            Place(state, 1, InfluenceService.GetLocationSlotId(location.LocationId, 1)); Invoke(state, "lua/characters/operator.dorothy.strategy.lua");
            Assert.That(CurrentRequest(state).MinSelections, Is.EqualTo(2)); state = Restore(state); Answer(state, CurrentRequest(state).CandidateIds.Take(2).ToArray()); Drain(state);
            Assert.That(state.Map.Influences.Count, Is.EqualTo(3)); Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.EqualTo(2));
        }
        [TestCase(false)] [TestCase(true)]
        public void DorothyStoresRealDepartureBeyondFourKeepsOwnerAndEmptyPersistence(bool replacement)
        {
            var state = State(); LoadMarkers(state, "operator.dorothy", 4); var location = map.Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount > 0);
            Place(state, 1, InfluenceService.GetLocationSlotId(location.LocationId, 0)); string id = state.Map.Influences.Single().InfluenceId;
            Run(state, replacement ? InfluenceEffectSpecFactory.ReplaceInfluence(2, id) : InfluenceEffectSpecFactory.RemoveInfluence(2, id));
            Assert.That(CurrentRequest(state).AnsweringPlayerId, Is.EqualTo(1)); state = Restore(state); Answer(state, "store"); Drain(state);
            var zone = CarrierZone(state, "operator.dorothy"); var cube = state.ItemZones.Items.Single(i => i.ItemId == id);
            Assert.That(cube.ZoneId, Is.EqualTo(zone.ZoneId)); Assert.That(cube.OwnerSubject.PlayerId, Is.EqualTo(1)); Assert.That(cube.TypeId, Is.EqualTo("core.player_marker")); Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == zone.ZoneId), Is.EqualTo(5));
            int removed = state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved);
            Run(state, EventSpec("PlayerCleanupStarted")); Answer(state, "activate"); Drain(state);
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == zone.ZoneId), Is.EqualTo(3)); Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.EqualTo(removed));
            state = Restore(state); Run(state, EventSpec("PlayerCleanupStarted")); Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            var card = Item(state, "operator.dorothy"); Run(state, MoveSpec(1, new[] { card.ItemId }, card.ZoneId, FindZone(state, "retired").ZoneId, "unordered", 1, "core.character_card_transfer"));
            Assert.That(state.ItemZones.Items.Any(i => i.ZoneId == zone.ZoneId), Is.False); Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, card.ItemId, "operator.dorothy.tactic"), Is.False);
            var empty = State(); LoadMarkers(empty, "operator.dorothy", 0); Assert.That(Item(empty, "operator.dorothy").ZoneId, Is.EqualTo(FindZone(empty, "persistent", 1).ZoneId));
            Run(empty, EventSpec("PlayerCleanupStarted")); Answer(empty, "activate"); Drain(empty); Assert.That(empty.Map.Influences, Is.Empty);
        }
        [TestCase(0, 0)] [TestCase(2, 0)] [TestCase(2, 2)] [TestCase(9, 4)]
        public void PassengerQuantityIsOptionalBoundedAndBothPlayersScore(int stock, int selected)
        {
            var state = State(); state.FindPlayer(2).Resources.Iron = stock; Invoke(state, "lua/characters/operator.passenger.strategy.lua"); Assert.That(CurrentRequest(state).CandidateIds.All(id => !id.EndsWith("|p1")), Is.True); Answer(state, "iron|p2"); Assert.That(CurrentRequest(state).CandidateIds, Is.EquivalentTo(Enumerable.Range(0, Math.Min(4, stock) + 1).Select(i => i.ToString())));
            state = Restore(state); Answer(state, selected.ToString()); Drain(state);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(20 + selected)); Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(stock - selected));
            Assert.That(state.Players.Select(p => p.Score), Is.EqualTo(new[] { 1, 1 }));
        }
        [Test] public void PassengerRefreshesStaleQuantityWithoutSilentlyChangingIt()
        {
            var state = State(); Invoke(state, "lua/characters/operator.passenger.strategy.lua"); Answer(state, "iron|p2");
            state.FindPlayer(2).Resources.Iron = 1; Answer(state, "4"); Assert.That(CurrentRequest(state).CandidateIds, Is.EquivalentTo(new[] { "0", "1" }));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(20)); Assert.That(state.Players.All(p => p.Score == 0), Is.True);
            Answer(state, "1"); Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(21)); Assert.That(state.FindPlayer(2).Resources.Iron, Is.Zero);
        }
        [Test] public void PassengerCompositePaymentSharesTwoMarkerLimitAndRestoresWithoutRepayment()
        {
            var state = State(); LoadMarkers(state, "operator.passenger", 5); var zone = CarrierZone(state, "operator.passenger");
            Run(state, CompositePay(1, "Originium", NormalizedValue.CreateInteger(2), "Iron", NormalizedValue.CreateInteger(2), "GoldVoucher", NormalizedValue.CreateInteger(1)));
            Assert.That(CurrentRequest(state).CandidateIds.All(id => id.Split('|').Skip(2).Select(int.Parse).Sum() <= 2), Is.True);
            string answer = CurrentRequest(state).CandidateIds.Single(id => id.EndsWith("|1|0|1")); state = Restore(state); Answer(state, answer);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(19)); Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(19)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(79));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == zone.ZoneId), Is.EqualTo(3)); state = Restore(state); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(79));
        }
        [Test] public void PassengerFailedWholeCostConsumesNoMarkersAndEmptyCarrierExitsAfterRealPayment()
        {
            var state = State(); LoadMarkers(state, "operator.passenger", 2); var zone = CarrierZone(state, "operator.passenger"); state.FindPlayer(1).Resources.PureOriginium = 0;
            var failed = Run(state, CompositePay(1, "Iron", NormalizedValue.CreateInteger(2), "PureOriginium", NormalizedValue.CreateInteger(1)));
            Assert.That(failed.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == zone.ZoneId), Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(20));
            Run(state, ResourceEffectSpecFactory.Pay(1, ResourceType.GoldVoucher, 1)); Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == zone.ZoneId), Is.EqualTo(2));
            Run(state, ResourceEffectSpecFactory.Pay(1, ResourceType.Iron, 2)); Answer(state, CurrentRequest(state).CandidateIds.Single(id => id.EndsWith("|0|0|2")));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(20)); Assert.That(Item(state, "operator.passenger").ZoneId, Is.EqualTo(FindZone(state, "discard", 1).ZoneId));
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.False);
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void MountainClearsRealRouteInExecutorOrderScoresDistinctOwnersAndCreatesMappedRoad(int players)
        {
            var state = State(players); var route = map.Routes.First(r => r.InfluenceSlotCount >= 2); Place(state, 1, InfluenceService.GetRouteSlotId(route.RouteId, 0)); Place(state, 2, InfluenceService.GetRouteSlotId(route.RouteId, 1));
            if (route.InfluenceSlotCount > 2) Place(state, 1, InfluenceService.GetRouteSlotId(route.RouteId, 2));
            Invoke(state, "lua/characters/mountain.strategy.lua", player: 2);
            Assert.That(CurrentRequest(state).InteractionTypeId, Is.EqualTo("effect.route.choose")); state = Restore(state); Answer(state, route.RouteId); Drain(state);
            Assert.That(state.Map.Influences.Any(i => i.RouteId == route.RouteId), Is.False); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(state.FindPlayer(2).Score, Is.EqualTo(2));
            Assert.That(state.ItemZones.Items.Single(i => i.TypeId == "core.road_token").DefinitionId, Is.EqualTo("enterprise.component.road"));
            var events = state.EffectRuntime.RuleEvents.Where(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved).ToArray();
            Assert.That(Value(events[0].Payload, "originalSubjectKeys").Items[0].StringValue, Is.EqualTo("player:2"));
            Assert.That(new InfluenceService(new MapQueryService(map)).GetRoadCandidateRouteIds(state), Does.Not.Contain(route.RouteId));
            var road = state.ItemZones.Items.Single(i => i.TypeId == "core.road_token"); string roadId = road.ItemId, zoneId = road.ZoneId;
            Assert.That(road.PhysicalKind, Is.EqualTo(ItemPhysicalKind.Token)); Assert.That(new ItemZoneQuery().IsInfluence(state, roadId), Is.False);
            var zone = state.ItemZones.Zones.Single(z => z.ZoneId == zoneId); Assert.That(zone.TemplateId, Is.EqualTo("core.zone.route_road"));
            var parent = state.ItemZones.Zones.Single(z => z.ZoneId == zone.ParentId); Assert.That(parent.TemplateId, Is.EqualTo("core.zone.map_route")); Assert.That(parent.LocalZoneId, Is.EqualTo(route.RouteId));
            var region = state.ItemZones.Zones.Single(z => z.ZoneId == parent.ParentId); Assert.That(region.TemplateId, Is.EqualTo("core.zone.map_region"));
            Assert.That(state.ItemZones.Zones.Single(z => z.ZoneId == region.ParentId).TemplateId, Is.EqualTo("core.zone.map"));
            var placement = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == LuaDomainEffectTypeIds.PlaceRoad);
            Assert.That(Text(placement.NormalizedResult, "roadItemId"), Is.EqualTo(roadId)); Assert.That(Text(placement.NormalizedResult, "routeId"), Is.EqualTo(route.RouteId));
            state = Restore(state); Assert.That(state.ItemZones.Items.Single(i => i.ItemId == roadId).ZoneId, Is.EqualTo(zoneId));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1).ItemZones.Items.Any(i => i.ItemId == roadId && i.ZoneId == zoneId), Is.True);
            Assert.Throws<InvalidOperationException>(() => state.Map.RoadRouteIds.Add(route.RouteId));
            int count = state.ItemZones.Items.Count(i => i.TypeId == "core.road_token");
            var duplicate = Run(state, EffectSpec.Create(LuaDomainEffectTypeIds.PlaceRoad, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("2"), "routeId", NormalizedValue.CreateString(route.RouteId)), 2));
            Assert.That(duplicate.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(state.ItemZones.Items.Count(i => i.TypeId == "core.road_token"), Is.EqualTo(count));
        }
        [TestCase("lua/characters/mlynar.tactic.lua", 1)]
        [TestCase("lua/characters/operator.kaltsit.strategy.lua", 2)]
        [TestCase("lua/characters/infected.character.flowing_shape_gamma.tactic.lua", 1)]
        [TestCase("lua/city_styles/city_style_large_enterprise_park.special.lua", 2)]
        [TestCase("lua/events/enterprise.event.cave_ruins.resolve.lua", 1)]
        public void RealUpgradeSourcesShareEnterpriseRequestAndKeepLevelOrPayment(string script, int levels)
        {
            var state = State(); string target = Item(state, "enterprise.fussen_bank").ItemId;
            int before = new EnterpriseStateQuery().GetLevel(state, 1, target), other = new EnterpriseStateQuery().GetLevel(state, 2, target), iron = state.FindPlayer(1).Resources.Iron;
            Invoke(state, script, Object("optionIndex", NormalizedValue.CreateInteger(2)));
            Assert.That(CurrentRequest(state).InteractionTypeId, Is.EqualTo("enterprise.upgrade"));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(iron - (script.Contains("cave_ruins") ? 1 : 0)));
            state = Restore(state); Answer(state, target); Drain(state);
            Assert.That(new EnterpriseStateQuery().GetLevel(state, 1, target), Is.EqualTo(Math.Min(5, before + levels)));
            Assert.That(new EnterpriseStateQuery().GetLevel(state, 2, target), Is.EqualTo(other));
        }
        [Test] public void CaveRuinsWithoutIronDoesNotCreateUpgradeChoiceOrUpgrade()
        {
            var state = State(); state.FindPlayer(1).Resources.Iron = 0; string target = Item(state, "enterprise.fussen_bank").ItemId;
            int before = new EnterpriseStateQuery().GetLevel(state, 1, target);
            Invoke(state, "lua/events/enterprise.event.cave_ruins.resolve.lua", Object("optionIndex", NormalizedValue.CreateInteger(2)));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(new EnterpriseStateQuery().GetLevel(state, 1, target), Is.EqualTo(before)); Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero); Restore(state);
        }
        [TestCase("capacity")][TestCase("type")][TestCase("influence_zone")]
        public void RoadRegionContractRejectsOverCapacityWrongTypeOrInfluenceZoneOnRecovery(string mutation)
        {
            var state = State(); Invoke(state, "lua/characters/mountain.strategy.lua"); Answer(state, CurrentRequest(state).CandidateIds[0]);
            var archive = HostRecoveryService.CreateArchive(state); var saved = archive.Snapshot.State;
            var road = saved.ItemZones.Items.Single(i => i.TypeId == "core.road_token");
            if (mutation == "capacity")
            {
                var extra = GameStateCloneService.DeepClone(road); extra.ItemId += ":duplicate"; saved.ItemZones.Items.Add(extra);
            }
            else if (mutation == "type") { road.TypeId = "core.player_marker"; road.PhysicalKind = ItemPhysicalKind.Cube; }
            else
            {
                var parent = saved.ItemZones.Zones.Single(z => z.ZoneId == road.ZoneId).ParentId;
                var influence = saved.ItemZones.Zones.Single(z => z.ParentId == parent && z.TemplateId == "core.zone.influence_slots");
                road.ZoneId = influence.ZoneId; road.SlotId = influence.LegalSlotIds[0];
            }
            Assert.That(new ItemZoneQuery().TryValidate(saved, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo(mutation == "capacity" ? "capacity_exceeded" : "acceptedItemKinds_acceptedTypeIds_rejected"));
            Assert.That(HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), registry).Succeeded, Is.False);
        }
        [Test] public void MountainEmptyRouteStillScoresExecutorAndRoadDoesNotChangeLegacyMapping()
        {
            var state = State(); Invoke(state, "lua/characters/mountain.strategy.lua"); Answer(state, CurrentRequest(state).CandidateIds[0]);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(state.FindPlayer(2).Score, Is.Zero);
            state.ContentDefinitions.Components.Clear(); Run(state, EffectSpec.Create(LuaDomainEffectTypeIds.PlaceRoad, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "routeId", NormalizedValue.CreateString(new InfluenceService(new MapQueryService(map)).GetRoadCandidateRouteIds(state)[0])), 1));
            Assert.That(state.ItemZones.Items.Any(i => i.DefinitionId == "core.component.road"), Is.True);
        }

        [Test] public void PassengerSubstitutionUsesNormalBuildPaymentAndDiscardsEmptyCarrierBeforePlacement()
        {
            var state = State(); FillSupply(state); LoadMarkers(state, "operator.passenger", 2);
            var item = state.ItemZones.Items.First(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot") &&
                FacilityCardDatabase.Get(state, i.DefinitionId).ResourceCost.Originium + FacilityCardDatabase.Get(state, i.DefinitionId).ResourceCost.OriginiumShard + FacilityCardDatabase.Get(state, i.DefinitionId).ResourceCost.Iron >= 2);
            var cost = new FacilityBuildCostService().GetEffectiveResourceCost(state, state.FindPlayer(1), FacilityCardDatabase.Get(state, item.DefinitionId));
            var allocation = new int[3]; int remaining = 2; var types = new[] { ResourceType.Originium, ResourceType.OriginiumShard, ResourceType.Iron };
            state.FindPlayer(1).Resources = GameStateCloneService.DeepClone(cost); state.FindPlayer(1).Resources.GoldVoucher = 0;
            for (int i = 0; i < 3; i++) { allocation[i] = Math.Min(remaining, cost.Get(types[i])); remaining -= allocation[i]; state.FindPlayer(1).Resources.Set(types[i], cost.Get(types[i]) - allocation[i]); }
            Assert.That(BuildFacilityService.CreateForEffectTree().ValidatePaymentMode(state, 1, item.ItemId, "resources").IsValid, Is.True);
            Run(state, EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, Object("sourceZone", NormalizedValue.CreateString("supply")), 1)); Answer(state, item.ItemId); Answer(state, "resources");
            Assert.That(CurrentRequest(state).PromptKey, Is.EqualTo("resource.payment.substitution")); state = Restore(state);
            Answer(state, CurrentRequest(state).CandidateIds.Single(id => id.EndsWith("|" + string.Join("|", allocation))));
            Assert.That(Item(state, "operator.passenger").ZoneId, Is.EqualTo(FindZone(state, "discard", 1).ZoneId));
            state = Restore(state); Drain(state); Assert.That(state.Map.Facilities.Any(f => f.ContentInstanceId == item.ItemId), Is.True);
            Assert.That(state.FindPlayer(1).Resources.Originium + state.FindPlayer(1).Resources.OriginiumShard + state.FindPlayer(1).Resources.Iron, Is.Zero);
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved), Is.False);
        }
        [Test] public void DorothyGenericCubeCaptureKeepsCompanyTypeAndReturnsOriginalSupplyOnExit()
        {
            var state = State(); LoadMarkers(state, "operator.dorothy", 0);
            registry.Register(new EffectRegistration("content.test.company_cube", c => {
                Assert.That(new ItemZoneStateFactory().TryCreateGrantedItem(c.State, c.Node.EffectId, "test_company", "test.generic_company_cube", "infected.company_marker", FindZone(c.State, "company_markers").ZoneId,
                    new ItemSubjectRef { Kind = ItemSubjectKind.Company, CompanyId = "stonehoove_security" }, out string granted, out string reason), Is.True, reason);
                return EffectStepResult.Completed(NormalizedValue.CreateString(granted)); }));
            string id = Run(state, EffectSpec.Create("content.test.company_cube", playerId: 1)).NormalizedResult.StringValue;
            var location = map.Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount > 0);
            var parent = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == location.LocationId);
            var destination = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.influence_slots" && z.ParentId == parent.ZoneId);
            var cube = state.ItemZones.Items.Single(i => i.ItemId == id); SetFixturePosition(state, cube, destination.ZoneId); cube.SlotId = InfluenceService.GetLocationSlotId(location.LocationId, 0); Valid(state);
            Run(state, InfluenceEffectSpecFactory.RemoveInfluence(2, id)); state = Restore(state); Answer(state, "store");
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId, Is.EqualTo(CarrierZone(state, "operator.dorothy").ZoneId));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == id).OwnerSubject.Kind, Is.EqualTo(ItemSubjectKind.Company));
            var card = Item(state, "operator.dorothy"); Run(state, MoveSpec(1, new[] { card.ItemId }, card.ZoneId, FindZone(state, "discard", 1).ZoneId, "unordered", 1, "core.character_card_transfer"));
            cube = state.ItemZones.Items.Single(i => i.ItemId == id); Assert.That(cube.TypeId, Is.EqualTo("infected.company_marker")); Assert.That(cube.ZoneId, Is.EqualTo(FindZone(state, "company_markers").ZoneId));
        }
        [Test] public void StoredFacilityIsExcludedFromSupplyOnlySkillsAndOtherPlayersCannotBuildIt()
        {
            var state = State(); FillSupply(state); StartCard(state, "operator.closure", "tactic"); string held = CurrentRequest(state).CandidateIds.First(id => id != "choice.skip"); Answer(state, held); Answer(state, "choice.skip");
            Assert.That(BuildFacilityService.CreateForEffectTree().ValidateFacility(state, 1, held).IsValid, Is.True);
            Assert.That(BuildFacilityService.CreateForEffectTree().ValidateFacility(state, 2, held).IsValid, Is.False);
            Run(state, EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, Object("sourceZone", NormalizedValue.CreateString("supply"), "paymentMode", NormalizedValue.CreateString("free"), "includeStoredFacilities", NormalizedValue.CreateBoolean(false)), 1));
            Assert.That(CurrentRequest(state).CandidateIds, Does.Not.Contain(held)); Drain(state);
            var result = Run(state, FacilityBuildEffectSpecFactory.Build(2, held, -1, "free", false, "test.foreign", false));
            Assert.That(result.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(state.ItemZones.Items.Single(i => i.ItemId == held).ZoneId, Is.EqualTo(CarrierZone(state, "operator.closure").ZoneId));
        }

        [Test] public void DorothyStrategyDoesNotRewardWhenResponseRemovesSecondSelectedInstance()
        {
            var state = State(); var location = map.Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount >= 2);
            Place(state, 1, InfluenceService.GetLocationSlotId(location.LocationId, 0)); Place(state, 1, InfluenceService.GetLocationSlotId(location.LocationId, 1));
            var ids = state.Map.Influences.Select(i => i.InfluenceId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("content.test.departure_response", InfluenceEventTypeIds.InfluenceRemoved, "*", "content.test.departure_response", c =>
                Value(c.Event.Payload, "removedItemIds")?.Items.Any(v => v.StringValue == ids[0]) == true ? new List<EffectSpec> { InfluenceEffectSpecFactory.RemoveInfluence(1, ids[1]) } : new List<EffectSpec>(), EffectHandlerRole.Observer));
            Invoke(state, "lua/characters/operator.dorothy.strategy.lua"); Answer(state, ids); Drain(state);
            Assert.That(state.Map.Influences, Is.Empty); Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluencePlaced), Is.EqualTo(2));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.FailureReason == "incomplete_influence_payment"), Is.True);
        }

        private EffectSpec EventSpec(string eventType)
        {
            const string type = "content.test.event";
            if (!registry.TryGet(type, out _)) registry.Register(new EffectRegistration(type, context => EffectStepResult.Completed().AddEvent(new EffectEventRequest {
                EventType = Text(context.Node.NormalizedArguments, "eventType"), PlayerId = context.Node.PlayerId, Payload = Object()
            })));
            return EffectSpec.Create(type, Object("eventType", NormalizedValue.CreateString(eventType)), 1);
        }
    }
}
