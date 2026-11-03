using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        private static EffectSpec CompileBranchRoutingProbe(string interaction)
        {
            string kind = interaction == "omitted" ? "" : ",interactionType='" + interaction + "'";
            string source = "return function(ctx)return {Effect.Choice({player=ctx.playerId" + kind +
                ",branches={{id='gold',effects={Effect.GainResource({player=ctx.playerId,resourceType='gold_voucher',amount=3})}}," +
                "{id='score',effects={Effect.GainScore({player=ctx.playerId,amount=2})}}}})} end";
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript(source),
                new LuaInvocationContext("branch.routing", "isolated", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault),
                Is.True, fault?.Diagnostic);
            return specs.Single();
        }

        [TestCase("omitted")]
        [TestCase("lua.choice")]
        public void ExecutableLuaBranchesOwnLogicalRequestAndRunOnlySelectedEffectAfterRestore(string kind)
        {
            var state = State(); var registry = Registry(); GenericLuaEffectExecutor.Register(registry);
            var spec = CompileBranchRoutingProbe(kind);
            Assert.That(Text(spec.NormalizedArguments, "interactionType"), Is.EqualTo("effect.choice.execute"));
            int gold = state.FindPlayer(1).Resources.GoldVoucher, score = state.FindPlayer(1).Score;
            var choice = Run(state, spec, registry); var request = Open(state);
            Assert.That(request.OwnerEffectId, Is.EqualTo(choice.EffectId));
            Assert.That(request.InteractionTypeId, Is.EqualTo("effect.choice.execute"));
            Assert.That(Text(request.PromptParameters, "choiceIntent"), Is.EqualTo("choice_execute"));
            Assert.That(choice.ChildEffectIds, Is.Empty); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            state = Restore(state, registry); AnswerRequest(state, registry, "gold");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 3));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score), "未选分支不能执行。");
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == choice.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 3));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HostExecutableBranchesUseSameLogicalContractWithoutLuaNormalizer(bool legacyKind)
        {
            var state = State(); var registry = Registry(); GenericLuaEffectExecutor.Register(registry);
            var args = Object("player", NormalizedValue.CreateString("1"), "options", Strings(new[] { "gain" }));
            if (legacyKind) args.Properties.Add(new NormalizedValueEntry { Name = "interactionType", Value = NormalizedValue.CreateString("lua.choice") });
            args.Properties.Sort((a, b) => System.StringComparer.Ordinal.Compare(a.Name, b.Name));
            var spec = EffectSpec.Create(LuaDomainEffectTypeIds.Choice, args, 1);
            spec.NestedEffectGroups.Add(new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(1, 2) });
            var choice = Run(state, spec, registry);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("effect.choice.execute"));
            Assert.That(choice.ChildEffectIds, Is.Empty); int score = state.FindPlayer(1).Score;
            AnswerRequest(state, registry, "gain"); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 2));
        }

        [Test]
        public void ExplicitCardBranchRetainsFormalCardSelectionRoute()
        {
            var spec = CompileBranchRoutingProbe("facility.supply.select");
            Assert.That(Text(spec.NormalizedArguments, "interactionType"), Is.EqualTo("facility.supply.select"));
            // 真实设施候选／实际移动和缺图模型资格由既有IndependentCardChoice用例核对；此处只核显式类型不被逻辑默认覆盖。
        }

        [Test]
        public void ValueAndQuantityChoicesRetainTheirIndependentContracts()
        {
            var state = State(); var registry = Registry(); GenericLuaEffectExecutor.Register(registry);
            var plain = Run(state, EffectSpec.Create(LuaDomainEffectTypeIds.Choice,
                Object("player", NormalizedValue.CreateString("1"), "options", Strings(new[] { "value" })), 1), registry);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("lua.choice"));
            AnswerRequest(state, registry, "value"); Assert.That(plain.NormalizedResult.StringValue, Is.EqualTo("value"));
            Run(state, EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString("1"),
                "amountRange", Object("minAmount", NormalizedValue.CreateInteger(0), "maxAmount", NormalizedValue.CreateInteger(3))), 1), registry);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(ContentPrimitiveEffectExecutor.QuantityInteractionTypeId));
            Assert.That(Open(state).AnswerSchema, Is.EqualTo(ContentPrimitiveEffectExecutor.QuantityAnswerSchema));
        }

        [Test]
        public void RealBranchCompositePaymentUsesClosedCostBudgetWithoutChangingItsAtomicCost()
        {
            string source = "return function(ctx)return {Effect.Choice({player=ctx.playerId,branches={{id='pay',effects={" +
                "Effect.Condition({leftEffects={Effect.PayResource({player=ctx.playerId,cost={Originium=2,Iron=1}})}," +
                "rightEffects={Effect.GainScore({player=ctx.playerId,amount=1})}})}}}})} end";
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript(source),
                new LuaInvocationContext("branch.cost", "isolated", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            var pay = specs.Single().NestedEffectGroups.Single().Single().NestedEffectGroups.First().Single();
            Assert.That(pay.EffectTypeId, Is.EqualTo(ResourceEffectTypeIds.Pay));
            Assert.That(Number(Value(pay.NormalizedArguments, "cost"), "Originium"), Is.EqualTo(2));
            Assert.That(Number(Value(pay.NormalizedArguments, "cost"), "Iron"), Is.EqualTo(1));
        }

        [Test]
        public void PlainObjectsNamedCostRetainOriginalDepthLimit()
        {
            string source = "return function(ctx)local deep={};local cursor=deep;for i=1,9 do cursor.child={};cursor=cursor.child end;return {{cost=deep}} end";
            var result = new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript(source),
                new LuaInvocationContext("branch.cost.invalid", "isolated", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(result.IsSuccess, Is.False); Assert.That(result.Diagnostic, Does.Contain("嵌套深度超过预算"));
        }

        [Test]
        public void CompositeCostCannotHideNestedDataInsideTheClosedCostPayload()
        {
            var result = new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript("return function(ctx)return {Effect.PayResource({player=ctx.playerId,cost={Originium={forged=2}}})} end"),
                new LuaInvocationContext("branch.cost.invalid", "isolated", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(result.IsSuccess, Is.False); Assert.That(result.Diagnostic, Does.Contain("composite_payment_cost_invalid"));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void PreviousBranchRouteDefinitionIsRejectedInNodesAndPendingSpecs(bool journal, bool pending)
        {
            var state = State(); var registry = Registry(); GenericLuaEffectExecutor.Register(registry);
            var archive = HostRecoveryService.CreateArchive(state); var choice = CompileBranchRoutingProbe("omitted");
            Run(state, EffectSpec.Condition(new[] { choice }, new[] { ResourceEffectSpecFactory.GainScore(1, 1) }), registry);
            if (journal) HostRecoveryService.AppendJournal(archive, state, state.ContentPackHash); else archive = HostRecoveryService.CreateArchive(state);
            var raw = JObject.Parse(HostRecoveryService.SerializeArchive(archive));
            var checkpoint = journal ? raw["Journal"]["Entries"].Last(e => e["StateAfter"] is JObject)["StateAfter"] : raw["Snapshot"]["State"];
            var saved = checkpoint["EffectRuntime"]["EffectNodes"];
            var target = pending ? saved.SelectMany(n => n["NestedEffects"]).First(n => (string)n["EffectTypeId"] == LuaDomainEffectTypeIds.Choice)
                : saved.First(n => (string)n["EffectTypeId"] == LuaDomainEffectTypeIds.Choice);
            target["DefinitionVersion"] = "2.0.0"; string original = raw.ToString();
            var recovered = HostRecoveryService.RecoverJson(original, registry);
            Assert.That(recovered.Succeeded, Is.False); Assert.That(recovered.Diagnostic, Is.EqualTo("choice_effect_outcome_version_unsupported"));
            Assert.That(raw.ToString(), Is.EqualTo(original));
        }
    }
}
