using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        private static EffectSpec CharacterMove(GameState state, string card, string destination, bool direct)
        {
            var spec = CharacterCardMovement.Create(state, 1, card, destination);
            if (direct)
            {
                spec.EffectTypeId = TypeId; spec.DefinitionVersion = TransferDefinitionVersion;
                spec.NormalizedArguments.Properties.Single(p => p.Name == "apiVersion").Value = NormalizedValue.CreateInteger(1);
            }
            return spec;
        }
        [TestCase(false)][TestCase(true)]
        public void CharacterMoveRealLuaTransfersSameInstanceAndKeepsPublicPrivateResults(bool direct)
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", 1)[0];
            string code = "return function(ctx) return {Effect." + (direct ? "MoveItem" : "MoveCharacterCard") + "({apiVersion=" + (direct ? "1" : "2") +
                ",player=ctx.playerId,itemRefs={{itemId='" + card + "'}},expectedSource={zoneId='" + Zone(state, "hand", 1).ZoneId +
                "'},destination={zoneId='" + Zone(state, "hand", 2).ZoneId + "'},insertion={kind='unordered'},operationProfileId='core.character_card_transfer',requiredCount=1})} end";
            var script = new LuaScriptDefinition("api19", "api19", "api19", "1.0.0", code, LuaContentHasher.ComputeSha256(code));
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(script, new LuaInvocationContext("api19", "isolated", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            var moved = Run(state, specs.Single(), registry);
            Assert.That(Number(moved.NormalizedResult, "movedCount"), Is.EqualTo(1)); Assert.That(Value(moved.NormalizedResult, "movedItemIds").Items.Single().StringValue, Is.EqualTo(card));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == card).OwnerSubject.PlayerId, Is.EqualTo(1));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == card).ControllerSubject.PlayerId, Is.EqualTo(2));
            var fact = state.EffectRuntime.RuleEvents.Single(e => e.EventType == "character.transferred");
            Assert.That(Number(fact.Payload, "recipientPlayerId"), Is.EqualTo(2)); Assert.That(Text(fact.HostOnlyPayload, "itemId"), Is.EqualTo(card));
            var spectator = GameStateViewProjector.Project(state, new GameStateViewer { Role = GameStateViewerRole.Spectator });
            Assert.That(JsonConvert.SerializeObject(spectator), Does.Not.Contain(card));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).ItemZones.Items.Any(i => i.ItemId == card), Is.True);
            state = Restore(state, registry); Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Completed, Is.True);
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "character.transferred"), Is.EqualTo(1));
        }
        [TestCase(false)][TestCase(true)]
        public void CharacterMoveWaitsForRequiredTransferResponseAndRestoresWithoutMovingTwice(bool direct)
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", 1)[0];
            registry.Register("api19.ack", c => c.Node.FlowStage == "" ? EffectStepResult.Continue("answered").AddInteraction(new EffectInteractionSpec {
                InteractionTypeId = "api19.ack", AnsweringPlayerId = 1, Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "confirm" }
            }) : EffectStepResult.Completed(), EffectExecutorKind.IntrinsicFlow);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("api19.ack", "character.transferred", "*", "api19.ack",
                c => new List<EffectSpec> { EffectSpec.Create("api19.ack", playerId: 1) }, role: EffectHandlerRole.Observer));
            string root = Run(state, CharacterMove(state, card, Zone(state, "hand", 2).ZoneId, direct), registry).EffectId;
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Ids(state, "hand", 2), Does.Contain(card)); state = Restore(state, registry);
            AnswerRequest(state, registry, "confirm");
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "character.transferred"), Is.EqualTo(1));
            var wrapper = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root);
            Assert.That(wrapper.PositionChanges.Count, Is.EqualTo(direct ? 1 : 0));
            var common = direct ? wrapper : YC.Domain.Items.SpecializedItemMovement.Body(state, wrapper);
            Assert.That(common.PositionChanges.Count, Is.EqualTo(1));
            if (!direct) Assert.That(common.CommonItemMovementV4.Receipt.Result.MovedItemIds, Is.EqualTo(new[] { card }));
            Restore(state, registry);
        }
        [TestCase(false)][TestCase(true)]
        public void CharacterMoveSamePlayerAndNoChangeNeverEmitCrossPlayerTransfer(bool direct)
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", 1)[0];
            var same = Run(state, CharacterMove(state, card, Zone(state, "hand", 1).ZoneId, direct), registry);
            Assert.That(Number(same.NormalizedResult, "movedCount"), Is.Zero);
            Run(state, CharacterMove(state, card, Zone(state, "discard", 1).ZoneId, direct), registry);
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == "character.transferred"), Is.False);
            Restore(state, registry);
        }
        [TestCase(false)][TestCase(true)]
        public void CharacterMoveInvalidSecondInstanceCannotPartiallyMoveOrEmitTransfer(bool direct)
        {
            var state = State(); var registry = Registry(); var own = Ids(state, "hand", 1); string foreign = Ids(state, "hand", 2)[0];
            var spec = CharacterMove(state, own[0], Zone(state, "hand", 2).ZoneId, direct);
            spec.NormalizedArguments.Properties.Single(p => p.Name == "itemRefs").Value = NormalizedValue.CreateArray(new[] { Object("itemId", NormalizedValue.CreateString(own[0])), Object("itemId", NormalizedValue.CreateString(foreign)) });
            string before = JsonConvert.SerializeObject(state.ItemZones);
            var result = Run(state, spec, registry); Assert.That(result.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(JsonConvert.SerializeObject(state.ItemZones), Is.EqualTo(before));
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == "character.transferred"), Is.False);
        }
        [TestCase(false)][TestCase(true)]
        public void EarlierCharacterMovementVersionIsRejectedBeforeRecovery(bool direct)
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", 1)[0];
            var node = Run(state, CharacterMove(state, card, Zone(state, "hand", 2).ZoneId, direct), registry);
            var raw = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            raw["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].Single(n => (string)n["EffectId"] == node.EffectId)["DefinitionVersion"] = direct ? "3.0.0" : "1.0.0";
            string before = raw.ToString(); var rejected = HostRecoveryService.RecoverJson(before, registry);
            Assert.That(rejected.Succeeded, Is.False); Assert.That(rejected.Diagnostic, Does.Contain(direct?"item_move_contract_version_unsupported":CharacterCardMovement.UnsupportedSavedVersion));
            Assert.That(raw.ToString(), Is.EqualTo(before));
        }
        [TestCase(false, "source")][TestCase(true, "source")]
        [TestCase(false, "controller")][TestCase(true, "controller")]
        [TestCase(false, "type")][TestCase(true, "type")]
        public void CharacterMoveRechecksFrozenSourceControllerAndRealType(bool direct, string change)
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", 1)[0];
            var spec = CharacterMove(state, card, Zone(state, "hand", 2).ZoneId, direct);
            if (change != "type")
            {
                Run(state, CharacterMove(state, card, Zone(state, "hand", 2).ZoneId, direct), registry);
                if (change == "controller") spec = CharacterMove(state, card, Zone(state, "hand", 1).ZoneId, direct);
            }
            else spec = CharacterMove(state, Ids(state, "facility_deck")[0], Zone(state, "hand", 2).ZoneId, direct);
            string before = JsonConvert.SerializeObject(state.ItemZones); int events = state.EffectRuntime.RuleEvents.Count(e => e.EventType == "character.transferred");
            var rejected = Run(state, spec, registry); Assert.That(rejected.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(JsonConvert.SerializeObject(state.ItemZones), Is.EqualTo(before));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "character.transferred"), Is.EqualTo(events));
            if (change == "controller") Assert.That(rejected.FailureReason, Is.EqualTo("transfer_permission_denied"));
        }
    }
}
