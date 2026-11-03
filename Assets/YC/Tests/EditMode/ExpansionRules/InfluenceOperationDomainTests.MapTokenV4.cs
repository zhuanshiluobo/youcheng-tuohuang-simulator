using System;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class InfluenceOperationDomainTests
    {
        private string MapToken(GameState state)
        {
            string id = state.ItemZones.Items.Single(i => i.TypeId == "infected.gavial_token").ItemId;
            SetPosition(state, id, Child(state, "quay", "core.zone.map_location", "core.zone.map_tokens").ZoneId);
            artifacts.WriteLabeledText("API30地图隔离前提", "私有地图和真实类型实例的隔离规则测试；不计正常对局、界面或联机验收。");
            return id;
        }
        private EffectSpec TokenSpec(GameState state, string token, string destination, bool direct = true)
        {
            if (!direct) return EffectSpec.Create(LuaDomainEffectTypeIds.OperateToken, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"),
                "tokenItemId", NormalizedValue.CreateString(token), "operation", NormalizedValue.CreateString("move"), "destinationLocationId", NormalizedValue.CreateString(destination), "operationProfileId", NormalizedValue.CreateString("core.adjacent_location_token_move")), 1);
            return EffectSpec.Create(TypeId, Object("apiVersion", NormalizedValue.CreateInteger(4), "player", NormalizedValue.CreateString("1"), "operation", NormalizedValue.CreateString("move_map_token"),
                "selector", Object("mode", NormalizedValue.CreateString("explicit"), "itemIds", Strings(new[] { token }), "expectedSource", Object("zoneId", NormalizedValue.CreateString(state.ItemZones.Items.Single(i => i.ItemId == token).ZoneId))),
                "destinationLocationId", NormalizedValue.CreateString(destination), "operationProfileId", NormalizedValue.CreateString("core.adjacent_location_token_move")), 1);
        }
        [TestCase(4294967300L)][TestCase(-4294967292L)]
        public void MapTokenV4RejectsIntegerWrappedVersionsBeforePlanning(long version)
        {
            var state = State(); string token = MapToken(state); var args = TokenSpec(state, token, "pier").NormalizedArguments;
            Value(args, "apiVersion").IntegerValue = version;
            Assert.That(ItemMovementV4.IsV4(TypeId, args), Is.False);
            Assert.That(ItemMovementV4.ValidateArguments(args), Is.EqualTo("item_move_v4_arguments_invalid"));
            Assert.That(state.EffectRuntime.EffectNodes, Is.Empty); Assert.That(state.EffectRuntime.RuleEvents, Is.Empty);
        }
        [Test]
        public void MapTokenV4MatchesDedicatedActualStateAndEventPayload()
        {
            var initial = State(); string token = MapToken(initial); var direct = GameStateCloneService.DeepClone(initial); var dedicated = GameStateCloneService.DeepClone(initial);
            var moved = Run(direct, TokenSpec(direct, token, "mine")); var wrapped = Run(dedicated, TokenSpec(dedicated, token, "mine", false));
            Assert.That(moved.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(wrapped.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(JsonConvert.SerializeObject(direct.ItemZones), Is.EqualTo(JsonConvert.SerializeObject(dedicated.ItemZones)));
            Assert.That(JsonConvert.SerializeObject(direct.Players), Is.EqualTo(JsonConvert.SerializeObject(dedicated.Players)));
            Assert.That(JsonConvert.SerializeObject(direct.Map), Is.EqualTo(JsonConvert.SerializeObject(dedicated.Map)));
            Assert.That(ItemTransferEffectTests.Api32Views(direct), Is.EqualTo(ItemTransferEffectTests.Api32Views(dedicated)));
            var receipt = moved.CommonItemMovementV4.Receipt; Assert.That(receipt.Items.Single().To.ZoneId, Is.EqualTo(Child(direct, "mine", "core.zone.map_location", "core.zone.map_tokens").ZoneId));
            Assert.That(Text(moved.NormalizedResult, "tokenItemId"), Is.EqualTo(token)); Assert.That(Number(moved.NormalizedResult, "movedCount"), Is.EqualTo(1));
            var directEvent = direct.EffectRuntime.RuleEvents.Single(e => e.EventType == "token.moved"); var wrappedEvent = dedicated.EffectRuntime.RuleEvents.Single(e => e.EventType == "token.moved");
            Assert.That(JsonConvert.SerializeObject(directEvent.Payload), Is.EqualTo(JsonConvert.SerializeObject(wrappedEvent.Payload)));
            Assert.That(directEvent.Visibility, Is.EqualTo(wrappedEvent.Visibility)); Assert.That(directEvent.ResponseKind, Is.EqualTo(wrappedEvent.ResponseKind));
            Assert.That(JsonConvert.SerializeObject(directEvent.HostOnlyPayload), Is.EqualTo(JsonConvert.SerializeObject(wrappedEvent.HostOnlyPayload)));
            Assert.That(directEvent.SourceEffectId, Is.EqualTo(moved.EffectId));
            Assert.That(wrappedEvent.SourceEffectId, Is.EqualTo(SpecializedItemMovement.Body(dedicated, wrapped).EffectId));
            Assert.That(Text(Value(wrapped.NormalizedResult, "destination"), "zoneId"), Is.EqualTo(receipt.Items.Single().To.ZoneId));
            Assert.That(direct.EffectRuntime.RuleEvents.Any(e => e.EventType == "CityMoveCompleted"), Is.False);
            direct = Restore(direct); dedicated = Restore(dedicated); Run(direct, TokenSpec(direct, token, "quay")); direct = Restore(direct);
            Assert.That(direct.EffectRuntime.RuleEvents.Count(e => e.EventType == "token.moved"), Is.EqualTo(2));
        }
        [TestCase(true, "closed")][TestCase(false, "closed")]
        [TestCase(true, "quay")][TestCase(false, "quay")]
        [TestCase(true, "missing")][TestCase(false, "missing")]
        [TestCase(true, "restricted")][TestCase(false, "restricted")]
        [TestCase(true, "controller")][TestCase(false, "controller")]
        public void MapTokenV4AndDedicatedRejectSameRulesWithoutChanges(bool direct, string fault)
        {
            var state = State(); string token = MapToken(state), destination = fault == "controller" || fault == "restricted" ? "mine" : fault;
            if (fault == "controller") state.ItemZones.Items.Single(i => i.ItemId == token).ControllerSubject = ItemSubjectRef.Player(2);
            if (fault == "restricted") registry.TokenMovementProfiles = new TokenMovementProfileRegistry(new MapQueryService(map)) { DestinationAllowed = (s, i, d) => d == "pier" };
            string before = JsonConvert.SerializeObject(state.ItemZones); var node = Run(state, TokenSpec(state, token, destination, direct));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(node.FailureReason, Is.EqualTo(fault == "controller" ? "transfer_permission_denied" : "token_destination_rejected"));
            Assert.That(JsonConvert.SerializeObject(state.ItemZones), Is.EqualTo(before)); Assert.That(node.PositionChanges, Is.Empty);
            // 正常失败终态仍发EffectCompleted(failed)；不得误当成token.moved业务事件。
            Assert.That(state.EffectRuntime.RuleEvents.Count, Is.EqualTo(1));
            var completion = state.EffectRuntime.RuleEvents.Single(); Assert.That(completion.EventType, Is.EqualTo("EffectCompleted")); Assert.That(Text(completion.Payload, "outcome"), Is.EqualTo("failed"));
            Assert.That(Text(completion.Payload, "failureReason"), Is.EqualTo(node.FailureReason)); Restore(state);
        }
        [TestCase("")][TestCase("v4_bound")][TestCase("v4_committed")][TestCase("v4_finished")]
        public void MapTokenV4RestoresAtRealExecutionBoundariesAndMovesOnce(string stage)
        {
            var state = State(); string token = MapToken(state); var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(TokenSpec(state, token, "pier"));
            for (int i = 0; engine.GetNode(id).FlowStage != stage && i < 25; i++) Assert.That(engine.Advance(), Is.True);
            Assert.That(engine.GetNode(id).FlowStage, Is.EqualTo(stage)); state = Restore(state); engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); state = Restore(state);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).PositionChanges.Count, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "token.moved"), Is.EqualTo(1)); Assert.That(TokenMovementProfileRegistry.GetLocationId(state, token), Is.EqualTo("pier"));
        }
        [Test]
        public void MapTokenV4RechecksRestrictionAtCommitAndRejectsChangedSource()
        {
            var state = State(); string token = MapToken(state); var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(TokenSpec(state, token, "pier"));
            while (engine.GetNode(id).FlowStage != "v4_bound") Assert.That(engine.Advance(), Is.True);
            registry.TokenMovementProfiles.DestinationAllowed = (s, i, d) => false; state = Restore(state); engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False); Assert.That(engine.GetNode(id).FailureReason, Is.EqualTo("token_destination_rejected")); Assert.That(engine.GetNode(id).PositionChanges, Is.Empty);
            registry.TokenMovementProfiles.DestinationAllowed = (s, i, d) => true; var stale = TokenSpec(state, token, "mine"); Run(state, TokenSpec(state, token, "pier"));
            Assert.That(Run(state, stale).FailureReason, Is.EqualTo("source_zone_changed")); Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "token.moved"), Is.EqualTo(1)); Restore(state);
        }
        [Test]
        public void MapTokenV4WaitsForRequiredMovedResponseAndRestoresOnce()
        {
            var state = State(); string token = MapToken(state);
            registry.Register("api30.token.ack", c => c.Node.FlowStage == "" ? EffectStepResult.Continue("answered").AddInteraction(new EffectInteractionSpec {
                InteractionTypeId = "api30.token.ack", AnsweringPlayerId = 2, Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "confirm" } }) : EffectStepResult.Completed(), EffectExecutorKind.IntrinsicFlow);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("api30.token.ack", "token.moved", "*", "api30.token.ack", c => new System.Collections.Generic.List<EffectSpec> { EffectSpec.Create("api30.token.ack", playerId: 2) }, role: EffectHandlerRole.Observer));
            string id = Run(state, TokenSpec(state, token, "pier")).EffectId; Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            state = Restore(state); var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open"); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 2, NormalizedValue.CreateString("confirm"), out var reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); state = Restore(state);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "token.moved"), Is.EqualTo(1)); Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).PositionChanges.Count, Is.EqualTo(1));
            Assert.That(new EffectTreeExecutor(state, registry).TrySubmitInteraction(request.GetStableInteractionId(), 2, NormalizedValue.CreateString("confirm"), out _), Is.False);
        }
        [TestCase("summary")][TestCase("event")][TestCase("destination")][TestCase("missing_receipt")][TestCase("old_definition")]
        public void MapTokenV4RejectsForgedSavedFactsWithoutChangingOriginal(string fault)
        {
            var state = State(); string token = MapToken(state); var node = Run(state, TokenSpec(state, token, "pier"));
            string clean = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)); var raw = JObject.Parse(clean);
            var saved = raw["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].Single(n => (string)n["EffectId"] == node.EffectId);
            if (fault == "old_definition") saved["DefinitionVersion"] = "4.2.0";
            if (fault == "summary") ((JArray)saved["NormalizedResult"]["Properties"]).Single(p => (string)p["Name"] == "tokenItemId")["Value"]["StringValue"] = "foreign";
            if (fault == "destination") ((JArray)saved["NormalizedArguments"]["Properties"]).Single(p => (string)p["Name"] == "destinationLocationId")["Value"]["StringValue"] = "mine";
            if (fault == "missing_receipt") saved["CommonItemMovementV4"]["Receipt"] = JValue.CreateNull();
            if (fault == "event") raw["Snapshot"]["State"]["EffectRuntime"]["RuleEvents"].Single(e => (string)e["EventType"] == "token.moved")["Payload"] = JToken.FromObject(Object("changed", NormalizedValue.CreateBoolean(false)));
            string bad = raw.ToString(); artifacts.WriteLabeledText("地图v4坏档_" + fault, bad);
            Assert.That(HostRecoveryService.RecoverJson(bad, registry).Succeeded, Is.False); Assert.That(raw.ToString(), Is.EqualTo(bad)); Assert.That(HostRecoveryService.RecoverJson(clean, registry).Succeeded, Is.True);
        }
        [Test]
        public void LuaMapTokenV4CompilesAndRejectsMixedFieldsAndOrdinaryProfileBypass()
        {
            var state = State(); string token = MapToken(state), zone = state.ItemZones.Items.Single(i => i.ItemId == token).ZoneId;
            string source = "return function(ctx) return {Effect.MoveItem({apiVersion=4,player=ctx.playerId,operation='move_map_token',selector={mode='explicit',itemIds={'" + token + "'},expectedSource={zoneId='" + zone + "'}},destinationLocationId='pier',operationProfileId='core.adjacent_location_token_move'})} end";
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("map.v4", "map.v4", "map.v4", "1", source, LuaContentHasher.ComputeSha256(source)), new LuaInvocationContext("map", "map", "p1", 0, "1", 2, 0, 0));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(new LuaEffectSpecCompiler().TryCompile(result, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var error), Is.True, error?.Diagnostic);
            Assert.That(Run(state, specs.Single()).Status, Is.EqualTo(EffectNodeStatus.Completed));
            var invalid = TokenSpec(state, token, "quay").NormalizedArguments;
            invalid.Properties.Add(new NormalizedValueEntry { Name = "requiredCount", Value = NormalizedValue.CreateInteger(0) }); invalid.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
            Assert.That(ItemMovementV4.ValidateArguments(invalid), Is.EqualTo("item_move_v4_field_rejected"));
            invalid.Properties.RemoveAll(p => p.Name == "requiredCount"); Value(invalid, "operation").StringValue = "transfer";
            invalid.Properties.RemoveAll(p => p.Name == "destinationLocationId"); invalid.Properties.Add(new NormalizedValueEntry { Name = "destination", Value = Object("zoneId", NormalizedValue.CreateString(zone)) });
            invalid.Properties.Add(new NormalizedValueEntry { Name = "insertion", Value = Object("kind", NormalizedValue.CreateString("unordered")) }); invalid.Properties.Add(new NormalizedValueEntry { Name = "requiredCount", Value = NormalizedValue.CreateInteger(1) });
            invalid.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
            Assert.That(ItemMovementV4.ValidateArguments(invalid), Is.EqualTo("operation_profile_rejected")); Restore(state);
        }
    }
}
