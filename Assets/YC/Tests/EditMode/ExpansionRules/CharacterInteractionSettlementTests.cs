using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CharacterInteractionSettlementTests
    {
        private EffectRegistry registry;
        private GameState state;

        [SetUp] public void SetUp()
        {
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            registry = new EffectRegistry();
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            ItemTransferEffectExecutor.Register(registry); ContentPrimitiveEffectExecutor.Register(registry);
            CharacterContentEffectExecutor.Register(registry);
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            var definitions = pack.CreateFacilities();
            registry.ContentPackHash = pack.ContentHash;
            state = new ItemZoneStateFactory().CreateForSetup(new GameState {
                GameId = "game.character_interaction", MapId = map.MapId, Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1, Phase = GamePhase.ActionRound1,
                ContentPackHash = pack.ContentHash, RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = pack.ContentHash },
                ContentDefinitions = new GameContentDefinitions { Facilities = definitions.ToList() },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Resources = new ResourceSet { Originium = 4 } }, new PlayerState { PlayerId = 2, Resources = new ResourceSet { Iron = 3 } } }
            }, map, new[] {
                new ItemRecipeEntry { EntryId = "marker", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 6, PerPlayer = true, InitialZoneRole = "player_markers" },
                new ItemRecipeEntry { EntryId = "card", DefinitionId = "texas", TypeId = "core.basic_character_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" }
            }.Concat(definitions.GroupBy(d => d.Name).Select(g => g.First()).Take(8).Select((d, i) => new ItemRecipeEntry { EntryId = "facility_" + i, DefinitionId = d.FacilityId, TypeId = "core.facility_card", Quantity = 1, InitialZoneRole = "facility_deck" })).ToList());
        }
        private EffectTreeExecutor Engine() => new EffectTreeExecutor(state, registry);
        private string Run(EffectSpec spec) { string id = Engine().CreateRoot(spec); Drain(); return id; }
        private void Drain() { Assert.That(Engine().RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); }
        private InteractionRequest Open() => state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
        private void Answer(NormalizedValue value)
        {
            var r = Open(); Assert.That(Engine().TrySubmitInteraction(r.GetStableInteractionId(), r.AnsweringPlayerId, r.StateRevision, value, out var reason), Is.True, reason); Drain();
        }
        private void Answer(string value) => Answer(NormalizedValue.CreateString(value));
        private EffectSpec ResourceQuantity() => EffectSpec.Create(LuaDomainEffectTypeIds.Choice,
            Object("player", NormalizedValue.CreateString("1"), "amountRange", Object("minAmount", NormalizedValue.CreateInteger(0),
                "maxAmount", NormalizedValue.CreateInteger(4), "sourcePlayer", NormalizedValue.CreateStableReference("player", "2"),
                "resourceType", NormalizedValue.CreateString("iron"))), 1);

        [Test] public void QuantityUsesEmptyCandidateRequestAndRefreshesStaleSupplyWithoutClamping()
        {
            string id = Run(ResourceQuantity()); var request = Open();
            Assert.That(request.InteractionTypeId, Is.EqualTo(ContentPrimitiveEffectExecutor.QuantityInteractionTypeId));
            Assert.That(request.CandidateIds, Is.Empty); Assert.That(Number(request.PromptParameters, "maxAmount"), Is.EqualTo(3));
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString("4"), out _), Is.False);
            state.FindPlayer(2).Resources.Iron = 1; Answer("3");
            Assert.That(Number(Open().PromptParameters, "maxAmount"), Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
            Answer("0"); Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(1));
            Assert.That(Engine().GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Engine().GetNode(id).NormalizedResult.IntegerValue, Is.Zero);
        }

        [Test] public void SupplyStopPreservesFirstRealFacilityAndItsImmediateRefill()
        {
            var card = SetUpEunectesTactic();
            string slot = FindZone(state, "facility_supply_0").ZoneId;
            Run(RefillSpec(1, slot));
            string original = state.ItemZones.Items.Single(i => i.ZoneId == slot).ItemId;
            string retired = FindZone(state, "retired").ZoneId;
            var retiredBefore = state.ItemZones.Items.Where(i => i.ZoneId == retired).Select(i => i.ItemId).ToList();
            int iron = state.FindPlayer(1).Resources.Iron;
            Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(card.ItemId);
            string id = Run(CharacterLifecycleService.CreateUseSpec(1, card.ItemId, "tactic"));
            Assert.That(Open().PromptKey, Is.EqualTo("core:interaction.facility.take.choose"));
            Assert.That(Open().CandidateIds, Is.EqualTo(new[] { original }));
            Assert.That(Open().CandidateIds, Does.Not.Contain("choice.skip")); Answer(original);
            string refill = state.ItemZones.Items.Single(i => i.ZoneId == slot).ItemId;
            Assert.That(refill, Is.Not.EqualTo(original));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == original).ZoneId, Is.EqualTo(retired));
            var transfer = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId &&
                Value(n.NormalizedArguments, "itemRefs").Items.Any(item => Text(item, "itemId") == original));
            Assert.That(transfer.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Value(transfer.NormalizedResult, "movedItemIds").Items.Select(v => v.StringValue), Is.EqualTo(new[] { original }));
            var refillNode = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ItemTransferEffectExecutor.RefillTypeId &&
                n.ContinuationContext != null && Text(n.ContinuationContext, "source") == card.ItemId);
            Assert.That(refillNode.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Value(refillNode.NormalizedResult, "movedItemIds").Items.Select(v => v.StringValue), Is.EqualTo(new[] { refill }));
            Assert.That(Open().PromptKey, Is.EqualTo("core:interaction.facility.take.choose"));
            Assert.That(Open().CandidateIds, Is.EqualTo(new[] { refill }), "立即补入的不同名真实设施应进入重新查询的候选。");
            Assert.That(Open().CandidateIds, Does.Not.Contain("choice.skip"));
            Assert.That(Open().MinSelections, Is.Zero); Answer(NormalizedValue.CreateBoolean(false));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == original).ZoneId, Is.EqualTo(retired));
            Assert.That(state.ItemZones.Items.Single(i => i.ZoneId == slot).ItemId, Is.EqualTo(refill));
            Assert.That(state.ItemZones.Items.Where(i => i.ZoneId == retired).Select(i => i.ItemId), Is.EquivalentTo(retiredBefore.Concat(new[] { original })));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId &&
                Text(Value(n.NormalizedArguments, "destination"), "zoneId") == retired), Is.EqualTo(1));
            Assert.That(Engine().GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(iron + 3));
            Assert.That(state.CharacterUses.Single().CompletedModes, Does.Contain("tactic"));
        }
        private ItemInstanceState SetUpEunectesTactic()
        {
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var configuration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2,
                EnabledExpansionIds = new List<string> { "core", "enterprise" } };
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"))
                .CreateActiveSnapshot(configuration);
            configuration.ContentPackHash = pack.ContentHash;
            var facilities = pack.CreateFacilities();
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            ItemTransferEffectExecutor.Register(registry); ContentPrimitiveEffectExecutor.Register(registry);
            CharacterCoverEffectExecutor.Register(registry); CharacterLifecycleService.Register(registry);
            CharacterCardLuaCatalog.Register(registry, pack);
            state = new ItemZoneStateFactory().CreateForSetup(new GameState {
                GameId = "game.eunectes_supply_stop", MapId = map.MapId, Round = 1, ActionRound = 1,
                CurrentPlayerId = 1, StartPlayerId = 1, Phase = GamePhase.ActionRound1,
                ContentPackHash = pack.ContentHash, RuleConfiguration = configuration,
                ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(), Facilities = facilities.ToList() },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Resources = new ResourceSet { Originium = 4 } },
                    new PlayerState { PlayerId = 2, Resources = new ResourceSet { Iron = 3 } } }
            }, map, new[] {
                new ItemRecipeEntry { EntryId = "eunectes", DefinitionId = "operator.eunectes", TypeId = "core.operator_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" }
            }.Concat(facilities.GroupBy(d => d.Name).Select(g => g.First()).Take(8).Select((d, i) => new ItemRecipeEntry {
                EntryId = "facility_" + i, DefinitionId = d.FacilityId, TypeId = "core.facility_card", Quantity = 1, InitialZoneRole = "facility_deck" })).ToList());
            return state.ItemZones.Items.Single(i => i.DefinitionId == "operator.eunectes" && i.ControllerSubject.PlayerId == 1);
        }

        [TestCase(0)] [TestCase(3)] public void MarkerQuantityMovesRealInstancesAndHonorsEmptyCarrierExit(int amount)
        {
            var card = state.ItemZones.Items.Single(i => i.TypeId == "core.basic_character_card" && i.ControllerSubject.PlayerId == 1);
            card.ZoneId = FindZone(state, "persistent", 1).ZoneId;
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, card.ItemId, new CardZoneDefinition {
                LocalZoneId = "markers", ZoneTemplateId = "core.zone.card_cube_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
                AcceptedTypeIds = new List<string> { "core.player_marker" }, CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered,
                VisibilityPolicyId = ItemZoneVisibility.Public, EntryPolicyId = "core.owned_player_marker", ExitPolicyId = "core.card_payment",
                CarrierExitPolicyId = "core.return_contents_before_exit", EmptyExitPolicyId = "core.discard_carrier_when_empty"
            }, out string zone, out string error), Is.True, error);
            var supply = state.ItemZones.Items.Where(i => i.ZoneId == FindZone(state, "player_markers", 1).ZoneId).Select(i => i.ItemId).ToHashSet();
            Run(EffectSpec.Create(ContentPrimitiveEffectExecutor.LoadMarkers, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"),
                "cardInstanceId", NormalizedValue.CreateString(card.ItemId), "localZoneId", NormalizedValue.CreateString("markers"), "maxCount", NormalizedValue.CreateInteger(4)), 1));
            Assert.That(Number(Open().PromptParameters, "maxAmount"), Is.EqualTo(4));
            if (amount == 0) Answer(NormalizedValue.CreateBoolean(false)); else Answer(amount.ToString());
            var actual = state.ItemZones.Items.Where(i => i.ZoneId == zone).ToList();
            Assert.That(actual.Count, Is.EqualTo(amount)); Assert.That(actual.All(i => supply.Contains(i.ItemId) && i.OwnerSubject.PlayerId == 1), Is.True);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == card.ItemId).ZoneId, Is.EqualTo(FindZone(state, amount == 0 ? "discard" : "persistent", 1).ZoneId));
        }

        private void OpenCleanup(bool mandatory = false)
        {
            new RoundExecutionService(new TurnOrderService(), new CharacterCardService(), new MainActionBudgetService(), new SpecialActionLifecycleService(), registry);
            state.Phase = GamePhase.Cleanup;
            var cards = state.ItemZones.Items.Where(i => i.TypeId == "core.basic_character_card").ToList();
            foreach (var card in cards)
            {
                card.ControllerSubject = ItemSubjectRef.Player(1); card.ZoneId = FindZone(state, "persistent", 1).ZoneId;
                state.CharacterUses.Add(new CharacterUseRuntimeState { CardInstanceId = card.ItemId, ExecutingPlayerId = 1,
                    Modes = new List<CharacterModeContract> { new CharacterModeContract { AbilityId = "cleanup." + card.ItemId, SkillType = "persistent", Activated = true, SubscriptionActive = true,
                        SettlementActionId = CharacterContentEffectExecutor.CleanupActionId, TitleKey = "收尾能力", DescriptionKey = "支付源岩后得分" } } });
            }
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.cleanup.sources", "PlayerCleanupStarted", "*", "test.cleanup.sources", context => {
                var offers = new List<EffectSpec>();
                if (mandatory) offers.Add(EffectSpec.Create("test.cleanup.required", playerId: 1));
                foreach (var card in cards)
                {
                    var spec = EffectSpec.Create(CharacterContentEffectExecutor.CleanupOfferTypeId, Object("apiVersion", NormalizedValue.CreateInteger(1),
                        "cardInstanceId", NormalizedValue.CreateString(card.ItemId), "abilityId", NormalizedValue.CreateString("cleanup." + card.ItemId),
                        "windowEffectId", NormalizedValue.CreateString(context.Event.SourceEffectId)), 1);
                    spec.NestedEffectGroups.Add(new List<EffectSpec> { EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1, ResourceType.Originium, 2) }, new[] { ResourceEffectSpecFactory.GainScore(1, 1) }) });
                    offers.Add(spec);
                }
                return offers;
            }, EffectHandlerRole.Observer));
            registry.Register(new EffectRegistration("test.cleanup.required", c => c.Node.FlowStage.Length > 0 ? EffectStepResult.Completed() :
                EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec { InteractionTypeId = "test.cleanup.required", AnsweringPlayerId = 1, AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "required" } })));
            var main = new MainlineNodeRuntimeState { NodeId = "main.cleanup", NodeTypeId = RoundMainlineNodeTypeIds.PlayerCleanupWindow, PlayerId = 1, RoundNumber = 1, ActionRound = 1 };
            state.EffectRuntime.MainNodes.Add(main); state.EffectRuntime.ActiveMainNodeId = main.NodeId;
            main.ExecutionEffectId = Engine().CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId, Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1));
            state.EffectRuntime.MainNodes.Single().ExecutionEffectId = main.ExecutionEffectId;
            Drain();
        }

        [Test] public void CleanupOffersWaitForClickAndResumeWithoutRepeatingPayment()
        {
            OpenCleanup(); Assert.That(Open().InteractionTypeId, Is.EqualTo(CharacterContentEffectExecutor.CleanupInteractionTypeId));
            var options = CharacterSettlementOptionQuery.GetOptions(state, 1, ""); Assert.That(options.Count, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(4));
            var option = options[0]; Assert.That(Engine().TryInvokeCharacterSettlementOption(1, option.OptionId, "", option.StateRevision, out _, out var error), Is.True, error); Drain();
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            // 序列化 EffectRuntime 的模板及收据后重建内核；不替代完整 HostRecovery 验收。
            var restoredRuntime = JsonConvert.DeserializeObject<EffectRuntimeState>(JsonConvert.SerializeObject(state.EffectRuntime));
            state = GameStateCloneService.DeepClone(state); state.EffectRuntime = restoredRuntime;
            Assert.That(Engine().TryInvokeCharacterSettlementOption(1, option.OptionId, "", option.StateRevision, out bool repeated, out error), Is.True, error);
            Assert.That(repeated, Is.True); Drain(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Answer(NormalizedValue.CreateBoolean(false)); Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.MainNodes.Single().CompletionRequested, Is.True);
            Assert.That(CharacterSettlementOptionQuery.GetOptions(state, 1, ""), Is.Empty);
        }

        [Test] public void CleanupInsufficientCostIsDisabledAndMandatoryRequestCannotBeSkipped()
        {
            OpenCleanup(true); var required = Open();
            Assert.That(required.AllowDecline, Is.False);
            Assert.That(Engine().TrySubmitInteraction(required.GetStableInteractionId(), 1, required.StateRevision, NormalizedValue.CreateBoolean(false), out _), Is.False);
            Assert.That(CharacterSettlementOptionQuery.GetOptions(state, 1, ""), Is.Empty);
            Answer("required"); state.FindPlayer(1).Resources.Originium = 0;
            var options = CharacterSettlementOptionQuery.GetOptions(state, 1, ""); Assert.That(options.Count, Is.EqualTo(2));
            Assert.That(options.All(o => !o.IsEnabled && o.DisabledReasonCode == "insufficient_resource"), Is.True);
            Answer(NormalizedValue.CreateBoolean(false)); Assert.That(state.FindPlayer(1).Score, Is.Zero);
        }

        [Test] public void PassengerRealLuaUsesOpponentThenResourceThenQuantityAndKeepsBothScores()
        {
            const string ability = "operator.passenger.strategy";
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var configuration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2,
                EnabledExpansionIds = new List<string> { "core", "enterprise" } };
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"))
                .CreateActiveSnapshot(configuration);
            configuration.ContentPackHash = pack.ContentHash;
            Assert.That(pack.ActiveDefinitions.Any(definition => definition.DefinitionId == "operator.passenger"), Is.True,
                "企业扩活动快照缺少异客定义。");
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            ItemTransferEffectExecutor.Register(registry); ContentPrimitiveEffectExecutor.Register(registry);
            CharacterContentEffectExecutor.Register(registry);
            CharacterCardLuaCatalog.Register(registry, pack);
            Assert.That(registry.HasEventHandler("content.operator.passenger.strategy"), Is.True,
                "企业扩活动快照未注册异客策略处理器。");
            state = new ItemZoneStateFactory().CreateForSetup(new GameState
            {
                GameId = "game.passenger_active_fixture", MapId = map.MapId, Round = 1, ActionRound = 1,
                CurrentPlayerId = 1, StartPlayerId = 1, Phase = GamePhase.ActionRound1, ContentPackHash = pack.ContentHash,
                RuleConfiguration = configuration,
                ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(), Facilities = pack.CreateFacilities().ToList() },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Resources = new ResourceSet { Originium = 4 } },
                    new PlayerState { PlayerId = 2, Resources = new ResourceSet { Iron = 3 } } }
            }, map, Array.Empty<ItemRecipeEntry>());
            registry.Register(new EffectRegistration("test.passenger", c => EffectStepResult.Completed().AddEvent(new EffectEventRequest
                { EventType = CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType, RouteKey = ability, PlayerId = 1 })));
            Run(EffectSpec.Create("test.passenger", playerId: 1));
            Assert.That(Open().CandidateIds, Is.EqualTo(new[] { "player:2" })); Answer("player:2");
            Assert.That(Open().CandidateIds, Is.EqualTo(new[] { "originium", "originium-shard", "iron" })); Answer("iron");
            Assert.That(Open().AnswerSchema, Is.EqualTo(ContentPrimitiveEffectExecutor.QuantityAnswerSchema)); Answer("0");
            Assert.That(state.Players.Select(p => p.Score), Is.EqualTo(new[] { 1, 1 })); Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(3));
        }
    }
}
