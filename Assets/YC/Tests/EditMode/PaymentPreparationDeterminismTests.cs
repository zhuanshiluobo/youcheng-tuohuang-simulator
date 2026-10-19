using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    // 保留正式档及原命令的隔离重放，不计作正常菜单或可见程序验收。
    public sealed class PaymentPreparationDeterminismTests
    {
        private static readonly string OriginalRoot = Path.Combine(Directory.GetCurrentDirectory(),
            "Logs/NetworkUiFixes/20261019/完整联机覆盖-040231");

        [Test]
        public void Official415CheckpointAndOriginal420PaymentExposeActualOutcome()
            => ReplayOriginalPayment(false);

        [Test]
        public void Original420MustNotBecomeRuleFailureFromWallClockDeadline()
            => ReplayOriginalPayment(true);

        [TestCase("return function(ctx) while true do end end", LuaFailureCode.InstructionBudgetExceeded)]
        [TestCase("return function(ctx) error('payment-protection-sentinel') end", LuaFailureCode.ScriptError)]
        public void AuthoritativeLuaStillRejectsRunawayAndRealScriptFailure(string source, LuaFailureCode expected)
        {
            var basis = LuaExecutionBudget.Default;
            var host = new MoonSharpLuaRuntimeHost(new LuaExecutionBudget(32, basis.MaxMilliseconds,
                basis.MaxTableDepth, basis.MaxTableEntries, basis.MaxEffectSpecs, basis.MaxScriptLength));
            var definition = new LuaScriptDefinition("payment.test", "payment.test", "payment.test", "1", source, LuaContentHasher.ComputeSha256(source));
            var result = host.Invoke(definition, new LuaInvocationContext("event.test", "test", "p1", 0, "1", 1, 0, 0));
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(expected));
            Assert.That(result.Effects, Is.Empty);
        }

        [Test]
        public void PresentationGuardKeepsItsWallClockDeadline()
        {
            var timer = Stopwatch.StartNew();
            var type = typeof(MoonSharpLuaRuntimeHost).GetNestedType("ExecutionGuard", BindingFlags.NonPublic);
            var guard = Activator.CreateInstance(type, new object[] { 10000L, 1, timer, true });
            Thread.Sleep(5);
            var error = Assert.Throws<TargetInvocationException>(() => type.GetMethod("ThrowIfTimeExceeded").Invoke(guard, null));
            Assert.That(error.InnerException.Message, Does.Contain("nmc_time_budget_exceeded"));
        }

        [Test]
        public void AuthoritativeLuaKeepsReturnedEffectCountLimit()
        {
            var basis = LuaExecutionBudget.Default;
            var host = new MoonSharpLuaRuntimeHost(new LuaExecutionBudget(basis.MaxInstructions, basis.MaxMilliseconds,
                basis.MaxTableDepth, basis.MaxTableEntries, 1, basis.MaxScriptLength));
            const string source = "return function(ctx) return {Effect.GainScore({recipient=ctx.playerId,amount=1}),Effect.GainScore({recipient=ctx.playerId,amount=1})} end";
            var definition = new LuaScriptDefinition("payment.limit", "payment.limit", "payment.limit", "1", source, LuaContentHasher.ComputeSha256(source));
            var result = host.Invoke(definition, new LuaInvocationContext("event.limit", "test", "p1", 0, "1", 1, 0, 0));
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(LuaFailureCode.ReturnLimitExceeded));
            Assert.That(result.Effects, Is.Empty);
        }

        private static void ReplayOriginalPayment(bool pauseExecution)
        {
            var envelope = JObject.Parse(File.ReadAllText(Path.Combine(OriginalRoot, "结束时正式存档只读备份/auto-2.json")));
            var save = JsonConvert.DeserializeObject<MatchSaveData>((string)envelope["Payload"]);
            var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var restored = bootstrapper.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { null, save });
            var session = (GameSession)restored.GetType().GetField("Session").GetValue(restored);
            var registry = (EffectRegistry)restored.GetType().GetField("EffectRegistry").GetValue(restored);
            Assert.That(session.State.GameId, Is.EqualTo("8c73cb4f2a1842749271a897e097df8e"));
            Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo(4177));
            var receipts = File.ReadLines(Path.Combine(OriginalRoot, "四端正常联机/host/四端全局应用回执.jsonl"))
                .Select(JObject.Parse).Select(row => row["Applied"])
                .Where(row => (long)row["GlobalCommandSequence"] > 415).OrderBy(row => (long)row["GlobalCommandSequence"]).ToArray();
            Assert.That(receipts.Select(row => (long)row["GlobalCommandSequence"]), Is.EqualTo(new long[] { 416, 417, 418, 419, 420 }));
            foreach (var receipt in receipts.Take(4))
            {
                var command = receipt["Command"].ToObject<GameCommandDto>().ToCommand();
                var result = session.Submit(command);
                TestContext.WriteLine("PAYMENT_PREFIX " + JsonConvert.SerializeObject(new { G = (long)receipt["GlobalCommandSequence"], result.Succeeded, result.Validation, Revision = session.State.EffectRuntime.StateRevision }));
                Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            }
            Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo(4216));
            string inputDigest = RuleStateDigestService.Compute(session.State);
            PaymentObservation control = null;
            var original = receipts.Last()["Command"].ToObject<GameCommandDto>().ToCommand();
            if (pauseExecution)
            {
                control = PrepareOriginal(session.State, registry, original, false);
                Assert.That(control.Result.Succeeded, Is.True, control.State.EffectRuntime.LastFaultMessage);
            }
            if (pauseExecution) PauseRealSettlementHostClocks(registry);
            var observed = PrepareOriginal(session.State, registry, original, pauseExecution);
            var work = observed.State; var actual = observed.Result;
            Assert.That(RuleStateDigestService.Compute(session.State), Is.EqualTo(inputDigest), "隔离工作副本不得回写原4216输入");
            Assert.That(actual.Succeeded, Is.True, work.EffectRuntime.LastFaultCode + ":" + work.EffectRuntime.LastFaultMessage);
            Assert.That(work.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(17), "准备阶段不提前扣款");
            var prepared = work.EffectRuntime.EffectNodes.Single(n => n.Payment?.Preparation?.TargetId == original.TargetId &&
                n.Payment.Preparation.ActionArguments.Properties.Any(p => p.Name == "paymentMode" && p.Value.StringValue == "gold") &&
                !n.Payment.Preparation.IsCancelled);
            Assert.That(prepared.Payment.PaymentCost.GoldVoucher, Is.EqualTo(10));
            Assert.That(prepared.Payment.BeforePaymentReady, Is.True);
            Assert.That(prepared.Payment.IsCommitted, Is.False);
            if (control != null)
            {
                Assert.That(JsonConvert.SerializeObject(actual), Is.EqualTo(JsonConvert.SerializeObject(control.Result)));
                Assert.That(RuleStateDigestService.Compute(work), Is.EqualTo(RuleStateDigestService.Compute(control.State)), "同输入、同预算、不同墙钟停顿必须得到同一完整规则状态");
            }
        }

        private sealed class PaymentObservation { internal CommandResult Result; internal GameState State; }

        private static PaymentObservation PrepareOriginal(GameState input, EffectRegistry registry, GameCommand original, bool pausedExecution)
        {
            var work = GameStateCloneService.DeepClone(input);
            OperationWindowService.Synchronize(work);
            Assert.That(OperationWindowService.TryResolveCommandContext(work, original, out var context, out var validation), Is.True, validation.Reason);
            work.EffectRuntime.CurrentOperationContext = context;
            var source = work.EffectRuntime.EffectNodes.Find(n => n.EffectId == context.SourceEffectId);
            if (source != null) source.ContinuationOperationContext = context.Clone();
            var timer = Stopwatch.StartNew();
            var actual = new PreparePaymentDraftCommandHandler(registry, new RoundExecutionService(registry)).Handle(work, original);
            TestContext.WriteLine("PAYMENT_420 " + JsonConvert.SerializeObject(new {
                PausedExecution = pausedExecution, DefaultWallBudgetMilliseconds = LuaExecutionBudget.Default.MaxMilliseconds,
                InputRuleDigest = RuleStateDigestService.Compute(input), OutputRuleDigest = RuleStateDigestService.Compute(work),
                OriginalCommand = GameCommandDto.FromCommand(original), actual.Succeeded, actual.Validation,
                work.EffectRuntime.LastFaultCode, work.EffectRuntime.LastFaultMessage,
                LastNodes = work.EffectRuntime.EffectNodes.TakeLast(5).Select(n => new { n.EffectId, n.EffectTypeId, n.FlowStage, n.Status, n.FailureReason }),
                GoldBefore = input.FindPlayer(3).Resources.GoldVoucher, GoldAfter = work.FindPlayer(3).Resources.GoldVoucher,
                work.EffectRuntime.StateRevision, ElapsedMilliseconds = timer.ElapsedMilliseconds }));
            return new PaymentObservation { Result = actual, State = work };
        }

        // 同一默认100ms预算、同一原命令及真实catalog，只在计时已开始后停顿125ms。
        // 不替换Lua正文、handler、offers、费用或随机，也不用于正常UI验收。
        private static void PauseRealSettlementHostClocks(EffectRegistry registry)
        {
            var catalogs = new HashSet<SettlementLuaCatalog>();
            foreach (string name in new[] { "settlementOfferProviders", "settlementAvailabilityHandlers" })
            {
                var providers = (IDictionary)typeof(EffectRegistry).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(registry);
                foreach (Delegate provider in providers.Values)
                    foreach (var field in provider.Target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        if (field.GetValue(provider.Target) is SettlementLuaCatalog catalog) catalogs.Add(catalog);
            }
            Assert.That(catalogs.Count, Is.GreaterThan(0));
            var constructor = typeof(MoonSharpLuaRuntimeHost).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(LuaExecutionBudget), typeof(Func<Stopwatch>) }, null);
            Assert.That(constructor, Is.Not.Null);
            foreach (var catalog in catalogs)
            {
                Func<Stopwatch> pausedClock = () => {
                    var clock = Stopwatch.StartNew();
                    Thread.Sleep(LuaExecutionBudget.Default.MaxMilliseconds + 25);
                    return clock;
                };
                var host = (MoonSharpLuaRuntimeHost)constructor.Invoke(new object[] { LuaExecutionBudget.Default, pausedClock });
                typeof(SettlementLuaCatalog).GetField("host", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(catalog, host);
                catalog.ClearQueryCache();
            }
        }
    }
}
