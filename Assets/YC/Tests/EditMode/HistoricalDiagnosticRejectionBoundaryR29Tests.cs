using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    // 仅Validation边界：复用已核R28夹具的准备/释放，不运行其旧案例，不启动正常局或修改恢复输入。
    internal static class RejectionBoundaryR29Fixture
    {
        internal const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        internal static object Read(object value, string name) => value?.GetType().GetField(name, Flags)?.GetValue(value)
            ?? value?.GetType().GetProperty(name, Flags)?.GetValue(value);
        internal static void Set(object value, string name, object data) => value.GetType().GetField(name, Flags).SetValue(value, data);
        internal static object Call(object value, string name, params object[] args)
        {
            try { return value.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        internal static void Prepare(object fixture)
        {
            string root = Environment.GetEnvironmentVariable("QY017_R29_OUTPUT");
            Assert.That(Path.IsPathRooted(root ?? ""), Is.True, "须提供本轮全新绝对输出目录");
            string previous = Environment.GetEnvironmentVariable("QY017_R28_OUTPUT");
            try
            {
                // 旧夹具仅在Prepare读取其输出变量；新输出按本轮用例隔离，随后立即恢复环境变量。
                Environment.SetEnvironmentVariable("QY017_R28_OUTPUT", Path.Combine(root, TestContext.CurrentContext.Test.ID));
                Call(fixture, "Prepare");
            }
            finally { Environment.SetEnvironmentVariable("QY017_R28_OUTPUT", previous); }
        }
        internal static EffectDiagnosticRuntimeState AddHistoricalDisplayFailure(GameState state)
        {
            state.EffectRuntime.StateRevision = Math.Max(20, state.EffectRuntime.StateRevision);
            var node = new EffectNodeRuntimeState { EffectId = "r29.private.old.display", Status = EffectNodeStatus.Completed,
                PlayerId = 2, CompletedEventId = "r29.private.completed", OperationContext = new OperationCommandContext {
                    SourceCommandId = "r29.private.old.command", SourceMainNodeId = "r29.private.old.main", ActingPlayerId = 2 } };
            state.EffectRuntime.EffectNodes.Add(node);
            state.EffectRuntime.MainNodes.Add(new MainlineNodeRuntimeState { NodeId = "r29.private.old.main", Status = EffectNodeStatus.Completed, PlayerId = 2 });
            var diagnostic = new EffectDiagnosticRuntimeState { Code = "presentation_failed", Message = "私有已结束显示失败",
                NodeId = node.EffectId, StateRevision = state.EffectRuntime.StateRevision - 1 };
            state.EffectRuntime.Diagnostics.Add(diagnostic);
            return diagnostic;
        }
        internal static object BeginAndReject(object driver, GameState state)
        {
            var observed = Call(driver, "BeginObservedAction", GameCommandKind.DeployInfluence, 1, "", "location:A-01:1", "action:deploy", null);
            AddRejection(driver, state); Call(driver, "ReadObservedActionIdentity", observed);
            return observed;
        }
        internal static void AddRejection(object driver, GameState state)
        {
            var result = new WorkflowSubmissionResult(CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InsufficientInfluence, "insufficientsupply")), false);
            ((List<object>)Read(driver, "commands")).Add(new { Command = new GameCommand { Kind = GameCommandKind.DeployInfluence, PlayerId = 1, TargetId = "location:A-01:1" },
                Result = result, BeforeRevision = state.EffectRuntime.StateRevision, AfterRevision = state.EffectRuntime.StateRevision });
        }
        internal static object Boundary(object driver, object action, string stage) => Call(driver, "RecordRejectedActionBoundary", action, stage);
        internal static bool Flag(object boundary, string group, string key) => ((Dictionary<string, bool>)Read(boundary, group))[key];
        internal static void Save(object fixture, string name, object value)
        {
            using var file = new FileStream(Path.Combine((string)Read(fixture, "output"), name + ".json"), FileMode.CreateNew);
            using var writer = new StreamWriter(file); writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented));
        }
        internal sealed class Probe { internal Exception Error; }
        internal static IEnumerator Drain(IEnumerator routine, Probe probe)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(routine);
            while (stack.Count > 0)
            {
                bool moved; object current = null;
                try { moved = stack.Peek().MoveNext(); if (moved) current = stack.Peek().Current; }
                catch (Exception error) { probe.Error = error; yield break; }
                if (!moved) { stack.Pop(); continue; }
                if (current is IEnumerator child) stack.Push(child);
                else if (current is WaitForEndOfFrame) { Canvas.ForceUpdateCanvases(); yield return null; }
                else yield return current;
            }
        }
    }

    public sealed class HistoricalDiagnosticBoundaryR29Tests
    {
        ActionReceiptDriverR28Tests fixture;
        object driver, action;
        GameState state;
        EffectDiagnosticRuntimeState diagnostic;
        [SetUp] public void Prepare()
        {
            fixture = new ActionReceiptDriverR28Tests(); RejectionBoundaryR29Fixture.Prepare(fixture);
            driver = RejectionBoundaryR29Fixture.Read(fixture, "driver"); state = (GameState)RejectionBoundaryR29Fixture.Read(fixture, "state");
            diagnostic = RejectionBoundaryR29Fixture.AddHistoricalDisplayFailure(state);
            action = RejectionBoundaryR29Fixture.BeginAndReject(driver, state);
        }
        [TearDown] public void Release() { if (fixture != null) RejectionBoundaryR29Fixture.Call(fixture, "Release"); }
        object Capture(string stage) => RejectionBoundaryR29Fixture.Boundary(driver, action, stage);
        [Test] public void UnchangedResolvedUnrelatedDisplayFailurePreservesEvidenceAndWholeGameFailure()
        {
            string digest = RuleStateDigestService.Compute(state); var boundary = Capture("历史显示失败保持");
            Assert.That(RejectionBoundaryR29Fixture.Read(boundary, "RuleClear"), Is.EqualTo(true));
            Assert.That(state.EffectRuntime.Diagnostics.Single(), Is.SameAs(diagnostic));
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(digest));
            Assert.That(((IList)RejectionBoundaryR29Fixture.Read(driver, "nonfatalUiFailures")).Count, Is.EqualTo(1));
            Assert.That(RejectionBoundaryR29Fixture.Read(action, "Completed"), Is.EqualTo(false));
            RejectionBoundaryR29Fixture.Save(fixture, "历史失败未被独立继续变为整局通过", boundary);
        }
        [Test] public void AddedRemovedOrAnyChangedDiagnosticFieldStopsAndBeforeSnapshotDoesNotAlias()
        {
            string original = JsonConvert.SerializeObject(diagnostic);
            foreach (string name in new[] { "Code", "Message", "NodeId", "EventId", "StateRevision", "CommitSequence" })
            {
                var field = typeof(EffectDiagnosticRuntimeState).GetField(name); object value = field.GetValue(diagnostic);
                field.SetValue(diagnostic, value is int number ? (object)(number + 1) : value + ".changed");
                var changed = Capture("改变诊断字段-" + name);
                Assert.That(RejectionBoundaryR29Fixture.Flag(changed, "RuleChecks", "diagnostics_unchanged"), Is.False, name);
                Assert.That(RejectionBoundaryR29Fixture.Read(changed, "RuleClear"), Is.EqualTo(false), name);
                var before = RejectionBoundaryR29Fixture.Read(action, "BeforeBoundary");
                Assert.That(JsonConvert.SerializeObject(((Array)RejectionBoundaryR29Fixture.Read(before, "Diagnostics")).GetValue(0)), Is.EqualTo(original));
                field.SetValue(diagnostic, value);
            }
            state.EffectRuntime.Diagnostics.Add(new EffectDiagnosticRuntimeState { Code = "presentation_failed", NodeId = diagnostic.NodeId, StateRevision = diagnostic.StateRevision });
            Assert.That(RejectionBoundaryR29Fixture.Flag(Capture("新增诊断"), "RuleChecks", "diagnostics_unchanged"), Is.False);
            state.EffectRuntime.Diagnostics.Clear();
            Assert.That(RejectionBoundaryR29Fixture.Flag(Capture("删除诊断"), "RuleChecks", "diagnostics_unchanged"), Is.False);
            state.EffectRuntime.Diagnostics.Add(diagnostic);
            Assert.That(RejectionBoundaryR29Fixture.Read(Capture("恢复私有测试输入后原边界"), "RuleClear"), Is.EqualTo(true));
        }
        [Test] public void UnfinishedRelatedOrUnknownHistoricalRecordCannotBeClassifiedAsSafe()
        {
            var runtime = state.EffectRuntime;
            var node = runtime.EffectNodes.Single(n => n.EffectId == diagnostic.NodeId);
            var main = runtime.MainNodes.Single(n => n.NodeId == "r29.private.old.main");
            void MustStop(string label) => Assert.That(RejectionBoundaryR29Fixture.Flag(Capture(label), "RuleChecks", "current_diagnostics_resolved_unrelated"), Is.False, label);
            node.Status = EffectNodeStatus.Blocked; MustStop("历史owner未结清"); node.Status = EffectNodeStatus.Completed;
            main.Status = EffectNodeStatus.Running; MustStop("历史主窗口未结清"); main.Status = EffectNodeStatus.Completed;
            string active = runtime.ActiveMainNodeId; runtime.ActiveMainNodeId = main.NodeId; MustStop("历史来源仍是当前窗口"); runtime.ActiveMainNodeId = active;
            var request = new InteractionRequest { InteractionId = "r29.private.open", OwnerEffectId = node.EffectId, Status = "open", AnsweringPlayerId = 2 };
            runtime.InteractionRequests.Add(request); MustStop("历史相关必答未结清"); runtime.InteractionRequests.Remove(request);
            var blocker = new EffectBlockerRuntimeState { OwnerEffectId = node.EffectId, BlockerId = "r29.private.blocker", IsResolved = false };
            runtime.Blockers.Add(blocker); MustStop("历史相关blocker未解除"); runtime.Blockers.Remove(blocker);
            diagnostic.Code = "other_rule_failure"; MustStop("未知诊断类型"); diagnostic.Code = "presentation_failed";
        }
        [Test] public void CommandIdentityRevisionAndFullDigestRemainStrict()
        {
            var command = (GameCommand)RejectionBoundaryR29Fixture.Read(action, "Command");
            string target = command.TargetId; command.TargetId = "location:B-01:0";
            Assert.That(RejectionBoundaryR29Fixture.Flag(Capture("命令目标变化"), "IdentityChecks", "same_target"), Is.False); command.TargetId = target;
            string id = command.CommandId; command.CommandId = "";
            Assert.That(RejectionBoundaryR29Fixture.Flag(Capture("命令ID为空"), "IdentityChecks", "unique_nonempty_command_id"), Is.False); command.CommandId = id;
            state.EffectRuntime.StateRevision++;
            Assert.That(RejectionBoundaryR29Fixture.Flag(Capture("状态修订变化"), "RuleChecks", "same_revision"), Is.False); state.EffectRuntime.StateRevision--;
            state.FindPlayer(1).RemainingMainActionsThisTurn++;
            Assert.That(RejectionBoundaryR29Fixture.Flag(Capture("同修订规则状态变化"), "RuleChecks", "same_rule_digest"), Is.False);
        }
    }

    public sealed class HistoricalDiagnosticRejectionUiR29Tests
    {
        KnownSupplyRejectionUiR28Tests fixture;
        object driver;
        GameState state;
        InfluenceActionPresenter influence;
        EffectDiagnosticRuntimeState diagnostic;
        IEnumerator Prepare()
        {
            fixture = new KnownSupplyRejectionUiR28Tests(); RejectionBoundaryR29Fixture.Prepare(fixture);
            driver = RejectionBoundaryR29Fixture.Read(fixture, "driver"); state = (GameState)RejectionBoundaryR29Fixture.Read(fixture, "state");
            influence = (InfluenceActionPresenter)RejectionBoundaryR29Fixture.Read(fixture, "influence");
            diagnostic = RejectionBoundaryR29Fixture.AddHistoricalDisplayFailure(state);
            yield return null; Canvas.ForceUpdateCanvases(); yield return null;
        }
        IEnumerator OpenOriginalDraft()
        {
            var hud = RejectionBoundaryR29Fixture.Read(fixture, "hud");
            var button = RejectionBoundaryR29Fixture.Read(RejectionBoundaryR29Fixture.Read(hud, "ActionPanelView"), "DeployButton");
            var probe = new RejectionBoundaryR29Fixture.Probe();
            yield return RejectionBoundaryR29Fixture.Drain((IEnumerator)RejectionBoundaryR29Fixture.Call(driver, "Press", button, "R29私有原部署入口"), probe);
            if (probe.Error != null) ExceptionDispatchInfo.Capture(probe.Error).Throw();
            Assert.That(influence.HasUnsubmittedMainActionDraft, Is.True);
        }
        object Begin() => RejectionBoundaryR29Fixture.Call(fixture, "Begin");
        [UnityTest] public IEnumerator ResolvedHistoricalDisplayFailureAllowsOriginalWithdrawalButNeverCreditsRejection()
        {
            yield return new EnterPlayMode();
            yield return Prepare(); string digest = RuleStateDigestService.Compute(state); string rows = JsonConvert.SerializeObject(state.EffectRuntime.Diagnostics);
            var observed = Begin(); yield return OpenOriginalDraft(); RejectionBoundaryR29Fixture.AddRejection(driver, state);
            var probe = new RejectionBoundaryR29Fixture.Probe();
            yield return RejectionBoundaryR29Fixture.Drain((IEnumerator)RejectionBoundaryR29Fixture.Call(driver, "ResolveObservedInfluenceAction", observed), probe);
            if (probe.Error != null) ExceptionDispatchInfo.Capture(probe.Error).Throw();
            Assert.That(RejectionBoundaryR29Fixture.Read(observed, "KnownSupplyRejectionHandled"), Is.EqualTo(true));
            Assert.That(RejectionBoundaryR29Fixture.Read(fixture, "deployClicks"), Is.EqualTo(2));
            Assert.That(influence.HasUnsubmittedMainActionDraft, Is.False); Assert.That(influence.IsActive, Is.False);
            Assert.That(((IList)RejectionBoundaryR29Fixture.Read(driver, "commands")).Count, Is.EqualTo(1));
            Assert.That(((Dictionary<string, int>)RejectionBoundaryR29Fixture.Read(RejectionBoundaryR29Fixture.Read(driver, "coverage"), "Completed")).Count, Is.Zero);
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(digest)); Assert.That(JsonConvert.SerializeObject(state.EffectRuntime.Diagnostics), Is.EqualTo(rows));
            Assert.That(((IList)RejectionBoundaryR29Fixture.Read(driver, "nonfatalUiFailures")).Count, Is.EqualTo(1));
            Assert.That(RejectionBoundaryR29Fixture.Call(driver, "ActionUnavailableThisWindow", GameCommandKind.DeployInfluence, 1), Is.EqualTo(true));
            Assert.Throws<InvalidOperationException>(() => Begin());
            RejectionBoundaryR29Fixture.Save(fixture, "历史失败保留且仅原UI草稿退出", new { BeforeDigest = digest, CurrentDigest = RuleStateDigestService.Compute(state),
                Diagnostics = state.EffectRuntime.Diagnostics, RejectionCompleted = false, NewCoverage = 0, WholeGameAcceptancePassed = false, NormalGameTest = false });
        }
        IEnumerator MustStopWithoutWithdrawal(string mutation)
        {
            yield return Prepare(); var observed = Begin(); yield return OpenOriginalDraft(); RejectionBoundaryR29Fixture.AddRejection(driver, state);
            if (mutation == "new_diagnostic") state.EffectRuntime.Diagnostics.Add(new EffectDiagnosticRuntimeState { Code = "presentation_failed", NodeId = diagnostic.NodeId, StateRevision = state.EffectRuntime.StateRevision });
            if (mutation == "fault") { state.EffectRuntime.Status = EffectRuntimeStatus.PausedFault; state.EffectRuntime.LastFaultCode = "r29.private.fault"; }
            if (mutation == "pending") RejectionBoundaryR29Fixture.Set(RejectionBoundaryR29Fixture.Read(fixture, "mobile"), "pendingEffectCommandId", "r29.private.pending");
            var probe = new RejectionBoundaryR29Fixture.Probe();
            yield return RejectionBoundaryR29Fixture.Drain((IEnumerator)RejectionBoundaryR29Fixture.Call(driver, "ResolveObservedInfluenceAction", observed), probe);
            Assert.That(probe.Error, Is.TypeOf<InvalidOperationException>()); Assert.That(probe.Error.Message, Does.Contain("state_or_pending_boundary_changed"));
            Assert.That(RejectionBoundaryR29Fixture.Read(observed, "KnownSupplyRejectionHandled"), Is.EqualTo(false));
            Assert.That(RejectionBoundaryR29Fixture.Read(fixture, "deployClicks"), Is.EqualTo(1)); Assert.That(influence.HasUnsubmittedMainActionDraft, Is.True);
            Assert.That(((IList)RejectionBoundaryR29Fixture.Read(driver, "commands")).Count, Is.EqualTo(1));
            var boundary = RejectionBoundaryR29Fixture.Boundary(driver, observed, "阻断后完整分项核对");
            if (mutation == "new_diagnostic") Assert.That(RejectionBoundaryR29Fixture.Flag(boundary, "RuleChecks", "diagnostics_unchanged"), Is.False);
            if (mutation == "fault") Assert.That(RejectionBoundaryR29Fixture.Flag(boundary, "RuleChecks", "runtime_active"), Is.False);
            if (mutation == "pending") Assert.That(RejectionBoundaryR29Fixture.Flag(boundary, "UiChecks", "no_pending_effect_command"), Is.False);
            RejectionBoundaryR29Fixture.Save(fixture, mutation + "不撤草稿且完整保边界", new { Error = probe.Error.ToString(), Boundary = boundary,
                OriginalRejection = RejectionBoundaryR29Fixture.Read(observed, "Receipt"), NewCompletion = 0, DraftStillPresent = true, RuleStateCleared = false });
        }
        [UnityTest] public IEnumerator NewDiagnosticStopsBeforeOriginalWithdrawal() { yield return new EnterPlayMode(); yield return MustStopWithoutWithdrawal("new_diagnostic"); }
        [UnityTest] public IEnumerator ActualFaultStopsBeforeOriginalWithdrawal() { yield return new EnterPlayMode(); yield return MustStopWithoutWithdrawal("fault"); }
        [UnityTest] public IEnumerator PendingSubmissionStopsBeforeOriginalWithdrawal() { yield return new EnterPlayMode(); yield return MustStopWithoutWithdrawal("pending"); }
        [UnityTearDown] public IEnumerator Release()
        {
            if (fixture != null)
            {
                // EditMode特殊yield须直接交给测试框架，不能埋在嵌套IEnumerator中。
                var release = (IEnumerator)RejectionBoundaryR29Fixture.Call(fixture, "Release");
                while (release.MoveNext()) yield return release.Current;
            }
            else if (UnityEngine.Application.isPlaying) yield return new ExitPlayMode();
        }
    }
}
