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
        private IsolatedTestArtifacts artifacts;
        private string contentRoot;

        [SetUp] public void SetUp()
        {
            state = null; cleanupFixture = null; artifacts = IsolatedTestArtifacts.BeginCase();
            if (TestContext.CurrentContext.Test.MethodName == nameof(CleanupOffersWaitForClickAndResumeWithoutRepeatingPayment) ||
                TestContext.CurrentContext.Test.MethodName == nameof(CleanupInsufficientCostIsDisabledAndMandatoryRequestCannotBeSkipped)) return;
            contentRoot = artifacts.CopyContent(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"), "R2旧收尾私有输入");
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            registry = new EffectRegistry();
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            ItemTransferEffectExecutor.Register(registry); ContentPrimitiveEffectExecutor.Register(registry);
            CharacterContentEffectExecutor.Register(registry);
            var pack = ExternalContentPack.Load(contentRoot); artifacts.RecordContent("R2实际收尾内容", contentRoot, pack.ContentHash);
            var definitions = pack.CreateFacilities();
            registry.ContentPackHash = pack.ContentHash;
            state = new ItemZoneStateFactory().CreateForSetup(new GameState {
                GameId = "game.character_interaction", MapId = map.MapId, Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1, Phase = GamePhase.ActionRound1,
                ContentPackHash = pack.ContentHash, RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = pack.ContentHash },
                ContentDefinitions = new GameContentDefinitions { Facilities = definitions.ToList() },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Resources = new ResourceSet { Originium = 4 } }, new PlayerState { PlayerId = 2, Resources = new ResourceSet { Iron = 3 } } }
            }, map, new[] {
                new ItemRecipeEntry { EntryId = "marker", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 6, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "card", DefinitionId = "texas", TypeId = "core.basic_character_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" }
            }.Concat(definitions.GroupBy(d => d.Name).Select(g => g.First()).Take(8).Select((d, i) => new ItemRecipeEntry { EntryId = "facility_" + i, DefinitionId = d.FacilityId, TypeId = "core.facility_card", Quantity = 1, InitialZoneRole = "facility_deck" })).ToList());
        }
        [TearDown] public void FinishEvidence()
        {
            if (state != null) artifacts.WriteLabeledText("当前真实状态", JsonConvert.SerializeObject(state));
            artifacts.Finish();
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
            var pack = ExternalContentPack.Load(contentRoot)
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

        private CleanupSourceTestFixture cleanupFixture;
        private void OpenCleanup(bool mandatory = false)
        {
            // R2旧合成cleanup_offer现场保存在005原证据。本夹具改走实际声明、事件、统一登记。
            cleanupFixture = new CleanupSourceTestFixture(artifacts, legacyResource: true, initialMarkers: 0, mandatory: mandatory, inMemoryInput: true);
            state = cleanupFixture.State; registry = cleanupFixture.Registry;
            if (mandatory)
            {
                var required = registry.GetSettlementOptions(state, 1).Single(o => !o.CanDeclineWholeEffect);
                Assert.That(Engine().TryInvokeCharacterSettlementOption(1, required.OptionId, "", required.StateRevision, out _, out string reason), Is.True, reason);
                Drain();
            }
        }

        [Test] public void CleanupOffersWaitForClickAndResumeWithoutRepeatingPayment()
        {
            OpenCleanup();
            // 旧character.cleanup.settlement模态断言迁移为同一业务等待：U15不伪造模态请求。
            Assert.That(SettlementEntryService.HasPendingPlayerSelection(state.EffectRuntime), Is.True);
            var options = registry.GetSettlementOptions(state, 1); Assert.That(options.Count, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(4));
            var option = options[0]; Assert.That(Engine().TryInvokeCharacterSettlementOption(1, option.OptionId, "", option.StateRevision, out _, out var error), Is.True, error); Drain();
            if (state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && r.CandidateIds.Contains("continue"))) Answer("continue");
            if (state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && r.InteractionTypeId == ResourceEffectExecutor.PaymentConfirmationInteractionTypeId))
                Answer(ResourceEffectExecutor.PaymentConfirmationCandidateId);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            // 不再仅序列化Runtime；正式Host恢复必须包含来源与付款防重证据。
            var restored = artifacts.Recover(state, "008旧收尾实际Host恢复", registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); state = restored.State;
            Assert.That(Engine().TryInvokeCharacterSettlementOption(1, option.OptionId, "", option.StateRevision, out bool repeated, out error), Is.True, error);
            Assert.That(repeated, Is.True); Drain(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(Engine().TryEndSettlementAction(1, option.WindowId, "", "", state.EffectRuntime.StateRevision, "008.once.close", out error), Is.True, error); Drain();
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.MainNodes.Single(n => n.ExecutionEffectId == cleanupFixture.RootId).CompletionRequested, Is.True);
            Assert.That(registry.GetSettlementOptions(state, 1), Is.Empty);
        }

        [Test] public void CleanupInsufficientCostIsDisabledAndMandatoryRequestCannotBeSkipped()
        {
            OpenCleanup(true); var required = Open();
            Assert.That(required.AllowDecline, Is.False);
            Assert.That(Engine().TrySubmitInteraction(required.GetStableInteractionId(), 1, required.StateRevision, NormalizedValue.CreateBoolean(false), out _), Is.False);
            // 正式投影保留禁用行；等价业务要求是进行中必答不能被别的条目绕过。
            Assert.That(registry.GetSettlementOptions(state, 1).All(o => !o.IsEnabled), Is.True);
            Answer("required"); state.FindPlayer(1).Resources.Originium = 0;
            var options = registry.GetSettlementOptions(state, 1); Assert.That(options.Count, Is.EqualTo(2));
            Assert.That(options.All(o => !o.IsEnabled && o.DisabledReasonCode == "core:presentation.unavailable"), Is.True);
            Assert.That(Engine().TryEndSettlementAction(1, options[0].WindowId, "", "", state.EffectRuntime.StateRevision, "008.once.insufficient.close", out string reason), Is.True, reason); Drain();
            Assert.That(state.FindPlayer(1).Score, Is.Zero);
        }

        [Test] public void PassengerRealLuaUsesOpponentThenResourceThenQuantityAndKeepsBothScores()
        {
            const string ability = "operator.passenger.strategy";
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var configuration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2,
                EnabledExpansionIds = new List<string> { "core", "enterprise" } };
            var pack = ExternalContentPack.Load(contentRoot)
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
