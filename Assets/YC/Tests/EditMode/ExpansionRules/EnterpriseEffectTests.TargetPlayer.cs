using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class EnterpriseEffectTests
    {
        private static EffectSpec TargetUpgrade(int actor, int? target, string item, int levels = 1)
        {
            var args = Object("apiVersion", NormalizedValue.CreateInteger(2), "player", NormalizedValue.CreateString(actor.ToString()),
                "enterpriseItemId", NormalizedValue.CreateString(item), "levels", NormalizedValue.CreateInteger(levels));
            if (target.HasValue) args.Properties.Add(new NormalizedValueEntry { Name = "targetPlayer", Value = NormalizedValue.CreateString(target.Value.ToString()) });
            args.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
            return EffectSpec.Create(LuaDomainEffectTypeIds.UpgradeEnterprise, args, actor);
        }
        [TestCase(false)][TestCase(true)]
        public void TargetPlayerReceivesActualLevelsRewardsAndEventsWhileInitiatorStaysUnchanged(bool other)
        {
            var state = State(); var registry = Registry(); int target = other ? 2 : 1;
            var node = Run(state, TargetUpgrade(1, other ? 2 : (int?)null, ItemId(state, Business), 3), registry);
            Assert.That(node.PlayerId, Is.EqualTo(1)); Assert.That(Level(state, Business, target), Is.EqualTo(3));
            Assert.That(Level(state, Business, other ? 1 : 2), Is.Zero);
            Assert.That(state.FindPlayer(target).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(other ? 1 : 2).Resources.GoldVoucher, Is.Zero);
            Assert.That(node.EnterpriseArrivalReceipts.All(r => r.PlayerId == target), Is.True);
            Assert.That(state.EffectRuntime.RuleEvents.Where(e => e.EventType == EnterpriseEffectExecutor.LevelChangedEvent).All(e => e.PlayerId == target), Is.True);
            Assert.That(Number(node.NormalizedResult, "initiatingPlayerId"), Is.EqualTo(1)); Assert.That(Number(node.NormalizedResult, "targetPlayerId"), Is.EqualTo(target));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(state.FindPlayer(target).Resources.GoldVoucher, Is.EqualTo(2)); Assert.That(Level(state, Business, target), Is.EqualTo(3));
        }
        [TestCase(false)][TestCase(true)]
        public void TargetPlayerMissingMarkerAndLevelCapReturnRealNoChange(bool missing)
        {
            var state = State(opponentBusinessLevel: 5); var registry = Registry();
            if (missing) RemoveCooperationMarkerForIsolation(state, Business, 2);
            var node = Run(state, TargetUpgrade(1, 2, ItemId(state, Business), 3), registry);
            Assert.That(Flag(node.NormalizedResult, "changed"), Is.False); Assert.That(node.EnterpriseArrivalReceipts, Is.Empty);
            Assert.That(Text(node.NormalizedResult, "reasonCode"), Is.EqualTo(missing ? "enterprise_cooperation_marker_missing" : "limit_reached"));
            Assert.That(state.Players.All(p => p.Score == 0 && p.Resources.GoldVoucher == 0), Is.True); Restore(state, registry);
        }
        [Test]
        public void TargetPlayerSelectionUsesTargetsMarkersAndKeepsActorAsAnsweringPlayer()
        {
            var state = State(); var registry = Registry(); var spec = TargetUpgrade(1, 2, ItemId(state, Business));
            spec.NormalizedArguments.Properties.RemoveAll(p => p.Name == "enterpriseItemId");
            spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "enterpriseItemIds", Value = Strings(new[] { ItemId(state, Business), ItemId(state, Research) }) });
            spec.NormalizedArguments.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
            var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(spec); engine.RunUntilQuiescent();
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open"); Assert.That(request.AnsweringPlayerId, Is.EqualTo(1));
            state = Restore(state, registry); engine = new EffectTreeExecutor(state, registry);
            request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString(ItemId(state, Business)), out var reason), Is.True, reason);
            engine.RunUntilQuiescent(); Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Level(state, Business, 2), Is.EqualTo(1)); Assert.That(Level(state, Business, 1), Is.Zero); Restore(state, registry);
        }
        [TestCase("argument")][TestCase("receipt")][TestCase("event")][TestCase("result")][TestCase("definition")]
        public void TargetPlayerRecoveryRejectsChangedTargetFactsWithoutRewriting(string fault)
        {
            var state = State(); var registry = Registry(); var node = Run(state, TargetUpgrade(1, 2, ItemId(state, Business)), registry);
            var archive = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            var runtime = (JObject)archive["Snapshot"]["State"]["EffectRuntime"]; var raw = ((JArray)runtime["EffectNodes"]).Single(n => (string)n["EffectId"] == node.EffectId);
            if (fault == "definition") raw["DefinitionVersion"] = "v2.marker_state";
            if (fault == "argument") ((JArray)raw["NormalizedArguments"]["Properties"]).Single(p => (string)p["Name"] == "targetPlayer")["Value"]["StringValue"] = "1";
            if (fault == "receipt") raw["EnterpriseArrivalReceipts"][0]["PlayerId"] = 1;
            if (fault == "event") ((JArray)runtime["RuleEvents"]).Single(e => (string)e["EventId"] == node.EnterpriseArrivalReceipts[0].EventId)["PlayerId"] = 1;
            if (fault == "result") ((JArray)raw["NormalizedResult"]["Properties"]).Single(p => (string)p["Name"] == "targetPlayerId")["Value"]["IntegerValue"] = 1;
            string bad = archive.ToString(); Assert.That(HostRecoveryService.RecoverJson(bad, registry).Succeeded, Is.False); Assert.That(archive.ToString(), Is.EqualTo(bad));
        }
        [TestCase("0")][TestCase("p2")][TestCase("3")]
        public void TargetPlayerInvalidOrNonexistentPlayerCannotUpgrade(string target)
        {
            var state = State(); var registry = Registry(); var spec = TargetUpgrade(1, 2, ItemId(state, Business)); Value(spec.NormalizedArguments, "targetPlayer").StringValue = target;
            var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(spec); engine.RunUntilQuiescent();
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(Level(state), Is.Zero); Assert.That(Level(state, Business, 2), Is.Zero);
        }
        [Test]
        public void TargetPlayerRealLuaConstructorAndCompilerKeepActorAndTargetDistinct()
        {
            var state = State(); string item = ItemId(state, Business);
            string source = "return function(ctx) return {Effect.UpgradeEnterprise({apiVersion=2,player=ctx.playerId,targetPlayer='p2',enterpriseItemId='" + item + "',levels=1})} end";
            var script = new LuaScriptDefinition("target.test", "target.test", "target.test", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(script, new LuaInvocationContext("target.test", "test", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            var node = Run(state, specs.Single(), Registry()); Assert.That(node.PlayerId, Is.EqualTo(1)); Assert.That(Level(state, Business, 2), Is.EqualTo(1));
        }
        [Test]
        public void TargetPlayerUpgradeWaitsForTargetsRealRequiredAnswerBeforeInitiatorsSibling()
        {
            var state = State(); var registry = Registry();
            registry.Register("test.target.wait", c => c.Node.FlowStage.Length == 0
                ? EffectStepResult.Continue("answered").AddInteraction(new EffectInteractionSpec { InteractionTypeId = "test.target.wait", AnsweringPlayerId = c.Node.PlayerId,
                    Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "confirm" } })
                : EffectStepResult.Completed());
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.target.wait.event", EnterpriseEffectExecutor.LevelChangedEvent, "", "test.target.wait.event",
                c => new[] { EffectSpec.Create("test.target.wait", playerId: c.Event.PlayerId) }, EffectHandlerRole.Observer));
            var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(EffectSpec.Condition(new[] { TargetUpgrade(1, 2, ItemId(state, Business)), ResourceEffectSpecFactory.GainScore(1, 9) }, Array.Empty<EffectSpec>()));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False); Assert.That(state.FindPlayer(1).Score, Is.Zero);
            state = Restore(state, registry); engine = new EffectTreeExecutor(state, registry);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open"); Assert.That(request.AnsweringPlayerId, Is.EqualTo(2));
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString("confirm"), out _), Is.False);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 2, request.StateRevision, NormalizedValue.CreateString("confirm"), out var reason), Is.True, reason);
            engine.RunUntilQuiescent(); Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(9));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(9));
        }
        [Test]
        public void TargetPlayerEmptyScopePreservesActorsAndDoesNotOfferInitiatorsMarkers()
        {
            var state = State(); var registry = Registry(); RemoveCooperationMarkerForIsolation(state, Business, 2); RemoveCooperationMarkerForIsolation(state, Research, 2);
            var spec = TargetUpgrade(1, 2, ItemId(state, Business)); spec.NormalizedArguments.Properties.RemoveAll(p => p.Name == "enterpriseItemId");
            spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "enterpriseItemIds", Value = Strings(new[] { ItemId(state, Business), ItemId(state, Research) }) });
            spec.NormalizedArguments.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
            var node = Run(state, spec, registry); Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Flag(node.NormalizedResult, "changed"), Is.False);
            Assert.That(Text(node.NormalizedResult, "reasonCode"), Is.EqualTo("no_candidates")); Assert.That(Number(node.NormalizedResult, "targetPlayerId"), Is.EqualTo(2));
            Assert.That(Number(node.NormalizedResult, "initiatingPlayerId"), Is.EqualTo(1)); Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty); Restore(state, registry);
        }
    }
}
