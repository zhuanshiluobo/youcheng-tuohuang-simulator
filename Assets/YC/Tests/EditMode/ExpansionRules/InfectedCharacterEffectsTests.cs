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
using YC.Domain.SpecialActions;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 仅装配显式完成的真实定义；不修改正式pack、不生成/覆盖界面资产。
    public sealed class InfectedCharacterEffectsTests
    {
        private static readonly string ContentRoot = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
        private static readonly string[] EnterprisePaths = {
            "enterprise/enterprise.rhine_lab.json", "enterprise/enterprise.fussen_bank.json",
            "enterprise/enterprise.raythean.json", "enterprise/enterprise.mama_johns.json", "enterprise/enterprise.blacksteel.json",
            "department/enterprise.department.defense.json", "department/enterprise.department.structural.json",
            "character/operator.mudrock.json", "character/operator.w.json", "character/operator.kaltsit.json",
            "character/mlynar.json", "component/enterprise.component.road.json",
            "enterprise/enterprise.rhodes_island.json", "enterprise/enterprise.hyde_brothers.json", "enterprise/enterprise.rim_billiton.json",
            "department/enterprise.department.engineering.json", "character/operator.silverash.json", "character/operator.eunectes.json",
            "character/operator.closure.json", "character/operator.dorothy.json", "character/operator.passenger.json", "character/mountain.json"
        };
        private static readonly string[] CharacterKeys = { "amiya", "muelsyse", "jessica_the_liberated", "swire", "lin", "silence_the_paradigmatic", "hellagur", "gavial_the_invincible", "ceobe", "flowing_shape_alpha", "flowing_shape_beta", "flowing_shape_gamma" };
        private static readonly string[] DefinitionPaths = EnterprisePaths.Concat(new[]{"liskarm","elysium","texas","cannot","tin-man"}.Select(id=>"character/"+id+".json")).Concat(CharacterKeys.Select(key => "character/infected.character." + key + ".json")).Concat(new[] { "component/infected.component.gavial_token.json", "component/infected.component.influence_lock_token.json" }).ToArray();
        private int diceFace = 5;
        private static readonly string[] ScriptCategories = { "enterprises", "departments", "characters" };
        private ExternalContentPack pack;
        private ExternalContentPack core;
        private EffectRegistry registry;
        private GameMapDefinition map;

        [OneTimeSetUp]
        public void LoadExplicitRealDefinitionsFromFormalPack()
        {
            core = ExternalContentPack.Load(ContentRoot);
            pack = core.CreateActiveSnapshot(new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId,
                PlayerCount = 3, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } });
        }
        private GameState State(int players = 2)
        {
            map = StaticMapDefinitions.CreateThreePlayerMap();
            var active = pack.ActiveDefinitions.Where(d => DefinitionPaths.Contains(d.ContentType + "/" + d.DefinitionId + ".json")).ToArray();
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            DiceRollEffectExecutor.Register(registry, max => diceFace - 1); ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            ItemTransferEffectExecutor.Register(registry); DeckShuffleEffectExecutor.Register(registry); RepeatEffectExecutor.Register(registry);
            var query = new MapQueryService(map); var influence = new InfluenceService(query);
            InfluenceEffectExecutor.Register(registry, influence); MapOperationEffectExecutor.Register(registry, query);
            ContentPrimitiveEffectExecutor.Register(registry, query, influence); FacilityEntryEffectExecutor.Register(registry);
            var travel = new TravelCostService(query);
            CityMoveEffectExecutor.Register(registry, query, influence, new CityMovementService(query, influence, travel), travel, new ResourceTokenService());
            ResourceCollectionEffectExecutor.Register(registry);
            CityStyleSpecialActionEffectExecutor.Register(registry,new SpecialActionOptionQueryService(query,influence,new CityMovementService(query,influence,travel),new SpecialActionLifecycleService()),null,query,influence,new CityMovementService(query,influence,travel));
            CityStyleLuaCatalog.Register(registry,pack);
            CharacterCoverEffectExecutor.Register(registry); CharacterLifecycleService.Register(registry);
            CharacterCardLuaCatalog.Register(registry, pack);
            EnterpriseEffectExecutor.Register(registry, LuaContentCatalog.CreateEnterpriseDefinitions(registry, pack.ActiveDefinitions, pack.GetScript, pack));
            var definitions = new GameContentDefinitions {
                Components = active.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition { DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"] }).ToList(),
                Characters = pack.CreateCharacters().ToList(), Facilities = pack.CreateFacilities().ToList(),
                Events = pack.CreateEvents().ToList(), CityStyles = pack.CreateCityStyles().ToList(), SpecialActions = pack.CreateSpecialActions().ToList()
            };
            var source = new GameState { GameId = "game.enterprise_content", MapId = map.MapId, ContentPackHash = pack.ContentHash,
                Round = 1, ActionRound = 1, StartPlayerId = 1, CurrentPlayerId = 1, Phase = GamePhase.ActionRound1,
                ContentDefinitions = definitions, RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = players, ContentPackHash = pack.ContentHash, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } } };
            for (int player = 1; player <= players; player++) source.Players.Add(new PlayerState { PlayerId = player, Resources = new ResourceSet { GoldVoucher = 80, Originium = 20, OriginiumShard = 20, Iron = 20, PureOriginium = 3 } });
            var entries = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "supply", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 29, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "mobile_cities", DefinitionId = "core.mobile_city", TypeId = "core.mobile_city", Quantity = 1, PerPlayer = true, InitialZoneRole = "city_reserve" }
            };
            foreach (var definition in active.Where(d => d.ContentType != "component" && d.ExpansionId!="core")) entries.Add(new ItemRecipeEntry {
                EntryId = definition.DefinitionId, DefinitionId = definition.DefinitionId, TypeId = definition.ItemTypeId, Quantity = 1,
                PerPlayer = definition.ContentType == "character" && (string)definition.Data["acquisitionRole"] == "initial",
                InitialZoneRole = definition.ContentType == "character" ? (string)definition.Data["acquisitionRole"] == "initial" ? "hand" : (string)definition.Data["acquisitionRole"] == "generated" ? "flowing_shapes" : "operator_deck" : definition.ContentType == "department" ? "departments" : "enterprises"
            });

            foreach (string id in new[] { "liskarm", "elysium", "texas", "cannot", "tin-man" }) entries.Add(new ItemRecipeEntry { EntryId = "initial_" + id, DefinitionId = id, TypeId = "core.basic_character_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" });
            foreach (var location in map.Locations.Where(l => !l.IsRedZone)) entries.Add(new ItemRecipeEntry { EntryId = "resource_point_" + location.LocationId, DefinitionId = "core.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + location.LocationId });
            foreach (var facility in definitions.Facilities.Where(d => d.GoldVoucherCost > 0).GroupBy(d => d.Name).Select(g => g.First()).Take(9)) entries.Add(new ItemRecipeEntry { EntryId = facility.FacilityId, DefinitionId = facility.FacilityId, TypeId = "core.facility_card", Quantity = 1, InitialZoneRole = "facility_deck" });
            foreach(var style in core.ActiveDefinitions.Where(d=>d.ContentType=="city_style")) entries.Add(new ItemRecipeEntry{EntryId=style.DefinitionId,DefinitionId=style.DefinitionId,TypeId="core.board_card",Quantity=1,InitialZoneRole="city_styles"});
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, entries, active.Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = d.CardZones }).ToList());
            foreach (var token in state.ItemZones.Items.Where(i => i.TypeId == "core.resource_point_token"))
                token.LocalState = Object("resourceType", NormalizedValue.CreateInteger((int)ResourceType.Originium), "amount", NormalizedValue.CreateInteger(2));
            state.EffectRuntime.PlayerOrderSnapshot = Enumerable.Range(1, players).ToList();
            state.Map.OpenLocationIds.AddRange(map.Locations.Where(l => !l.IsRedZone).Select(l => l.LocationId));
            foreach (var board in active.Where(d => d.ContentType == "enterprise"))
            foreach (int player in Enumerable.Range(1, players))
            {
                var declaration = new CardZoneDefinition { LocalZoneId = "cooperation_" + player, ZoneTemplateId = "core.zone.board_marker_track",
                    AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" },
                    CapacityPolicy = "bounded", Capacity = 1, OrderingKind = ZoneOrderingKind.Track, VisibilityPolicyId = ItemZoneVisibility.Public,
                    EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
                Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, Item(state, board.DefinitionId).ItemId, declaration, out string zone, out string reason), Is.True, reason);
                var marker = state.ItemZones.Items.First(i => i.ZoneId == FindZone(state, "supply", player).ZoneId);
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
                string choice = request.CandidateIds.Contains("stop") ? "stop" : request.CandidateIds.Contains("choice.skip") ? "choice.skip" : request.CandidateIds.Contains("decline") ? "decline" : request.CandidateIds[0];
                if (request.MinSelections > 1) Answer(state, request.CandidateIds.Take(request.MinSelections).ToArray()); else Answer(state, choice);
            }
            Assert.Fail("内容交互未在业务步数上限内完成。");
        }
        private GameState Restore(GameState state)
        {
            var recovery = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(recovery.Succeeded, Is.True, recovery.Diagnostic); Valid(recovery.State); return recovery.State;
        }
        private static InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.LastOrDefault(r=>r.Status=="open");
        private void FillSupply(GameState state) => Run(state,EffectSpec.Create(ItemTransferEffectExecutor.RefillTypeId,Object("apiVersion",NormalizedValue.CreateInteger(1),"player",NormalizedValue.CreateString("1"),"slotZoneId",NormalizedValue.CreateString(state.ItemZones.Zones.First(z=>z.LocalZoneId=="facility_supply_0").ZoneId)),1));
        private ItemInstanceState Card(GameState state,string key) => state.ItemZones.Items.First(i=>i.DefinitionId=="infected.character."+key && (i.ControllerSubject.PlayerId==1 || i.TypeId!="core.basic_character_card"));
        private string StartCard(GameState state,string key,string mode)
        {
            var card=Card(state,key); if(card.ZoneId!=FindZone(state,"hand",1).ZoneId) Run(state,MoveSpec(1,new[]{card.ItemId},card.ZoneId,FindZone(state,"hand",1).ZoneId,"unordered",1,"core.character_card_transfer"));
            Run(state,EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId,playerId:1)); Answer(state,card.ItemId);
            return Run(state,CharacterLifecycleService.CreateUseSpec(1,card.ItemId,mode)).EffectId;
        }
        private void Use(GameState state,string key,string mode) { StartCard(state,key,mode); Drain(state); }
        private void Emit(GameState state,string eventType)
        {
            if(!registry.TryGet("fixture.lifecycle",out _))registry.Register(new EffectRegistration("fixture.lifecycle",c=>EffectStepResult.Completed().AddEvent(new EffectEventRequest{EventType=Text(c.Node.NormalizedArguments,"eventType"),PlayerId=c.Node.PlayerId,Payload=Object()})));
            Run(state,EffectSpec.Create("fixture.lifecycle",Object("eventType",NormalizedValue.CreateString(eventType)),1));
        }
        private static IEnumerable<TestCaseData> Modes() { foreach(string key in CharacterKeys) foreach(string mode in new[]{"strategy","tactic"})yield return new TestCaseData(key,mode); }
        [TestCaseSource(nameof(Modes))]
        public void EveryModeUsesRealDefinitionAndEffectLifecycle(string key,string mode)
        {
            var state=State(3); DockFixtureCity(state,1); FillSupply(state);
            string root=StartCard(state,key,mode); Drain(state);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root).Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.Status==EffectNodeStatus.Failed || n.Status==EffectNodeStatus.Faulted || n.EffectTypeId==ContentPrimitiveEffectExecutor.IncompleteEffect),Is.False);
            Assert.That(state.CharacterUses.Last().CompletedModes,Does.Contain(mode));
            state=Restore(state); Assert.That(state.CharacterUses.Last().Finished,Is.True);
        }
        [TestCase("amiya")][TestCase("muelsyse")][TestCase("jessica_the_liberated")]
        [TestCase("swire")][TestCase("lin")][TestCase("gavial_the_invincible")]
        public void RealActiveEffectProjectsFullTextForOwnerOpponentAndRecovery(string key)
        {
            var state = State(3); DockFixtureCity(state, 1); FillSupply(state);
            if (key == "swire" || key == "lin")
            {
                LoadInitialMarkers(state, key, 2);
                Assert.That(Open(state), Is.Null, "取得标记流程未在业务步数上限内完成。");
            }
            else Use(state, key, "tactic");
            var card = Card(state, key);
            var display = PlayerDetailDisplayCatalog.Read(pack.ActiveDefinitions);
            var entry = YC.Presentation.Workflows.PlayerDetailEntryProjector.Project(View(state), display).Single(value => value.SourceItemId == card.ItemId);
            Assert.That(entry.DetailPlayerId, Is.EqualTo(1));
            Assert.That(entry.AbilityId, Is.EqualTo(card.DefinitionId + ".tactic"));
            Assert.That(entry.EffectText, Is.EqualTo(display.Single(value => value.AbilityId == entry.AbilityId).EffectText));
            Assert.That(entry.SourceEffectId, Is.Not.Empty);
            if (key == "muelsyse") { Assert.That(entry.HasCoverArea, Is.True); Assert.That(entry.CoverSlotIds, Is.Empty); }
            if (key == "jessica_the_liberated") Assert.That(entry.BindingId, Is.EqualTo(state.FacilityCharacterBindings.Single().BindingId));
            if (key == "swire" || key == "lin") Assert.That(entry.StorageZoneIds.Count, Is.EqualTo(1));
            Assert.That(YC.Presentation.Workflows.PlayerDetailEntryProjector.Project(View(state, 2), display).Single(value => value.SourceItemId == card.ItemId).EntryId, Is.EqualTo(entry.EntryId));
            state = Restore(state);
            Assert.That(YC.Presentation.Workflows.PlayerDetailEntryProjector.Project(View(state), display).Single(value => value.SourceItemId == card.ItemId).EntryId, Is.EqualTo(entry.EntryId));
            Run(state, MoveSpec(1, new[] { card.ItemId }, card.ZoneId, FindZone(state, "discard", 1).ZoneId, "unordered", 1, "core.character_card_transfer"));
            Assert.That(YC.Presentation.Workflows.PlayerDetailEntryProjector.Project(View(state), display).Any(value => value.SourceItemId == card.ItemId), Is.False);
        }
        [TestCase("hellagur")][TestCase("muelsyse")]
        public void RealDeferredObligationSurvivesSourceExitAndEndsAtCleanup(string key)
        {
            var state = State(3); DockFixtureCity(state, 1); Use(state, key, "strategy");
            var card = Card(state, key); var display = PlayerDetailDisplayCatalog.Read(pack.ActiveDefinitions);
            var entries = YC.Presentation.Workflows.PlayerDetailEntryProjector.Project(View(state), display);
            Assert.That(entries.Single(value => value.SourceItemId == card.ItemId).AbilityId, Is.EqualTo(card.DefinitionId + ".strategy"));
            Assert.That(card.ZoneId, Is.EqualTo(FindZone(state, "discard", 1).ZoneId));
            state = Restore(state); Cleanup(state); Drain(state);
            Assert.That(YC.Presentation.Workflows.PlayerDetailEntryProjector.Project(View(state), display).Any(value => value.SourceItemId == card.ItemId), Is.False);
        }
        [Test]
        public void RealDefinitionsKeepAmiyaBasicAndGeneratedCardsOutOfBasicLimit()
        {
            var state=State(); Assert.That(CharacterLifecycleService.GetRetainedBasicCharacterCount(state,1),Is.EqualTo(8));
            Assert.That(Card(state,"amiya").TypeId,Is.EqualTo("core.basic_character_card"));
            foreach(string key in CharacterKeys.Skip(1)) Assert.That(Card(state,key).TypeId,Is.EqualTo(key.StartsWith("flowing_shape")?"core.generated_character_card":"core.operator_card"));
            Assert.That(state.ItemZones.Items.Count(i=>i.TypeId=="core.generated_character_card"),Is.EqualTo(3));
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)]
        public void DicePersistsAndPureTransferKeepsSameInstance(int face)
        {
            diceFace=face; var state=State(); DockFixtureCity(state,1); FillSupply(state); StartCard(state,"ceobe","tactic");
            var dice=state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==LuaDomainEffectTypeIds.RollDice); Assert.That(Number(dice.NormalizedResult,"diceResult"),Is.EqualTo(face));
            diceFace=face%6+1; state=Restore(state); Drain(state); var card=Card(state,"ceobe");
            Assert.That(Number(state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==LuaDomainEffectTypeIds.RollDice).NormalizedResult,"diceResult"),Is.EqualTo(face));
            if(face>=5) { Assert.That(card.ControllerSubject.PlayerId,Is.EqualTo(2)); Assert.That(card.ZoneId,Is.EqualTo(FindZone(state,"hand",2).ZoneId)); Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==OperatorDispatchFlow.TypeId),Is.False); }
            else { Assert.That(card.ZoneId,Is.EqualTo(FindZone(state,"discard",1).ZoneId)); if(face<=2)Assert.That(state.Map.Influences.Count,Is.EqualTo(2));else Assert.That(state.FindPlayer(1).Resources.PureOriginium,Is.EqualTo(5)); }
            diceFace=5;
        }
        [Test]
        public void MuelsyseAcquiresThreeOriginalInstancesOnceAndStaysPersistentAcrossRounds()
        {
            var state=State(); var original=state.ItemZones.Items.Where(i=>i.TypeId=="core.generated_character_card").Select(i=>i.ItemId).ToArray();
            Use(state,"muelsyse","tactic"); state=Restore(state); state.ActionRound=2; state.Phase=GamePhase.ActionRound2; Emit(state,"ActionRoundStarted"); Drain(state);
            Assert.That(Card(state,"muelsyse").ZoneId,Is.EqualTo(FindZone(state,"persistent",1).ZoneId));
            Assert.That(state.ItemZones.Items.Where(i=>i.TypeId=="core.generated_character_card").Select(i=>i.ItemId),Is.EquivalentTo(original));
            Assert.That(original.All(id=>state.ItemZones.Items.Single(i=>i.ItemId==id).ControllerSubject.PlayerId==1),Is.True);
        }
        [TestCase(1)] [TestCase(2)]
        public void InfluenceLockExpiresAtImmediatelyFollowingActionRound(int round)
        {
            var state=State(); DockFixtureCity(state,1); state.ActionRound=round; state.Phase=round==1?GamePhase.ActionRound1:GamePhase.ActionRound2;
            Use(state,"silence_the_paradigmatic","tactic"); Assert.That(state.InfluenceLocks.Single().ExpiresBeforeActionRoundSequence,Is.EqualTo(round+1));
            state=Restore(state); state.Phase=GamePhase.Cleanup; Emit(state,"PlayerCleanupStarted"); Assert.That(state.InfluenceLocks.Single().Active,Is.True);
            if(round==2)state.Round++; state.ActionRound=round==1?2:1; state.Phase=round==1?GamePhase.ActionRound2:GamePhase.ActionRound1; Emit(state,"ActionRoundStarted");
            Assert.That(state.InfluenceLocks.Single().Active,Is.False); Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==state.InfluenceLocks.Single().SourceItemId).ZoneId,Is.EqualTo(FindZone(state,"retired").ZoneId));
        }
        [Test]
        public void GavialTokenSurvivesSourceLeavingPersistentArea()
        {
            var state=State(); Use(state,"gavial_the_invincible","tactic"); var token=state.ItemZones.Items.Single(i=>i.TypeId=="infected.gavial_token"); string id=token.ItemId,zone=token.ZoneId;
            var card=Card(state,"gavial_the_invincible"); Run(state,MoveSpec(1,new[]{card.ItemId},card.ZoneId,FindZone(state,"discard",1).ZoneId,"unordered",1,"core.character_card_transfer")); state=Restore(state);
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==id).ZoneId,Is.EqualTo(zone));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.EventType==CityMoveEventTypeIds.CityMoveCompleted),Is.Zero);
        }

        private void MoveToHand(GameState state,ItemInstanceState card) => Run(state,MoveSpec(1,new[]{card.ItemId},card.ZoneId,FindZone(state,"hand",1).ZoneId,"unordered",1,"core.character_card_transfer"));
        private void Cleanup(GameState state)
        {
            state.Phase = GamePhase.Cleanup;
            _ = new RoundExecutionService(registry);
            var main = new MainlineNodeRuntimeState { NodeId = "fixture.cleanup." + state.Round + "." + state.ActionRound,
                NodeTypeId = RoundMainlineNodeTypeIds.PlayerCleanupWindow, PlayerId = 1, RoundNumber = state.Round,
                ActionRound = state.ActionRound, Status = EffectNodeStatus.Running };
            state.EffectRuntime.MainNodes.Add(main); state.EffectRuntime.ActiveMainNodeId = main.NodeId;
            var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId,
                Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1));
            state.EffectRuntime.MainNodes.Single(n => n.NodeId == main.NodeId).ExecutionEffectId = root;
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private void InvokeCleanup(GameState state)
        {
            var option = registry.GetSettlementOptions(state, 1).Single(o => o.IsEnabled);
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TryInvokeCharacterSettlementOption(1, option.OptionId, "", option.StateRevision, out _, out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private void LoadInitialMarkers(GameState state, string key, int count)
        {
            StartCard(state, key, "tactic");
            if (key == "lin")
            {
                Assert.That(Open(state).AnswerSchema, Is.EqualTo(ContentPrimitiveEffectExecutor.QuantityAnswerSchema));
                Assert.That(Open(state).AnsweringPlayerId, Is.EqualTo(1));
                Answer(state, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.That(StoredMarkers(state, key).All(i => i.OwnerSubject.PlayerId == 1), Is.True);
            }
            else
            {
                if (count == 0) AnswerBoolean(state, false);
                else Answer(state, Enumerable.Range(2, count).Select(player => "p" + player).ToArray());
            }
            Drain(state);
        }
        private void AnswerBoolean(GameState state, bool answer)
        {
            var request = Open(state); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId,
                request.StateRevision, NormalizedValue.CreateBoolean(answer), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private List<ItemInstanceState> StoredMarkers(GameState state,string key) {
            string id=Card(state,key).ItemId; return state.ItemZones.Items.Where(i=>state.ItemZones.Zones.Any(z=>z.ZoneId==i.ZoneId && z.ParentId==id && z.LocalZoneId=="markers")).ToList();
        }
        private GameStateView View(GameState state,int player=1) => GameStateViewProjector.Project(state,new GameStateViewer{Role=GameStateViewerRole.Player,PlayerId=player});
        [Test]
        public void AmiyaInsertionRestoresConsumedReceiptAndResolvesBothGeneratedModesOnce()
        {
            var state=State(); Use(state,"amiya","tactic"); StartCard(state,"flowing_shape_gamma","");
            var use=state.CharacterUses.Last(); var option=CharacterSettlementOptionQuery.GetOptions(state,1,use.UseId).Single();
            Assert.That(option.IsEnabled,Is.True); Assert.That(CharacterSettlementOptionQuery.GetOptions(state,2,use.UseId),Is.Empty);
            var engine=new EffectTreeExecutor(state,registry);
            Assert.That(engine.TryInvokeCharacterSettlementOption(1,option.OptionId,use.UseId,option.StateRevision,out _,out string error),Is.True,error);
            for(int i=0;i<100 && !state.CharacterUses.Last().SourceConsumed;i++)Assert.That(engine.Advance(),Is.True);
            state=Restore(state); engine=new EffectTreeExecutor(state,registry);
            Assert.That(engine.TryInvokeCharacterSettlementOption(1,option.OptionId,use.UseId,option.StateRevision,out bool duplicate,out error),Is.True,error); Assert.That(duplicate,Is.True);
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False); Answer(state,"strategy-first"); Drain(state);
            Assert.That(state.CharacterUses.Last().CompletedModes,Is.EqualTo(new[]{"strategy","tactic"}));
            Assert.That(Card(state,"amiya").ZoneId,Is.EqualTo(FindZone(state,"discard",1).ZoneId));
            Assert.That(Card(state,"flowing_shape_gamma").ZoneId,Is.EqualTo(FindZone(state,"retired").ZoneId));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==CharacterLifecycleService.ConsumeSourceTypeId),Is.EqualTo(1));
        }
        [Test]
        public void MuelsyseReusesOneExtraSlotAndCollectionReturnsOnlyCoveredShape()
        {
            var state=State(); StartCard(state,"muelsyse","tactic"); Answer(state,"hand");Answer(state,"hand");Answer(state,"discard");
            string first=Open(state).CandidateIds.First(id=>id!="decline");Answer(state,first); Drain(state);
            var slot=state.CharacterCoverSlots.Single(v=>v.Reusable);string slotId=slot.SlotId;
            var detail = YC.Presentation.Workflows.PlayerDetailEntryProjector.Project(View(state), PlayerDetailDisplayCatalog.Read(pack.ActiveDefinitions)).Single(value => value.SourceItemId == Card(state, "muelsyse").ItemId);
            Assert.That(detail.CoverSlotIds, Is.EqualTo(new[] { slotId }));
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==first).ZoneId,Is.EqualTo(FindZone(state,"covered",1).ZoneId));
            Run(state,CharacterLifecycleService.CreateUseSpec(1,first,"strategy"));
            while(Open(state)!=null && !Open(state).CandidateIds.Contains("decline"))Answer(state,Open(state).CandidateIds.First());
            Assert.That(Open(state),Is.Not.Null); string second=Open(state).CandidateIds.First(id=>id!="decline");Answer(state,second);Drain(state);
            Assert.That(state.CharacterCoverSlots.Count(v=>v.Reusable),Is.EqualTo(1));Assert.That(state.CharacterCoverSlots.Single(v=>v.Reusable).SlotId,Is.EqualTo(slotId));
            state=Restore(state); Emit(state,"CollectionStarted");Answer(state,"recycle");
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==first).ZoneId,Is.EqualTo(FindZone(state,"discard",1).ZoneId));
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==second).ZoneId,Is.EqualTo(FindZone(state,"hand",1).ZoneId));
            Assert.That(Card(state,"muelsyse").ZoneId,Is.EqualTo(FindZone(state,"persistent",1).ZoneId));
        }
        [TestCase("flowing_shape_alpha")][TestCase("flowing_shape_beta")][TestCase("flowing_shape_gamma")]
        public void GeneratedModesHaveIndependentDiscardAndRetirement(string key)
        {
            var state=State();FillSupply(state);Use(state,key,"strategy");Assert.That(Card(state,key).ZoneId,Is.EqualTo(FindZone(state,"discard",1).ZoneId));
            state.ActionRound=2;state.Phase=GamePhase.ActionRound2;Use(state,key,"tactic");Assert.That(Card(state,key).ZoneId,Is.EqualTo(FindZone(state,"retired").ZoneId));
        }
        [Test]
        public void JessicaBindingChargesPrintedCostAndRejectsInsufficientAndDuplicateSettlement()
        {
            var state=State();DockFixtureCity(state,1);FillSupply(state);StartCard(state,"jessica_the_liberated","tactic");
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("action.decline_effect")); Answer(state,"continue");
            Assert.That(state.FindPlayer(1).Score,Is.EqualTo(-2));Drain(state);
            int builtScore=state.FindPlayer(1).Score;var binding=state.FacilityCharacterBindings.Single();
            var built=state.ItemZones.Items.Single(i=>i.ItemId==binding.FacilityItemId);
            int cost=checked(FacilityCardDatabase.Get(state,built.DefinitionId).GoldVoucherCost+2);
            Assert.That(binding.CharacterItemId,Is.EqualTo(Card(state,"jessica_the_liberated").ItemId));
            Assert.That(state.ItemZones.Zones.Single(z=>z.ZoneId==built.ZoneId).TemplateId,Is.EqualTo("core.zone.city_facility_cell"));
            state.FindPlayer(1).Resources.GoldVoucher=cost-1;Cleanup(state);
            var option=registry.GetSettlementOptions(state,1).Single();Assert.That(option.IsEnabled,Is.False);
            Assert.That(option.DisabledReasonCode,Is.EqualTo("core:presentation.unavailable"));
            Assert.That(new EffectTreeExecutor(state,registry).TryInvokeCharacterSettlementOption(1,option.OptionId,"",option.StateRevision,out _,out _),Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(cost-1));Assert.That(state.FindPlayer(1).Score,Is.EqualTo(builtScore));
            Assert.That(state.FacilityCharacterBindings.Single().Active,Is.True);
            Run(state,ResourceEffectSpecFactory.Gain(1,ResourceType.GoldVoucher,1));state=Restore(state);
            option=registry.GetSettlementOptions(state,1).Single();Assert.That(option.IsEnabled,Is.True);
            var engine=new EffectTreeExecutor(state,registry);Assert.That(engine.TryInvokeCharacterSettlementOption(1,option.OptionId,"",option.StateRevision,out _,out string error),Is.True,error);
            for(int step=0;step<100&&state.FindPlayer(1).Resources.GoldVoucher>0;step++)
            {
                var request=Open(state);
                if(request!=null)Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,
                    NormalizedValue.CreateString(request.CandidateIds.First()),out error),Is.True,error);
                if(state.FindPlayer(1).Resources.GoldVoucher>0)Assert.That(engine.Advance(),Is.True);
            }
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.Zero);
            Assert.That(state.FindPlayer(1).Score,Is.EqualTo(builtScore));state=Restore(state);engine=new EffectTreeExecutor(state,registry);
            Assert.That(engine.TryInvokeCharacterSettlementOption(1,option.OptionId,"",option.StateRevision,out bool duplicate,out error),Is.True,error);
            Assert.That(duplicate,Is.True);Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            state=Restore(state);engine=new EffectTreeExecutor(state,registry);
            Assert.That(engine.TryInvokeCharacterSettlementOption(1,option.OptionId,"",state.EffectRuntime.StateRevision,out _,out error),Is.False);
            Assert.That(error,Is.EqualTo("settlement_window_not_active"));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.Zero);Assert.That(state.FindPlayer(1).Score,Is.EqualTo(builtScore+2));
            Assert.That(state.FacilityCharacterBindings.Single().Active,Is.False);Assert.That(Card(state,"jessica_the_liberated").ZoneId,Is.EqualTo(FindZone(state,"discard",1).ZoneId));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==ResourceEffectTypeIds.Pay&&n.Status==EffectNodeStatus.Completed),Is.EqualTo(1));
        }
        [Test]
        public void JessicaRetiresWithBoundFacilityAndLosesSettlement()
        {
            var state=State();DockFixtureCity(state,1);FillSupply(state);Use(state,"jessica_the_liberated","tactic");var binding=state.FacilityCharacterBindings.Single();
            ContentLifecycleEffectExecutor.Register(registry);Run(state,EffectSpec.Create(ContentLifecycleEffectExecutor.RetireFacilityTypeId,Object("apiVersion",NormalizedValue.CreateInteger(1),"player",NormalizedValue.CreateString("1"),"facilityItemId",NormalizedValue.CreateString(binding.FacilityItemId)),1));
            state=Restore(state);Assert.That(Card(state,"jessica_the_liberated").ZoneId,Is.EqualTo(FindZone(state,"retired").ZoneId));Assert.That(state.FacilityCharacterBindings.Single().Active,Is.False);
            Cleanup(state);Assert.That(CharacterSettlementOptionQuery.GetOptions(state,1,""),Is.Empty);
        }
        [TestCase("swire")][TestCase("lin")]
        public void MarkersPreserveConfiguredSourceAndReturnToOriginalSupply(string key)
        {
            var state=State(3);LoadInitialMarkers(state,key,1);var first=StoredMarkers(state,key).Single();int owner=first.OwnerSubject.PlayerId;
            Assert.That(owner,key=="lin"?Is.EqualTo(1):Is.Not.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests.All(r=>r.AnsweringPlayerId==1),Is.True);state=Restore(state);
            Assert.That(StoredMarkers(state,key).Single().ItemId,Is.EqualTo(first.ItemId));
            foreach(var player in state.Players.Where(player=>player.PlayerId!=1))player.Resources.GoldVoucher=19;
            int before=state.FindPlayer(1).Resources.GoldVoucher;Cleanup(state);InvokeCleanup(state);if(key!="swire")Answer(state,first.ItemId);Drain(state);
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==first.ItemId).ZoneId,Is.EqualTo(FindZone(state,"supply",owner).ZoneId));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(before+(key=="swire"?6:0)));
            Assert.That(Card(state,key).ZoneId,Is.EqualTo(FindZone(state,"discard",1).ZoneId));state=Restore(state);
        }
        [Test]
        public void LinTenSuccessfulTradesUsePerTransactionPricesAndNoEleventhRequest()
        {
            var state=State();StartCard(state,"lin","strategy");int gold=state.FindPlayer(1).Resources.GoldVoucher;
            string source=Card(state,"lin").ItemId;
            var trades=new[]{"sell_originium","buy_originium","sell_originium_shard","buy_originium_shard","sell_iron","buy_iron","sell_pure_originium","buy_pure_originium","sell_originium","buy_originium"};
            var deltas=new[]{3,-1,4,-2,5,-3,15,-13,3,-1};
            for(int i=0;i<10;i++)
            {
                CompleteTrade(state,trades[i]);gold+=deltas[i];
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold));
                Assert.That(TradeReceipts(View(state),source).Count,Is.EqualTo(i+1));
                if(i==8)state=Restore(state);
            }
            Assert.That(Open(state),Is.Null);state=Restore(state);
            Assert.That(TradeReceipts(View(state),source).Count,Is.EqualTo(10));
            Assert.That(Open(state),Is.Null,"第十次成交及恢复后不得生成第十一次交易请求。");
        }
        private void CompleteTrade(GameState state,string trade)
        {
            Assert.That(Open(state).PromptKey,Is.EqualTo("core:interaction.trade.choose"));
            Assert.That(Open(state).CandidateIds,Does.Contain(trade));Answer(state,trade);
            AdvanceToCharacterPrompt(state,"core:interaction.trade.choose");
        }
        private void AdvanceToCharacterPrompt(GameState state,string prompt)
        {
            for(int step=0;step<100;step++)
            {
                var request=Open(state);if(request==null || request.PromptKey==prompt)return;
                Assert.That(request.AnsweringPlayerId,Is.EqualTo(1));
                string choice=request.CandidateIds.Contains("continue")?"continue":request.CandidateIds.Contains("pay")?"pay":request.CandidateIds.First();
                Answer(state,choice);
            }
            Assert.Fail("角色正式请求未在业务步数上限内续接完成。");
        }
        private static List<RuleEventView> TradeReceipts(GameStateView view,string source) => view.Events.Where(e=>
            e.EventType=="EffectCompleted" && Text(e.HostOnlyPayload,"outcome")=="completed" &&
            Text(Value(e.HostOnlyPayload,"continuationContext"),"source")==source &&
            Text(Value(e.HostOnlyPayload,"continuationContext"),"stage")=="trade_committed" &&
            Text(Value(e.HostOnlyPayload,"normalizedResult"),"outcome")=="condition_met").ToList();
        [Test]
        public void LinTradeCandidatesFollowCurrentAffordability()
        {
            var state=State();state.FindPlayer(1).Resources=new ResourceSet{Originium=1};StartCard(state,"lin","strategy");
            Assert.That(Open(state).CandidateIds,Is.EquivalentTo(new[]{"sell_originium","stop"}));Answer(state,"sell_originium");
            Assert.That(Open(state).CandidateIds,Does.Contain("buy_iron"));Assert.That(Open(state).CandidateIds,Does.Not.Contain("buy_pure_originium"));Answer(state,"buy_iron");Answer(state,"stop");Assert.That(state.FindPlayer(1).Resources.Iron,Is.EqualTo(1));
        }
        [TestCase(1,false)][TestCase(2,false)][TestCase(1,true)]
        public void HellagurNewCoverKeepsOldSlotAndBlocksOnlyCurrentActionRound(int round,bool generated)
        {
            var state=State();state.ActionRound=round;state.Phase=round==1?GamePhase.ActionRound1:GamePhase.ActionRound2;
            string chosen=generated?Card(state,"flowing_shape_gamma").ItemId:state.ItemZones.Items.First(i=>i.ZoneId==FindZone(state,"hand",1).ZoneId).ItemId;
            if(generated)MoveToHand(state,Card(state,"flowing_shape_gamma"));
            StartCard(state,"hellagur","tactic");string oldSlot=Card(state,"hellagur").SlotId;Answer(state,chosen);Assert.That(Open(state).CandidateIds,Does.Contain(chosen));Answer(state,chosen);
            Assert.That(state.CharacterCoverSlots.Single(v=>v.CardInstanceId==chosen).SlotId,Is.Not.EqualTo(oldSlot));Assert.That(CharacterLifecycleService.GetAvailability(state,1,chosen).CanUse,Is.False);state=Restore(state);
            if(round==2){registry.Register("fixture.infected_recycle",c=>InfectedRoundRules.RecycleCharacters(c,1));state.Phase=GamePhase.Cleanup;Run(state,EffectSpec.Create("fixture.infected_recycle",playerId:1));Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==chosen).ZoneId,Is.EqualTo(FindZone(state,"hand",1).ZoneId));Assert.That(Card(state,"hellagur").ZoneId,Is.EqualTo(FindZone(state,"hand",1).ZoneId));}
            else {state.ActionRound=2;state.Phase=GamePhase.ActionRound2;Assert.That(CharacterLifecycleService.GetAvailability(state,1,chosen).CanUse,Is.True);}
        }
        [Test]
        public void DiceAndLockArchivesRejectMissingOrCorruptFacts()
        {
            var state=State();DockFixtureCity(state,1);Use(state,"silence_the_paradigmatic","tactic");
            var raw=JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));((JObject)raw["Snapshot"]["State"]["InfluenceLocks"][0]).Remove("ExpiresBeforeActionRoundSequence");Assert.That(HostRecoveryService.RecoverJson(raw.ToString(),registry).Succeeded,Is.False);
            state=State();diceFace=5;StartCard(state,"ceobe","tactic");raw=JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));var die=raw["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectTypeId"]==LuaDomainEffectTypeIds.RollDice);die["ContinuationData"]["Properties"].Single(v=>(string)v["Name"]=="diceResult")["Value"]["IntegerValue"]=7;Assert.That(HostRecoveryService.RecoverJson(raw.ToString(),registry).Succeeded,Is.False);
        }
        [Test]
        public void LockProjectionContainsActualCityAndExpiryAndGavialIsPermanent()
        {
            var state=State();DockFixtureCity(state,1);Use(state,"silence_the_paradigmatic","tactic");var projected=View(state,2).MapTokens.Single();Assert.That(projected.ExpiresBeforeActionRoundSequence,Is.EqualTo(2));Assert.That(projected.BoundCityItemId,Is.EqualTo(state.InfluenceLocks.Single().BoundCityItemId));
            Use(state,"gavial_the_invincible","tactic");Assert.That(View(state,2).MapTokens.Single(t=>t.TypeId=="infected.gavial_token").IsPermanent,Is.True);
        }

        private ItemInstanceState PlaceFixtureInfluence(GameState state,int player,string slot)
        {
            var item=state.ItemZones.Items.First(i=>i.ZoneId==FindZone(state,"supply",player).ZoneId);
            var zone=state.ItemZones.Zones.Single(z=>z.TemplateId=="core.zone.influence_slots" && z.LegalSlotIds.Contains(slot));SetFixturePosition(state,item,zone.ZoneId);item.SlotId=slot;Valid(state);return item;
        }
        private List<string> Slots(GameState state)=>state.ItemZones.Zones.Where(z=>z.TemplateId=="core.zone.influence_slots").SelectMany(z=>z.LegalSlotIds).ToList();
        [Test]
        public void LockBlocksRegionEntryAndCompanyAndReplacementButAllowsExitAndFollowsCity()
        {
            var state=State();string origin=DockFixtureCity(state,1);var service=new InfluenceService(new MapQueryService(map));
            string city=state.ItemZones.Items.Single(i=>i.TypeId=="core.mobile_city" && i.OwnerSubject.PlayerId==1).ItemId;
            string region=InfluenceSubjectQuery.GetRegionId(state,city);
            string source=Slots(state).First(slot=>service.CanPlace(state,2,slot).IsValid && RegionOfSlot(state,slot)==region);
            var existing=PlaceFixtureInfluence(state,2,source);Use(state,"silence_the_paradigmatic","tactic");
            Assert.That(Slots(state).Where(slot=>RegionOfSlot(state,slot)==region).All(slot=>!InfluenceLockRules.PlacementAllowed(state,slot)),Is.True);
            Assert.That(service.ValidateItemPlacement(state,ItemSubjectRef.Company(CompanyInfluenceRules.CompanyId),source).Succeeded,Is.False);
            var replace=Run(state,InfluenceEffectSpecFactory.ReplaceInfluence(1,existing.ItemId));Assert.That(state.Map.Influences.Any(i=>i.SlotId==source && i.PlayerId==1),Is.False);
            if(state.Map.Influences.Any(i=>i.InfluenceId==existing.ItemId))Run(state,InfluenceEffectSpecFactory.RemoveInfluence(1,existing.ItemId));
            existing=PlaceFixtureInfluence(state,2,source);string outside=Slots(state).First(slot=>RegionOfSlot(state,slot)!=region && service.CanMove(state,2,existing.ItemId,slot).IsValid);
            Run(state,InfluenceEffectSpecFactory.MoveInfluence(1,existing.ItemId,outside));Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==existing.ItemId).SlotId,Is.EqualTo(outside));
            Run(state,CityMoveEffectSpecFactory.Move(1,"",true,false));Assert.That(Open(state),Is.Not.Null);string target=Open(state).CandidateIds.First();Answer(state,target);Drain(state);
            var binding=state.InfluenceLocks.Single();Assert.That(TokenMovementProfileRegistry.GetLocationId(state,binding.SourceItemId),Is.EqualTo(state.FindPlayer(1).CityLocationId));Assert.That(state.FindPlayer(1).CityLocationId,Is.Not.EqualTo(origin));state=Restore(state);
        }
        private string RegionOfSlot(GameState state,string slot)
        {
            var zone=state.ItemZones.Zones.Single(z=>z.TemplateId=="core.zone.influence_slots" && z.LegalSlotIds.Contains(slot));var parent=InfluenceSubjectQuery.GetMapParent(state,zone.ZoneId);return state.ItemZones.Zones.Single(z=>z.ZoneId==parent.ParentId).LocalZoneId;
        }
        [TestCase(false)][TestCase(true)]
        public void GavialDeploymentDependsOnActualRemovalResult(bool opponent)
        {
            var state=State();DockFixtureCity(state,1);StartCard(state,"gavial_the_invincible","strategy");var request=Open(state);Assert.That(request.InteractionTypeId,Is.EqualTo(CityMoveEffectExecutor.TargetInteractionTypeId));
            string target=request.CandidateIds.First();if(opponent)PlaceFixtureInfluence(state,2,InfluenceService.GetLocationSlotId(target,0));int shard=state.FindPlayer(1).Resources.OriginiumShard;
            Answer(state,target);Drain(state);var move=state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==CityMoveEffectTypeIds.Move);
            Assert.That(Number(move.NormalizedResult,"removedOpponentCount"),Is.EqualTo(opponent?1:0));Assert.That(state.FindPlayer(1).Resources.OriginiumShard,Is.EqualTo(shard));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==InfluenceEffectTypeIds.PlaceInfluence && Text(n.NormalizedArguments,"causeKind")==InfluenceCauseKinds.Direct),Is.EqualTo(opponent?1:0));
        }
        [Test]
        public void GavialCleanupMovesSameTokenAndOpponentInfluenceWithoutCityMoveSideEffects()
        {
            var state=State();Use(state,"gavial_the_invincible","tactic");var token=state.ItemZones.Items.Single(i=>i.TypeId=="infected.gavial_token");
            string target=new TokenMovementProfileRegistry().GetDestinationLocationIds(state,token.ItemId,"core.adjacent_location_token_move").First();var marker=PlaceFixtureInfluence(state,2,InfluenceService.GetLocationSlotId(target,0));
            int gold=state.FindPlayer(1).Resources.GoldVoucher;Cleanup(state);Answer(state,target);Answer(state,marker.ItemId);Assert.That(Open(state),Is.Not.Null);Answer(state,Open(state).CandidateIds.First());Drain(state);state=Restore(state);
            Assert.That(TokenMovementProfileRegistry.GetLocationId(state,token.ItemId),Is.EqualTo(target));Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==marker.ItemId).OwnerSubject.PlayerId,Is.EqualTo(2));Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold));Assert.That(state.EffectRuntime.RuleEvents.Any(e=>e.EventType==CityMoveEventTypeIds.CityMoveCompleted),Is.False);
        }
        [Test]
        public void SilencePublicStylePaysPrintedCostWithoutDeclarationOrMainActionBudget()
        {
            var state=State();DockFixtureCity(state,1);int ore=state.FindPlayer(1).Resources.Originium,iron=state.FindPlayer(1).Resources.Iron,shards=state.FindPlayer(1).Resources.OriginiumShard;
            StartCard(state,"silence_the_paradigmatic","strategy");Answer(state,"special_action.city_style.composite_power_system");Drain(state);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard,Is.EqualTo(shards-1));Assert.That(state.FindPlayer(1).Resources.Originium+state.FindPlayer(1).Resources.Iron,Is.EqualTo(ore+iron-3));Assert.That(state.FindPlayer(1).DeclaredCityStyles,Is.Empty);
            Assert.That(state.FindPlayer(1).UsedSpecialActionIdsThisRound,Does.Contain("special_action.city_style.composite_power_system"));state=Restore(state);
        }
        [Test]
        public void SilenceUndeclaredMilitaryStyleUsesZeroMarkersForX()
        {
            var state=State();StartCard(state,"silence_the_paradigmatic","strategy");Answer(state,"special_action.city_style.military_industrial_area");Drain(state);Assert.That(state.Map.Influences,Is.Empty);Assert.That(state.FindPlayer(1).DeclaredCityStyles,Is.Empty);
        }
        [Test]
        public void CeobeStrategyRestrictsActionChoicesWhileDiceAllowsSpecialAndEmptyHandRecipient()
        {
            var state=State();StartCard(state,"ceobe","strategy");Assert.That(Open(state).CandidateIds,Is.EquivalentTo(new[]{"deploy","dispatch","explore","move_city","build"}));Assert.That(state.FindPlayer(1).Resources.OriginiumShard,Is.EqualTo(19));Drain(state);
            state=State();foreach(var item in state.ItemZones.Items.Where(i=>i.ZoneId==FindZone(state,"hand",2).ZoneId).ToList())SetFixturePosition(state,item,FindZone(state,"discard",2).ZoneId);
            diceFace=5;StartCard(state,"ceobe","tactic");Assert.That(Open(state).CandidateIds,Does.Contain("special"));state=Restore(state);Drain(state);Assert.That(state.FindPlayer(2).HandCardIds,Is.EqualTo(new[]{Card(state,"ceobe").ItemId}));
        }

        [Test]
        public void JessicaBorrowsOpponentUnlockedSpecialAndKeepsBenefit()
        {
            var state=State();var board=Item(state,"enterprise.fussen_bank");var marker=state.ItemZones.Items.Single(i=>i.OwnerSubject.PlayerId==1 && state.ItemZones.Zones.Any(z=>z.ZoneId==i.ZoneId && z.ParentId==board.ItemId));marker.SlotId="level_0";
            var levels=new EnterpriseStateQuery(registry.EnterpriseDefinitions);
            Assert.That(levels.GetLevel(state,1,board.ItemId),Is.Zero);Assert.That(levels.GetLevel(state,2,board.ItemId),Is.EqualTo(1));
            int gold=state.FindPlayer(1).Resources.GoldVoucher,other=state.FindPlayer(2).Resources.GoldVoucher;StartCard(state,"jessica_the_liberated","strategy");
            Assert.That(Open(state).PromptKey,Is.EqualTo("core:interaction.enterprise.owner.choose"));
            Assert.That(Open(state).CandidateIds,Does.Contain("p2"));Answer(state,"p2");state=Restore(state);
            Assert.That(Open(state).InteractionTypeId,Is.EqualTo("enterprise.special"));
            Assert.That(Open(state).PromptKey,Is.EqualTo("enterprise.special.choose"));
            string choice=StableIdFactory.Create("candidate",board.ItemId,"enterprise.fussen_bank.level_1.special");Assert.That(Open(state).CandidateIds,Does.Contain(choice));Answer(state,choice);Drain(state);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold+8));Assert.That(state.FindPlayer(2).Resources.GoldVoucher,Is.EqualTo(other));
            var activated=View(state).Events.Single(e=>e.EventType=="EnterpriseSpecialEffectActivated");
            Assert.That(Text(activated.Payload,"specialId"),Is.EqualTo("enterprise.fussen_bank.level_1.special"));
            Assert.That(Number(activated.Payload,"unlockedByPlayerId"),Is.EqualTo(2));
            Assert.That(Number(activated.Payload,"executingPlayerId"),Is.EqualTo(1));
            Assert.That(Number(activated.Payload,"beneficiaryPlayerId"),Is.EqualTo(1));
        }
        [Test]
        public void SwireSelectsTwoDifferentSpecialsAndNeverRepeatsFirst()
        {
            var state=State();StartCard(state,"swire","strategy");string first=Open(state).CandidateIds.First();Answer(state,first);
            AdvanceToCharacterPrompt(state,"enterprise.special.choose");
            Assert.That(Open(state),Is.Not.Null);Assert.That(Open(state).InteractionTypeId,Is.EqualTo("enterprise.special"));
            Assert.That(Open(state).PromptKey,Is.EqualTo("enterprise.special.choose"));
            Assert.That(Open(state).CandidateIds,Does.Not.Contain(first));Answer(state,Open(state).CandidateIds.First());Drain(state);
            var activated=View(state).Events.Where(e=>e.EventType=="EnterpriseSpecialEffectActivated").ToList();
            Assert.That(activated.Count,Is.EqualTo(2));Assert.That(activated.Select(e=>Text(e.Payload,"specialId")).Distinct().Count(),Is.EqualTo(2));
            Assert.That(activated.All(e=>Number(e.Payload,"unlockedByPlayerId")==1 && Number(e.Payload,"executingPlayerId")==1),Is.True);
            Assert.That(Open(state),Is.Null);state=Restore(state);
            Assert.That(View(state).Events.Count(e=>e.EventType=="EnterpriseSpecialEffectActivated"),Is.EqualTo(2));
        }
        [TestCase("swire")][TestCase("lin")]
        public void MarkersRefreshCandidatesAndCleanupConsumesAtMostOnePerRound(string key)
        {
            var state=State(3);LoadInitialMarkers(state,key,2);
            var markers=StoredMarkers(state,key);Assert.That(markers.Count,Is.EqualTo(2));if(key=="swire")Assert.That(markers.Select(i=>i.OwnerSubject.PlayerId).Distinct().Count(),Is.EqualTo(2));else Assert.That(markers.All(i=>i.OwnerSubject.PlayerId==1),Is.True);
            int gold=state.FindPlayer(1).Resources.GoldVoucher;Cleanup(state);InvokeCleanup(state);Answer(state,markers.First().ItemId);
            if(key=="swire")
            {
                string enterprise=Open(state).CandidateIds.First();int owner=markers.First().OwnerSubject.PlayerId;var levels=new EnterpriseStateQuery(registry.EnterpriseDefinitions);int ownLevel=levels.GetLevel(state,1,enterprise),otherLevel=levels.GetLevel(state,owner,enterprise);
                Answer(state,enterprise);Drain(state);Assert.That(levels.GetLevel(state,1,enterprise),Is.EqualTo(ownLevel+1));Assert.That(levels.GetLevel(state,owner,enterprise),Is.EqualTo(otherLevel+1));
            }
            else
            {
                Assert.That(Open(state).PromptKey,Is.EqualTo("core:interaction.lin.transfer.choose"));
                Assert.That(Open(state).CandidateIds,Is.EquivalentTo(new[]{"p2","p3"}));
                int targetGold=state.FindPlayer(2).Resources.GoldVoucher;Answer(state,"p2");Drain(state);
                Assert.That(state.FindPlayer(2).Resources.GoldVoucher,Is.EqualTo(targetGold-10));
                Assert.That(state.EffectRuntime.InteractionRequests.All(r=>r.AnsweringPlayerId==1),Is.True,"林付款不得要求对手同意。");
            }
            Drain(state);Assert.That(StoredMarkers(state,key).Count,Is.EqualTo(1));Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold+(key=="swire"?6:10)));
            Emit(state,"PlayerCleanupStarted");Assert.That(Open(state),Is.Null);Assert.That(StoredMarkers(state,key).Count,Is.EqualTo(1));state=Restore(state);
            state.Round++;Cleanup(state);InvokeCleanup(state);if(key!="swire")Answer(state,StoredMarkers(state,key).Single().ItemId);
            if(key=="lin")
            {
                Assert.That(Card(state,key).ZoneId,Is.EqualTo(FindZone(state,"discard",1).ZoneId),"末标记归还后原卡应已离场。");
                int ownGold=state.FindPlayer(1).Resources.GoldVoucher,targetGold=state.FindPlayer(2).Resources.GoldVoucher;
                Answer(state,"p2");Drain(state);
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(ownGold+10));
                Assert.That(state.FindPlayer(2).Resources.GoldVoucher,Is.EqualTo(targetGold-10));
                Assert.That(state.EffectRuntime.InteractionRequests.All(r=>r.AnsweringPlayerId==1),Is.True);
            }
            Drain(state);Assert.That(StoredMarkers(state,key),Is.Empty);Assert.That(Card(state,key).ZoneId,Is.EqualTo(FindZone(state,"discard",1).ZoneId));
        }
        [Test]
        public void PlayerCandidatesIncludeEmptyOpponentSuppliesAndNeverRequestOpponentConsent()
        {
            var state=State(3);foreach(var marker in state.ItemZones.Items.Where(i=>i.ZoneId==FindZone(state,"supply",2).ZoneId).ToList())SetFixturePosition(state,marker,FindZone(state,"retired").ZoneId);
            StartCard(state,"swire","tactic");Assert.That(Open(state).CandidateIds,Is.EquivalentTo(new[]{"p2","p3"}));Answer(state,"p2","p3");Drain(state);Assert.That(StoredMarkers(state,"swire").Select(i=>i.OwnerSubject.PlayerId),Is.EqualTo(new[]{3}));Assert.That(state.EffectRuntime.InteractionRequests.All(r=>r.AnsweringPlayerId==1),Is.True);
        }
        [Test]
        public void FlowingShapeBetaRefillsEachChosenSlotImmediatelyWithDistinctNames()
        {
            var state=State();FillSupply(state);StartCard(state,"flowing_shape_beta","strategy");string first=Open(state).CandidateIds.First(id=>state.ItemZones.Items.Any(i=>i.ItemId==id));var initial=state.ItemZones.Items.Single(i=>i.ItemId==first);string slot=initial.ZoneId,name=FacilityCardDatabase.Get(state,initial.DefinitionId).Name;
            Answer(state,first);Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==first).ZoneId,Is.EqualTo(FindZone(state,"facility_deck").ZoneId));Assert.That(state.ItemZones.Items.Any(i=>i.ZoneId==slot),Is.True);
            var next=Open(state).CandidateIds.Where(id=>state.ItemZones.Items.Any(i=>i.ItemId==id)).ToList();Assert.That(next,Is.Not.Empty);Assert.That(next.All(id=>FacilityCardDatabase.Get(state,state.ItemZones.Items.Single(i=>i.ItemId==id).DefinitionId).Name!=name),Is.True);Answer(state,next.First());Drain(state);
            Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==ItemTransferEffectExecutor.RefillTypeId),Is.GreaterThanOrEqualTo(3));state=Restore(state);
        }
        [Test]
        public void TradeProgressProjectionShowsOnlyOwnerCountsAfterRecovery()
        {
            var state = State();
            StartCard(state, "lin", "strategy");
            string source=Card(state,"lin").ItemId;
            CompleteTrade(state, "buy_originium");
            state = Restore(state);
            var own=TradeReceipts(View(state),source);Assert.That(own.Count,Is.EqualTo(1));
            Assert.That(TradeReceipts(View(state,2),source),Is.Empty);
            var ownReceipt=own.Single();
            var otherReceipt=View(state,2).Events.SingleOrDefault(e=>e.EventId==ownReceipt.EventId);
            Assert.That(otherReceipt==null || otherReceipt.HostOnlyPayload.Kind==NormalizedValueKind.Null,Is.True);
            var ownRequest = Open(state);
            Assert.That(View(state).Interactions.Any(r => r.InteractionId == ownRequest.GetStableInteractionId()), Is.True);
            Assert.That(View(state, 2).Interactions.Any(r => r.InteractionId == ownRequest.GetStableInteractionId() && r.CandidateIds.Count > 0), Is.False);
            // 上限来自正式Lua续接与实际请求终止，不再读取已撤组合节点的专有字段。
            for(int count=1;count<10;count++)CompleteTrade(state,"buy_originium");
            Assert.That(TradeReceipts(View(state),source).Count,Is.EqualTo(10));Assert.That(Open(state),Is.Null);
            Assert.That(TradeReceipts(View(state,2),source),Is.Empty);
        }
    }
}
