using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离库存变化／付款前响应／真实Host恢复，不计正常页面或完整对局。
    public sealed class ResourceExchangeFlowTests
    {
        private PaymentTestFixture f;
        [SetUp] public void Begin() => f = new PaymentTestFixture();
        [TearDown] public void End() => f.Dispose();
        private static NormalizedValue Args(int target = 1) => Object("apiVersion", NormalizedValue.CreateInteger(1),
            "player", NormalizedValue.CreateString("1"), "targetPlayer", NormalizedValue.CreateString(target.ToString()),
            "resourceType", NormalizedValue.CreateString("iron"), "receiveResourceType", NormalizedValue.CreateString("gold_voucher"),
            "amount", NormalizedValue.CreateString("all"), "rate", NormalizedValue.CreateInteger(2));
        [TestCase(0)][TestCase(1)][TestCase(50)][TestCase(51)][TestCase(110)]
        public void AllCurrentStockUsesRealOrderedBatchesAndRestoresWithoutRepeating(int stock)
        {
            f.State.FindPlayer(1).Resources.Iron = stock;
            var flow = f.Run(EffectSpec.Create(ResourceExchangeFlow.TypeId, Args(), 1));
            Assert.That(flow.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.Zero);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100 + stock * 2));
            Assert.That(Number(flow.NormalizedResult, "paidAmount"), Is.EqualTo(stock));
            var payments = f.State.EffectRuntime.EffectNodes.Where(n => n.ParentEffectId == flow.EffectId && n.EffectTypeId == ResourceEffectTypeIds.Pay).ToArray();
            Assert.That(payments.Length, Is.EqualTo((stock + 49) / 50));
            Assert.That(payments.All(p => p.Payment.IsCommitted && !p.Payment.AllowsSubstitution), Is.True);
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == ResourcePaymentPreparationService.BeforeResourcePayment), Is.EqualTo(payments.Length));
            f.Restore("实际兑换收益后恢复");
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100 + stock * 2));
        }
        private void WaitBeforeEachPayment()
        {
            f.Registry.Register("fixture.exchange.wait", context => context.Node.FlowStage == "" ?
                EffectStepResult.Continue("answered").AddInteraction(new EffectInteractionSpec { InteractionTypeId = "fixture.exchange.wait", AnsweringPlayerId = context.Node.PlayerId,
                    AnswerSchema = "candidate_id", CandidateIds = { "ack" }, MinSelections = 1, MaxSelections = 1 }) : EffectStepResult.Completed());
            f.Registry.RegisterEventHandler(new EffectEventHandlerRegistration("fixture.exchange.before", ResourcePaymentPreparationService.BeforeResourcePayment,
                "*", "fixture.exchange.before", context => new[] { EffectSpec.Create("fixture.exchange.wait", playerId: 1) }, EffectHandlerRole.Observer));
        }
        [Test]
        public void EveryPendingBatchAndPriorGainResumeWithoutDuplicatePaymentOrGain()
        {
            WaitBeforeEachPayment(); f.State.FindPlayer(1).Resources.Iron = 110;
            var root = f.Run(EffectSpec.Create(ResourceExchangeFlow.TypeId, Args(), 1));
            int[] remaining = { 110, 60, 10 }; int[] gold = { 100, 200, 300 };
            for (int i = 0; i < 3; i++)
            {
                Assert.That(f.Open().InteractionTypeId, Is.EqualTo("fixture.exchange.wait"));
                Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(remaining[i]));
                Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold[i]));
                var request = f.Open(); string id = request.GetStableInteractionId();
                f.Restore("实际兑换批次待付款" + i); f.Answer("ack");
                Assert.That(f.Engine.TrySubmitInteraction(id, 1, request.StateRevision, NormalizedValue.CreateString("ack"), out _), Is.False);
            }
            Assert.That(f.Engine.GetNode(root.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.Zero);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(320));
        }
        [Test]
        public void ChangedInventoryAtPaymentCannotGrantUnpaidRewardOrConditionScore()
        {
            WaitBeforeEachPayment(); f.State.FindPlayer(1).Resources.Iron = 3;
            f.Run(EffectSpec.Condition(new[] { EffectSpec.Create(ResourceExchangeFlow.TypeId, Args(), 1) },
                new[] { ResourceEffectSpecFactory.GainScore(1, 1) }));
            f.State.FindPlayer(1).Resources.Iron = 0; f.Answer("ack");
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100));
            Assert.That(f.State.FindPlayer(1).Score, Is.Zero);
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ResourceEffectTypeIds.Gain), Is.False);
        }
        [TestCase("apiVersion", "2")][TestCase("rate", "0")][TestCase("rate", "101")][TestCase("rate", "4294967297")]
        public void InvalidNumericContractIsRejected(string key, string value)
        {
            if (long.Parse(value) > int.MaxValue)
            { Assert.Throws<ArgumentOutOfRangeException>(() => NormalizedValue.CreateInteger(long.Parse(value))); return; }
            var args = Args(); args.Properties.Single(p => p.Name == key).Value = NormalizedValue.CreateInteger(long.Parse(value));
            Assert.That(ResourceExchangeFlow.ValidateArguments(args), Is.Not.Empty);
        }
        [TestCase("amount", "1")][TestCase("receiveResourceType", "iron")][TestCase("resourceType", "unknown")]
        public void InvalidResourceOrAmountContractIsRejected(string key, string value)
        {
            var args = Args(); args.Properties.Single(p => p.Name == key).Value = NormalizedValue.CreateString(value);
            Assert.That(ResourceExchangeFlow.ValidateArguments(args), Is.Not.Empty);
        }
        [Test]
        public void IncompatibleSavedIdentityOrProgressIsRejectedBeforePayment()
        {
            WaitBeforeEachPayment(); f.Run(EffectSpec.Create(ResourceExchangeFlow.TypeId, Args(), 1));
            foreach (bool identity in new[] { true, false })
            {
                var archive = HostRecoveryService.CreateArchive(f.State);
                var flow = archive.Snapshot.State.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ResourceExchangeFlow.TypeId);
                if (identity) flow.DefinitionVersion = "2.0.0";
                else flow.ContinuationData.Properties.Single(p => p.Name == "remaining").Value = NormalizedValue.CreateInteger(1);
                var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), f.Registry);
                Assert.That(restored.Succeeded, Is.False, restored.Diagnostic);
            }
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(100));
        }
        internal static LuaInvocationResult InvokeCannot(string content)
        {
            string source = File.ReadAllText(Path.Combine(content, "lua/characters/cannot.tactic.lua"));
            var def = new LuaScriptDefinition("content.characters.cannot.tactic", "character.cannot.tactic", "characters.cannot.tactic", "1.4.0", source, LuaContentHasher.ComputeSha256(source));
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 2, "1.4.0");
            snapshot.Players.Clear();
            snapshot.Players.Add(new LuaPlayerSnapshot("p1", 0, 0, "", 0, 0, 0, 0));
            snapshot.Players.Add(new LuaPlayerSnapshot("p2", 0, 0, "", 0, 0, 0, 0));
            return new MoonSharpLuaRuntimeHost().Invoke(def, new LuaInvocationContext("cannot.actual", "CharacterEffectActivated", "p1", 0, "1.4.0", 2, 0, 0, snapshot));
        }
        internal static void RegisterCannotCompletion(EffectRegistry registry, string content)
        {
            GenericLuaEffectExecutor.Register(registry);
            string source = File.ReadAllText(Path.Combine(content, "lua/characters/cannot.tactic.lua"));
            var script = new LuaScriptDefinition("content.characters.cannot.tactic", "character.cannot.tactic", "characters.cannot.tactic.completed", "1.4.0", source, LuaContentHasher.ComputeSha256(source));
            if (!registry.HasEventHandler(script.HandlerId))
                new LuaEffectEventDispatcher(registry).RegisterContinuation(new LuaHandlerDefinition(script.HandlerId, "EffectCompleted", "", script,
                    EffectHandlerRole.Continuation, 0, "characters.core", "character.cannot.tactic"));
        }
        [TestCase("originium", ResourceType.Originium)][TestCase("originium-shard", ResourceType.OriginiumShard)][TestCase("iron", ResourceType.Iron)]
        public void CannotBranchesUseStockAtExecutionAndScoreOnlyAfterEveryPlayer(string answer, ResourceType type)
        {
            var invocation = InvokeCannot(f.PrivateContent); Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(invocation.Effects.Single().NestedEffectGroups.All(g => g.Count == 2 && g.All(e => e.EffectTypeId == ResourceExchangeFlow.TypeId)), Is.True);
            RegisterCannotCompletion(f.Registry, f.PrivateContent);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1,
                ContentInstanceId = "characters.core", AbilityId = "character.cannot.tactic", CompletionHandlerId = "characters.cannot.tactic.completed" }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            f.Run(specs.Single()); f.State.FindPlayer(1).Resources.Set(type, 51); f.State.FindPlayer(2).Resources.Set(type, 3);
            f.Restore("三资源真实分支未选"); f.Answer(answer);
            Assert.That(f.State.FindPlayer(1).Resources.Get(type), Is.Zero); Assert.That(f.State.FindPlayer(2).Resources.Get(type), Is.Zero);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(202));
            Assert.That(f.State.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(6));
            Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(1));
            f.Restore("全玩家兑换及最后得分后恢复"); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(1));
        }
        private EffectSpec CannotChoice()
        {
            var invocation = InvokeCannot(f.PrivateContent); Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            RegisterCannotCompletion(f.Registry, f.PrivateContent);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1,
                ContentInstanceId = "characters.core", AbilityId = "character.cannot.tactic", CompletionHandlerId = "characters.cannot.tactic.completed" }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            return specs.Single();
        }
        [Test]
        public void CannotAllZeroStockCompletesRealBranchesAndStillScoresOnce()
        {
            f.State.FindPlayer(1).Resources.Iron = f.State.FindPlayer(2).Resources.Iron = 0;
            var root = f.Run(CannotChoice()); f.Answer("iron");
            Assert.That(f.Engine.GetNode(root.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == ResourceExchangeFlow.TypeId), Is.EqualTo(2));
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ResourceEffectTypeIds.Pay), Is.False);
        }
        [Test]
        public void CannotFailedActualPaymentCannotTriggerCompletionScore()
        {
            WaitBeforeEachPayment(); f.State.FindPlayer(1).Resources.Iron = 3; f.State.FindPlayer(2).Resources.Iron = 0;
            var root = f.Run(CannotChoice()); f.Answer("iron");
            Assert.That(f.Open().InteractionTypeId, Is.EqualTo("fixture.exchange.wait"));
            f.State.FindPlayer(1).Resources.Iron = 0; f.Answer("ack");
            Assert.That(f.Engine.GetNode(root.EffectId).Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(f.State.FindPlayer(1).Score, Is.Zero);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100));
        }
        [Test]
        public void FailureAfterCommittedPaymentReportsActualPaidAmountWithoutInventedGain()
        {
            f.Registry.Register("fixture.exchange.receive-full", context => {
                context.State.FindPlayer(1).Resources.GoldVoucher = int.MaxValue;
                return EffectStepResult.Completed();
            });
            f.Registry.RegisterEventHandler(new EffectEventHandlerRegistration("fixture.exchange.receive-full.before", ResourcePaymentPreparationService.BeforeResourcePayment,
                "*", "fixture.exchange.receive-full.before", _ => new[] { EffectSpec.Create("fixture.exchange.receive-full", playerId: 1) }, EffectHandlerRole.Observer));
            f.State.FindPlayer(1).Resources.Iron = 1;
            var flow = f.Run(EffectSpec.Create(ResourceExchangeFlow.TypeId, Args(), 1));
            Assert.That(flow.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.Zero);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(int.MaxValue));
            Assert.That(Number(flow.NormalizedResult, "paidAmount"), Is.EqualTo(1));
            Assert.That(Number(flow.NormalizedResult, "receivedAmount"), Is.Zero);
            Assert.That(Flag(flow.NormalizedResult, "changed"), Is.True);
        }
    }
}
