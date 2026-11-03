using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class RouteResourceSelectionTests
    {
        private string StartFormalExploreBody(GameState state, EffectRegistry registry, string path)
        {
            // 真实正式脚本正文的隔离消费者；不冒充能力授权、正常入场或正常页面验收。
            var pack = ExternalContentPack.Current; string source = pack.GetScript(path);
            var definition = new LuaScriptDefinition(pack.PackId, "api15.formal", "api15.formal", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(definition, new LuaInvocationContext("api15.formal", "isolated", "p1", 0, "1.0.0", 2, 0, 4));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler(presentationPack: pack).TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1, SourceId = path }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            registry.Register(new EffectRegistration("api15.formal.body", c => {
                if (c.Node.FlowStage == "") return EffectStepResult.Continue("waiting").AddChildren(specs);
                if (c.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed || n.Status == EffectNodeStatus.Faulted)) return EffectStepResult.Failed("api15.formal.child_failed");
                if (c.ChildNodes.Any(n => n.Status != EffectNodeStatus.Completed)) return EffectStepResult.NoProgress();
                return EffectStepResult.Completed();
            }, EffectExecutorKind.IntrinsicFlow));
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(EffectSpec.Create("api15.formal.body", playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            exploreArtifacts.WriteLabeledText("正式探索正文身份", path + "\n" + definition.ContentHash); return id;
        }
        [TestCase(false, false)][TestCase(false, true)][TestCase(true, false)][TestCase(true, true)]
        public void ExploreV3FormalVehicleAndMaylanderKeepBranchCostAndFollowingRemoval(bool maylander, bool cancel)
        {
            Production(out var state, out var registry, out _); int budget = state.FindPlayer(1).RemainingMainActionsThisTurn;
            string path = maylander ? "lua/enterprises/infected.enterprise.maylander_foundation.level_2.special.lua" : "lua/facilities/remove_then_dispatch_or_explore.lua";
            StartFormalExploreBody(state, registry, path);
            if (!maylander)
            {
                var choice = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
                Submit(state, registry, choice, new[] { "vehicle.explore" });
            }
            var root = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ExplorationEffectTypeIds.Explore && ExplorationContract.IsV3(n.NormalizedArguments));
            Assert.That(Flag(root.NormalizedArguments, "consumeMainAction"), Is.False);
            string removalId = "";
            if (maylander)
            {
                var removal = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence);
                removalId = removal.EffectId;
                Assert.That(removal.Status, Is.EqualTo(EffectNodeStatus.Created));
                Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.OwnerEffectId == removal.EffectId), Is.False);
            }
            state = ExploreRecover(state, registry, "正式正文探索选择待答"); var q = Selection(state);
            Submit(state, registry, q, cancel ? new[] { q.CandidateIds.Single(id => id.EndsWith(".cancel", StringComparison.Ordinal)) } : SelectionAnswer(q, "A-01", "A1", "A2"));
            if (!cancel)
            {
                Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.EqualTo(4));
                Assert.That(ExploreRoot(state, root.EffectId).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
                if (maylander) Assert.That(ExploreRoot(state, removalId).Status, Is.EqualTo(EffectNodeStatus.Created));
                FinishEvents(state, registry);
            }
            Assert.That(Text(ExploreRoot(state, root.EffectId).NormalizedResult, "outcome"), Is.EqualTo(cancel ? "cancelled" : "completed"));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(budget));
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ContentPrimitiveEffectExecutor.RetiredExplorationSelectionTypeId), Is.False);
            if (maylander) Assert.That(ExploreRoot(state, removalId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            else Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence), Is.False);
            ExploreRecover(state, registry, "正式探索及原后续结束");
        }
    }
}
