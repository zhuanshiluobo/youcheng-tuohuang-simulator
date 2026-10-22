using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    // 固定依赖隔离边界；不表示四端物理连接、正式恢复或实际UI验收通过。
    public sealed class NetworkUiValidationRecoveryTests
    {
        private static Type Driver => Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
        private static object Call(string name, params object[] args) =>
            Driver.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
        private static DeterministicSimulationSessionOptions Source() => new DeterministicSimulationSessionOptions {
            GameId = "game", ContentPackHash = "content", SimulationEpoch = 1, MembershipVersion = 2, LocalPlayerId = 4 };
        private static SimulationRecoveryNoticeDto Notice() => new SimulationRecoveryNoticeDto {
            GameId = "game", SourceSimulationEpoch = 1, SimulationEpoch = 2, MembershipVersion = 2,
            ConfirmedGlobalSequence = 360, Phase = SimulationRecoveryPhase.Prepare, ResumeAllowed = true };

        [Test]
        public void OnlyFormallyAcceptedSameIdentityConnectionLossCanWait()
        {
            var source = Source();
            var fault = new SimulationProtocolFaultDto { GameId = "game", SimulationEpoch = 1, MembershipVersion = 2,
                FaultCode = "simulation_connection_lost", RequiresRecovery = true };
            bool CanWait(bool accepted = true) => (bool)Call("NetworkRecoveryFaultCanWait", fault, source, accepted);
            Assert.That(CanWait(), Is.True);
            Assert.That(CanWait(false), Is.False, "未接纳的断线不能忽略");
            fault.GameId = "other"; Assert.That(CanWait(), Is.False); fault.GameId = "game";
            fault.SimulationEpoch = 2; Assert.That(CanWait(), Is.False); fault.SimulationEpoch = 1;
            fault.MembershipVersion = 3; Assert.That(CanWait(), Is.False); fault.MembershipVersion = 2;
            fault.ProtocolVersion++; Assert.That(CanWait(), Is.False); fault.ProtocolVersion--;
            fault.RequiresRecovery = false; Assert.That(CanWait(), Is.False); fault.RequiresRecovery = true;
            fault.FaultCode = "simulation_local_reconnect_timeout"; Assert.That(CanWait(), Is.False, "正式期限失败照实失败");
            fault.FaultCode = "simulation_digest_mismatch"; Assert.That(CanWait(), Is.False, "其他真实fault不能被恢复等待吞掉");
        }

        [Test]
        public void RecoveryStartCannotBorrowForeignOrSkippedEpoch()
        {
            var source = Source(); var notice = Notice();
            bool Matches() => (bool)Call("NetworkRecoveryNoticeMatches", notice, source);
            Assert.That(Matches(), Is.True);
            notice.SourceSimulationEpoch = 0; Assert.That(Matches(), Is.False); notice.SourceSimulationEpoch = 1;
            notice.SimulationEpoch = 3; Assert.That(Matches(), Is.False); notice.SimulationEpoch = 2;
            notice.MembershipVersion = 1; Assert.That(Matches(), Is.False); notice.MembershipVersion = 2;
            notice.GameId = "foreign"; Assert.That(Matches(), Is.False);
        }

        [Test]
        public void PhysicalConnectionAndClearedAwaitFlagCannotReplaceInstalledConfirmedPrefix()
        {
            var source = Source(); var notice = Notice(); var installed = Source();
            installed.SimulationEpoch = 2; installed.CheckpointGlobalSequence = 360;
            var boundary = new SimulationBoundaryDto { GameId = "game", SimulationEpoch = 2, MembershipVersion = 2, GlobalFenceSequence = 360 };
            bool Ready(bool completed = true, bool awaiting = false, bool recovering = false, bool syncing = false,
                bool stopped = false, bool recoveryRequired = false, bool submissionReady = true, long applied = 360, long validated = 360,
                SimulationBoundaryDto currentBoundary = null) => (bool)Call("NetworkConfirmedRecoveryCanResume", source, notice, installed,
                    completed, awaiting, recovering, syncing, stopped, recoveryRequired, submissionReady, applied, validated, 360L, currentBoundary ?? boundary);
            Assert.That(Ready(), Is.True, "正式完成事件、准确新代次协调器、前缀和消费者均就绪才继续");
            Assert.That(Ready(completed: false), Is.False, "物理已连接且await=false仍不足");
            Assert.That(Ready(awaiting: true), Is.False);
            Assert.That(Ready(recovering: true), Is.False);
            Assert.That(Ready(syncing: true), Is.False);
            Assert.That(Ready(stopped: true), Is.False, "ResumeAllowed=false留下的停止不能当完成");
            Assert.That(Ready(recoveryRequired: true), Is.False);
            Assert.That(Ready(submissionReady: false), Is.False);
            Assert.That(Ready(applied: 361), Is.False, "未确认尾段不能解除等待");
            installed.CheckpointGlobalSequence = 359; Assert.That(Ready(), Is.False); installed.CheckpointGlobalSequence = 360;
            boundary.SimulationEpoch = 1; Assert.That(Ready(), Is.False); boundary.SimulationEpoch = 2;
            Assert.That((bool)Call("NetworkConfirmedRecoveryCanResume", source, notice, installed, true, false, false,
                false, false, false, true, 360L, 360L, 360L, null), Is.False, "必须确实存在新代次的确认边界");
            installed.LocalPlayerId = 3; Assert.That(Ready(), Is.False); installed.LocalPlayerId = 4;
            installed.ContentPackHash = "other"; Assert.That(Ready(), Is.False);
        }

        [Test]
        public void OriginalPendingCommandNeedsItsExactRealResultAndRetainsEventIdentity()
        {
            var original = new GameCommand { CommandId = "original-id", Kind = GameCommandKind.AnswerInteraction, PlayerId = 4 };
            original.OptionIds.Add("original-answer"); original.Parameters.Add("expectedRevision", "3867");
            string before = JsonConvert.SerializeObject(original);
            var result = CommandResult.SuccessResult(new List<GameEvent> { new GameEvent { EventId = "stable-event", PlayerId = 4 } }, "真实Applied");
            var receipt = new SimulationCommandAppliedDto { GameId = "game", SimulationEpoch = 2, MembershipVersion = 2,
                Command = new GameCommandDto { CommandId = original.CommandId, Kind = original.Kind, PlayerId = original.PlayerId },
                GlobalCommandSequence = 361, RuleResult = result };
            WorkflowSubmissionResult Resolve() => (WorkflowSubmissionResult)Call("NetworkExactAppliedResult", original, receipt, "game");
            receipt.Command.CommandId = "other-id"; Assert.That(Resolve(), Is.Null);
            receipt.Command.CommandId = original.CommandId; receipt.GameId = "other-game"; Assert.That(Resolve(), Is.Null);
            receipt.GameId = "game"; receipt.Command.PlayerId = 3; Assert.That(Resolve(), Is.Null);
            receipt.Command.PlayerId = 4;
            var resolved = Resolve(); Assert.That(resolved.AppliedLocally, Is.True);
            Assert.That(resolved.CommandResult, Is.SameAs(result), "不重建结果或改EventId");
            Assert.That(JsonConvert.SerializeObject(original), Is.EqualTo(before), "原CommandId、答案和revision保持，不重签重发");
            receipt.RuleResult = CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.StateRevisionMismatch, "真实拒绝"));
            Assert.That(Resolve().AppliedLocally, Is.False, "真实失败不能冒充成功");
        }

        [Test]
        public void AcceptedRecoveryDoesNotEraseEarlierFatalRuntimeOrProtocolError()
        {
            var driver = FormatterServices.GetUninitializedObject(Driver);
            Driver.GetField("networkAwaitedConnectionFault", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(driver,
                new SimulationProtocolFaultDto { FaultCode = "simulation_connection_lost", RequiresRecovery = true });
            Driver.GetField("networkFault", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(driver, "先前真实运行错误");
            var failure = Assert.Throws<TargetInvocationException>(() => Driver.GetMethod("NetworkCheck", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(driver, null));
            Assert.That(failure.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(failure.InnerException.Message, Does.Contain("先前真实运行错误"));
            Assert.That(Driver.GetField("networkBegan", BindingFlags.NonPublic | BindingFlags.Instance), Is.Null, "移除整局总限字段");
        }

        [Test]
        public void OriginalCommandWaitExcludesOnlyAcceptedRecoveryPause()
        {
            float Clock(float now, float pauses, bool paused, float began) => (float)Call("NetworkActiveClock", now, pauses, paused, began);
            float commandBegan = Clock(10000f, 0f, false, 0f);
            Assert.That(Clock(10020f, 0f, false, 0f) - commandBegan, Is.EqualTo(20f));
            Assert.That(Clock(10100f, 0f, true, 10020f) - commandBegan, Is.EqualTo(20f), "80秒恢复等待不消耗原45秒回执步骤");
            Assert.That(Clock(10101f, 80f, false, 0f) - commandBegan, Is.EqualTo(21f));
            Assert.That(Clock(10126f, 80f, false, 0f) - commandBegan, Is.EqualTo(46f), "恢复后的真实无回执时间仍可超出原步骤期限");
        }

        [Test]
        public void PartiallyPublishedPeerStatusCannotReleaseObservedRecoveryPause()
        {
            var driver = FormatterServices.GetUninitializedObject(Driver);
            var transportType = Type.GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport, Assembly-CSharp", true);
            var transport = FormatterServices.GetUninitializedObject(transportType);
            var coordinator = FormatterServices.GetUninitializedObject(typeof(DeterministicSimulationCoordinator));
            typeof(DeterministicSimulationCoordinator).GetField("options", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(coordinator, Source());
            transportType.GetField("simulationCoordinator", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(transport, coordinator);
            var evidenceRoot = Environment.GetEnvironmentVariable("YC_NETWORK_RECOVERY_EVIDENCE_ROOT") ??
                Path.Combine(TestContext.CurrentContext.WorkDirectory, "Logs", "NetworkUiFixes", "20261017", "driver-recovery");
            var folder = Path.Combine(evidenceRoot, "peer-status-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            void Set(string name, object value) => Driver.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(driver, value);
            Set("networkTransport", transport); Set("networkRoot", folder); Set("networkRole", "host");
            Set("networkRoles", new[] { "host", "client2" }); Set("networkRecoveringPeers", new HashSet<string>());
            bool Waiting() => (bool)Driver.GetMethod("NetworkPeerAwaitingRecovery", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(driver, null);
            string path = Path.Combine(folder, "状态-client2.json");
            object Status(bool pending) => new { GameId = "game", ContentHash = "content", MembershipVersion = 2, SimulationEpoch = 1, RecoveryPending = pending };
            File.WriteAllText(path, JsonConvert.SerializeObject(Status(true)));
            Assert.That(Waiting(), Is.True);
            File.WriteAllText(path, "{");
            Assert.That(Waiting(), Is.True, "发布中暂不可读不能被当成已恢复");
            File.WriteAllText(path, JsonConvert.SerializeObject(Status(false)));
            Assert.That(Waiting(), Is.False, "只有完整有效的退出等待状态才解除该peer暂停");
        }

        [Test]
        public void RecoveryRefusalBelongsToExactPendingCommandAndActiveStep()
        {
            var source = Source(); var notice = Notice(); var installed = Source(); installed.SimulationEpoch = 2;
            var original = new GameCommand { CommandId = "original", PlayerId = 4, Kind = GameCommandKind.AnswerInteraction };
            bool CanRetire(bool pending = true, long epoch = 1, string active = "old-step", string game = "game", string id = "original",
                bool accepted = false, CommandErrorCode code = CommandErrorCode.StateRevisionMismatch, bool refresh = true) =>
                (bool)Call("NetworkCanRetireForRecovery", original, pending, epoch, "old-step", active, game, id, accepted, code, refresh, source, notice, installed);
            Assert.That(CanRetire(), Is.True);
            Assert.That(CanRetire(pending: false), Is.False);
            Assert.That(CanRetire(epoch: 2), Is.False);
            Assert.That(CanRetire(active: "new-step"), Is.False);
            Assert.That(CanRetire(game: "other"), Is.False);
            Assert.That(CanRetire(id: "other"), Is.False);
            Assert.That(CanRetire(accepted: true), Is.False);
            Assert.That(CanRetire(code: CommandErrorCode.UnknownCommand), Is.False);
            Assert.That(CanRetire(refresh: false), Is.False);
            installed.MembershipVersion++; Assert.That(CanRetire(), Is.False);
        }

        [Test]
        public void RecoveryRetirementDiscardsOldCoroutineAndResumesFreshRootDecision()
        {
            bool staleTail = false, childTail = false, disposed = false; string selected = null, current = "old-visible";
            IEnumerator Child() { yield return null; childTail = true; }
            IEnumerator Old()
            {
                try { yield return Child(); staleTail = true; }
                finally { disposed = true; }
            }
            IEnumerator Fresh() { selected = current; yield return null; }
            var boundaryType = Driver.GetNestedType("NetworkStepBoundary", BindingFlags.NonPublic);
            var boundary = (IEnumerator)Activator.CreateInstance(boundaryType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { "old-step", Old() }, null);
            IEnumerator Root() { yield return boundary; yield return Fresh(); }
            var root = Root(); var stack = new Stack<IEnumerator>(); stack.Push(root);
            Assert.That(root.MoveNext(), Is.True); stack.Push((IEnumerator)root.Current);
            Assert.That(boundary.MoveNext(), Is.True); var child = (IEnumerator)boundary.Current; stack.Push(child); child.MoveNext();
            Call("NetworkDiscardStep", stack, "old-step");
            Assert.That(stack.Count, Is.EqualTo(1)); Assert.That(stack.Peek(), Is.SameAs(root));
            Assert.That(disposed, Is.True); Assert.That(staleTail || childTail, Is.False, "旧步骤后半段不能被恢复后的回执放行");
            current = "current-visible";
            Assert.That(root.MoveNext(), Is.True); ((IEnumerator)root.Current).MoveNext();
            Assert.That(selected, Is.EqualTo("current-visible"), "从根的新步骤重新读取当前候选，未执行旧选择");
        }
    }
}
