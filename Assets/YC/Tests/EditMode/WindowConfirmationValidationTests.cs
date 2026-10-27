using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using YC.Application.Sessions;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
#if !YC_CONFIRMATION_OFFLINE
using NUnit.Framework;
#endif

namespace YC.Tests.EditMode
{
    // 只验证原窗口协议的明确yield/Ready/确认，不是正常界面或R6命令重放。
    public sealed class WindowConfirmationValidationTests
    {
        internal static Action<object> Observation;
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        private sealed class Fixture
        {
            internal GameSession Session;
            internal WindowLocalExecution Stream;
            internal WindowExecutionSeal Seal;
        }
        private static Fixture Create(bool executeOne = false)
        {
            var state = new GameState { GameId = "r26-confirmation-unit", ContentPackHash = "r26-content", Phase = GamePhase.ActionRound1,
                Round = 1, CurrentPlayerId = 1, StartPlayerId = 1, UseSeatTurnOrder = true, LaunchPlayerOrder = new List<int> { 1, 2, 3 }, MapId = StaticMapDefinitions.FourPlayerMapId };
            for (int player = 1; player <= 3; player++) state.Players.Add(new PlayerState { PlayerId = player });
            state.RuleConfiguration = new GameRuleConfiguration { MapId = state.MapId, PlayerCount = 3, ContentPackHash = state.ContentPackHash };
            var registry = new EffectRegistry(); var round = new RoundExecutionService(registry);
            Require(round.PrepareActionWindowForSmoke(state, 1).IsValid, "隔离主窗未建立");
            if (executeOne)
            {
                registry.Register(new EffectRegistration("test.r26.confirmation.input", context => context.GetLatestInteractionAnswer() != null
                    ? EffectStepResult.Completed() : EffectStepResult.Continue("awaiting").AddInteraction(new EffectInteractionSpec {
                        InteractionTypeId = "test.r26.confirmation.answer", AnsweringPlayerId = 1, AnswerSchema = "boolean_accept" }), EffectExecutorKind.IntrinsicFlow));
                var executor = round.CreateCommandExecutor(state);
                Require(executor.TryCreatePlayerActionEffect(1, EffectSpec.Create("test.r26.confirmation.input", playerId: 1), "1", "test.r26", out _), "原行动效果未建立");
                Require(!executor.RunUntilQuiescent().Faulted, "原请求未产生");
            }
            var session = new GameSession(state); var options = DeterministicSimulationSessionOptions.CreateInitial(true, 1, session, new[] { 1, 2, 3 });
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
            var host = new DeterministicSimulationCoordinator(session, options); WindowExecutionGrant grant = null;
            host.WindowPermitProduced += permit => {
                var root = OperationWindowService.ReadOpenWindows(session.State).Single(w => w.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow);
                grant = WindowExecutionCodec.CreateGrant(session, options, permit, 1, root.OperationWindowId, 1, 1);
            };
            host.Begin(); var boundary = host.ReadBoundary(0); Require(boundary != null, "真实边界未生成");
            foreach (int player in new[] { 2, 3 })
            {
                var replicaSession = new GameSession(GameStateCloneService.DeepClone(state));
                var replica = new DeterministicSimulationCoordinator(replicaSession, DeterministicSimulationSessionOptions.CreateInitial(false, player, replicaSession, new[] { 1, 2, 3 }));
                replica.Begin(); Require(replica.ReceiveBoundary(boundary), "副本边界不一致");
                Require(host.ReceiveWindowReady(player, new SimulationWindowReadyDto { GameId = boundary.GameId, SimulationEpoch = 1, MembershipVersion = 1,
                    PlayerId = player, ReplicaId = DeterministicSimulationSessionOptions.ReplicaIdForPlayer(player), Boundary = replica.ReadBoundary(0) }), "副本Ready未接纳");
            }
            Require(grant != null, "未获正式原许可");
            var sequencer = (OrderedCommandSequencer)typeof(DeterministicSimulationCoordinator).GetField("sequencer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host);
            var stream = new WindowLocalExecution(session, grant, 1, true, sequencer, retainStandaloneCheckpoint: false);
            if (executeOne)
            {
                var request = session.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == "test.r26.confirmation.answer");
                var command = new GameCommand { CommandId = "r26-original-unit-answer", PlayerId = 1, Kind = GameCommandKind.AnswerInteraction };
                command.Parameters["interactionId"] = request.GetStableInteractionId(); command.Parameters["expectedRevision"] = session.State.EffectRuntime.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                command.Parameters["answerValue"] = "true";
                var result = stream.ExecuteLocal(command);
                Require(result.Status == "applied_locally" && result.RuleResult.Succeeded && stream.ActualRuleExecutions == 1 && stream.Tail == 1, "正式原规则答案没有执行一次");
            }
            return new Fixture { Session = session, Stream = stream, Seal = stream.Seal(true) };
        }
        private static void ReadyAll(Fixture fixture)
        { foreach (int player in new[] { 1, 2, 3 }) Require(fixture.Stream.ReceiveReady(player, fixture.Seal), "原正确Ready被拒绝"); }

#if !YC_CONFIRMATION_OFFLINE
        [Test]
#endif
        public void EligibleConfirmationValidatesCurrentStateOnceAndKeepsExactSeal()
        {
            var fixture = Create(true); ReadyAll(fixture); int before = fixture.Stream.FullRuleDigestComputations;
            string stateBefore = RuleStateDigestService.Compute(fixture.Session.State);
            var result = fixture.Stream.TryConfirm(); int calls = fixture.Stream.FullRuleDigestComputations - before;
            string stateAfter = RuleStateDigestService.Compute(fixture.Session.State);
            Require(result != null && fixture.Stream.HasConfirmedSeal && !fixture.Stream.IsStopped, "原全部Ready后未确认");
            Require(result.Seal.SealDigest == fixture.Seal.SealDigest && result.ReadyReplicaIds.SequenceEqual(new[] { "player.1", "player.2", "player.3" }) && stateBefore == stateAfter,
                "确认改变原封口/副本集合/规则图");
            Require(fixture.Stream.ConfirmedGlobalSequence == 1 && fixture.Stream.ActualRuleExecutions == 1, "确认丢失原规则尾号或重新执行");
            Observation?.Invoke(new { Case = "eligible", DigestCalls = calls, SealDigest = result.Seal.SealDigest, StateDigest = stateAfter, Confirmed = fixture.Stream.HasConfirmedSeal,
                fixture.Stream.ConfirmedGlobalSequence, fixture.Stream.ActualRuleExecutions });
            Require(calls == 1, "全部Ready后的同次确认重复完整规则摘要：" + calls);
        }

#if !YC_CONFIRMATION_OFFLINE
        [Test]
#endif
        public void MissingOrForeignReadyStillWaitsAndStillDetectsLocalCorruption()
        {
            var fixture = Create(); Require(!fixture.Stream.ReceiveReady(4, fixture.Seal), "接纳非规定副本");
            Require(fixture.Stream.ReceiveReady(1, fixture.Seal), "本人Ready失败"); int before = fixture.Stream.FullRuleDigestComputations;
            Require(fixture.Stream.TryConfirm() == null && !fixture.Stream.HasConfirmedSeal && !fixture.Stream.IsStopped, "缺副本时提前确认");
            Require(fixture.Stream.FullRuleDigestComputations == before + 1, "等待期间原本地校验被移除");
            // 有意破坏隔离状态作负例，不能计作正常合法推进。
            fixture.Session.State.FindPlayer(1).Score++;
            Require(fixture.Stream.TryConfirm() == null && fixture.Stream.IsStopped && fixture.Stream.FirstFault == "boundary_digest_conflict", "等待期间未保留本地损坏检测");
            Observation?.Invoke(new { Case = "waiting_corruption", Fault = fixture.Stream.FirstFault, Confirmed = fixture.Stream.HasConfirmedSeal });
        }

#if !YC_CONFIRMATION_OFFLINE
        [Test]
#endif
        public void FullReadinessStillRejectsChangedStateAndConflictingSeal()
        {
            var fixture = Create(); ReadyAll(fixture); fixture.Session.State.FindPlayer(1).Score++;
            Require(fixture.Stream.TryConfirm() == null && fixture.Stream.IsStopped && fixture.Stream.FirstFault == "boundary_digest_conflict" && !fixture.Stream.HasConfirmedSeal,
                "全部Ready后跳过最终状态校验");
            var conflict = Create(); var wrong = WindowExecutionCodec.Copy(conflict.Seal); wrong.RuleDigest = new string('0', 64); wrong.SealDigest = WindowExecutionCodec.SealDigest(wrong);
            Require(!conflict.Stream.ReceiveReady(2, wrong) && conflict.Stream.IsStopped && conflict.Stream.FirstFault == "ready_digest_conflict", "错误封口被接纳");
            Observation?.Invoke(new { Case = "eligible_corruption", Fault = fixture.Stream.FirstFault, ConflictingSealFault = conflict.Stream.FirstFault, Confirmed = fixture.Stream.HasConfirmedSeal });
        }
    }
}
