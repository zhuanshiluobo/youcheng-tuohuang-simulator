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
using YC.Domain.Items;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>只在隔离运行内容副本中装配测试声明；不计正常游戏、正式企业家或像素验收。</summary>
    public sealed class UsageTrackAreaColorTests
    {
        private const string Card = "infected.award.enterprise_special", Track = "qy003.fixture.track", Quick = "qy003.fixture.quick", Special = "qy003.fixture.special";
        private ExternalContentPack pack;
        private string fixtureRoot;
        private EffectRegistry registry;
        private IsolatedTestArtifacts artifacts;
        [SetUp] public void LoadFrozenJsonAndLua()
        {
            artifacts = IsolatedTestArtifacts.BeginCase();
            string source = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var card = JObject.Parse(File.ReadAllText(Path.Combine(source, "award/" + Card + ".json")));
            var track = new UsageTrackDeclaration { TrackId = Track, CardZoneLocalId = "tactic", InitialSlotId = "unused", Slots = new List<string> { "unused", "used" },
                ActivationTransitions = new List<UsageTrackTransition> { Transition("unused", "used") }, ResetTransitions = new List<UsageTrackTransition> { Transition("used", "unused") } };
            var zone = new CardZoneDefinition { LocalZoneId = "tactic", ZoneTemplateId = "core.zone.board_marker_track", Slots = track.Slots,
                AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" }, OrderingKind = ZoneOrderingKind.Track,
                CapacityPolicy = "bounded", Capacity = 1, VisibilityPolicyId = ItemZoneVisibility.Public, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
            var sourceDeclaration = new AbilitySourceDeclaration { AbilityId = "qy003.fixture.ability", Kind = "quick", HandlerRef = "qy003.fixture.body", DeclarationVersion = "1.0.0", AuthorizationPolicy = "holder", PresentationRef = Card,
                EffectiveZones = new List<string> { "core.zone.player_rewards" }, Actions = new List<AbilityActionDeclaration> {
                    new AbilityActionDeclaration { ActionId = Quick, EntryKind = "quick", BodyHandlerRef = "qy003.fixture.body", UsageTrackId = Track, PresentationRef = Card },
                    new AbilityActionDeclaration { ActionId = Special, EntryKind = "special", BodyHandlerRef = "qy003.fixture.body", UsageTrackId = Track, PresentationRef = Card } } };
            var camel = new JsonSerializerSettings { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() };
            JToken Json(object value) => JToken.Parse(JsonConvert.SerializeObject(value, camel));
            card["usageTracks"] = Json(new[] { track }); card["cardZones"] = Json(new[] { zone }); card["abilitySources"] = Json(new[] { sourceDeclaration });
            card["abilityHandlers"] = Json(new[] { new ExternalAbilityHandler { HandlerId = "qy003.fixture.body", Script = "lua/qy003.fixture.lua", Version = "1.0.0" } });
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(source, "pack.json"))); manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected");
            if (!((JArray)manifest["definitions"]).Values<string>().Contains("award/" + Card + ".json")) ((JArray)manifest["definitions"]).Add("award/" + Card + ".json");
            // 003的通用颜色/混合供应隔离夹具明确许可测试帮派；正式标准模式继续过滤企业家组件。
            var fixtureGang = JObject.Parse(File.ReadAllText(Path.Combine(source, "component/entrepreneur.component.gang_marker.json")));
            fixtureGang["applicableModes"] = new JArray("standard", "entrepreneur");
            fixtureRoot = artifacts.CopyContent(source, "使用轨道基础内容", new Dictionary<string, string> { ["pack.json"] = manifest.ToString(), ["award/" + Card + ".json"] = card.ToString(),
                ["component/entrepreneur.component.gang_marker.json"] = fixtureGang.ToString(),
                ["lua/qy003.fixture.lua"] = "return function(ctx) return { Effect.GainResource({recipient=ctx.playerId,resourceType='GoldVoucher',amount=5}) } end" });
            pack = ExternalContentPack.Load(fixtureRoot);
            artifacts.RecordContent("使用轨道装载内容", fixtureRoot, pack.ContentHash);
        }
        [TearDown] public void RecordActualTestResult() => artifacts?.Finish();
        private static UsageTrackTransition Transition(string from, string to) => new UsageTrackTransition { FromSlotId = from, ToSlotId = to };
        private GameState State(int players = 2, int markers = 3, bool mixed = false, bool finite = false)
        {
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); CharacterCoverEffectExecutor.Register(registry);
            CharacterCardLuaCatalog.Register(registry, pack); CityStyleLuaCatalog.Register(registry, pack); ContentLifecycleLuaCatalog.Register(registry, pack); AbilitySourceLuaCatalog.Register(registry, pack);
            var map = StaticMapDefinitions.Resolve(players == 4 ? StaticMapDefinitions.FourPlayerMapId : StaticMapDefinitions.ThreePlayerMapId);
            var mapQuery = new MapQueryService(map); var influenceService = new InfluenceService(mapQuery);
            InfluenceEffectExecutor.Register(registry, influenceService); ContentPrimitiveEffectExecutor.Register(registry, mapQuery, influenceService);
            var source = new GameState { GameId = "qy003.isolated." + players, MapId = map.MapId, ContentPackHash = pack.ContentHash,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = players, ContentPackHash = pack.ContentHash },
                ContentDefinitions = new GameContentDefinitions { UsageTracks = pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(), AbilitySources = registry.AbilityDeclarations.ToList(),
                    Components = pack.ActiveDefinitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition { DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList(),
                    Characters = pack.CreateCharacters().ToList(), Facilities = pack.CreateFacilities().ToList(), Awards = ContentLifecycleLuaCatalog.CreateAwards(pack), CityStyles = pack.CreateCityStyles().ToList(), SpecialActions = pack.CreateSpecialActions().ToList() },
                Players = Enumerable.Range(1, players).Select(p => new PlayerState { PlayerId = p, Name = "玩家" + p, Color = (PlayerColor)(p - 1), Resources = new ResourceSet { GoldVoucher = 20 } }).ToList() };
            source.LaunchPlayerOrder = Enumerable.Range(1, players).ToList();
            var entries = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "card", DefinitionId = Card, TypeId = "core.award_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "rewards" },
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = markers, PerPlayer = true, InitialZoneRole = "supply" } };
            if (finite)
            {
                entries.Add(new ItemRecipeEntry { EntryId = "style", DefinitionId = "city_style_source_stone_industrial_hub", TypeId = "core.board_card", Quantity = 1, InitialZoneRole = "city_styles" });
                entries.AddRange(new[] { (0, "blue"), (3, "red"), (4, "blue"), (6, "yellow"), (7, "yellow"), (8, "red") }.Select(cell => new ItemRecipeEntry {
                    EntryId = "style.facility." + cell.Item1, DefinitionId = pack.CreateFacilities().First(f => f.Color == cell.Item2).FacilityId, TypeId = "core.facility_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "city_cell_" + cell.Item1 }));
                entries.AddRange(new[] { "blue", "red", "blue", "yellow", "yellow", "red" }.Select((color, index) => new ItemRecipeEntry { EntryId = "fresh.facility." + index, DefinitionId = pack.CreateFacilities().First(f => f.Color == color).FacilityId, TypeId = "core.facility_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "reserve" }));
            }
            if (mixed)
            {
                entries.Add(new ItemRecipeEntry { EntryId = "gang", DefinitionId = "entrepreneur.component.gang_marker", TypeId = "entrepreneur.gang_marker", Quantity = 4, PerPlayer = true, InitialZoneRole = "supply" });
                entries.Add(new ItemRecipeEntry { EntryId = "tower", DefinitionId = pack.CreateFacilities().First().FacilityId, TypeId = "core.facility_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "supply" });
            }
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, entries, pack.ActiveDefinitions.Where(d => d.CardZones.Count > 0).Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = d.CardZones }).ToList());
            state.Round = 1; state.Phase = GamePhase.ActionRound1; state.ActionRound = 1; state.CurrentPlayerId = 1; state.StartPlayerId = 1;
            new MainActionBudgetService().ResetForNewActionTurn(state, 1); return state;
        }
        private static string CardId(GameState state, int player = 1) => state.ItemZones.Items.Single(i => i.DefinitionId == Card && i.OwnerSubject.PlayerId == player).ItemId;
        private static void Run(EffectTreeExecutor engine)
        { var result = engine.RunUntilQuiescent(); Assert.That(result.Faulted, Is.False, engine.State.EffectRuntime.LastFaultMessage); }
        private EffectTreeExecutor Initialize(GameState state, int player = 1)
        { var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(UsageTracks.CreateInitialization(player, CardId(state, player), Track)); Run(engine); Assert.That(engine.GetNode(root).Status == EffectNodeStatus.Completed || engine.GetNode(root).FailureReason == "usage_track_already_initialized", Is.True, engine.GetNode(root).FailureReason); return engine; }
        private GameState Restore(GameState state, string label)
        {
            var result = artifacts.Recover(state, label, registry); Assert.That(result.Succeeded, Is.True, result.Diagnostic); return result.State;
        }
        private string DeclareFinite(GameState state, int player)
        {
            var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(CityStyleDeclarationEffectExecutor.Create(player, "city_style_source_stone_industrial_hub", new[] { 0, 3, 4, 6, 7, 8 })); Run(engine);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed), engine.GetNode(root).FailureReason);
            return state.FindPlayer(player).DeclaredCityStyles.Last().InfluenceMarkerId;
        }
        private EffectTreeExecutor BeginRealCleanup(GameState state, out RoundMainChain chain, out RoundExecutionService rounds)
        {
            var existing = state.EffectRuntime.MainNodes.Where(n => n.RoundNumber == state.Round && n.RoundExecutionId == state.EffectRuntime.CurrentRoundExecutionId).OrderBy(n => n.MainlineIndex).ToList();
            int start = existing.Count > 0 ? existing[0].StartPlayerId : 1; var order = new TurnOrderService().GetTurnOrder(state, start).ToList();
            chain = existing.Count > 0 ? new RoundMainChain { RoundExecutionId = state.EffectRuntime.CurrentRoundExecutionId, RoundNumber = state.Round, StartPlayerId = start, PlayerOrderSnapshot = order, FirstNodeId = existing[0].NodeId, Nodes = existing } : new RoundMainChainBuilder().Build(state.GameId, "", state.Round, order, start);
            if (existing.Count == 0) state.EffectRuntime.MainNodes.AddRange(chain.Nodes);
            var first = chain.Nodes.First(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow);
            foreach (var previous in chain.Nodes.Where(n => n.MainlineIndex < first.MainlineIndex)) previous.Status = EffectNodeStatus.Completed;
            state.EffectRuntime.FirstMainNodeId = chain.FirstNodeId; state.EffectRuntime.ActiveMainNodeId = first.NodeId; state.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId;
            state.EffectRuntime.CurrentRoundNumber = state.Round; state.EffectRuntime.CurrentRoundStartPlayerId = start; state.EffectRuntime.PlayerOrderSnapshot = order;
            rounds = new RoundExecutionService(registry); var engine = rounds.CreateCommandExecutor(state); return engine;
        }
        private static void StartMain(EffectTreeExecutor engine, MainlineNodeRuntimeState main)
        {
            engine.State.EffectRuntime.ActiveMainNodeId = main.NodeId; main.Status = EffectNodeStatus.Running;
            main.ExecutionEffectId = engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId, Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), main.PlayerId));
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)] public void RealAllPlayerCleanupResetsOneTimeAndRestoresBetweenBindings(int players)
        {
            var state = State(players, finite: true); Initialize(state); string marker = DeclareFinite(state, 1);
            var engine = new EffectTreeExecutor(state, registry); var quick = registry.QueryActions(state, 1).Single(o => o.ActionId == Quick); engine.CreateRoot(AbilityActionExecutor.Create(quick, "qy003.once", Object())); Run(engine);
            var style = registry.QueryActions(state, 1).Single(o => o.ActionId == YC.Domain.SpecialActions.SpecialActionDatabase.SourceStoneIndustrialHub); engine.CreateRoot(AbilityActionExecutor.Create(style, "qy003.finite", Object())); Run(engine);
            engine = BeginRealCleanup(state, out var chain, out var rounds);
            foreach (var cleanup in chain.Nodes.Where(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow))
            {
                StartMain(engine, cleanup); Run(engine); Assert.That(state.ItemZones.Items.Single(i => i.ItemId == marker).SlotId, Is.EqualTo("used_from_2"));
            }
            StartMain(engine, chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.RoundEnded));
            for (int i = 0; i < 100 && state.EffectRuntime.UsageTrackResets.Sum(r => r.ProcessedBindingIds.Count) < 1; i++) Assert.That(engine.Advance(), Is.True, engine.LastDiagnostic);
            Assert.That(state.EffectRuntime.UsageTrackResets.Sum(r => r.ProcessedBindingIds.Count), Is.EqualTo(1));
            state = Restore(state, players + "人全体复位中途"); rounds = new RoundExecutionService(registry); engine = rounds.CreateCommandExecutor(state);
            for (int i = 0; i < 100 && !state.EffectRuntime.UsageTrackResets.Any(r => r.Phase == "completed"); i++) Assert.That(engine.Advance(), Is.True, engine.LastDiagnostic);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == marker).SlotId, Is.EqualTo("1"));
            Assert.That(state.EffectRuntime.EffectNodes.SelectMany(n => n.PositionChanges).Count(c => c.ItemId == marker && c.From.SlotId == "used_from_2" && c.To.SlotId == "1"), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.UsageTrackResets.Single().Phase, Is.EqualTo("completed")); Restore(state, players + "人全体复位完成");
            if (players == 2)
            {
                Run(engine); Assert.That(state.Round, Is.EqualTo(2));
                // 隔离规则定位跳过无关第二回合正文；有限次数仍通过真实激活和真实全体收尾转换。
                state.Phase = GamePhase.ActionRound1; state.ActionRound = 1; state.CurrentPlayerId = 1; new MainActionBudgetService().ResetForNewActionTurn(state, 1);
                Initialize(state, 2); state = Restore(state, "后续回合新增轨道不改旧复位集合");
                engine = new EffectTreeExecutor(state, registry); var nextStyle = registry.QueryActions(state, 1).Single(o => o.ActionId == YC.Domain.SpecialActions.SpecialActionDatabase.SourceStoneIndustrialHub);
                Assert.That(nextStyle.Enabled, Is.True, nextStyle.DisabledReason); engine.CreateRoot(AbilityActionExecutor.Create(nextStyle, "qy003.finite.second", Object())); Run(engine);
                Assert.That(state.ItemZones.Items.Single(i => i.ItemId == marker).SlotId, Is.EqualTo("used_from_1"));
                engine = BeginRealCleanup(state, out chain, out rounds);
                foreach (var cleanup in chain.Nodes.Where(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow)) { StartMain(engine, cleanup); Run(engine); }
                StartMain(engine, chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.RoundEnded));
                for (int i = 0; i < 100 && !state.EffectRuntime.UsageTrackResets.Any(r => r.RoundNumber == 2 && r.Phase == "completed"); i++) Assert.That(engine.Advance(), Is.True, engine.LastDiagnostic);
                Assert.That(state.ItemZones.Items.Single(i => i.ItemId == marker).SlotId, Is.EqualTo("0")); Restore(state, "有限次数两轮耗尽不补满");
            }
        }
        [Test] public void OneSharedTrackUsesARealMarkerOnceAndKeepsZeroInventoryAbilityVisible()
        {
            var state = State(markers: 1); Initialize(state); string item = CardId(state);
            Assert.That(PlayerAreaPolicy.Supply(state, 1, "core.player_marker"), Is.Empty); Assert.That(registry.QueryActions(state, 1).Count(o => o.SourceRef.ItemId == item), Is.EqualTo(2));
            var options = registry.QueryActions(state, 1); Assert.That(options.All(o => o.Enabled), Is.True); Assert.That(options.Select(o => o.TrackBindingId).Distinct().Count(), Is.EqualTo(1));
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(AbilityActionExecutor.Create(options.Single(o => o.ActionId == Quick), "qy003.quick", Object())); Run(engine);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(25)); Assert.That(registry.QueryActions(state, 1).All(o => !o.Enabled), Is.True);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == state.EffectRuntime.UsageTrackBindings.Single().MarkerItemId).SlotId, Is.EqualTo("used"));
            int count = state.ItemZones.Items.Count; Initialize(state); Assert.That(state.ItemZones.Items.Count, Is.EqualTo(count)); Assert.That(state.EffectRuntime.UsageTrackBindings.Count, Is.EqualTo(1));
            state.FindPlayer(1).Name = "改名后"; state = Restore(state, "真实激活与改名后"); Assert.That(registry.QueryActions(state, 1).All(o => !o.Enabled), Is.True);
            Assert.That(state.EffectRuntime.UsageGroupReceipts.Count, Is.EqualTo(1));
        }
        [Test] public void SetupRoundZeroInitializationIsOnceAndCanRestore()
        {
            var state = State(); state.Round = 0; state.Phase = GamePhase.Setup; Initialize(state); state = Restore(state, "设置期零回合初始化");
            Assert.That(state.EffectRuntime.UsageTrackBindings.Single().InitializedRoundNumber, Is.Zero); Initialize(state); Assert.That(state.EffectRuntime.UsageTrackBindings.Count, Is.EqualTo(1));
        }
        [Test] public void EntrepreneurPendingAndUnselectedRoundCardsDoNotProvideAbilities()
        {
            var state = State(); var item = state.ItemZones.Items.Single(i => i.ItemId == CardId(state)); item.TypeId = "entrepreneur.ability_card";
            var declaration = GameStateCloneService.DeepClone(state.ContentDefinitions.AbilitySources.Single(d => d.DefinitionId == Card)); declaration.EffectiveZones = new List<string> { "core.zone.entrepreneur_pending", "core.zone.entrepreneur_active" };
            item.ZoneId = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.entrepreneur_pending" && z.OwnerPlayerId == 1).ZoneId;
            Assert.That(AbilitySourceService.QuerySources(state, new[] { declaration }, 1), Is.Empty);
            item.ZoneId = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.entrepreneur_active" && z.OwnerPlayerId == 1).ZoneId;
            Assert.That(AbilitySourceService.QuerySources(state, new[] { declaration }, 1).Count, Is.EqualTo(1));
            item.TypeId = "entrepreneur.round_card"; Assert.That(AbilitySourceService.QuerySources(state, new[] { declaration }, 1), Is.Empty);
        }
        [Test] public void ActivationAndBodyRestoreWithoutSecondMoveOrReward()
        {
            var state = State(); Initialize(state); var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(AbilityActionExecutor.Create(registry.QueryActions(state, 1).Single(o => o.ActionId == Quick), "qy003.activation", Object()));
            for (int i = 0; i < 30 && !state.EffectRuntime.AbilityActionInvocations.Any(v => v.ActivationCommitted); i++) Assert.That(engine.Advance(), Is.True, engine.LastDiagnostic);
            state = Restore(state, "激活后正文前");
            state = Restore(state, "激活后正文前"); // 同一标签再次恢复也必须保存新文件，首次现场保留。
            engine = new EffectTreeExecutor(state, registry); Run(engine);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(25)); Assert.That(state.EffectRuntime.EffectNodes.SelectMany(n => n.PositionChanges).Count(c => c.From.SlotId == "unused" && c.To.SlotId == "used"), Is.EqualTo(1));
            Restore(state, "正文完成后");
        }
        [Test] public void MixedSupplyCountsTypesSeparatelyAndDoesNotAcceptUnregisteredCards()
        {
            var state = State(mixed: true); Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(3));
            Assert.That(PlayerAreaPolicy.Supply(state, 1, "entrepreneur.gang_marker").Count, Is.EqualTo(4)); Assert.That(PlayerAreaPolicy.Supply(state, 1, "core.facility_card").Count, Is.EqualTo(1));
            var supply = state.ItemZones.Zones.Single(z => z.LocalZoneId == "supply" && z.OwnerPlayerId == 1);
            Assert.That(new ItemZoneQuery().Accepts(supply, state.ItemZones.Items.Single(i => i.ItemId == CardId(state))), Is.False);
            var nonMixed = State(); var zone = nonMixed.ItemZones.Zones.Single(z => z.LocalZoneId == "supply" && z.OwnerPlayerId == 1);
            Assert.That(new ItemZoneQuery().Accepts(zone, state.ItemZones.Items.First(i => i.TypeId == "entrepreneur.gang_marker")), Is.False);
            Restore(state, "混合供应不变实例");
        }
        [Test] public void MarkerDepartureDisablesBothEntriesAndCannotInitializeAnotherPlayer()
        {
            var state = State(finite: true); string marker = DeclareFinite(state, 1);
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(EffectSpec.Create(ContentLifecycleEffectExecutor.ReturnMarkerTypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1")), 1)); Run(engine);
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open"); Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString(marker), out string reason), Is.True, reason); Run(engine);
            // 隔离夹具换为另一组六张真实实例，使第二次宣告仍满足“不同已用设施”条件；不计正常建设验收。
            foreach (var facility in state.ItemZones.Items.Where(i => i.OwnerSubject.PlayerId == 1 && state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.city_facility_cell")).ToList())
            {
                var fresh = state.ItemZones.Items.First(i => i.OwnerSubject.PlayerId == 1 && i.DefinitionId == facility.DefinitionId && !state.FindPlayer(1).DeclaredCityStyles[0].UsedFacilityIds.Contains(i.ItemId) && state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.box_reserve"));
                string position = facility.ZoneId; facility.ZoneId = fresh.ZoneId; fresh.ZoneId = position;
            }
            string replacement = DeclareFinite(state, 1); Assert.That(state.ItemZones.Items.Single(i => i.ItemId == replacement).SlotId, Is.EqualTo("declared"));
            Assert.That(registry.QueryActions(state, 1).Single(o => o.ActionId == YC.Domain.SpecialActions.SpecialActionDatabase.SourceStoneIndustrialHub).DisabledReason, Is.EqualTo("usage_marker_departed"));
            Assert.That(state.EffectRuntime.UsageTrackBindings.Count, Is.EqualTo(1)); Restore(state, "回收最后标记重宣告只记录");
        }
        [Test] public void GenericTransferCannotMoveHostManagedUsageMarker()
        {
            var state = State(); Initialize(state); var binding = state.EffectRuntime.UsageTrackBindings.Single(); var marker = state.ItemZones.Items.Single(i => i.ItemId == binding.MarkerItemId);
            var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(MoveSpec(1, new[] { marker.ItemId }, marker.ZoneId, marker.OriginalSupplyZoneId, "unordered", 1, "core.player_marker_storage")); Run(engine);
            Assert.That(engine.GetNode(root).FailureReason, Is.EqualTo("usage_track_host_managed")); Assert.That(state.ItemZones.Items.Single(i => i.ItemId == marker.ItemId).SlotId, Is.EqualTo("unused"));
        }
        [Test] public void EmptySupplyInitializationUsesFormalOwnedInfluenceRemovalAndRestoresBeforeAnswer()
        {
            var state = State(markers: 1); var location = state.ItemZones.Zones.First(z => z.TemplateId == "core.zone.map_location"); state.Map.OpenLocationIds.Add(location.LocalZoneId);
            var slots = state.ItemZones.Zones.First(z => z.TemplateId == "core.zone.influence_slots" && state.ItemZones.Zones.Any(p => p.ZoneId == z.ParentId && p.TemplateId == "core.zone.map_route"));
            string marker = PlayerAreaPolicy.Supply(state, 1, "core.player_marker").Single().ItemId;
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1, slots.LegalSlotIds[0])); Run(engine);
            Assert.That(PlayerAreaPolicy.Supply(state, 1, "core.player_marker"), Is.Empty);
            engine.CreateRoot(UsageTracks.CreateInitialization(1, CardId(state), Track)); Run(engine);
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open"); Assert.That(request.CandidateIds, Is.EqualTo(new[] { marker })); Assert.That(request.AnsweringPlayerId, Is.EqualTo(1));
            state = Restore(state, "供应不足合法取回待答"); engine = new EffectTreeExecutor(state, registry); request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString(marker), out string reason), Is.True, reason); Run(engine);
            Assert.That(state.EffectRuntime.UsageTrackBindings.Single().MarkerItemId, Is.EqualTo(marker)); Assert.That(state.ItemZones.Items.Count(i => i.ItemId == marker), Is.EqualTo(1));
            Restore(state, "供应不足取回后初始化完成");
        }
        [Test] public void TypedLuaPlacementUsesGangAtZeroOrdinarySupplyAndReturnsTheSameInstance()
        {
            var state = State(markers: 1, mixed: true); Initialize(state);
            var location = state.ItemZones.Zones.First(z => z.TemplateId == "core.zone.map_location"); state.Map.OpenLocationIds.Add(location.LocalZoneId);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.Zero);
            string lua = "return function(ctx) return {Effect.PlaceInfluence({executingPlayer=ctx.playerId,ownerSubject=ctx.playerId,influenceSource='player_supply',itemTypeId='entrepreneur.gang_marker'})} end";
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("qy003.typed", "qy003.typed", "qy003.typed", "1.0.0", lua, LuaContentHasher.ComputeSha256(lua)), new LuaInvocationContext("event.map", "event.test", "1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var failure), Is.True, failure?.Diagnostic);
            Assert.That(Text(specs[0].NormalizedArguments, "itemTypeId"), Is.EqualTo("entrepreneur.gang_marker"));
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(specs[0]); Run(engine);
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open"); Assert.That(request.CandidateIds.Count, Is.GreaterThan(0));
            state = Restore(state, "普通库存零的帮派放置候选"); engine = new EffectTreeExecutor(state, registry); request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString(request.CandidateIds[0]), out string reason), Is.True, reason); Run(engine);
            var placed = state.ItemZones.Items.Single(i => new ItemZoneQuery().IsInfluence(state, i.ItemId)); Assert.That(placed.TypeId, Is.EqualTo("entrepreneur.gang_marker")); Assert.That(CubeAppearance.Resolve(state, placed.ItemId).Rgb, Is.EqualTo("A020F0"));
            engine.CreateRoot(InfluenceEffectSpecFactory.RemoveInfluence(1, placed.ItemId)); Run(engine);
            Assert.That(PlayerAreaPolicy.Supply(state, 1, "entrepreneur.gang_marker").Count, Is.EqualTo(4)); Assert.That(state.FindPlayer(1).InfluenceSupply, Is.Zero); Restore(state, "帮派真实离场返原供应");
        }
        [TestCase("owner_player", "", "", true)] [TestCase("owner_player", "Purple", "A020F0", false)] [TestCase("fixed", "Orange", "d97000", true)]
        [TestCase("fixed", "", "A020F0", false)] [TestCase("fixed", "Purple", "#A020F0", false)] [TestCase("fixed", "Purple", "FFFFFG", false)] [TestCase("viewer", "", "", false)]
        public void ColorDeclarationIsExplicitAndNormalized(string mode, string name, string rgb, bool expected)
        {
            bool valid = CubeAppearance.TryNormalize(new CubeAppearanceDeclaration { Color = new CubeColorDeclaration { Mode = mode, Name = name, Rgb = rgb } }, out var color, out _);
            Assert.That(valid, Is.EqualTo(expected)); if (valid) Assert.That(color.Color.Rgb, Is.EqualTo(rgb.ToUpperInvariant()));
        }
        [TestCase("color_rgb")] [TestCase("color_mode")] [TestCase("color_mixed")] [TestCase("color_unknown_field")]
        [TestCase("track_unknown_slot")] [TestCase("track_duplicate_from")] [TestCase("track_reference")] [TestCase("track_global_duplicate")]
        public void ProductionLoaderRejectsInvalidColorOrTrackJson(string change)
        {
            string companyPath = Path.Combine(fixtureRoot, "component/infected.component.company_marker.json"), cardPath = Path.Combine(fixtureRoot, "award/" + Card + ".json");
            string companyOriginal = File.ReadAllText(companyPath), cardOriginal = File.ReadAllText(cardPath);
            var company = JObject.Parse(companyOriginal); var card = JObject.Parse(cardOriginal);
            if (change == "color_rgb") company["cubeAppearance"]["color"]["rgb"] = "#D97000";
            if (change == "color_mode") company["cubeAppearance"]["color"]["mode"] = "viewer";
            if (change == "color_mixed") company["cubeAppearance"]["color"]["mode"] = "owner_player";
            if (change == "color_unknown_field") company["cubeAppearance"]["color"]["fallback"] = "red";
            if (change == "track_unknown_slot") card["usageTracks"][0]["activationTransitions"][0]["toSlotId"] = "invented";
            if (change == "track_duplicate_from") ((JArray)card["usageTracks"][0]["activationTransitions"]).Add(card["usageTracks"][0]["activationTransitions"][0].DeepClone());
            if (change == "track_reference") card["abilitySources"][0]["actions"][0]["usageTrackId"] = "missing";
            if (change == "track_global_duplicate") { card["usageTracks"][0]["trackId"] = "usage.city_style_source_stone_industrial_hub"; foreach (var action in card["abilitySources"][0]["actions"]) action["usageTrackId"] = "usage.city_style_source_stone_industrial_hub"; }
            string invalid = artifacts.CopyContent(fixtureRoot, "非法声明现场", new Dictionary<string, string> { ["component/infected.component.company_marker.json"] = company.ToString(), ["award/" + Card + ".json"] = card.ToString() });
            Assert.Throws<InvalidDataException>(() => ExternalContentPack.Load(invalid));
        }
        [Test] public void FrozenOwnerAndFixedColorsSurviveViewsAndTransfers()
        {
            var state = State(mixed: true); string one = PlayerAreaPolicy.Supply(state, 1, "core.player_marker")[0].ItemId, two = PlayerAreaPolicy.Supply(state, 2, "core.player_marker")[0].ItemId;
            string gang = PlayerAreaPolicy.Supply(state, 1, "entrepreneur.gang_marker")[0].ItemId;
            Assert.That(CubeAppearance.Resolve(state, one).PlayerColor, Is.Not.EqualTo(CubeAppearance.Resolve(state, two).PlayerColor));
            foreach (int viewer in new[] { 1, 2 })
            {
                var view = GameStateViewProjector.ProjectForPlayer(state, viewer); Assert.That(view.CubeColors.Single(c => c.ItemId == one).OwnerPlayerId, Is.EqualTo(1));
                Assert.That(view.CubeColors.Single(c => c.ItemId == gang).Rgb, Is.EqualTo("A020F0"));
            }
            Initialize(state); var cardZone = state.ItemZones.Zones.Single(z => z.ParentId == CardId(state) && z.LocalZoneId == "tactic");
            Assert.That(cardZone.LegalSlotIds, Is.EqualTo(new[] { "unused", "used" }));
            state = Restore(state, "冻结颜色与真实轨道"); Assert.That(CubeAppearance.Resolve(state, gang).Name, Is.EqualTo("Purple"));
            var company = state.ContentDefinitions.Components.Single(d => d.TypeId == "infected.company_marker"); Assert.That(company.CubeAppearance.Color.Rgb, Is.EqualTo("D97000"));
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)] public void BarrierWaitsForEveryPlayerAndNewObligations(int players)
        {
            var state = State(players); var progress = new UsageTrackResetProgress { RoundId = "qy003.round", RoundNumber = 1, ParticipantIds = state.Players.Select(p => p.PlayerId).ToList() };
            registry.Register("qy003.cleanup", c => EffectStepResult.Completed()); var engine = new EffectTreeExecutor(state, registry);
            for (int player = 1; player <= players; player++)
            {
                Assert.That(UsageTracks.BarrierReady(state, progress, out _), Is.False);
                string root = engine.CreateRoot(EffectSpec.Create("qy003.cleanup", playerId: player)); Run(engine);
                state.EffectRuntime.MainNodes.Add(new MainlineNodeRuntimeState { NodeId = "cleanup." + player, RoundExecutionId = progress.RoundId, NodeTypeId = RoundMainlineNodeTypeIds.PlayerCleanupWindow, RoundNumber = 1, PlayerId = player, ExecutionEffectId = root, Status = EffectNodeStatus.Completed });
            }
            Assert.That(UsageTracks.BarrierReady(state, progress, out _), Is.True);
            registry.Register("qy003.obligation", c => EffectStepResult.NoProgress("隔离待结义务"));
            string obligation = engine.CreateRoot(EffectSpec.Create("qy003.obligation", playerId: 1)); progress.RequiredObligationIds.Add(obligation);
            Assert.That(UsageTracks.BarrierReady(state, progress, out _), Is.False);
        }
        [TestCase("BindingId")] [TestCase("MarkerItemId")] [TestCase("DefinitionVersion")] [TestCase("InitializationReceiptId")]
        public void RecoveryRejectsCorruptOrMissingBindingFields(string field)
        {
            var state = State(); Initialize(state); string json = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)); var archive = JObject.Parse(json);
            ((JObject)archive["Snapshot"]["State"]["EffectRuntime"]["UsageTrackBindings"][0]).Remove(field);
            Assert.That(HostRecoveryService.RecoverJson(archive.ToString(), registry).Succeeded, Is.False);
            state.EffectRuntime.UsageTrackBindings[0] = null;
            Assert.That(UsageTrackRecovery.Validate(state, registry, out _), Is.False);
        }
        [Test] public void AreaMigrationPreservesOpaqueZoneIdsAndOriginalSupplyReferences()
        {
            var state = State(); string card = CardId(state); var initialIds = state.ItemZones.Items.Select(i => i.ItemId).ToList();
            state.SetupRecipe = new GameSetupRecipe();
            var entrepreneurZones = state.ItemZones.Zones.Where(z => z.TemplateId.Contains("entrepreneur_")).Select(z => z.ZoneId).ToList();
            state.ItemZones.Zones.RemoveAll(z => entrepreneurZones.Contains(z.ZoneId));
            foreach (var z in state.ItemZones.Zones)
            {
                if (z.TemplateId == "core.zone.player_supply") { z.TemplateId = "core.zone.player_marker_supply"; z.LocalZoneId = "player_markers"; }
                if (z.TemplateId == "core.zone.player_rewards") { z.TemplateId = "core.zone.player_awards"; z.LocalZoneId = "awards"; }
            }
            state.ItemZones.NamingVersion = 1; string supplyId = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker").OriginalSupplyZoneId;
            Assert.That(PlayerAreaPolicy.TryMigrateV1(state, out var migrated, out string reason), Is.True, reason);
            Assert.That(migrated.ItemZones.Items.Select(i => i.ItemId), Is.EqualTo(initialIds)); Assert.That(migrated.ItemZones.Items.Single(i => i.ItemId == card).ZoneId, Is.EqualTo(state.ItemZones.Items.Single(i => i.ItemId == card).ZoneId));
            Assert.That(migrated.ItemZones.Zones.Any(z => z.ZoneId == supplyId && z.TemplateId == "core.zone.player_supply"), Is.True); Assert.That(state.ItemZones.NamingVersion, Is.EqualTo(1));
        }
    }
}
