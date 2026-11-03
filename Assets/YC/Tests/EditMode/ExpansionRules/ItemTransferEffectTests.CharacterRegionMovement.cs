using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        private static EffectSpec RevealCharacterMove(GameState state, string card, string target, bool direct)
        {
            var spec = CharacterMove(state, card, target, direct);
            spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "reveal", Value = NormalizedValue.CreateBoolean(true) });
            spec.NormalizedArguments.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            return spec;
        }
        [TestCase(false, "hand")][TestCase(true, "hand")]
        [TestCase(false, "discard")][TestCase(true, "discard")]
        public void CharacterRegionMoveRealLuaRevealsOnlyTheMovedInstance(bool direct, string target)
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", 1)[0], other = Ids(state, "hand", 1)[1];
            if (target == "hand") Run(state, CharacterMove(state, card, Zone(state, "discard", 1).ZoneId, direct), registry);
            var spec = RevealCharacterMove(state, card, Zone(state, target, 1).ZoneId, direct);
            string body = "return function(ctx) return {Effect." + (direct ? "MoveItem" : "MoveCharacterCard") + "({apiVersion=" + (direct ? 1 : 2) +
                ",player=ctx.playerId,itemRefs={{itemId='" + card + "'}},expectedSource={zoneId='" + Text(Value(spec.NormalizedArguments, "expectedSource"), "zoneId") +
                "'},destination={zoneId='" + Zone(state, target, 1).ZoneId + "'},insertion={kind='unordered'},operationProfileId='core.character_card_transfer',requiredCount=1,reveal=true})} end";
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript(body), new LuaInvocationContext("api23", "isolated", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            var moved = Run(state, specs.Single(), registry);
            Assert.That(Number(moved.NormalizedResult, "movedCount"), Is.EqualTo(1));
            var fact = state.EffectRuntime.RuleEvents.Single(e => e.EventType == "character.cards.revealed");
            Assert.That(fact.Visibility, Is.EqualTo("public")); Assert.That(fact.Payload.IsValid(), Is.True);
            Assert.That(Value(fact.Payload, "itemIds").Items.Select(v => v.StringValue), Is.EqualTo(new[] { card }));
            Assert.That(Text(fact.Payload, "recoveredItemId"), Is.EqualTo(target == "hand" ? card : ""));
            Assert.That(Value(fact.Payload, "discardedItemIds").Items.Select(v => v.StringValue), Is.EqualTo(target == "discard" ? new[] { card } : Array.Empty<string>()));
            Assert.That(JsonConvert.SerializeObject(fact.Payload), Does.Not.Contain(other));
            Assert.That(JsonConvert.SerializeObject(GameStateViewProjector.ProjectForSpectator(state)), Does.Not.Contain(other));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "character.cards.revealed"), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == "character.transferred"), Is.False);
        }
        [TestCase(false)][TestCase(true)]
        public void CharacterRegionMoveAcquiresExactPublicShapeWithoutTransactionIdentity(bool direct)
        {
            var state = State(publicShape: true); var registry = Registry(); string shape = Ids(state, "flowing_shapes").Single();
            var owner = GameStateCloneService.DeepClone(state.ItemZones.Items.Single(i => i.ItemId == shape).OwnerSubject);
            var moved = Run(state, CharacterMove(state, shape, Zone(state, "hand", 1).ZoneId, direct), registry);
            Assert.That(Number(moved.NormalizedResult, "movedCount"), Is.EqualTo(1));
            Assert.That(Ids(state, "hand", 1), Does.Contain(shape));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == shape).ControllerSubject.PlayerId, Is.EqualTo(1));
            Assert.That(JsonConvert.SerializeObject(state.ItemZones.Items.Single(i => i.ItemId == shape).OwnerSubject), Is.EqualTo(JsonConvert.SerializeObject(owner)));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == "effect.content.transaction.move_character"), Is.False);
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == "character.transferred" || e.EventType == "character.cards.revealed"), Is.False);
            Restore(state, registry);
        }
        [TestCase(false, "omitted")][TestCase(true, "omitted")]
        [TestCase(false, "false")][TestCase(true, "false")]
        [TestCase(false, "noop")][TestCase(true, "noop")]
        [TestCase(false, "required_noop")][TestCase(true, "required_noop")]
        [TestCase(false, "failed")][TestCase(true, "failed")]
        public void CharacterRegionMoveDefaultFalseNoChangeAndFailureDoNotReveal(bool direct, string mode)
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", mode == "failed" ? 2 : 1)[0];
            string target = Zone(state, mode == "noop" || mode == "required_noop" ? "hand" : "discard", 1).ZoneId;
            var spec = mode == "omitted" ? CharacterMove(state, card, target, direct) : RevealCharacterMove(state, card, target, direct);
            if (mode == "false") spec.NormalizedArguments.Properties.Single(p => p.Name == "reveal").Value = NormalizedValue.CreateBoolean(false);
            // 零变化允许requiredCount=0；明确要求至少移动一张时仍须不足失败。
            if (mode == "noop") spec.NormalizedArguments.Properties.Single(p => p.Name == "requiredCount").Value = NormalizedValue.CreateInteger(0);
            string before = JsonConvert.SerializeObject(state.ItemZones);
            var node = Run(state, spec, registry);
            Assert.That(node.Status, Is.EqualTo(mode == "failed" || mode == "required_noop" ? EffectNodeStatus.Failed : EffectNodeStatus.Completed));
            if (mode == "required_noop") Assert.That(node.FailureReason, Is.EqualTo("insufficient_items"));
            if (mode == "noop" || mode == "required_noop" || mode == "failed") Assert.That(JsonConvert.SerializeObject(state.ItemZones), Is.EqualTo(before));
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == "character.cards.revealed"), Is.False);
        }
        [TestCase(false)][TestCase(true)]
        public void CharacterRegionMoveRequiredRevealResponseRestoresWithoutRevealingTwice(bool direct)
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", 1)[0];
            registry.Register("api23.reveal.ack", c => c.Node.FlowStage == "" ? EffectStepResult.Continue("answered").AddInteraction(new EffectInteractionSpec {
                InteractionTypeId = "api23.reveal.ack", AnsweringPlayerId = 1, Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "confirm" }
            }) : EffectStepResult.Completed(), EffectExecutorKind.IntrinsicFlow);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("api23.reveal.ack", "character.cards.revealed", "*", "api23.reveal.ack",
                c => new[] { EffectSpec.Create("api23.reveal.ack", playerId: 1) }, role: EffectHandlerRole.Observer));
            string root = Run(state, RevealCharacterMove(state, card, Zone(state, "discard", 1).ZoneId, direct), registry).EffectId;
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            state = Restore(state, registry); AnswerRequest(state, registry, "confirm");
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            var movement = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root);
            if (!direct) movement = state.EffectRuntime.EffectNodes.Single(n => movement.ChildEffectIds.Contains(n.EffectId) && ItemMovementV4.IsV4(n.EffectTypeId, n.NormalizedArguments));
            Assert.That(movement.PositionChanges.Count, Is.EqualTo(1));
            Assert.That(ItemMovementV4.TryValidateState(state, movement, out var movementError), Is.True, movementError);
            Assert.That(state.EffectRuntime.RuleEvents.Single(e => e.EventType == "character.cards.revealed").SourceEffectId, Is.EqualTo(movement.EffectId));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "character.cards.revealed"), Is.EqualTo(1));
            Restore(state, registry);
        }
        [TestCase(false)][TestCase(true)]
        public void CharacterRegionMovePreviousSharedDefinitionCannotRestoreAsRevealContract(bool direct)
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", 1)[0];
            var node = Run(state, RevealCharacterMove(state, card, Zone(state, "discard", 1).ZoneId, direct), registry);
            var raw = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            raw["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].Single(n => (string)n["EffectId"] == node.EffectId)["DefinitionVersion"] = direct ? "3.1.0" : "2.1.0";
            string before = raw.ToString(); var rejected = HostRecoveryService.RecoverJson(before, registry);
            Assert.That(rejected.Succeeded, Is.False); Assert.That(rejected.Diagnostic, Does.Contain(direct ? "item_move_contract_version_unsupported" : YC.Domain.Cards.CharacterCardMovement.UnsupportedSavedVersion));
            Assert.That(raw.ToString(), Is.EqualTo(before));
        }
        [TestCase(false, "'true'", "core.character_card_transfer")][TestCase(true, "'true'", "core.character_card_transfer")]
        [TestCase(false, "1", "core.character_card_transfer")][TestCase(true, "1", "core.character_card_transfer")]
        [TestCase(true, "true", "core.facility_card_transfer")][TestCase(true, "false", "core.cube_storage")]
        public void CharacterRegionMoveRejectsInvalidRevealAndNonCharacterProfile(bool direct, string reveal, string profile)
        {
            string body = "return function(ctx) return {Effect." + (direct ? "MoveItem" : "MoveCharacterCard") + "({apiVersion=" + (direct ? 1 : 2) +
                ",player=ctx.playerId,itemRefs={{itemId='card'}},expectedSource={zoneId='source'},destination={zoneId='target'},insertion={kind='unordered'},operationProfileId='" + profile + "',requiredCount=1,reveal=" + reveal + "})} end";
            var result = new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript(body), new LuaInvocationContext("api23", "isolated", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(result.IsSuccess, Is.False); Assert.That(result.Diagnostic, Does.Contain("character_reveal_invalid"));
        }
        [TestCase(false)][TestCase(true)]
        public void CharacterRegionMoveLargeValidGroupKeepsAllRevealIdsAndResults(bool direct)
        {
            var state = State(characters: 50); var registry = Registry(); var ids = Ids(state, "hand", 1);
            var spec = RevealCharacterMove(state, ids[0], Zone(state, "discard", 1).ZoneId, direct);
            spec.NormalizedArguments.Properties.Single(p => p.Name == "itemRefs").Value = NormalizedValue.CreateArray(ids.Select(id => Object("itemId", NormalizedValue.CreateString(id))).ToList());
            Assert.That(spec.NormalizedArguments.IsValid(), Is.True);
            var node = Run(state, spec, registry); Assert.That(Number(node.NormalizedResult, "movedCount"), Is.EqualTo(50));
            Assert.That(node.NormalizedResult.IsValid(), Is.True);
            var fact = state.EffectRuntime.RuleEvents.Single(e => e.EventType == "character.cards.revealed");
            Assert.That(fact.Payload.IsValid(), Is.True);
            Assert.That(Value(fact.Payload, "itemIds").Items.Select(v => v.StringValue), Is.EqualTo(ids));
            Assert.That(Value(fact.Payload, "definitionIds").Items.Count, Is.EqualTo(50));
            Assert.That(Value(fact.Payload, "recoveredItemId"), Is.Null);
            state = Restore(state, registry); Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "character.cards.revealed"), Is.EqualTo(1));
        }
    }
}
