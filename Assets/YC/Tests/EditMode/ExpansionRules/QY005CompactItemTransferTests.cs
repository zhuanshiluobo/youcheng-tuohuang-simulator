using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 55通用机制的独立隔离夹具。不加载并行修改中的正式内容包，不代表正常局验收。
    public sealed class QY005TokenTransferTests
    {
        private static IsolatedTestArtifacts artifacts;
        private static readonly List<GameState> observedStates = new List<GameState>();
        [SetUp] public void BeginEvidence() { observedStates.Clear(); artifacts = IsolatedTestArtifacts.BeginCase(); }
        [TearDown] public void EndEvidence()
        {
            foreach (var state in observedStates) artifacts.WriteLabeledText("005实际隔离输入及节点状态", JsonConvert.SerializeObject(state));
            artifacts.Finish(); artifacts = null; observedStates.Clear();
        }
        private static GameState JsonRoundTripWithEvidence(GameState state)
        {
            // 正式 Host 字段序列化和恢复门禁，不把只读兼容投影再次反序列化为物理位置。
            var result = artifacts.Recover(state, "005正式Host恢复", Registry());
            Assert.That(result.Succeeded, Is.True, result.FaultCode + ":" + result.Diagnostic);
            observedStates.Add(result.State);
            return result.State;
        }
        private static GameState State(int count = 3, bool carrier = false, bool reserve = false, bool tokens = false, bool mixedTokens = false)
        {
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var source = new GameState { GameId = "qy005.compact.isolated", MapId = map.MapId, ContentPackHash = "qy005.compact.fixture",
                ContentDefinitions = new GameContentDefinitions(),
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "qy005.compact.fixture" },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "隔离甲" }, new PlayerState { PlayerId = 2, Name = "隔离乙" } },
                LaunchPlayerOrder = new List<int> { 1, 2 }, Round = 1, ActionRound = 1, Phase = GamePhase.ActionRound1, CurrentPlayerId = 1, StartPlayerId = 1 };
            var recipe = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "cubes", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = count, PerPlayer = true, InitialZoneRole = "supply" } };
            if (reserve) recipe.Add(new ItemRecipeEntry { EntryId = "reserve-cube", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 1, PerPlayer = true, InitialZoneRole = "reserve" });
            if (carrier) recipe.Add(new ItemRecipeEntry { EntryId = "carrier", DefinitionId = "qy005.carrier", TypeId = "entrepreneur.ability_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "entrepreneur_active" });
            if (tokens) foreach (string type in new[] { "infected.gavial_token", "core.die" }) recipe.Add(new ItemRecipeEntry {
                EntryId = type, DefinitionId = type, TypeId = type, Quantity = 1, PerPlayer = true, InitialZoneRole = "reserve" });
            if (mixedTokens) {
                recipe.Add(new ItemRecipeEntry { EntryId = "gang", DefinitionId = "entrepreneur.gang_marker", TypeId = "entrepreneur.gang_marker", Quantity = 1, PerPlayer = true, InitialZoneRole = "reserve" });
                recipe.Add(new ItemRecipeEntry { EntryId = "company", DefinitionId = "infected.company_marker", TypeId = "infected.company_marker", Quantity = 1, InitialZoneRole = "reserve" });
            }
            var zones = new List<ItemCardZoneDeclaration>();
            if (carrier) zones.Add(new ItemCardZoneDeclaration { DefinitionId = "qy005.carrier", CardZones = new List<CardZoneDefinition> { new CardZoneDefinition {
                LocalZoneId = "stored", ZoneTemplateId = "core.zone.card_cube_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
                AcceptedTypeIds = new List<string> { "core.cube" }, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer",
                CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered, VisibilityPolicyId = ItemZoneVisibility.Owner, CarrierExitPolicyId = "core.preserve_contents" } } });
            var result = new ItemZoneStateFactory().CreateForSetup(source, map, recipe, zones); observedStates.Add(result);
            result.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            return result;
        }
        private static EffectRegistry Registry()
        {
            var registry = new EffectRegistry(); ItemTransferEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry);
            return registry;
        }
        private static List<string> Supply(GameState s, int player = 1) => s.ItemZones.Items.Where(i => i.ZoneId == FindZone(s, "supply", player).ZoneId).Select(i => i.ItemId).OrderBy(i => i, StringComparer.Ordinal).ToList();
        private static EffectNodeRuntimeState Run(GameState state, EffectSpec spec, EffectRegistry registry = null)
        {
            var engine = new EffectTreeExecutor(state, registry ?? Registry()); string id = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
            return engine.GetNode(id);
        }
        [TestCase(3, 2)] [TestCase(3, 1)] [TestCase(2, 2)] [TestCase(99, 99)]
        public void ExplicitWholeGroupMovesWithoutCreatingAnyChoice(int available, int requested)
        {
            var state = State(available); var selected = Supply(state).Take(requested).ToList();
            var spec = TokenSpec(1, selected, FindZone(state, "supply", 1).ZoneId, FindZone(state, "retired").ZoneId, requested);
            Assert.That(spec.NormalizedArguments.TryValidate(out string reason), Is.True, reason);
            var node = Run(state, spec);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Supply(state).Count, Is.EqualTo(available - requested));
            Assert.That(Number(node.NormalizedResult, "movedCount"), Is.EqualTo(requested));
            Assert.That(node.NormalizedResult.TryValidate(out reason), Is.True, reason);
            Assert.That(node.ContinuationData.TryValidate(out reason), Is.True, reason);
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
            Assert.That(ItemTransferService.TryValidateTokenState(state, node, out reason), Is.True, reason);
        }
        [Test] public void ShortfallMovesNothingAndDoesNotExecuteConditionRightSide()
        {
            var state = State(1); var selected = Supply(state);
            var move = TokenSpec(1, selected, FindZone(state, "supply", 1).ZoneId, FindZone(state, "retired").ZoneId, 2);
            Run(state, EffectSpec.Condition(new[] { move }, new[] { ResourceEffectSpecFactory.GainScore(1, 5) }, decisionPlayerId: 1));
            Assert.That(Supply(state), Is.EqualTo(selected)); Assert.That(state.FindPlayer(1).Score, Is.Zero);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.FailureReason == "insufficient_items"), Is.True);
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
        }
        [Test] public void SuccessfulConditionContinuesOnceAfterActualTransfer()
        {
            var state = State();
            var move = TokenSpec(1, Supply(state).Take(2), FindZone(state, "supply", 1).ZoneId, FindZone(state, "retired").ZoneId, 2);
            Run(state, EffectSpec.Condition(new[] { move }, new[] { ResourceEffectSpecFactory.GainScore(1, 5) }, decisionPlayerId: 1));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(5)); Assert.That(Supply(state).Count, Is.EqualTo(1));
            var restored = JsonRoundTripWithEvidence(state); ItemZoneLegacyProjection.Bind(restored);
            Assert.That(new EffectTreeExecutor(restored, Registry()).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(restored.FindPlayer(1).Score, Is.EqualTo(5)); Assert.That(Supply(restored).Count, Is.EqualTo(1));
        }
        [TestCase("missing")] [TestCase("duplicate")] [TestCase("wrong_source")] [TestCase("wrong_player")]
        public void InvalidMemberCannotPartiallyMoveOtherMembers(string kind)
        {
            var state = State(); var original = Supply(state); var selected = original.Take(2).ToList();
            string source = FindZone(state, "supply", 1).ZoneId;
            if (kind == "missing") selected[1] = "not-an-item";
            if (kind == "duplicate") selected[1] = selected[0];
            if (kind == "wrong_source") source = FindZone(state, "reserve").ZoneId;
            if (kind == "wrong_player") { selected = Supply(state, 2).Take(2).ToList(); source = FindZone(state, "supply", 2).ZoneId; }
            var node = Run(state, TokenSpec(1, selected, source, FindZone(state, "retired").ZoneId, 2));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(node.PositionChanges, Is.Empty);
            Assert.That(Supply(state), Is.EqualTo(original)); Assert.That(Supply(state, 2).Count, Is.EqualTo(3));
        }
        [Test] public void OriginalSupplyDestinationRemainsPerInstanceAcrossOneAtomicCommit()
        {
            var state = State(1, reserve: true); string from = FindZone(state, "retired").ZoneId;
            var items = state.ItemZones.Items.Where(i => i.PhysicalKind == ItemPhysicalKind.Cube && i.OwnerSubject.PlayerId == 1).ToList();
            // 隔离机制安排；不注入正常程序或计作正常界面验收。
            foreach (var item in items) item.ZoneId = from;
            Assert.That(items.Select(i => i.OriginalSupplyZoneId).Distinct().Count(), Is.EqualTo(2));
            var node = Run(state, TokenSpec(1, items.Select(i => i.ItemId), from, null, 2));
            Assert.That(items.All(i => state.ItemZones.Items.Single(actual => actual.ItemId == i.ItemId).ZoneId == i.OriginalSupplyZoneId), Is.True);
            Assert.That(node.TransferBatchRequest.Transfers.Count, Is.EqualTo(2));
            Assert.That(ItemTransferService.TryValidateTokenState(state, node, out string reason), Is.True, reason);
        }
        [Test] public void ReceiptRoundTripReusesHistoryAfterLaterLegalMoveAndRejectsTampering()
        {
            var state = State(); var ids = Supply(state).Take(2).ToList(); string supply = FindZone(state, "supply", 1).ZoneId, retired = FindZone(state, "retired").ZoneId;
            var node = Run(state, TokenSpec(1, ids, supply, retired, 2)); string effectId = node.EffectId;
            Run(state, TokenSpec(1, ids, retired, null, 2));
            var restored = JsonRoundTripWithEvidence(state); ItemZoneLegacyProjection.Bind(restored);
            node = restored.EffectRuntime.EffectNodes.Single(n => n.EffectId == effectId);
            Assert.That(ItemTransferService.TryValidateTokenState(restored, node, out string reason), Is.True, reason);
            var context = (EffectExecutionContext)Activator.CreateInstance(typeof(EffectExecutionContext), BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { restored, node, Registry(), null }, null);
            var before = JsonConvert.SerializeObject(restored.ItemZones);
            var replay = new ItemTransferService().RelocateBatch(context, node.TransferBatchRequest);
            Assert.That(replay.MovedCount, Is.EqualTo(2)); Assert.That(JsonConvert.SerializeObject(restored.ItemZones), Is.EqualTo(before));
            var damaged = HostRecoveryService.CreateArchive(restored);
            damaged.Snapshot.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == effectId).PositionChanges[0].To.ZoneId = supply;
            string damagedJson = HostRecoveryService.SerializeArchive(damaged);
            artifacts.WriteLabeledText("005损坏历史token收据正式存档", damagedJson);
            var rejected = HostRecoveryService.RecoverJson(damagedJson, Registry());
            Assert.That(rejected.Succeeded, Is.False);
            Assert.That(rejected.Diagnostic, Does.Contain("token_transfer_receipt_invalid"));
            node.PositionChanges[0].To.ZoneId = supply;
            Assert.That(ItemTransferService.TryValidateBatchReceipt(node, out _), Is.False);
            Assert.Throws<InvalidOperationException>(() => new ItemTransferService().RelocateBatch(context, node.TransferBatchRequest));
        }
        [Test] public void CapacityFailureInOneDestinationRollsBackEveryDestination()
        {
            var state = State(1, reserve: true); string from = FindZone(state, "retired").ZoneId;
            var items = state.ItemZones.Items.Where(i => i.PhysicalKind == ItemPhysicalKind.Cube && i.OwnerSubject.PlayerId == 1).ToList();
            foreach (var item in items) item.ZoneId = from;
            var supply = FindZone(state, "supply", 1); supply.Configuration.CapacityPolicy = "bounded"; supply.Configuration.Capacity = 0;
            var node = Run(state, TokenSpec(1, items.OrderBy(i => i.OriginalSupplyZoneId == supply.ZoneId ? 1 : 0).Select(i => i.ItemId), from, null, 2));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(node.PositionChanges, Is.Empty);
            Assert.That(items.All(i => state.ItemZones.Items.Single(actual => actual.ItemId == i.ItemId).ZoneId == from), Is.True);
        }
        [Test] public void HeterogeneousLocalStateFactsRemainDistinctWithoutChoosingForLua()
        {
            var state = State(); var items = state.ItemZones.Items.Where(i => Supply(state).Contains(i.ItemId)).Take(2).ToArray();
            items[0].LocalState = Object("amount", NormalizedValue.CreateInteger(1)); items[1].LocalState = Object("amount", NormalizedValue.CreateInteger(2));
            Assert.That(ItemTransferService.LocalStateDigest(items[0]), Is.Not.EqualTo(ItemTransferService.LocalStateDigest(items[1])));
            string retained = items[0].ItemId, selected = items[1].ItemId;
            Run(state, TokenSpec(1, new[] { selected }, FindZone(state, "supply", 1).ZoneId, FindZone(state, "retired").ZoneId, 1));
            Assert.That(Supply(state), Does.Contain(retained)); Assert.That(Supply(state), Does.Not.Contain(selected));
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
        }
        [Test] public void RawArgumentsCannotBeChangedByRehashingTypedReceipt()
        {
            var state = State(); var node = Run(state, TokenSpec(1, Supply(state).Take(2), FindZone(state, "supply", 1).ZoneId, FindZone(state, "retired").ZoneId, 2));
            Value(node.NormalizedArguments, "requiredCount").IntegerValue = 1;
            Assert.That(ItemTransferService.TryValidateBatchReceipt(node, out _), Is.False);
        }
        [Test] public void FacilityBatchProtocolTwoStillRejectsCubeProfiles()
        {
            var state = State(); var leg = new ItemTransferRequest { ExecutingPlayerId = 1, ItemIds = Supply(state).Take(2).ToList(), RequiredCount = 2,
                ExpectedSource = new ItemPositionRef { ZoneId = FindZone(state, "supply", 1).ZoneId }, Destination = new ZoneRef { ZoneId = FindZone(state, "retired").ZoneId }, InsertionKind = "unordered", OperationProfileId = "core.cube_storage" };
            var batch = ItemTransferService.CreateBatch(state, 1, "fixture-v2", leg);
            Assert.That(new ItemTransferService().ValidateBatch(state, batch).IsValid, Is.False);
            Assert.That(ValidateArguments(TypeId, ItemTransferService.BatchArguments(batch)), Is.EqualTo("transfer_batch_leg_invalid"));
        }
        [TestCase("core.facility_card_transfer")] [TestCase("core.character_card_transfer")] [TestCase("core.reward_card_transfer")]
        public void TokenProtocolCannotBorrowAnyCardProfile(string profile)
        {
            var state = State(); var args = TokenArguments(1, Supply(state).Take(1), FindZone(state, "supply", 1).ZoneId, FindZone(state, "retired").ZoneId, 1, profile);
            Assert.That(ValidateTokenArguments(args), Is.EqualTo("operation_profile_rejected"));
            var cardArguments = MoveSpec(1, new[] { "card-instance" }, "card-source", "card-target", "unordered", 1, profile).NormalizedArguments;
            Assert.That(ValidateArguments(TypeId, cardArguments), Is.Empty, "v1卡牌合同未被token合同缩窄");
        }
        [Test] public void ProtectedMapAndNoOpDestinationsCannotBecomeGenericTransfers()
        {
            var state = State(); var ids = Supply(state).Take(1).ToList(); string from = FindZone(state, "supply", 1).ZoneId;
            var noop = Run(state, TokenSpec(1, ids, from, from, 1)); Assert.That(noop.Status, Is.EqualTo(EffectNodeStatus.Failed));
            var map = state.ItemZones.Zones.First(z => z.TemplateId == "core.zone.influence_slots");
            var blocked = Run(state, TokenSpec(1, ids, from, map.ZoneId, 1)); Assert.That(blocked.FailureReason, Is.EqualTo("operation_profile_rejected"));
            Assert.That(Supply(state).Count, Is.EqualTo(3));
        }
        [Test] public void CarrierTransferUsesCurrentDeclaredSourceAndDoesNotAcceptOldPaymentPermission()
        {
            var state = State(carrier: true); var card = state.ItemZones.Items.Single(i => i.DefinitionId == "qy005.carrier" && i.OwnerSubject.PlayerId == 1);
            var storage = state.ItemZones.Zones.Single(z => z.ParentId == card.ItemId && z.LocalZoneId == "stored");
            var ids = Supply(state).Take(2).ToList(); foreach (string id in ids) state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId = storage.ZoneId;
            var declaration = new AbilitySourceDeclaration { DefinitionId = card.DefinitionId, AbilityId = "qy005.transfer", Kind = "quick", HandlerRef = "qy005.transfer",
                DeclarationVersion = "1.0.0", EffectiveZones = new List<string> { "core.zone.entrepreneur_active" },
                AllowedOperations = new List<string> { "carrier.transfer.original_supply:qy005.carrier:stored:core.cube" } };
            var declarations = new[] { declaration }; state.ContentDefinitions.AbilitySources = declarations.ToList();
            var source = AbilitySourceService.QuerySources(state, declarations, 1).Single();
            var args = TokenArguments(1, ids, storage.ZoneId, null, 2); var service = new ItemTransferService();
            Assert.That(service.TryPlanTokens(state, source, args, declarations, "fixture:move", out var plan, out string reason), Is.True, reason);
            Assert.That(plan.Transfers.SelectMany(t => t.ItemIds), Is.EqualTo(ids));
            Assert.That(plan.ExpectedOrders, Is.Empty, "token计划不带设施层序");
            Assert.Throws<InvalidOperationException>(() => ItemTransferService.BatchArguments(plan));
            Assert.That(service.TryPlanTokens(state, null, args, declarations, "fixture:move", out _, out reason), Is.False);
            declaration.AllowedOperations[0] = "carrier.pay.original_supply:qy005.carrier:stored:core.cube";
            Assert.That(CardMarkerCarrier.ValidPermission(declaration.AllowedOperations[0]), Is.False);
            Assert.That(service.TryPlanTokens(state, source, args, declarations, "fixture:move", out _, out reason), Is.False);
        }
        [TestCase(2)] [TestCase(99)] public void LuaTokenConstructorCompilesAndRetiredConstructorIsAbsent(int count)
        {
            string code = "return function(ctx) local ids={} for i=1," + count + " do ids[i]='token-'..i end return {Effect.MoveItem({apiVersion=3,player=ctx.playerId,itemIds=ids,expectedSource={zoneId='from'},destination={kind='original_supply'},insertion={kind='unordered'},operationProfileId='core.cube_storage',requiredCount=" + count + "})} end";
            var definition = new LuaScriptDefinition("qy005.compact", "qy005.transfer", "qy005.handler", "1.0.0", code, LuaContentHasher.ComputeSha256(code));
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(definition, new LuaInvocationContext("event.qy005", "event.test", "1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            Assert.That(specs.Single().EffectTypeId, Is.EqualTo(TypeId)); Assert.That(Number(specs.Single().NormalizedArguments, "apiVersion"), Is.EqualTo(3));
            Assert.That(Value(specs.Single().NormalizedArguments, "itemIds").Items.Count, Is.EqualTo(count));
            code = "return function(ctx) assert(Effect.PayCardMarkers == nil) return {} end";
            definition = new LuaScriptDefinition("qy005.retired", "qy005.transfer", "qy005.handler", "1.0.0", code, LuaContentHasher.ComputeSha256(code));
            Assert.That(new MoonSharpLuaRuntimeHost().Invoke(definition, new LuaInvocationContext("event.qy005", "event.test", "1", 0, "1.0.0", 2, 0, 0)).IsSuccess, Is.True);
        }
        [Test] public void LegacyPaymentAndLoadNodesAreDiagnosticallyRejected()
        {
            var registry = Registry(); ContentPrimitiveEffectExecutor.Register(registry);
            Assert.That(registry.TryGet(ContentPrimitiveEffectExecutor.PayMarkers, out _), Is.False);
            Assert.That(registry.TryGet(ContentPrimitiveEffectExecutor.RetiredMarkerLoadTypeId, out _), Is.False);
            var state = State(); state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectTypeId = ContentPrimitiveEffectExecutor.PayMarkers, FlowStage = "choose_items" });
            Assert.That(ContentPrimitiveEffectExecutor.TryValidateContentContinuations(state, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo(ContentPrimitiveEffectExecutor.RetiredMarkerPaymentReason));
        }
        [Test] public void NonCubeTokensUseTheirOwnProfileAndAtomicReceipt()
        {
            var state = State(tokens: true); var tokens = state.ItemZones.Items.Where(i => i.PhysicalKind == ItemPhysicalKind.Token && i.OwnerSubject.PlayerId == 1).ToList();
            Assert.That(tokens.Any(i => i.TypeId == "infected.gavial_token"), Is.True);
            string source = FindZone(state, "reserve").ZoneId, target = FindZone(state, "retired").ZoneId;
            var wrongProfile = Run(state, TokenSpec(1, tokens.Select(i => i.ItemId), source, target, 2));
            Assert.That(wrongProfile.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(wrongProfile.PositionChanges, Is.Empty);
            var node = Run(state, TokenSpec(1, tokens.Select(i => i.ItemId), source, target, 2, "core.token_storage"));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(node.PositionChanges.Count, Is.EqualTo(2));
            var restored = JsonRoundTripWithEvidence(state); ItemZoneLegacyProjection.Bind(restored);
            Assert.That(ItemTransferService.TryValidateTokenState(restored, restored.EffectRuntime.EffectNodes.Single(n => n.EffectId == node.EffectId), out string reason), Is.True, reason);
            var returned = Run(restored, TokenSpec(1, tokens.Select(i => i.ItemId), target, null, 2, "core.token_storage"));
            Assert.That(returned.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(returned.PositionChanges.All(c => c.To.ZoneId == source), Is.True);
            var other = restored.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.Token && i.TypeId == "core.die" && i.OwnerSubject.PlayerId == 2);
            var denied = Run(restored, TokenSpec(1, new[] { other.ItemId }, source, target, 1, "core.token_storage"));
            Assert.That(denied.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(denied.PositionChanges, Is.Empty);
        }
        [Test] public void MapTokenCannotUseStorageTransferToEscapePermanentLifecycle()
        {
            var state = State(tokens: true); var token = state.ItemZones.Items.Single(i => i.TypeId == "infected.gavial_token" && i.OwnerSubject.PlayerId == 1);
            var zone = state.ItemZones.Zones.First(z => z.TemplateId == "core.zone.map_tokens");
            // 独立机制夹具中的已有地图位置；不是正式局注入或正常界面验收。
            token.ZoneId = zone.ZoneId;
            var before = JsonConvert.SerializeObject(state.ItemZones);
            var denied = Run(state, TokenSpec(1, new[] { token.ItemId }, zone.ZoneId, null, 1, "core.token_storage"));
            Assert.That(denied.FailureReason, Is.EqualTo("operation_profile_rejected"));
            Assert.That(denied.PositionChanges, Is.Empty);
            Assert.That(JsonConvert.SerializeObject(state.ItemZones), Is.EqualTo(before));
            // 同一保护也覆盖旧api1，不能从另外的入口绕行。
            denied = Run(state, MoveSpec(1, new[] { token.ItemId }, zone.ZoneId, token.OriginalSupplyZoneId, "unordered", 1, "core.token_storage"));
            Assert.That(denied.FailureReason, Is.EqualTo("token_storage_requires_lifecycle"));
            Assert.That(denied.PositionChanges, Is.Empty);
            Assert.That(JsonConvert.SerializeObject(state.ItemZones), Is.EqualTo(before));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == token.ItemId).ZoneId, Is.EqualTo(zone.ZoneId));
            Assert.That(ItemTypeRegistry.CreateBuiltIn().HasOperationProfile(token.TypeId, "core.adjacent_location_token_move"), Is.True);
        }
        [Test] public void TokenKindDoesNotConflateCardsCitiesOrTrackPermissions()
        {
            Assert.That(ItemTransferService.IsTokenKind(ItemPhysicalKind.Token), Is.True);
            Assert.That(ItemTransferService.IsTokenKind(ItemPhysicalKind.Cube), Is.True);
            Assert.That(ItemTransferService.IsTokenKind(ItemPhysicalKind.Card), Is.False);
            Assert.That(ItemTransferService.IsTokenKind(ItemPhysicalKind.MobileCity), Is.False);
            var state = State(); var token = state.ItemZones.Items.Single(i => i.ItemId == Supply(state)[0]);
            var track = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.score_track");
            var denied = Run(state, TokenSpec(1, new[] { token.ItemId }, token.ZoneId, track.ZoneId, 1));
            Assert.That(denied.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(denied.PositionChanges, Is.Empty);
        }
        [Test] public void AuthorizedTokenFactsAndEntryPreviewArePureAndPageAllInstances()
        {
            var state = State(9, carrier: true); var card = state.ItemZones.Items.Single(i => i.DefinitionId == "qy005.carrier" && i.OwnerSubject.PlayerId == 1);
            var zone = state.ItemZones.Zones.Single(z => z.ParentId == card.ItemId && z.LocalZoneId == "stored");
            var ids = Supply(state); foreach (string id in ids) state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId = zone.ZoneId;
            var declaration = new AbilitySourceDeclaration { DefinitionId = card.DefinitionId, AbilityId = "qy005.facts", Kind = "quick", HandlerRef = "qy005.facts",
                DeclarationVersion = "1.0.0", PresentationRef = "qy005.facts", EffectiveZones = new List<string> { "core.zone.entrepreneur_active" },
                AllowedOperations = new List<string> { "carrier.transfer.original_supply:qy005.carrier:stored:core.player_marker" } };
            state.ContentDefinitions.AbilitySources = new List<AbilitySourceDeclaration> { declaration };
            var registry = Registry(); registry.ContentPackHash = state.ContentPackHash;
            registry.RegisterAbilityHandler(declaration.HandlerRef, "qy005.facts.hash", _ => new List<EffectSpec>());
            registry.FreezeAbilityCatalog(new[] { declaration });
            var owner = registry.QuerySources(state, 1).Single(); string before = JsonConvert.SerializeObject(state);
            Assert.That(registry.PreviewItemTransferEntryCost(state, owner, TokenSpec(1, ids.Take(3), zone.ZoneId, null, 3)), Is.Empty);
            Assert.That(registry.PreviewItemTransferEntryCost(state, owner, TokenSpec(1, ids.Take(2), zone.ZoneId, null, 3)), Is.EqualTo("insufficient_items"));
            var facts = SourceFactsLuaCatalog.Create(state, registry, owner);
            Assert.That(facts.Get("transferableTokenCount").IntegerValue, Is.EqualTo(9));
            Assert.That(facts.Get("transferableTokens").Items.Select(i => i.Get("itemId").StringValue), Is.EqualTo(ids));
            string code = @"return function(ctx)
                assert(Global==nil and Effect==nil)
                local count=0; local cursor=0; local seen={}; local key=nil
                repeat
                    local p=SourceData.GetTransferableTokens({cursor=cursor,typeId='core.player_marker',localZoneId='stored',expectedStateRevision=ctx.stateRevision})
                    assert(p.totalCount==9 and #p.items<=4)
                    if key then assert(key==p.ruleOwnerSourceKey) else key=p.ruleOwnerSourceKey end
                    for _,i in ipairs(p.items) do assert(not seen[i.itemId]);seen[i.itemId]=true;count=count+1
                        assert(i.originalSupplyZoneId~='' and i.localStateDigest~='' and i.operations[1]=='transfer.original_supply') end
                    cursor=p.nextCursor
                until cursor==-1
                assert(count==9)
                assert(SourceData.GetTransferableTokens({localZoneId='missing'}).totalCount==0)
                assert(SourceData.GetCallableActions().transferableTokenCount==9)
                return {enabled=true,disabledReasonKey=''} end";
            var definition = new LuaScriptDefinition("qy005.facts", "qy005.facts", "qy005.facts", "1.0.0", code, LuaContentHasher.ComputeSha256(code));
            var invocation = new LuaInvocationContext("qy005.pure", "PureProbe", "p1", state.EffectRuntime.StateRevision, "1.0.0", 2, 0, 0) {
                SourceFacts = facts, ResponseKind = "settlementAvailability", SettlementContext = new SettlementWindowContext {
                    Id = "qy005.pure", Kind = SettlementEntryService.PlayerCleanup, OwnerEffectId = owner.SourceKey, PlayerId = 1, StateRevision = state.EffectRuntime.StateRevision } };
            var result = new MoonSharpLuaRuntimeHost().Invoke(definition, invocation);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            code = "return function(ctx) SourceData.GetTransferableTokens(); return {} end";
            definition = new LuaScriptDefinition("qy005.scope", "qy005.scope", "qy005.scope", "1.0.0", code, LuaContentHasher.ComputeSha256(code));
            invocation = new LuaInvocationContext("qy005.stale", "PureProbe", "p2", state.EffectRuntime.StateRevision, "1.0.0", 2, 0, 0) { SourceFacts = facts };
            result = new MoonSharpLuaRuntimeHost().Invoke(definition, invocation);
            Assert.That(result.IsSuccess, Is.False); Assert.That(result.Diagnostic, Does.Contain("source_facts_scope_stale"));
            // 隔离的调度来源收据，验证执行入口复查真实来源；不计正式AbilityAction/收尾调度覆盖。
            var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(TokenSpec(1, ids.Take(3), zone.ZoneId, null, 3));
            state.EffectRuntime.AbilitySourceUses.Add(new AbilitySourceUseReceipt { ReceiptId = "qy005.source", OwnerEffectId = root, PlayerId = 1,
                SourceKey = owner.SourceKey, ItemId = owner.ItemId, AbilityId = owner.AbilityId, SourceRef = owner });
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var node = engine.GetNode(root); Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(node.PositionChanges.Count, Is.EqualTo(3)); Assert.That(Supply(state).Count, Is.EqualTo(3));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == zone.ZoneId), Is.EqualTo(6));
            Assert.That(ItemTransferService.TryValidateTokenState(state, node, out string reason), Is.True, reason);
        }
        [Test] public void MixedCubeTypesAndOriginalOwnersReturnTogetherWithoutAutomaticChoice()
        {
            var state = State(carrier: true, mixedTokens: true);
            var card = state.ItemZones.Items.Single(i => i.DefinitionId == "qy005.carrier" && i.OwnerSubject.PlayerId == 1);
            var zone = state.ItemZones.Zones.Single(z => z.ParentId == card.ItemId && z.LocalZoneId == "stored");
            var tokens = new[] { state.ItemZones.Items.Single(i => i.ItemId == Supply(state, 2)[0]),
                state.ItemZones.Items.Single(i => i.TypeId == "entrepreneur.gang_marker" && i.OwnerSubject.PlayerId == 1),
                state.ItemZones.Items.Single(i => i.TypeId == "infected.company_marker") };
            foreach (var token in tokens) token.ZoneId = zone.ZoneId;
            tokens[0].LocalState = Object("amount", NormalizedValue.CreateInteger(2));
            var declaration = new AbilitySourceDeclaration { DefinitionId = card.DefinitionId, AbilityId = "qy005.mixed", Kind = "quick", HandlerRef = "qy005.mixed",
                DeclarationVersion = "1.0.0", PresentationRef = "qy005.mixed", EffectiveZones = new List<string> { "core.zone.entrepreneur_active" },
                AllowedOperations = new List<string> { "carrier.transfer.original_supply:qy005.carrier:stored:core.cube" } };
            state.ContentDefinitions.AbilitySources = new List<AbilitySourceDeclaration> { declaration };
            var registry = Registry(); registry.ContentPackHash = state.ContentPackHash;
            registry.RegisterAbilityHandler(declaration.HandlerRef, "qy005.mixed.hash", _ => new List<EffectSpec>());
            registry.FreezeAbilityCatalog(new[] { declaration }); var source = registry.QuerySources(state, 1).Single();
            string before = JsonConvert.SerializeObject(state);
            var facts = SourceFactsLuaCatalog.Create(state, registry, source).Get("transferableTokens").Items;
            Assert.That(facts.Count, Is.EqualTo(3));
            Assert.That(facts.Single(i => i.Get("itemId").StringValue == tokens[0].ItemId).Get("ownerSubject").StringValue, Is.EqualTo("p2"));
            Assert.That(facts.Single(i => i.Get("itemId").StringValue == tokens[0].ItemId).Get("localState").Get("amount").IntegerValue, Is.EqualTo(2));
            Assert.That(facts.Single(i => i.Get("itemId").StringValue == tokens[2].ItemId).Get("ownerSubject").StringValue, Is.EqualTo("company:stonehoove_security"));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Assert.That(CardMarkerCarrier.Accepts(state, zone, tokens[0], 1, new[] { "core.cube" }), Is.False, "装载筛选仍受本人原主限制");
            var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(TokenSpec(1, tokens.Select(i => i.ItemId), zone.ZoneId, null, 3));
            state.EffectRuntime.AbilitySourceUses.Add(new AbilitySourceUseReceipt { ReceiptId = "qy005.mixed.source", OwnerEffectId = root, PlayerId = 1,
                SourceKey = source.SourceKey, ItemId = source.ItemId, AbilityId = source.AbilityId, SourceRef = source });
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var node = engine.GetNode(root); Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(tokens.All(t => state.ItemZones.Items.Single(i => i.ItemId == t.ItemId).ZoneId == t.OriginalSupplyZoneId), Is.True);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == tokens[0].ItemId).OwnerSubject.PlayerId, Is.EqualTo(2));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == tokens[2].ItemId).OwnerSubject.CompanyId, Is.EqualTo("stonehoove_security"));
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
            Assert.That(ItemTransferService.TryValidateTokenState(state, node, out string reason), Is.True, reason);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == card.ItemId).ZoneId, Is.EqualTo(card.ZoneId), "清空留牌配置不被转移改成退场");
        }
    }
}
