using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using static YC.Tests.EditMode.ExpansionRules.EnterpriseEffectTests;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        private static string MarkerStorageV4(GameState state, string card, bool discardWhenEmpty = false)
        {
            var definition = new CardZoneDefinition { LocalZoneId = "v4_markers", ZoneTemplateId = "core.zone.card_cube_storage",
                AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" },
                CapacityPolicy = "bounded", Capacity = 2, OrderingKind = ZoneOrderingKind.Unordered, EntryPolicyId = "core.owned_player_marker", ExitPolicyId = "core.explicit_transfer",
                VisibilityPolicyId = ItemZoneVisibility.Public, CarrierExitPolicyId = "core.preserve_contents", EmptyExitPolicyId = discardWhenEmpty ? "core.discard_carrier_when_empty" : "" };
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, card, definition, out var id, out var reason), Is.True, reason); return id;
        }
        [TestCase(1)][TestCase(2)]
        public void MarkerV4CanStoreAndReturnOpponentMarkerWithoutCarrierSourceWhitelist(int carrierPlayer)
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", carrierPlayer)[0], marker = Ids(state, "supply", 2)[0];
            string storage = MarkerStorageV4(state, card), source = Zone(state, "supply", 2).ZoneId;
            var stored = Run(state, MarkerSpecV4(1, MarkerLegV4(marker, source, storage)), registry);
            Assert.That(Text(stored.NormalizedResult, "originalOwner"), Is.EqualTo("p2"));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == marker).OwnerSubject.PlayerId, Is.EqualTo(2));
            state = Restore(state, registry);
            var returned = Run(state, MarkerSpecV4(1, MarkerLegV4(marker, storage, null)), registry);
            Assert.That(Text(returned.NormalizedResult, "originalOwner"), Is.EqualTo("p2")); Assert.That(Ids(state, "supply", 2), Does.Contain(marker));
            var receipt = returned.ItemMovementV4.Receipt;
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == returned.EffectId).ItemMovementV4.Receipt.ReceiptId, Is.EqualTo(receipt.ReceiptId));
            Assert.That(state.EffectRuntime.EffectNodes.SelectMany(n => n.PositionChanges).Count(c => c.ItemId == marker), Is.EqualTo(2));
        }
        [Test]
        public void MarkerV4FreezesActualSourceAndOwnerBeforeCommitAndCanRestorePreparedIntent()
        {
            var state = State(); var registry = Registry(); string marker = Ids(state, "supply", 2)[0], source = Zone(state, "supply", 2).ZoneId;
            var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(MarkerSpecV4(1, MarkerLegV4(marker, source, Zone(state, "retired").ZoneId)));
            for (int step = 0; step < 10 && engine.GetNode(root).FlowStage != "v4_prepared"; step++) Assert.That(engine.Advance(), Is.True);
            var prepared = engine.GetNode(root);
            Assert.That(prepared.FlowStage, Is.EqualTo("v4_prepared")); Assert.That(prepared.ItemMovementV4.Receipt, Is.Null);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == marker).ZoneId, Is.EqualTo(source));
            state = Restore(state, registry);
            // 隔离反例改变真实来源，不作为正常对局或合法移动覆盖。
            var item = state.ItemZones.Items.Single(i => i.ItemId == marker); item.ZoneId = Zone(state, "retired").ZoneId;
            var resumed = new EffectTreeExecutor(state, registry); Assert.That(resumed.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(resumed.GetNode(root).FailureReason, Is.EqualTo("source_zone_changed")); Assert.That(resumed.GetNode(root).PositionChanges, Is.Empty);
        }
        [Test]
        public void MarkerV4RealLuaConstructorCompilesCrossPlayerInventoryContract()
        {
            var state = State(); string marker = Ids(state, "supply", 2)[0], storage = MarkerStorageV4(state, Ids(state, "hand", 1)[0]);
            string body = "return function(ctx) return {Effect.MoveItem({apiVersion=4,player=ctx.playerId,operation='transfer',selector={mode='explicit',itemIds={'" + marker +
                "'},expectedSource={zoneId='" + Zone(state, "supply", 2).ZoneId + "'}},destination={zoneId='" + storage + "'},insertion={kind='unordered'},operationProfileId='core.player_marker_storage',requiredCount=1})} end";
            var result = new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript(body), new LuaInvocationContext("api25", "isolated", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(result, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            var node = Run(state, specs.Single(), Registry()); Assert.That(Number(node.NormalizedResult, "movedCount"), Is.EqualTo(1));
            Assert.That(Text(node.NormalizedResult, "originalOwner"), Is.EqualTo("p2"));
        }
        [Test]
        public void MarkerV4LastMarkerExitsOtherHoldersCarrierUsingThatRealHoldersLifecycle()
        {
            var state = State(); var registry = Registry(); CharacterLifecycleService.Register(registry);
            string card = Ids(state, "hand", 2)[0], marker = Ids(state, "supply", 2)[0]; string storage = MarkerStorageV4(state, card, true);
            Run(state, MoveSpec(2, new[] { card }, Zone(state, "hand", 2).ZoneId, Zone(state, "persistent", 2).ZoneId, "unordered", 1, "core.character_card_transfer"), registry);
            Run(state, MarkerSpecV4(1, MarkerLegV4(marker, Zone(state, "supply", 2).ZoneId, storage)), registry);
            var moved = Run(state, MarkerSpecV4(1, MarkerLegV4(marker, storage, null)), registry);
            Assert.That(moved.PlayerId, Is.EqualTo(1)); Assert.That(moved.ItemMovementV4.Receipt.CarrierExits.Single().PlayerId, Is.EqualTo(2));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == card).ZoneId, Is.EqualTo(Zone(state, "discard", 2).ZoneId));
            Assert.That(Ids(state, "supply", 2), Does.Contain(marker)); Restore(state, registry);
        }
        [TestCase("operation")][TestCase("selector")][TestCase("profile")][TestCase("carrier")]
        [TestCase("unknown_field")][TestCase("oversize")][TestCase("depth")]
        public void MarkerV4RejectsUnsupportedOrOversizedArgumentsBeforeCreatingNodes(string fault)
        {
            var state = State(); string marker = Ids(state, "supply", 2)[0];
            var spec = MarkerSpecV4(1, MarkerLegV4(marker, Zone(state, "supply", 2).ZoneId, null)); var args = spec.NormalizedArguments;
            if (fault == "operation") Value(args, "operation").StringValue = "cover";
            if (fault == "selector") Value(Value(args, "selector"), "mode").StringValue = "select";
            if (fault == "profile") Value(args, "operationProfileId").StringValue = "core.deck_shuffle";
            if (fault == "carrier") args.Properties.Add(new NormalizedValueEntry { Name = "carrierPolicy", Value = NormalizedValue.CreateString("discard") });
            if (fault == "unknown_field") args.Properties.Add(new NormalizedValueEntry { Name = "receiptId", Value = NormalizedValue.CreateString("fake") });
            if (fault == "oversize") Value(Value(args, "selector"), "itemIds").Items.AddRange(Enumerable.Range(0, 129).Select(n => NormalizedValue.CreateString("not-an-instance-" + n)));
            if (fault == "depth")
            {
                var nested = NormalizedValue.CreateString("x");
                for (int depth = 0; depth < 9; depth++) nested = Object("nested", nested);
                args.Properties.Add(new NormalizedValueEntry { Name = "metadata", Value = nested });
            }
            args.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
            Assert.That(ItemMovementV4.ValidateArguments(args), Is.Not.Empty);
            var success = typeof(LuaInvocationResult).GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Single(method => method.Name == "Success" && method.GetParameters().Length == 2);
            var invocation = (LuaInvocationResult)success.Invoke(null, new object[] { ShuffleScript("return function(ctx) return {} end"), new[] { LuaEffectSpec.FromArguments(TypeId, args) } });
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out _, out _), Is.False);
            Assert.That(state.EffectRuntime.EffectNodes, Is.Empty);
            // 手写内核规格按既有架构可先建节点，但执行校验仍须失败且零移动。
            var engine = new EffectTreeExecutor(state, Registry());
            if (engine.TryCreateRoot(spec, "", "", out string root))
            {
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(engine.GetNode(root).PositionChanges, Is.Empty);
            }
            else Assert.That(state.EffectRuntime.EffectNodes, Is.Empty);
        }
        [TestCase("wrong_supply")][TestCase("original_supply")][TestCase("type")][TestCase("capacity")]
        public void MarkerV4RejectsRealInventoryBoundaryViolationsWithoutPartialMovement(string fault)
        {
            var state = State(); var registry = Registry(); string storage = MarkerStorageV4(state, Ids(state, "hand", 1)[0]);
            string supply = Zone(state, "supply", 2).ZoneId; string marker = Ids(state, "supply", 2)[0];
            if (fault == "capacity")
                foreach (string id in Ids(state, "supply", 2).Take(2).ToArray()) Run(state, MarkerSpecV4(1, MarkerLegV4(id, supply, storage)), registry);
            if (fault == "capacity") marker = Ids(state, "supply", 2)[0];
            if (fault == "type") { marker = Ids(state, "hand", 2)[0]; supply = Zone(state, "hand", 2).ZoneId; }
            if (fault == "original_supply") state.ItemZones.Items.Single(i => i.ItemId == marker).OriginalSupplyZoneId = Zone(state, "supply", 1).ZoneId;
            string target = fault == "wrong_supply" ? Zone(state, "supply", 1).ZoneId : storage;
            var before = state.ItemZones.Items.ToDictionary(i => i.ItemId, i => i.ZoneId + ":" + i.SlotId);
            var engine = new EffectTreeExecutor(state, registry); var spec = MarkerSpecV4(1, MarkerLegV4(marker, supply, target));
            if (fault == "original_supply")
            {
                Assert.That(engine.TryCreateRoot(spec, "", "", out _), Is.False);
                Assert.That(state.EffectRuntime.LastFaultMessage, Does.Contain("OriginalSupplyZoneId_invalid")); Assert.That(state.EffectRuntime.EffectNodes, Is.Empty);
                Assert.That(state.ItemZones.Items.All(i => before[i.ItemId] == i.ZoneId + ":" + i.SlotId), Is.True); return;
            }
            string root = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(engine.GetNode(root).FailureReason, Is.Not.Empty);
            Assert.That(engine.GetNode(root).PositionChanges, Is.Empty); Assert.That(state.ItemZones.Items.All(i => before[i.ItemId] == i.ZoneId + ":" + i.SlotId), Is.True);
        }
        [TestCase(1)][TestCase(2)]
        public void MarkerV4DoesNotReinterpretOldDefinitionMarkerPlans(int version)
        {
            var state = State(); string marker = Ids(state, "supply", 2)[0]; string supply = Zone(state, "supply", 2).ZoneId;
            var old = MoveSpec(1, new[] { marker }, supply, Zone(state, "supply", 2).ZoneId, "unordered", 1, "core.player_marker_storage");
            var args = old.NormalizedArguments;
            if (version == 2)
            {
                var leg = args.Clone(); leg.Properties.RemoveAll(p => p.Name == "apiVersion" || p.Name == "player");
                Value(leg, "operationProfileId").StringValue = "core.facility_card_transfer";
                args = Object("apiVersion", NormalizedValue.CreateInteger(2), "player", NormalizedValue.CreateString("1"),
                    "operationId", NormalizedValue.CreateString("old"), "transfers", NormalizedValue.CreateArray(new[] { leg }), "expectedOrders", NormalizedValue.CreateArray(new NormalizedValue[0]));
            }
            Assert.That(ValidateArguments(TypeId, args), Is.Empty);
            Assert.That(CanRestoreLegacy(state, new EffectNodeRuntimeState { EffectTypeId = TypeId, DefinitionVersion = "2.0.0", NormalizedArguments = args }), Is.False);
        }
        [Test]
        public void ExplicitMarkerMinimumMayExceedDeclaredItemsButFailsWholeMoveWithoutSelection()
        {
            var state=State();string marker=Ids(state,"supply",2)[0];
            var spec=MarkerSpecV4(1,MarkerLegV4(marker,Zone(state,"supply",2).ZoneId,null));Value(spec.NormalizedArguments,"requiredCount").IntegerValue=2;
            Assert.That(ItemMovementV4.ValidateArguments(spec.NormalizedArguments),Is.Empty);
            string before=JsonConvert.SerializeObject(state.ItemZones);var node=Run(state,spec);
            Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(node.FailureReason,Is.EqualTo("insufficient_items"));
            Assert.That(node.PositionChanges,Is.Empty);Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);Assert.That(JsonConvert.SerializeObject(state.ItemZones),Is.EqualTo(before));
        }
    }
}
