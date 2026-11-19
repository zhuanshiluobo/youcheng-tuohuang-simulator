using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // API-14：使用真实注册/地图/托管的隔离规则验证，不计正常游戏或UI视觉验收。
    public sealed partial class RouteResourceSelectionTests
    {
        private IsolatedTestArtifacts exploreArtifacts;
        [SetUp] public void BeginExploreEvidence() => exploreArtifacts = IsolatedTestArtifacts.BeginCase();
        [TearDown] public void FinishExploreEvidence() => exploreArtifacts?.Finish();
        private const string ExploreSelect = "apiVersion=3,targetMode='select',player=ctx.playerId,consumeMainAction=false,targetLocationId='A-01'";
        private const string ExploreExplicit = "apiVersion=3,targetMode='explicit',player=ctx.playerId,consumeMainAction=false,targetLocationId='A-01',routeIds={'A1','A2'},recipients={{routeId='A1',subject='bank'},{routeId='A2',subject='bank'}}";
        private static EffectNodeRuntimeState ExploreRoot(GameState state, string id) => state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
        private GameState ExploreRecover(GameState state, EffectRegistry registry, string label)
        {
            var result = exploreArtifacts.Recover(state, label, registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic); return result.State;
        }
        private static string StartExploreV3(GameState state, EffectRegistry registry, string fields)
        {
            var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(CompileLua("Effect.Explore({" + fields + "})"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); return root;
        }
        [Test]
        public void GrantedExploreAtFourPlayerRoundStartCanSelectOpenRedZoneAndComplete()
        {
            Production(out var state, out var registry, out var exploration, 4);
            state.Round = 3; state.Phase = GamePhase.RoundStart; state.CurrentPlayerId = 2;
            state.FindPlayer(1).Resources.GoldVoucher = 40;
            Assert.That(exploration.GetSelectableLocationIds(state, 1, true), Does.Not.Contain("E-03"),
                "效果授予探索也不能提前进入尚未开放的红区");
            state.Round = 4;
            Assert.That(exploration.GetSelectableLocationIds(state, 1), Is.Empty, "回合开始不能直接发起普通主要行动");
            Assert.That(exploration.GetSelectableLocationIds(state, 1, true), Does.Contain("E-03"),
                "四人局第4回合，效果授予玩家探索时红区已开放");
            var path = exploration.FindDefaultPath(state, 1, "E-03");
            Assert.That(path, Is.Not.Null);
            string root = StartExploreV3(state, registry, ExploreSelect.Replace("'A-01'", "'E-03'"));
            var request = Selection(state);
            Assert.That(request.AnsweringPlayerId, Is.EqualTo(1));
            Submit(state, registry, request, SelectionAnswer(request, "E-03", path.RouteIds.ToArray()));
            FinishEvents(state, registry);
            Assert.That(Text(ExploreRoot(state, root).NormalizedResult, "outcome"), Is.EqualTo("completed"));
            Assert.That(Text(ExploreRoot(state, root).NormalizedResult, "targetLocationId"), Is.EqualTo("E-03"));
            Assert.That(state.Map.ResourceTokens.Any(token => token.LocationId == "E-03"), Is.True);
            Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.Zero);
        }
        [TestCase(false, false)][TestCase(false, true)][TestCase(true, false)][TestCase(true, true)]
        public void ExploreV3BothModesReserveRespondSettleAndConsumeOnlyTheirDeclaredAction(bool explicitPlan, bool consumes)
        {
            Production(out var state, out var registry, out _);
            string root = StartExploreV3(state, registry, (explicitPlan ? ExploreExplicit : ExploreSelect).Replace("consumeMainAction=false", "consumeMainAction=" + (consumes ? "true" : "false")));
            if (!explicitPlan)
            {
                state = ExploreRecover(state, registry, "统一探索地图待答"); var request = Selection(state);
                Assert.That(ExploreRoot(state, request.OwnerEffectId).ParentEffectId, Is.EqualTo(root));
                Submit(state, registry, request, SelectionAnswer(request, "A-01", "A1", "A2"));
            }
            Assert.That(ExploreRoot(state, root).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ContentPrimitiveEffectExecutor.RetiredExplorationSelectionTypeId), Is.False);
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.EqualTo(4));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            state = ExploreRecover(state, registry, "统一探索路费已预留待事件响应"); FinishEvents(state, registry);
            var node = ExploreRoot(state, root); Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Text(node.NormalizedResult, "outcome"), Is.EqualTo("completed")); Assert.That(Text(node.NormalizedResult, "targetLocationId"), Is.EqualTo("A-01"));
            var actual = ExploreRoot(state, Text(node.NormalizedResult, "explorationEffectId"));
            Assert.That(actual.EffectId, Is.Not.EqualTo(root)); Assert.That(actual.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Text(node.NormalizedResult, "tollSettlementReceiptId"), Is.EqualTo(Text(actual.NormalizedResult, "tollSettlementReceiptId")));
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(consumes ? 0 : 1));
            Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(consumes ? 1 : 0));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.SourceEffectId == root && e.EventType == "EffectCompleted"), Is.EqualTo(1));
            var observer = new AbilitySourceDeclaration { CompletedEffectTypes = new List<string> { ExplorationEffectTypeIds.Explore } };
            Assert.That(state.EffectRuntime.RuleEvents.Where(e => e.EventType == "EffectCompleted" && AbilityEventLuaFacts.MatchesCompletion(state, e, observer))
                .Select(e => e.SourceEffectId), Is.EqualTo(new[] { actual.EffectId }), "实际探索完成观察只计算结算子节点，流程根的绑定续接仍交付一次");
            state = ExploreRecover(state, registry, "统一探索交付后完成"); string completed = JsonConvert.SerializeObject(state);
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(completed));
        }
        [Test] public void ExploreV3CancelAndNoTargetHaveDifferentResultsAndNeverCreateSettlement()
        {
            foreach (bool noTarget in new[] { false, true })
            {
                Production(out var state, out var registry, out var exploration);
                string fields = ExploreSelect;
                if (noTarget)
                {
                    var eligible = exploration.GetSelectableLocationIds(state, 1, true);
                    string unavailable = exploration.MapQuery.Map.Locations.First(l => !eligible.Contains(l.LocationId)).LocationId;
                    fields = fields.Replace("'A-01'", "'" + unavailable + "'");
                }
                string before = JsonConvert.SerializeObject(state.ItemZones); int deck = state.Decks.EventDeckGreen.Count;
                string root = StartExploreV3(state, registry, fields);
                if (!noTarget)
                {
                    state = ExploreRecover(state, registry, "探索确认前可取消"); var request = Selection(state);
                    Submit(state, registry, request, new[] { request.CandidateIds.Single(id => id.EndsWith(".cancel", StringComparison.Ordinal)) });
                }
                Assert.That(Text(ExploreRoot(state, root).NormalizedResult, "outcome"), Is.EqualTo(noTarget ? "no_legal_target" : "cancelled"));
                Assert.That(Text(ExploreRoot(state, root).NormalizedResult, "explorationEffectId"), Is.Empty);
                var observer = new AbilitySourceDeclaration { CompletedEffectTypes = new List<string> { ExplorationEffectTypeIds.Explore } };
                Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == "EffectCompleted" && AbilityEventLuaFacts.MatchesCompletion(state, e, observer)), Is.False);
                Assert.That(JsonConvert.SerializeObject(state.ItemZones), Is.EqualTo(before)); Assert.That(state.Decks.EventDeckGreen.Count, Is.EqualTo(deck));
                Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
                Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1)); ExploreRecover(state, registry, "探索取消或无目标终态");
            }
        }
        [TestCase("select", EffectNodeStatus.Created)][TestCase("select", EffectNodeStatus.Ready)][TestCase("select", EffectNodeStatus.Running)]
        [TestCase("explicit", EffectNodeStatus.Created)][TestCase("explicit", EffectNodeStatus.Ready)][TestCase("explicit", EffectNodeStatus.Running)]
        public void ExploreV3InitialRealSchedulerStatesRecoverWithoutInventingSelection(string mode, EffectNodeStatus status)
        {
            Production(out var state, out var registry, out _); var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(CompileLua("Effect.Explore({" + (mode == "select" ? ExploreSelect : ExploreExplicit) + "})"));
            for (int i = 0; i < 50 && engine.GetNode(root).Status != status; i++) Assert.That(engine.Advance(), Is.True);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(status)); Assert.That(engine.GetNode(root).FlowStage, Is.Empty);
            state = ExploreRecover(state, registry, "探索首次执行前_" + mode + "_" + status);
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            if (mode == "select") { var request = Selection(state); Submit(state, registry, request, SelectionAnswer(request, "A-01", "A1", "A2")); }
            FinishEvents(state, registry); Assert.That(Text(ExploreRoot(state, root).NormalizedResult, "outcome"), Is.EqualTo("completed"));
        }
        [TestCase(false)][TestCase(true)] public void ExploreV3SealedSelectionBeforeReservationRecoversToTheSameSingleConsumer(bool explicitPlan)
        {
            Production(out var state, out var registry, out _); var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(CompileLua("Effect.Explore({" + (explicitPlan ? ExploreExplicit : ExploreSelect) + "})"));
            if (!explicitPlan)
            {
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False); var request = Selection(state);
                Assert.That(engine.TrySubmitInteraction(request.InteractionId, 1, state.EffectRuntime.StateRevision,
                    NormalizedValue.CreateArray(SelectionAnswer(request, "A-01", "A1", "A2").Select(id => NormalizedValue.CreateStableReference("candidate", id)).ToList()), out string error), Is.True, error);
            }
            for (int i = 0; i < 200 && !state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ExplorationEffectTypeIds.Explore && Value(n.NormalizedArguments, "paymentProtocol") != null); i++)
                Assert.That(engine.Advance(), Is.True);
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero);
            state = ExploreRecover(state, registry, "探索选择封存但尚未预留路费");
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.EqualTo(4)); FinishEvents(state, registry);
            Assert.That(Text(ExploreRoot(state, root).NormalizedResult, "outcome"), Is.EqualTo("completed"));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => Value(n.ContinuationData, RouteTollEscrowService.ReceiptField) != null), Is.EqualTo(1));
        }
        [TestCase("apiVersion=3,player=ctx.playerId,consumeMainAction=false")]
        [TestCase("apiVersion=3,targetMode='explicit',player=ctx.playerId,consumeMainAction=false")]
        [TestCase("apiVersion=3,targetMode='select',player=ctx.playerId")]
        [TestCase("apiVersion=3,targetMode='select',player='p2',consumeMainAction=false")]
        [TestCase("apiVersion=4294967299,targetMode='select',player=ctx.playerId,consumeMainAction=false")]
        [TestCase("player=ctx.playerId")]
        [TestCase(ExploreSelect + ",paymentProtocol='route_toll_escrow.v1'")]
        [TestCase(ExploreSelect + ",selectionOwnerEffectId='forged'")]
        [TestCase(ExploreSelect + ",routeIds={}")]
        [TestCase(ExploreSelect + ",allowDecline=true")]
        public void ExploreV3PublicContractRejectsMissingPlansMixedModesAndTrustedReceipts(string fields)
        {
            string script = "return function(ctx) return {Effect.Explore({" + fields + "})} end";
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("api14", "api14", "api14", "1.0.0", script, LuaContentHasher.ComputeSha256(script)),
                new LuaInvocationContext("api14", "isolated", "p1", 0, "1.0.0", 2, 0, 4));
            exploreArtifacts.WriteLabeledText("探索非法公开参数拒绝", JsonConvert.SerializeObject(new { fields, invocation.IsSuccess, invocation.Diagnostic }));
            Assert.That(invocation.IsSuccess, Is.False);
        }
        [Test] public void ExploreV3RawCompilerAndKernelRejectForgedReceiptsAndInvalidModes()
        {
            Production(out var state, out var registry, out _);
            var args = ExplorationContract.Select(1, false).NormalizedArguments;
            args.Properties.Add(new NormalizedValueEntry { Name = "commitmentId", Value = NormalizedValue.CreateString("forged") });
            var compiler = new LuaEffectSpecCompiler(); var raw = LuaEffectSpec.FromArguments(ExplorationEffectTypeIds.Explore, args);
            var success = typeof(LuaInvocationResult).GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Single(m => m.Name == "Success" && m.GetParameters().Length == 2);
            string script = "return function(ctx) return {} end";
            var definition = new LuaScriptDefinition("api14", "api14", "api14", "1.0.0", script, LuaContentHasher.ComputeSha256(script));
            var invocation = (LuaInvocationResult)success.Invoke(null, new object[] { definition, new[] { raw } });
            Assert.That(compiler.TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out _, out _), Is.False);
            Func<string> rules = () => JsonConvert.SerializeObject(new { state.ItemZones, state.Players, state.Map, state.Decks, state.RandomRuntime });
            string original = rules();
            Assert.That(new EffectTreeExecutor(state, registry).TryCreateRoot(EffectSpec.Create(ExplorationEffectTypeIds.Explore, args, 1), out _), Is.False);
            Assert.That(rules(), Is.EqualTo(original)); Assert.That(state.EffectRuntime.EffectNodes, Is.Empty);
        }
        [Test] public void ExploreV3RawRecoveryRejectsMissingControlsAndChangedChildBindingWithoutMutatingInput()
        {
            Production(out var state, out var registry, out _); string root = StartExploreV3(state, registry, ExploreSelect);
            var archive = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            foreach (string field in new[] { "Status", "PendingOutcome", "PlayerId", "DecisionPlayerId", "AllowDecline", "NormalizedResult", "ChildEffectIds" })
            {
                var bad = (JObject)archive.DeepClone(); var node = bad["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].OfType<JObject>().Single(n => (string)n["EffectId"] == root); node.Remove(field);
                string input = bad.ToString(); var rejected = HostRecoveryService.RecoverJson(input, registry);
                Assert.That(rejected.Succeeded, Is.False, field); Assert.That(bad.ToString(), Is.EqualTo(input));
            }
            var tampered = GameStateCloneService.DeepClone(state);
            var selector = tampered.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == root);
            selector.NormalizedArguments.Properties.Single(p => p.Name == "consumeMainAction").Value = NormalizedValue.CreateBoolean(true);
            string broken = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            var altered = JObject.Parse(broken); altered["Snapshot"]["State"] = JObject.FromObject(tampered); string serialized = altered.ToString();
            var result = HostRecoveryService.RecoverJson(serialized, registry); Assert.That(result.Succeeded, Is.False); Assert.That(altered.ToString(), Is.EqualTo(serialized));
        }
        [Test] public void ExploreV3RealEventFaultKeepsOriginalEscrowAndDoesNotCompleteOrConsume()
        {
            Production(out var state, out var registry, out _);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("api14.event_fault", EventCardEffectEventTypeIds.Revealed, "*", "api14.event_fault",
                c => { throw new KernelException("api14.event_fault", "探索隔离事件响应故障。"); }, role: EffectHandlerRole.Observer));
            var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(CompileLua("Effect.Explore({" + ExploreExplicit + "})"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.True); Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.EqualTo(4));
            Assert.That(ExploreRoot(state, root).Status, Is.Not.EqualTo(EffectNodeStatus.Completed)); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            int nodes = state.EffectRuntime.EffectNodes.Count; state = ExploreRecover(state, registry, "统一探索真实响应故障保留托管现场");
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.True); Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(nodes));
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.EqualTo(4));
        }
        [Test] public void ExploreV3InvalidExplicitPlanFailsWithoutOpeningSelectionOrSpending()
        {
            Production(out var state, out var registry, out _);
            string before = JsonConvert.SerializeObject(new { state.ItemZones, state.Map, state.Decks, state.Players });
            string root = StartExploreV3(state, registry, ExploreExplicit.Replace("{'A1','A2'}", "{'A1'}").Replace(",{routeId='A2',subject='bank'}", ""));
            Assert.That(ExploreRoot(state, root).Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(Text(ExploreRoot(state, root).NormalizedResult, "outcome"), Is.EqualTo("failed"));
            Assert.That(Text(ExploreRoot(state, root).NormalizedResult, "failureCode"), Is.Not.Empty);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(JsonConvert.SerializeObject(new { state.ItemZones, state.Map, state.Decks, state.Players }), Is.EqualTo(before));
            ExploreRecover(state, registry, "已确定的断路方案失败不回退任意选择");
        }
        [TestCase(false)][TestCase(true)] public void ExploreV3InFlightConsumerPolicyAndCompletedResultCannotBeForged(bool explicitPlan)
        {
            Production(out var state, out var registry, out _); string root = StartExploreV3(state, registry, explicitPlan ? ExploreExplicit : ExploreSelect);
            if (!explicitPlan) { var q = Selection(state); Submit(state, registry, q, SelectionAnswer(q, "A-01", "A1", "A2")); }
            var archive = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            var nodes = archive["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].OfType<JObject>();
            var actual = nodes.Single(n => (string)n["EffectTypeId"] == ExplorationEffectTypeIds.Explore &&
                ((JArray)n["NormalizedArguments"]["Properties"]).OfType<JObject>().Any(p => (string)p["Name"] == "paymentProtocol"));
            var policy = ((JArray)actual["NormalizedArguments"]["Properties"]).OfType<JObject>().Single(p => (string)p["Name"] == "consumeMainAction");
            policy["Value"]["BooleanValue"] = true; string input = archive.ToString();
            Assert.That(HostRecoveryService.RecoverJson(input, registry).Succeeded, Is.False); Assert.That(archive.ToString(), Is.EqualTo(input));
            FinishEvents(state, registry); archive = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            var parent = archive["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].OfType<JObject>().Single(n => (string)n["EffectId"] == root);
            ((JArray)parent["NormalizedResult"]["Properties"]).OfType<JObject>().Single(p => (string)p["Name"] == "targetLocationId")["Value"]["StringValue"] = "A-02";
            input = archive.ToString(); Assert.That(HostRecoveryService.RecoverJson(input, registry).Succeeded, Is.False); Assert.That(archive.ToString(), Is.EqualTo(input));
            ExploreRecover(state, registry, "实际探索结果未篡改对照");
        }
    }
}
