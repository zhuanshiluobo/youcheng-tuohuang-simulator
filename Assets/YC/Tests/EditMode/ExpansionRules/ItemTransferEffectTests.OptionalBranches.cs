using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        [TestCase(false)] [TestCase(true)]
        public void HostOptionalBranchContractRejectsMultiselectAndEmptyEffectsBeforePublishing(bool empty)
        {
            var state = State(); var registry = Registry(); GenericLuaEffectExecutor.Register(registry); int score = state.FindPlayer(1).Score;
            var spec = EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString("1"),
                "options", Strings(empty ? new[] { "gain" } : new[] { "gain", "other" }),
                "minSelections", NormalizedValue.CreateInteger(0), "maxSelections", NormalizedValue.CreateInteger(empty ? 1 : 2)), 1);
            spec.NestedEffectGroups.Add(empty ? new List<EffectSpec>() : new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(1, 1) });
            if (!empty) spec.NestedEffectGroups.Add(new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(1, 2) });
            var node = Run(state, spec, registry);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(node.FailureReason, Is.EqualTo("choice_branch_selection_contract_invalid"));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(node.ChildEffectIds, Is.Empty); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score));
        }

        [TestCase("minSelections=0,maxSelections=1", true)]
        [TestCase("minSelections=0", true)]
        [TestCase("minSelections=1,maxSelections=1", true)]
        [TestCase("minSelections=2", false)]
        [TestCase("minSelections=0.5", false)]
        [TestCase("minSelections=false", false)]
        [TestCase("maxSelections=2", false)]
        [TestCase("maxSelections=0", false)]
        public void BranchCardinalityIsOptionalSingleExecutionOnly(string range, bool valid)
        {
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript("return function(ctx)return {Effect.Choice({player=ctx.playerId," + range +
                ",branches={{id='gain',effects={Effect.GainScore({player=ctx.playerId,amount=1})}}}})} end"),
                new LuaInvocationContext("branch.optional", "isolated", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.EqualTo(valid), invocation.Diagnostic);
            if (valid) Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out _, out var fault), Is.True, fault?.Diagnostic);
        }

        [Test]
        public void OptionalBranchCannotRepresentDeclineAsAnEmptyEffect()
        {
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript("return function(ctx)return {Effect.Choice({player=ctx.playerId,minSelections=0,branches={{id='empty',effects={}}}})} end"),
                new LuaInvocationContext("branch.optional", "isolated", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.False); Assert.That(invocation.Diagnostic, Does.Contain("放弃不能写成空分支"));
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void PreviousMandatoryBranchDefinitionCannotRestoreAsOptionalContract(bool journal, bool pending)
        {
            var state = State(); var registry = Registry(); GenericLuaEffectExecutor.Register(registry);
            var archive = HostRecoveryService.CreateArchive(state); var choice = CompileBranchRoutingProbe("omitted");
            Run(state, EffectSpec.Condition(new[] { choice }, new[] { ResourceEffectSpecFactory.GainScore(1, 1) }), registry);
            if (journal) HostRecoveryService.AppendJournal(archive, state, state.ContentPackHash); else archive = HostRecoveryService.CreateArchive(state);
            var raw = JObject.Parse(HostRecoveryService.SerializeArchive(archive));
            var checkpoint = journal ? raw["Journal"]["Entries"].Last(e => e["StateAfter"] is JObject)["StateAfter"] : raw["Snapshot"]["State"];
            var nodes = checkpoint["EffectRuntime"]["EffectNodes"];
            var target = pending ? nodes.SelectMany(n => n["NestedEffects"]).First(n => (string)n["EffectTypeId"] == LuaDomainEffectTypeIds.Choice)
                : nodes.First(n => (string)n["EffectTypeId"] == LuaDomainEffectTypeIds.Choice);
            target["DefinitionVersion"] = "3.0.0";
            var recovered = HostRecoveryService.RecoverJson(raw.ToString(), registry);
            Assert.That(recovered.Succeeded, Is.False); Assert.That(recovered.Diagnostic, Is.EqualTo("choice_effect_outcome_version_unsupported"));
        }
    }
}
