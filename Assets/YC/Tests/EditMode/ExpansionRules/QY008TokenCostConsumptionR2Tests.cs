using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 57调用端独占隔离合同。真实ItemZones/纯规则调度/付款执行；不计正常对局或完整Host验收。
    public sealed class QY008TokenCostConsumptionR2Tests
    {
        private GameState state;
        private EffectRegistry registry;
        private IsolatedTestArtifacts artifacts;
        private readonly List<string> carriers = new List<string>(), zones = new List<string>();
        private readonly List<List<string>> selected = new List<List<string>>();
        private EffectTreeExecutor Engine => new EffectTreeExecutor(state, registry);
        [SetUp] public void Begin() => artifacts = IsolatedTestArtifacts.BeginCase();
        [TearDown] public void End()
        {
            if (state != null) artifacts.WriteLabeledText("008-R2调用端隔离最终状态", JsonConvert.SerializeObject(state, Formatting.Indented));
            artifacts.Finish(); state = null; carriers.Clear(); zones.Clear(); selected.Clear();
        }
        private void Setup(int firstCount = 3, int limit = 2, bool sharedQuota = false, string selectionPolicy = "equivalent", bool discardFirstCarrier = false)
        {
            var map = StaticMapDefinitions.CreateThreePlayerMap(); const string hash = "qy008.token-cost.r2.isolated";
            var source = new GameState { GameId = "qy008.r2." + Guid.NewGuid().ToString("N"), MapId = map.MapId, ContentPackHash = hash,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = hash }, ContentDefinitions = new GameContentDefinitions(),
                Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1,
                LaunchPlayerOrder = new List<int> { 1, 2 }, Players = new List<PlayerState> {
                    new PlayerState { PlayerId = 1, Name = "隔离甲", Resources = new ResourceSet { Originium = 100, Iron = 100, OriginiumShard = 100, GoldVoucher = 100 } },
                    new PlayerState { PlayerId = 2, Name = "隔离乙" } } };
            var recipe = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "supply", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = firstCount + 2, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "reserve", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 2, PerPlayer = true, InitialZoneRole = "reserve" },
                new ItemRecipeEntry { EntryId = "non-cube", DefinitionId = "infected.gavial_token", TypeId = "infected.gavial_token", Quantity = 1, PerPlayer = true, InitialZoneRole = "reserve" } };
            var declarations = new List<ItemCardZoneDeclaration>();
            for (int i = 0; i < 2; i++)
            {
                string definition = "qy008.cost.source_" + i;
                recipe.Add(new ItemRecipeEntry { EntryId = definition, DefinitionId = definition, TypeId = discardFirstCarrier && i == 0 ? "core.operator_card" : "entrepreneur.ability_card",
                    Quantity = 1, PerPlayer = true, InitialZoneRole = discardFirstCarrier && i == 0 ? "hand" : "entrepreneur_active" });
                declarations.Add(new ItemCardZoneDeclaration { DefinitionId = definition, CardZones = new List<CardZoneDefinition> {
                    new CardZoneDefinition { LocalZoneId = "markers", ZoneTemplateId = "core.zone.card_cube_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
                        AcceptedTypeIds = new List<string> { "core.cube" }, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer",
                        CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered, VisibilityPolicyId = ItemZoneVisibility.Owner, CarrierExitPolicyId = "core.preserve_contents",
                        EmptyExitPolicyId = discardFirstCarrier && i == 0 ? "core.discard_carrier_when_empty" : "" } } });
            }
            state = new ItemZoneStateFactory().CreateForSetup(source, map, recipe, declarations);
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            registry = new EffectRegistry { ContentPackHash = hash };
            ItemTransferEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry); ResourcePaymentSourceService.Register(registry);
            var abilities = new List<AbilitySourceDeclaration>();
            for (int i = 0; i < 2; i++)
            {
                int index = i; string definition = "qy008.cost.source_" + i, handler = definition + ".query";
                var card = state.ItemZones.Items.Single(item => item.DefinitionId == definition && item.ControllerSubject.PlayerId == 1);
                if (discardFirstCarrier && i == 0) card.ZoneId = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.character_persistent" && z.OwnerPlayerId == 1).ZoneId;
                string zone = state.ItemZones.Zones.Single(z => z.ParentId == card.ItemId && z.LocalZoneId == "markers").ZoneId;
                carriers.Add(card.ItemId); zones.Add(zone);
                string from = i == 0 ? FindZone(state, "supply", 1).ZoneId : FindZone(state, "reserve").ZoneId;
                var ids = state.ItemZones.Items.Where(item => item.ZoneId == from && item.TypeId == "core.player_marker" && item.OwnerSubject.PlayerId == 1)
                    .OrderBy(item => item.ItemId, StringComparer.Ordinal).Take(i == 0 ? firstCount : 2).Select(item => item.ItemId).ToList();
                // 仅安排本用例初始库存；不注入独立程序，不冒充正常装载或激活。
                foreach (string id in ids) state.ItemZones.Items.Single(item => item.ItemId == id).ZoneId = zone;
                selected.Add(ids);
                abilities.Add(new AbilitySourceDeclaration { DefinitionId = definition, AbilityId = definition, HandlerRef = handler, DeclarationVersion = "1.0.0", Kind = "ruleQuery",
                    AuthorizationPolicy = "controller", EffectiveZones = new List<string> { state.ItemZones.Zones.Single(z => z.ZoneId == card.ZoneId).TemplateId }, QueryKinds = new List<string> { ResourcePaymentSourceService.QueryKind }, PresentationRef = definition,
                    AllowedOperations = new List<string> { "carrier.transfer.original_supply:" + definition + ":markers:core.cube" } });
                registry.RegisterAbilityHandler(handler, "qy008.r2.rule_" + i, _ => Array.Empty<EffectSpec>());
                registry.RegisterAbilityPureHandler(handler, (call, query) => Object("queryId", NormalizedValue.CreateString(query.QueryId), "basisDigest", NormalizedValue.CreateString(query.FactsDigest),
                    "items", NormalizedValue.CreateArray(new[] { Object("ruleId", NormalizedValue.CreateString("source_" + index), "purposeKind", NormalizedValue.CreateString("payment"),
                        "allowedResourceTypes", Strings(new[] { "Originium", "OriginiumShard", "Iron" }), "maxUnits", NormalizedValue.CreateInteger(limit),
                        "quotaGroupId", NormalizedValue.CreateString(sharedQuota ? "shared" : "source_" + index), "quotaLimit", NormalizedValue.CreateInteger(limit), "quotaScope", NormalizedValue.CreateString("payment"),
                        "unitResourceCost", PaymentResources.Write(index == 0 ? new ResourceSet { GoldVoucher = 1 } : new ResourceSet()),
                        "itemCost", Object("localZoneId", NormalizedValue.CreateString("markers"), "countPerUnit", NormalizedValue.CreateInteger(1), "destinationKind", NormalizedValue.CreateString("original_supply"),
                            "operationProfileId", NormalizedValue.CreateString("core.cube_storage"), "selectionPolicy", NormalizedValue.CreateString(selectionPolicy))) })));
            }
            registry.FreezeAbilityCatalog(abilities); state.ContentDefinitions.AbilitySources = registry.AbilityDeclarations.ToList();
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
        }
        private void Pump()
        {
            var result = Engine.RunUntilQuiescent();
            if (result.Faulted) artifacts.WriteLabeledText("008-R2首次故障", JsonConvert.SerializeObject(state, Formatting.Indented));
            Assert.That(result.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private EffectNodeRuntimeState Run(EffectSpec spec) { string id = Engine.CreateRoot(spec); Pump(); return Engine.GetNode(id); }
        private EffectNodeRuntimeState Open(ResourceSet cost = null) => Run(ResourceEffectSpecFactory.Pay(1, cost ?? new ResourceSet { Originium = 2, Iron = 2 }));
        private PaymentSourceRule Rule(EffectNodeRuntimeState owner, int index) => ResourcePaymentSourceService.Query(state, registry, owner, owner.Payment.PaymentCost).Single(r => r.RuleId == "source_" + index);
        private bool Draft(EffectNodeRuntimeState owner, int index, ResourceSet allocation, IReadOnlyList<string> ids, out string reason)
        {
            var rule = Rule(owner, index);
            return ResourcePaymentDraftService.TrySetSourceDraft(state, registry, owner, rule.SourceRef.SourceKey, rule.RuleId, owner.Payment.PaymentGeneration, 0, allocation, ids, "", out reason);
        }
        private void TwoDrafts(EffectNodeRuntimeState owner)
        {
            Assert.That(Draft(owner, 0, new ResourceSet { Originium = 1, Iron = 1 }, selected[0].Take(2).ToList(), out string reason), Is.True, reason);
            Assert.That(Draft(owner, 1, new ResourceSet { Originium = 1, Iron = 1 }, selected[1], out reason), Is.True, reason);
        }
        private EffectExecutionContext Context(EffectNodeRuntimeState node) => (EffectExecutionContext)Activator.CreateInstance(typeof(EffectExecutionContext), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { state, node, registry, EffectRuntimeLimits.Default }, null);
        private void Pay()
        {
            var input = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(Engine.TrySubmitInteraction(input.GetStableInteractionId(), 1, input.StateRevision, NormalizedValue.CreateString("pay"), out string reason), Is.True, reason); Pump();
        }

        [Test] public void NonCubeInventoryTokenIsARealConditionCost()
        {
            Setup(); var item = state.ItemZones.Items.Single(i => i.TypeId == "infected.gavial_token" && i.OwnerSubject.PlayerId == 1);
            Assert.That(item.PhysicalKind, Is.EqualTo(ItemPhysicalKind.Token));
            Run(EffectSpec.Condition(new[] { TokenSpec(1, new[] { item.ItemId }, item.ZoneId, FindZone(state, "retired").ZoneId, 1, "core.token_storage") },
                new[] { ResourceEffectSpecFactory.GainScore(1, 1) }));
            var transfer = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == TypeId);
            Assert.That(AbilityActionExecutor.IsCommittedCost(state, transfer), Is.True); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
        }
        [Test] public void ProofMatchesRequestedTokensRatherThanWholePositionListLength()
        {
            Setup(); var ids = state.ItemZones.Items.Where(i => i.ZoneId == FindZone(state, "supply", 1).ZoneId).Take(2).Select(i => i.ItemId).ToList();
            var first = Run(TokenSpec(1, ids.Take(1), FindZone(state, "supply", 1).ZoneId, FindZone(state, "retired").ZoneId, 1)); string firstId = first.EffectId;
            var second = Run(TokenSpec(1, ids.Skip(1), FindZone(state, "supply", 1).ZoneId, FindZone(state, "retired").ZoneId, 1));
            first = Engine.GetNode(firstId);
            // 构造附加位置证据的合同控制，不声称此安排执行了真实空载体离场。
            first.PositionChanges.Add(GameStateCloneService.DeepClone(second.PositionChanges.Single()));
            Assert.That(AbilityActionExecutor.HasCommittedTokenTransfer(state, first), Is.True);
            first.PositionChanges.Add(GameStateCloneService.DeepClone(first.PositionChanges[0]));
            Assert.That(AbilityActionExecutor.HasCommittedTokenTransfer(state, first), Is.False);
        }
        [Test] public void StructuralReceiptCannotHideChangedOriginalSupply()
        {
            Setup(); string id = state.ItemZones.Items.First(i => i.ZoneId == FindZone(state, "supply", 1).ZoneId).ItemId;
            Run(TokenSpec(1, new[] { id }, FindZone(state, "supply", 1).ZoneId, FindZone(state, "retired").ZoneId, 1));
            var returned = Run(TokenSpec(1, new[] { id }, FindZone(state, "retired").ZoneId, null, 1));
            var bad = GameStateCloneService.DeepClone(state); bad.ItemZones.Items.Single(i => i.ItemId == id).OriginalSupplyZoneId = FindZone(state, "retired").ZoneId;
            var node = bad.EffectRuntime.EffectNodes.Single(n => n.EffectId == returned.EffectId);
            Assert.That(ItemTransferService.TryValidateBatchReceipt(node, out _), Is.True);
            Assert.That(AbilityActionExecutor.HasCommittedTokenTransfer(bad, node), Is.False);
        }
        [Test] public void TwoTokenSourcesAndResourceSurchargesCommitOnceAndRecoverTheirExactRequests()
        {
            Setup(); var owner = Open(); TwoDrafts(owner); string before = JsonConvert.SerializeObject(state);
            Assert.That(ResourcePaymentPlanService.TryBuild(state, registry, owner, out var plan, out string reason), Is.True, reason);
            Assert.That(plan.TokenPlans.Count, Is.EqualTo(2)); Assert.That(plan.FinalCost.GoldVoucher, Is.EqualTo(2));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before)); Pay(); owner = Engine.GetNode(owner.EffectId);
            Assert.That(owner.Payment.SourceReceipts.Count, Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(98));
            foreach (var receipt in owner.Payment.SourceReceipts)
            {
                Assert.That(receipt.ItemIds.All(id => state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == state.ItemZones.Items.Single(i => i.ItemId == id).OriginalSupplyZoneId), Is.True);
                Assert.That(ItemTransferService.TryValidateTokenCommit(state, 1, receipt.TokenArguments, receipt.TokenTransfer, out reason), Is.True, reason);
            }
            string paidItem = owner.Payment.SourceReceipts[0].ItemIds[0];
            Run(TokenSpec(1, new[] { paidItem }, state.ItemZones.Items.Single(i => i.ItemId == paidItem).ZoneId, FindZone(state, "retired").ZoneId, 1));
            string receiptsBeforeRecovery = JsonConvert.SerializeObject(owner.Payment.SourceReceipts);
            // 完整状态使用正式Host归档/JSON恢复；历史费用收据不能把后来合法移动回滚。
            var recovered = artifacts.Recover(state, "008-R2两来源已付后合法移动Host恢复", registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); state = recovered.State;
            owner = Engine.GetNode(owner.EffectId);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == paidItem).ZoneId, Is.EqualTo(FindZone(state, "retired").ZoneId));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(98));
            Assert.That(JsonConvert.SerializeObject(owner.Payment.SourceReceipts), Is.EqualTo(receiptsBeforeRecovery));
            foreach (var receipt in owner.Payment.SourceReceipts)
                Assert.That(ItemTransferService.TryValidateTokenCommit(state, 1, receipt.TokenArguments, receipt.TokenTransfer, out reason), Is.True, reason);
            Assert.That(ResourcePaymentRecovery.TryValidate(state, registry, out reason), Is.True, reason);
            before = JsonConvert.SerializeObject(state); Pump(); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }
        [TestCase("source_exit")] [TestCase("second_destination_full")]
        public void InvalidSecondSourceCannotLeaveFirstTokenCostOrResourcesCommitted(string failure)
        {
            Setup(); var owner = Open(); TwoDrafts(owner);
            Assert.That(ResourcePaymentPlanService.TryBuild(state, registry, owner, out var plan, out string reason), Is.True, reason);
            if (failure == "source_exit") state.ItemZones.Items.Single(i => i.ItemId == carriers[1]).ZoneId = FindZone(state, "retired").ZoneId;
            else
            {
                var zone = FindZone(state, "reserve");
                var template = ZoneTemplateRegistry.CreateBuiltIn(ItemTypeRegistry.CreateBuiltIn()).Find(zone.TemplateId);
                if (zone.Configuration == null) zone.Configuration = new CardZoneDefinition {
                    LocalZoneId = zone.LocalZoneId, ZoneTemplateId = zone.TemplateId,
                    AcceptedItemKinds = template.AcceptedItemKinds.ToList(), AcceptedTypeIds = template.AcceptedTypeIds.ToList(),
                    EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer",
                    OrderingKind = template.OrderingKind, VisibilityPolicyId = zone.VisibilityPolicyId,
                    CarrierExitPolicyId = "core.preserve_contents", Slots = zone.LegalSlotIds.ToList() };
                zone.Configuration.CapacityPolicy = "bounded";
                zone.Configuration.Capacity = state.ItemZones.Items.Count(i => i.ZoneId == zone.ZoneId);
            }
            Assert.That(new ItemZoneQuery().TryValidate(state, out reason), Is.True, reason);
            owner.Payment.IsCommitRequested = true; string before = JsonConvert.SerializeObject(state);
            var error = Assert.Throws<InvalidOperationException>(() => ResourcePaymentPlanService.Apply(Context(owner), plan));
            Assert.That(error.Message, Is.EqualTo(failure == "source_exit" ? "payment_substitution_source_changed" : "destination_capacity_exceeded"));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before)); Assert.That(owner.Payment.SourceReceipts, Is.Empty);
            Assert.That(owner.Payment.IsCommitted, Is.False);
        }
        [Test] public void PurePlanFromAnotherSourceIsNotAnExecutionGrantOnThisPayment()
        {
            Setup(); var owner = Open(); Assert.That(Draft(owner, 0, new ResourceSet { Originium = 2 }, selected[0].Take(2).ToList(), out string reason), Is.True, reason);
            Assert.That(ResourcePaymentSourceService.TryPlanItemCost(state, registry, owner, Rule(owner, 1), 2, selected[1], out var borrowed, out reason), Is.True, reason);
            owner.Payment.IsCommitRequested = true; string before = JsonConvert.SerializeObject(state);
            var error = Assert.Throws<InvalidOperationException>(() => new ItemTransferService().RelocateTokenBatches(Context(owner), new[] { borrowed.Request }));
            Assert.That(error.Message, Is.EqualTo("payment_token_source_authority_invalid")); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }
        [Test] public void EmptyCarrierExitKeepsItsOwnPositionEvidenceWithoutInvalidatingPaidTokenReceipt()
        {
            Setup(firstCount: 2, discardFirstCarrier: true); var owner = Open(new ResourceSet { Originium = 2 });
            Assert.That(Draft(owner, 0, new ResourceSet { Originium = 2 }, selected[0], out string reason), Is.True, reason); Pay(); owner = Engine.GetNode(owner.EffectId);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == carriers[0]).ZoneId, Is.EqualTo(FindZone(state, "discard", 1).ZoneId));
            Assert.That(owner.PositionChanges.Count, Is.EqualTo(3)); Assert.That(owner.Payment.SourceReceipts.Single().TokenTransfer.Result.MovedCount, Is.EqualTo(2));
            Assert.That(ResourcePaymentRecovery.TryValidate(state, registry, out reason), Is.True, reason);
        }
        [TestCase("same")] [TestCase("state")] [TestCase("owner")]
        public void LuaEquivalenceDeclarationDoesNotMergeDifferentRuleFacts(string difference)
        {
            Setup(); var owner = Open(new ResourceSet { Originium = 2 });
            if (difference == "state") state.ItemZones.Items.Single(i => i.ItemId == selected[0][0]).LocalState = Object("amount", NormalizedValue.CreateInteger(1));
            if (difference == "owner")
            {
                state.ItemZones.Items.Single(i => i.ItemId == selected[0][0]).ZoneId = FindZone(state, "supply", 1).ZoneId;
                state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.ZoneId == FindZone(state, "supply", 2).ZoneId).ZoneId = zones[0];
            }
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
            if (difference == "state") Assert.That(selected[0].Select(id => ResourcePaymentSourceService.ItemSelectionGroup(state, state.ItemZones.Items.Single(i => i.ItemId == id))).Distinct().Count(), Is.EqualTo(2));
            bool accepted = Draft(owner, 0, new ResourceSet { Originium = 2 }, Array.Empty<string>(), out reason);
            Assert.That(accepted, Is.EqualTo(difference != "state"));
            if (difference == "state") Assert.That(reason, Is.EqualTo("payment_token_selection_required"));
            else Assert.That(owner.Payment.SourceDrafts.Single().SelectedItemIds.Count, Is.EqualTo(2));
            if (difference == "owner") Assert.That(owner.Payment.SourceDrafts.Single().SelectedItemIds.All(id => state.ItemZones.Items.Single(i => i.ItemId == id).OwnerSubject.PlayerId == 1), Is.True);
            Assert.That(state.EffectRuntime.InteractionRequests.Count(r => r.Status == "open"), Is.EqualTo(1));
        }
        [Test] public void OnlyLegalCombinationIsAutomaticEvenWhenItsFactsDiffer()
        {
            Setup(firstCount: 2, selectionPolicy: "explicit"); var owner = Open(new ResourceSet { Originium = 2 });
            // 使用正式局部数量事实；唯一合法组合仍按真实枚数消费，不把amount当额外token。
            for (int i = 0; i < selected[0].Count; i++) state.ItemZones.Items.Single(item => item.ItemId == selected[0][i]).LocalState = Object("amount", NormalizedValue.CreateInteger(i + 1));
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
            Assert.That(selected[0].Select(id => ResourcePaymentSourceService.ItemSelectionGroup(state, state.ItemZones.Items.Single(i => i.ItemId == id))).Distinct().Count(), Is.EqualTo(2));
            Assert.That(Draft(owner, 0, new ResourceSet { Originium = 2 }, Array.Empty<string>(), out reason), Is.True, reason);
            Assert.That(owner.Payment.SourceDrafts.Single().SelectedItemIds, Is.EquivalentTo(selected[0]));
        }
        [Test] public void CommittedTokenReceiptRoundTripsAndClonesWithoutAliasing()
        {
            Setup(); var owner = Open(); TwoDrafts(owner); Pay(); owner = Engine.GetNode(owner.EffectId);
            var source = owner.Payment.SourceReceipts[0]; string original = JsonConvert.SerializeObject(source.TokenTransfer);
            var copies = new[] { GameStateCloneService.DeepClone(source.TokenTransfer), JsonConvert.DeserializeObject<ItemTransferBatchCommit>(original) };
            foreach (var copy in copies)
            {
                Assert.That(JsonConvert.SerializeObject(copy), Is.EqualTo(original));
                Assert.That(ItemTransferService.TryValidateTokenCommit(state, 1, source.TokenArguments, copy, out string reason), Is.True, reason);
                copy.Request.Transfers[0].ItemIds[0] = "qy008.invalid_item";
                copy.Request.Transfers[0].ExpectedSource.ZoneId = "qy008.invalid_source";
                copy.Request.Transfers[0].Destination.ZoneId = "qy008.invalid_destination";
                copy.Request.ExpectedOrders.Add(new ItemTransferOrderExpectation { ZoneId = "qy008.invalid_order" });
                copy.Result.MovedItemIds[0] = "qy008.invalid_item";
                copy.Result.PositionChanges[0].From.ZoneId = "qy008.invalid_source";
                copy.Result.PositionChanges[0].To.ZoneId = "qy008.invalid_destination";
                copy.Receipt.Properties.Single(p => p.Name == "requestDigest").Value.StringValue = "qy008.invalid_digest";
                Assert.That(ItemTransferService.TryValidateTokenCommit(state, 1, source.TokenArguments, copy, out reason), Is.False);
                Assert.That(JsonConvert.SerializeObject(source.TokenTransfer), Is.EqualTo(original));
            }
        }
        [Test] public void ResourceAllocationApiStillUsesRealPaymentRulesAndDoesNotPayDuringEditing()
        {
            Setup(); ResourcePaymentSubstitutionEffectExecutor.Register(registry); var owner = Open();
            var spec = ResourcePaymentSubstitutionEffectExecutor.Create(1, owner.EffectId, zones[0], 2);
            // 明确隔离的执行器合同探针：实际付款及规则来源不伪造，不计正式插入或页面验收。
            var node = new EffectNodeRuntimeState { EffectId = "qy008.resource-editor.probe", ParentEffectId = owner.EffectId, EffectTypeId = spec.EffectTypeId,
                DefinitionVersion = spec.DefinitionVersion, PlayerId = 1, NormalizedArguments = spec.NormalizedArguments.Clone() };
            state.EffectRuntime.EffectNodes.Add(node);
            var step = registry.Get(spec.EffectTypeId).Executor(Context(node));
            Assert.That(step.Interactions.Count, Is.EqualTo(1)); Assert.That(step.Interactions.Single().AnswerSchema, Is.EqualTo(ResourcePaymentSubstitutionEffectExecutor.AnswerSchema));
            Assert.That(Number(step.Interactions.Single().PromptParameters, "maxAmount"), Is.EqualTo(2));
            Assert.That(owner.Payment.SourceDrafts, Is.Empty); Assert.That(owner.Payment.IsCommitted, Is.False);
            Assert.That(selected[0].All(id => state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == zones[0]), Is.True);
        }
        [Test] public void WrongExpectedSourceCannotCreateAWaitOrMoveAnUnrelatedRealId()
        {
            Setup(); var item = state.ItemZones.Items.First(i => i.ZoneId == FindZone(state, "supply", 1).ZoneId);
            string before = JsonConvert.SerializeObject(state.ItemZones);
            var node = Run(TokenSpec(1, new[] { item.ItemId }, FindZone(state, "supply", 2).ZoneId, FindZone(state, "retired").ZoneId, 1));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(node.FailureReason, Is.EqualTo("source_zone_changed"));
            Assert.That(node.PositionChanges, Is.Empty); Assert.That(JsonConvert.SerializeObject(state.ItemZones), Is.EqualTo(before));
            Assert.That(state.EffectRuntime.Blockers.Any(b => b.OwnerEffectId == node.EffectId && !b.IsResolved), Is.False);
        }
        [Test] public void StateOnlyCostConsumersUseTheirActualRegistryWithoutBorrowingAnotherState()
        {
            Setup(); state.FindPlayer(1).Resources.Originium = 0; state.FindPlayer(1).Resources.Iron = 0; state.FindPlayer(1).Resources.OriginiumShard = 0;
            var engine = Engine; string before = JsonConvert.SerializeObject(state); var cost = new ResourceSet { Originium = 2 };
            Assert.That(ResourceEffectExecutor.CanPayWithStoredMarkers(state, 1, cost), Is.True);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            var independent = GameStateCloneService.DeepClone(state);
            Assert.That(ResourceEffectExecutor.CanPayWithStoredMarkers(independent, 1, cost), Is.False, "没有实际Registry绑定的另一状态不借用同哈希来源。");
            new EffectTreeExecutor(independent, registry);
            Assert.That(ResourceEffectExecutor.CanPayWithStoredMarkers(independent, 1, cost), Is.True);
        }
        [Test] public void SamePaymentQuotaAndBasicResourceScopeRemainAuthoritative()
        {
            Setup(sharedQuota: true); var owner = Open();
            Assert.That(Draft(owner, 0, new ResourceSet { Originium = 2 }, selected[0].Take(2).ToList(), out string reason), Is.True, reason);
            Assert.That(Draft(owner, 1, new ResourceSet { Iron = 1 }, selected[1].Take(1).ToList(), out reason), Is.False); Assert.That(reason, Is.EqualTo("payment_source_quota_exceeded"));
            Assert.That(Draft(owner, 1, new ResourceSet { GoldVoucher = 1 }, selected[1].Take(1).ToList(), out reason), Is.False);
            Assert.That(owner.Payment.SourceDrafts.Count, Is.EqualTo(1)); Assert.That(owner.Payment.IsCommitted, Is.False);
        }
        [Test] public void CompleteTwentyTokenInventoryAndOneAtomicCostHaveNoLegacySixteenLimit()
        {
            Setup(firstCount: 20, limit: 20, selectionPolicy: "explicit"); var owner = Open(new ResourceSet { Originium = 20 });
            var row = ResourcePaymentDraftHost.QuerySources(state, registry, owner).Sources.Single(r => r.Rule.RuleId == "source_0");
            Assert.That(row.AvailableItemCount, Is.EqualTo(20)); Assert.That(row.MaxAmount, Is.EqualTo(20));
            Assert.That(Draft(owner, 0, new ResourceSet { Originium = 20 }, Array.Empty<string>(), out string reason), Is.True, reason); Pay();
            owner = Engine.GetNode(owner.EffectId); Assert.That(owner.Payment.SourceReceipts.Single().TokenTransfer.Result.MovedCount, Is.EqualTo(20));
            Assert.That(ResourcePaymentRecovery.TryValidate(state, registry, out reason), Is.True, reason);
        }
        [TestCase("request")] [TestCase("type")] [TestCase("policy")]
        public void PaidRecoveryRejectsTamperedTokenIdentityOrPolicy(string kind)
        {
            Setup(); var owner = Open(); TwoDrafts(owner); Pay(); state = GameStateCloneService.DeepClone(state); owner = Engine.GetNode(owner.EffectId);
            var receipt = owner.Payment.SourceReceipts[0];
            if (kind == "type") state.ItemZones.Items.Single(i => i.ItemId == receipt.ItemIds[0]).PhysicalKind = ItemPhysicalKind.Card;
            else if (kind == "policy") owner.Payment.FinalReceipt.CostReceiptPolicyVersion = "";
            else
            {
                receipt.TokenTransfer.Request.Transfers[0].Destination.ZoneId = FindZone(state, "retired").ZoneId;
                receipt.TokenTransfer.Receipt.Properties.Single(p => p.Name == "requestDigest").Value = NormalizedValue.CreateString(ItemTransferService.BatchRequestDigest(receipt.TokenTransfer.Request));
            }
            Assert.That(ResourcePaymentRecovery.TryValidate(state, registry, out _), Is.False);
        }
    }
}
