#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        private sealed class ObservedAction
        {
            public string GameId, ContentHash, Scope, Key, Source, Target, RequestId, OwnerId, Window, BeforeDigest;
            public int Actor, Revision, SubmissionRevision, BeforeCommands, RequestRevision;
            public GameCommandKind Kind;
            public HashSet<string> BeforeNodes, BeforeInfluences;
            public string[] Roots, Locations;
            public object Receipt;
            public GameCommand Command;
            public bool StateObserved, Completed, Cover, KnownSupplyRejectionHandled;
            public RejectedActionBoundary BeforeBoundary;
        }
        private sealed class HistoricalDisplayDiagnostic
        {
            public EffectDiagnosticRuntimeState Diagnostic;
            public string SourceCommandId, SourceMainNodeId;
            public Dictionary<string, bool> Checks;
            public bool ResolvedUnrelated => Checks.Values.All(value => value);
        }
        private sealed class RejectedActionBoundary
        {
            public string GameId, ContentHash, Window, Digest, RuntimeStatus, LastFaultCode, LastFaultMessage, Phase;
            public string PendingEffectCommandId, CharacterUseCommandId, FlowMode;
            public int Revision, SubmissionRevision, LocalPlayer, CurrentPlayer, CommandCount, Round, ActionRound;
            public object CurrentRequest;
            public string[] PendingActionCommandIds;
            public EffectDiagnosticRuntimeState[] Diagnostics;
            public HistoricalDisplayDiagnostic[] HistoricalDisplayFacts;
            public Dictionary<string, bool> IdentityChecks, RuleChecks, UiChecks, DraftChecks, WithdrawalChecks;
            public bool IdentityClear => IdentityChecks.Values.All(value => value);
            public bool RuleClear => RuleChecks.Values.All(value => value);
            public bool UiClear => UiChecks.Values.All(value => value);
            public bool DraftClear => DraftChecks.Values.All(value => value);
            public bool CanWithdrawOriginalDraft => IdentityClear && RuleClear && UiClear;
            public bool CanContinueIndependentAction => CanWithdrawOriginalDraft && DraftClear;
        }
        private readonly HashSet<string> rejectedActionScopes = new HashSet<string>();
        private readonly HashSet<string> unavailableActionWindows = new HashSet<string>();
        private bool lastInfluenceActionHandledRejection;
        private readonly Dictionary<string, ObservedAction> pendingActionReceipts = new Dictionary<string, ObservedAction>();
        private readonly Dictionary<string, ObservedAction> verifiedActionReceipts = new Dictionary<string, ObservedAction>();
        private static bool ReceiptRequiredCoverage(string key) => new[] {
            "action:deploy", "action:dispatch", "action:explore", "action:granted-harvest", "action:harvest" }.Contains(key);
        private string ActionWindow(GameCommandKind kind, int actor) => State.GameId + ":" + State.ContentPackHash + ":" +
            State.EffectRuntime.ActiveMainNodeId + ":" + State.Round + ":" + State.ActionRound + ":" + actor + ":" + kind;
        private bool ActionUnavailableThisWindow(GameCommandKind kind, int actor) => unavailableActionWindows.Contains(ActionWindow(kind, actor));

        private ObservedAction BeginObservedAction(GameCommandKind kind, int actor, string source, string target,
            string key, InteractionRequestProjection request = null)
        {
            string scope = State.GameId + ":" + State.ContentPackHash + ":" + State.EffectRuntime.StateRevision + ":" + actor + ":" + kind;
            Require(!ActionUnavailableThisWindow(kind, actor), "本行动窗口该动作已有真实供应拒绝，不重复部署或调度");
            Require(!rejectedActionScopes.Contains(scope), "同一修订的该主行动已有准确拒绝，禁止重新点击掩盖首拒绝");
            ObserveActionReceipts();
            Require(!pendingActionReceipts.Values.Any(a => !a.Completed && a.Actor == actor && a.Kind == kind),
                "该行动已受理仍待真实结算，不能重复点击入口");
            var action = new ObservedAction { GameId = State.GameId, ContentHash = State.ContentPackHash, Scope = scope,
                Actor = actor, Kind = kind, Source = source ?? "", Target = target ?? "", Key = key,
                Revision = State.EffectRuntime.StateRevision, BeforeCommands = commands.Count, Cover = CoverageMode,
                Window = ActionWindow(kind, actor), BeforeDigest = RuleStateDigestService.Compute(State),
                BeforeNodes = new HashSet<string>(State.EffectRuntime.EffectNodes.Select(n => n.EffectId)),
                BeforeInfluences = new HashSet<string>(State.Map.Influences.Select(n => n.InfluenceId)),
                RequestId = request?.InteractionId, OwnerId = request?.OwnerEffectId, RequestRevision = request?.StateRevision ?? -1 };
            action.BeforeBoundary = CaptureRejectedActionBoundary(action);
            if (action.BeforeBoundary.Diagnostics.Length > 0)
            {
                // 只保留测试失败事实，不修改规则诊断，也不将可独立继续误报为整局通过。
                foreach (var diagnostic in action.BeforeBoundary.Diagnostics)
                {
                    string issue = "保留规则诊断：" + JsonConvert.SerializeObject(diagnostic);
                    if (!nonfatalUiFailures.Contains(issue)) nonfatalUiFailures.Add(issue);
                }
                Append("历史显示失败保留.jsonl", new { action.GameId, action.Revision, action.Window,
                    Diagnostics = action.BeforeBoundary.Diagnostics, WholeGameAcceptancePassed = false,
                    DiagnosticsCleared = false, HistoricalDisplayFacts = action.BeforeBoundary.HistoricalDisplayFacts });
            }
            return action;
        }

        private static bool AccurateActionReceipt(GameCommand command, WorkflowSubmissionResult result,
            GameCommandKind kind, int actor, string source, string target) =>
            !string.IsNullOrEmpty(command?.CommandId) && command.Kind == kind && command.PlayerId == actor &&
            (command.SourceId ?? "") == (source ?? "") && (command.TargetId ?? "") == (target ?? "") &&
            result?.CommandResult?.Succeeded == true && result.AppliedLocally;

        private void SaveObservedRejection(ObservedAction action, string reason, bool knownSupply)
        {
            if (rejectedActionScopes.Add(action.Scope))
                Write("主行动首个拒绝-" + (action.Command?.CommandId ?? action.Revision + "-" + action.Actor + "-" + action.Kind) + ".json",
                    new { action.GameId, action.ContentHash, action.Actor, action.Kind, action.Source, action.Target,
                        ExpectedRevision = action.Revision, action.SubmissionRevision, Reason = reason, AccurateCommandAndReceipt = action.Receipt,
                        NewRecords = commands.Skip(action.BeforeCommands).ToArray(), CurrentRevision = State.EffectRuntime.StateRevision,
                        Completed = false, RetryAllowed = false, TreatedAsNoTarget = false, KnownSupplyReason = knownSupply,
                        BeforeBoundary = action.BeforeBoundary, CurrentBoundary = CaptureRejectedActionBoundary(action) });
        }

        private void StopObservedAction(ObservedAction action, string reason)
        {
            SaveObservedRejection(action, reason, false);
            RecordRejectedActionBoundary(action, reason);
            throw new InvalidOperationException("主行动原确认未获准确完成依据：" + reason + "；已保首个回执，不重试或记完成");
        }

        private WorkflowSubmissionResult ReadObservedActionIdentity(ObservedAction action)
        {
            if (commands.Count != action.BeforeCommands + 1) StopObservedAction(action, "new_command_count_mismatch");
            action.Receipt = commands[action.BeforeCommands];
            action.Command = Read(action.Receipt, "Command") as GameCommand;
            var result = Read(action.Receipt, "Result") as WorkflowSubmissionResult;
            bool unique = action.Command != null && !commands.Take(action.BeforeCommands)
                .Any(r => (Read(r, "Command") as GameCommand)?.CommandId == action.Command.CommandId);
            bool revision = Read(action.Receipt, "BeforeRevision") is int before && before >= action.Revision;
            if (revision)
            {
                action.SubmissionRevision = (int)Read(action.Receipt, "BeforeRevision");
                action.Scope = action.GameId + ":" + action.ContentHash + ":" + action.SubmissionRevision + ":" + action.Actor + ":" + action.Kind;
            }
            bool request = action.RequestId == null || action.Command != null &&
                action.Command.Parameters.TryGetValue("interactionId", out var id) && id == action.RequestId &&
                action.Command.Parameters.TryGetValue("expectedRevision", out var rev) && rev == action.RequestRevision.ToString();
            if (!unique || !revision || !request || action.Command?.Kind != action.Kind || action.Command.PlayerId != action.Actor ||
                (action.Command.SourceId ?? "") != action.Source || (action.Command.TargetId ?? "") != action.Target)
                StopObservedAction(action, "receipt_identity_mismatch");
            return result;
        }

        private void ReadObservedActionReceipt(ObservedAction action)
        {
            var result = ReadObservedActionIdentity(action);
            if (!AccurateActionReceipt(action.Command, result, action.Kind, action.Actor, action.Source, action.Target))
                StopObservedAction(action, result?.CommandResult?.Succeeded == false
                    ? result.CommandResult.Validation?.Reason ?? "command_rejected" : "receipt_applied_mismatch");
            if (State.GameId != action.GameId || State.ContentPackHash != action.ContentHash ||
                State.EffectRuntime.StateRevision <= action.SubmissionRevision) StopObservedAction(action, "action_state_identity_or_revision_mismatch");
        }

        private static bool KnownSupplyRejection(WorkflowSubmissionResult result) => result?.CommandResult?.Succeeded == false &&
            !result.AppliedLocally && result.CommandResult.Validation?.ErrorCode == CommandErrorCode.InsufficientInfluence &&
            result.CommandResult.Validation.Reason == "insufficientsupply";

        private HistoricalDisplayDiagnostic DescribeHistoricalDisplayDiagnostic(EffectDiagnosticRuntimeState diagnostic, ObservedAction action)
        {
            var runtime = State.EffectRuntime;
            var matches = runtime.EffectNodes.Where(n => n.EffectId == diagnostic?.NodeId).ToArray();
            var node = matches.Length == 1 ? matches[0] : null;
            var contexts = new[] { node?.OperationContext, node?.ContinuationOperationContext }.Where(c => c != null).ToArray();
            var context = contexts.FirstOrDefault();
            string sourceCommand = context?.SourceCommandId, sourceMain = context?.SourceMainNodeId;
            var mains = runtime.MainNodes.Where(n => n.NodeId == sourceMain).ToArray();
            var related = runtime.EffectNodes.Where(n => !string.IsNullOrEmpty(sourceCommand) &&
                (n.OperationContext?.SourceCommandId == sourceCommand || n.ContinuationOperationContext?.SourceCommandId == sourceCommand)).ToArray();
            var ids = new HashSet<string>(related.Select(n => n.EffectId));
            return new HistoricalDisplayDiagnostic { Diagnostic = diagnostic, SourceCommandId = sourceCommand,
                SourceMainNodeId = sourceMain, Checks = new Dictionary<string, bool> {
                    ["presentation_record"] = diagnostic?.Code == "presentation_failed",
                    ["strictly_before_action_revision"] = diagnostic != null && diagnostic.StateRevision >= 0 && diagnostic.StateRevision < action.Revision,
                    ["unique_existing_owner"] = node != null && action.BeforeNodes.Contains(node.EffectId),
                    ["owner_completed_without_rule_failure"] = node?.Status == EffectNodeStatus.Completed && string.IsNullOrEmpty(node.FailureReason),
                    ["unambiguous_command_cause"] = !string.IsNullOrEmpty(sourceCommand) && contexts.All(c => c.SourceCommandId == sourceCommand && c.SourceMainNodeId == sourceMain),
                    ["different_command"] = !string.IsNullOrEmpty(sourceCommand) && sourceCommand != action.Command?.CommandId,
                    ["different_completed_main_window"] = !string.IsNullOrEmpty(sourceMain) && sourceMain != runtime.ActiveMainNodeId &&
                        mains.Length == 1 && mains[0].Status == EffectNodeStatus.Completed,
                    ["not_answer_owner"] = node != null && node.EffectId != action.OwnerId,
                    ["related_command_nodes_completed"] = related.Length > 0 && related.All(n => n.Status == EffectNodeStatus.Completed && string.IsNullOrEmpty(n.FailureReason)),
                    ["no_related_open_request"] = !runtime.InteractionRequests.Any(r => ids.Contains(r.OwnerEffectId) && r.Status == "open"),
                    ["no_related_unresolved_blocker"] = !runtime.Blockers.Any(b => !b.IsResolved && (ids.Contains(b.OwnerEffectId) || ids.Contains(b.TargetEffectId)))
                } };
        }

        private RejectedActionBoundary CaptureRejectedActionBoundary(ObservedAction action)
        {
            var influence = Read(mobile, "influenceActionPresenter") as InfluenceActionPresenter;
            var turn = Read(mobile, "turnActionPresenter") as TurnActionPresenter;
            var flow = Read(mobile, "flowCoordinator") as InteractionFlowCoordinator;
            var runtime = State.EffectRuntime;
            // 脱离状态对象复制六个持久化字段，防止后续原地改写把“提交前”证据一起改掉。
            var diagnostics = runtime.Diagnostics.Select(d => d == null ? null : new EffectDiagnosticRuntimeState {
                Code = d.Code, Message = d.Message, NodeId = d.NodeId, EventId = d.EventId,
                StateRevision = d.StateRevision, CommitSequence = d.CommitSequence }).ToArray();
            var history = diagnostics.Select(d => DescribeHistoricalDisplayDiagnostic(d, action)).ToArray();
            string digest = action.BeforeBoundary == null ? action.BeforeDigest : RuleStateDigestService.Compute(State);
            string pending = Read(mobile, "pendingEffectCommandId") as string, character = Read(mobile, "characterUseCommandId") as string;
            var before = action.BeforeBoundary;
            return new RejectedActionBoundary { GameId = State.GameId, ContentHash = State.ContentPackHash,
                Revision = runtime.StateRevision, SubmissionRevision = action.SubmissionRevision,
                Phase = State.Phase.ToString(), Round = State.Round, ActionRound = State.ActionRound,
                Window = ActionWindow(action.Kind, action.Actor), Digest = digest, RuntimeStatus = runtime.Status.ToString(),
                LastFaultCode = runtime.LastFaultCode, LastFaultMessage = runtime.LastFaultMessage,
                LocalPlayer = LocalPlayer, CurrentPlayer = State.CurrentPlayerId, CommandCount = commands.Count,
                PendingEffectCommandId = pending, CharacterUseCommandId = character, FlowMode = flow?.CurrentMode.ToString(),
                CurrentRequest = Request == null ? null : JsonConvert.DeserializeObject(JsonConvert.SerializeObject(Request)),
                PendingActionCommandIds = pendingActionReceipts.Values.Where(a => !a.Completed && a.Actor == action.Actor).Select(a => a.Command?.CommandId).ToArray(),
                Diagnostics = diagnostics, HistoricalDisplayFacts = history,
                IdentityChecks = new Dictionary<string, bool> {
                    ["one_new_command"] = commands.Count == action.BeforeCommands + 1,
                    ["unique_nonempty_command_id"] = !string.IsNullOrEmpty(action.Command?.CommandId) && !commands.Take(action.BeforeCommands)
                        .Any(r => (Read(r, "Command") as GameCommand)?.CommandId == action.Command.CommandId),
                    ["same_kind"] = action.Command?.Kind == action.Kind, ["same_actor"] = action.Command?.PlayerId == action.Actor,
                    ["same_source"] = action.Command != null && (action.Command.SourceId ?? "") == action.Source,
                    ["same_target"] = action.Command != null && (action.Command.TargetId ?? "") == action.Target,
                    ["receipt_before_revision_matches"] = Read(action.Receipt, "BeforeRevision") is int br && br == action.Revision,
                    ["receipt_after_revision_unchanged"] = Read(action.Receipt, "AfterRevision") is int ar && ar == action.Revision,
                    ["exact_known_unapplied_rejection"] = KnownSupplyRejection(Read(action.Receipt, "Result") as WorkflowSubmissionResult)
                },
                RuleChecks = new Dictionary<string, bool> {
                    ["same_game"] = State.GameId == action.GameId, ["same_content"] = State.ContentPackHash == action.ContentHash,
                    ["same_revision"] = runtime.StateRevision == action.Revision,
                    ["submission_revision_matches"] = action.SubmissionRevision == action.Revision,
                    ["same_action_window"] = ActionWindow(action.Kind, action.Actor) == action.Window,
                    ["same_phase"] = before == null || State.Phase.ToString() == before.Phase,
                    ["runtime_active"] = runtime.Status == EffectRuntimeStatus.Active,
                    ["no_fault_code"] = string.IsNullOrEmpty(runtime.LastFaultCode), ["no_fault_message"] = string.IsNullOrEmpty(runtime.LastFaultMessage),
                    ["no_faulted_effect_node"] = !runtime.EffectNodes.Any(n => n.Status == EffectNodeStatus.Faulted),
                    ["same_rule_digest"] = digest == action.BeforeDigest,
                    ["diagnostics_unchanged"] = before == null || JsonConvert.SerializeObject(diagnostics) == JsonConvert.SerializeObject(before.Diagnostics),
                    ["before_diagnostics_resolved_unrelated"] = before == null ? history.All(d => d.ResolvedUnrelated) : before.HistoricalDisplayFacts.All(d => d.ResolvedUnrelated),
                    ["current_diagnostics_resolved_unrelated"] = history.All(d => d.ResolvedUnrelated),
                    ["before_runtime_healthy"] = before == null || before.RuntimeStatus == EffectRuntimeStatus.Active.ToString() &&
                        string.IsNullOrEmpty(before.LastFaultCode) && string.IsNullOrEmpty(before.LastFaultMessage)
                },
                UiChecks = new Dictionary<string, bool> {
                    ["influence_presenter"] = influence != null, ["turn_presenter"] = turn != null, ["flow_coordinator"] = flow != null,
                    ["local_actor"] = LocalPlayer == action.Actor, ["current_actor"] = State.CurrentPlayerId == action.Actor,
                    ["no_current_request"] = Request == null, ["no_blocking_choice"] = !ActionPaymentPreparation.HasBlockingChoice(State, action.Actor),
                    ["no_open_business_request"] = !runtime.InteractionRequests.Any(r => r.Status == "open" && !r.IsInternalMainlineInteraction()),
                    ["no_pending_effect_command"] = string.IsNullOrEmpty(pending), ["no_pending_character_command"] = string.IsNullOrEmpty(character),
                    ["no_main_submission"] = turn != null && !turn.IsMainActionSubmissionInFlight,
                    ["no_deploy_submission"] = influence != null && !influence.IsDeploySubmissionInFlight,
                    ["no_dispatch_submission"] = influence != null && !influence.IsDispatchSubmissionInFlight,
                    ["no_pending_action_receipt"] = !pendingActionReceipts.Values.Any(a => !a.Completed && a.Actor == action.Actor),
                    ["main_action_available"] = new MainActionBudgetService().HasAvailableMainAction(State, action.Actor),
                    ["before_ui_clear"] = before == null || before.UiClear
                },
                DraftChecks = new Dictionary<string, bool> {
                    ["no_main_draft"] = turn != null && !turn.HasUnsubmittedMainActionDraft,
                    ["no_influence_confirmation"] = influence != null && !influence.HasPendingConfirmation,
                    ["influence_inactive"] = influence != null && !influence.IsActive,
                    ["choose_action_or_hidden"] = flow != null && (flow.CurrentMode == InteractionMode.ChooseAction || flow.CurrentMode == InteractionMode.Hidden)
                },
                WithdrawalChecks = new Dictionary<string, bool> {
                    ["deploy_action"] = action.Kind == GameCommandKind.DeployInfluence,
                    ["unsubmitted_deploy_draft"] = influence != null && influence.HasUnsubmittedMainActionDraft,
                    ["selecting_deploy_target"] = influence != null && influence.IsSelectingDeployTarget,
                    ["no_confirmation"] = influence != null && !influence.HasPendingConfirmation
                } };
        }

        private RejectedActionBoundary RecordRejectedActionBoundary(ObservedAction action, string stage)
        {
            var current = CaptureRejectedActionBoundary(action);
            Append("供应拒绝分项边界.jsonl", new { Stage = stage, DriverStage = this.stage,
                AtUtc = DateTime.UtcNow.ToString("o"), Frame = UnityEngine.Time.frameCount,
                action.Command, action.Actor, action.Kind, action.Window,
                BeforeBoundary = action.BeforeBoundary, CurrentBoundary = current,
                FailedIdentityChecks = current.IdentityChecks.Where(p => !p.Value).Select(p => p.Key).ToArray(),
                FailedRuleChecks = current.RuleChecks.Where(p => !p.Value).Select(p => p.Key).ToArray(),
                FailedUiChecks = current.UiChecks.Where(p => !p.Value).Select(p => p.Key).ToArray(),
                FailedDraftChecks = current.DraftChecks.Where(p => !p.Value).Select(p => p.Key).ToArray(),
                FailedWithdrawalChecks = current.WithdrawalChecks.Where(p => !p.Value).Select(p => p.Key).ToArray(),
                KnownGameDiagnosticsRetained = current.Diagnostics.Length > 0,
                WholeGameAcceptancePassed = false, Completed = false, DiagnosticsCleared = false });
            return current;
        }

        private IEnumerator ResolveObservedInfluenceAction(ObservedAction action)
        {
            var result = ReadObservedActionIdentity(action);
            if ((action.Kind != GameCommandKind.DeployInfluence && action.Kind != GameCommandKind.DispatchInfluence) || !KnownSupplyRejection(result))
            { FinishObservedAction(action); yield break; }
            SaveObservedRejection(action, "insufficientsupply", true);
            var boundary = RecordRejectedActionBoundary(action, "准确供应拒绝后原撤草稿前");
            if (!boundary.CanWithdrawOriginalDraft)
                StopObservedAction(action, "known_supply_rejection_state_or_pending_boundary_changed:" + string.Join(",",
                    boundary.IdentityChecks.Concat(boundary.RuleChecks).Concat(boundary.UiChecks).Where(p => !p.Value).Select(p => p.Key)));
            if (!boundary.DraftClear)
            {
                // 原部署按钮在已有未提交部署草稿时走原TurnActionPresenter撤回路径；不调用Cancel/Clear或改生产状态。
                if (!boundary.WithdrawalChecks.Values.All(value => value))
                    StopObservedAction(action, "known_supply_rejection_draft_lifecycle_not_verified");
                int count = commands.Count;
                yield return Press(hud.ActionPanelView.DeployButton, "供应拒绝后原部署按钮撤回未提交草稿");
                boundary = RecordRejectedActionBoundary(action, "原撤草稿控件返回后");
                if (commands.Count != count || !boundary.CanContinueIndependentAction)
                    StopObservedAction(action, "known_supply_rejection_original_ui_did_not_leave_draft");
            }
            unavailableActionWindows.Add(action.Window); action.KnownSupplyRejectionHandled = true;
            Append("真实供应拒绝与独立行动.jsonl", new { action.Command.CommandId, action.Kind, action.Actor, action.Source, action.Target,
                action.Revision, action.Window, Reason = "insufficientsupply", StateUnchanged = true, NoPendingResponse = true,
                OriginalDraftClosed = true, CommandCount = commands.Count, Completed = false, NewCompletionCount = 0,
                SameWindowRetryAllowed = false, OriginalCommandAndReceipt = action.Receipt,
                HistoricalDiagnosticsRetained = boundary.Diagnostics, WholeGameAcceptancePassed = false });
        }

        private bool FinishObservedAction(ObservedAction action)
        {
            ReadObservedActionReceipt(action);
            if (!string.IsNullOrEmpty(action.OwnerId)) action.Roots = new[] { action.OwnerId };
            else
            {
                var nodes = State.EffectRuntime.EffectNodes.Where(n => !action.BeforeNodes.Contains(n.EffectId) && n.PlayerId == action.Actor &&
                    (n.OperationContext?.SourceCommandId == action.Command.CommandId || n.ContinuationOperationContext?.SourceCommandId == action.Command.CommandId)).ToArray();
                var ids = new HashSet<string>(nodes.Select(n => n.EffectId));
                action.Roots = nodes.Where(n => !ids.Contains(n.ParentEffectId)).Select(n => n.EffectId).ToArray();
            }
            if (action.Roots.Length == 0) StopObservedAction(action, "accurate_effect_owner_missing");
            pendingActionReceipts.Add(action.Command.CommandId, action);
            Append("主行动受理与结清.jsonl", new { action.Command.CommandId, action.Kind, action.Actor, action.Key,
                Accepted = true, Completed = false, action.Roots, AccurateCommandAndReceipt = action.Receipt });
            ObserveActionReceipts();
            return action.Completed;
        }

        private bool ObservedActionState(ObservedAction action)
        {
            if (action.Kind == GameCommandKind.DeployInfluence)
                return State.Map.Influences.Any(n => n.PlayerId == action.Actor && n.SlotId == action.Target &&
                    !string.IsNullOrEmpty(n.InfluenceId) && !action.BeforeInfluences.Contains(n.InfluenceId));
            if (action.Kind == GameCommandKind.DispatchInfluence)
                return State.Map.Influences.Any(n => n.PlayerId == action.Actor && n.SlotId == action.Target &&
                    InfluenceIdentity.GetStableId(State, n) == action.Source);
            if (action.Kind == GameCommandKind.ExploreLocation)
                return State.Map.ResourceTokens.Any(n => n.LocationId == action.Target);
            bool answered = action.RequestId != null && State.EffectRuntime.InteractionRequests.Any(r =>
                r.InteractionId == action.RequestId && r.AnsweringPlayerId == action.Actor && r.Status == "answered");
            if (action.Key == "action:harvest")
                return answered && (State.FindPlayer(action.Actor)?.HasCollectedResourcesThisRound == true ||
                    (State.Phase == GamePhase.FinalScoring || State.Phase == GamePhase.GameOver) && State.FinalScoring?.IsResolved == true);
            if (action.Key == "action:explore" && action.Locations != null)
                return answered && action.Locations.Length > 0 && action.Locations.All(id => State.Map.ResourceTokens.Any(t => t.LocationId == id));
            return answered;
        }

        private void ObserveActionReceipts()
        {
            foreach (var action in pendingActionReceipts.Values.Where(a => !a.Completed).ToArray())
            {
                if (State.GameId != action.GameId || State.ContentPackHash != action.ContentHash)
                    StopObservedAction(action, "pending_action_scope_changed");
                var nodes = action.Roots.Select(id => State.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == id)).ToArray();
                if (nodes.Any(n => n == null || n.Status == EffectNodeStatus.Failed)) StopObservedAction(action, "accepted_action_effect_missing_or_failed");
                action.StateObserved |= ObservedActionState(action);
                if (!nodes.All(n => n.Status == EffectNodeStatus.Completed)) continue;
                if (!action.StateObserved) StopObservedAction(action, "completed_effect_without_actual_action_state");
                action.Completed = true; verifiedActionReceipts[action.Key] = action;
                if (action.Cover) Covered(action.Key);
                if (CampaignMode && (action.Kind == GameCommandKind.DeployInfluence || action.Kind == GameCommandKind.ExploreLocation ||
                    action.Key == "action:explore") && recordedPlayCommands.Add(action.Command.CommandId))
                {
                    if (action.Kind == GameCommandKind.DeployInfluence) PlayProgress(action.Actor).Deployments++;
                    else
                    {
                        PlayProgress(action.Actor).Explorations++;
                        if (StaticMapDefinitions.Resolve(State.MapId).Locations.Any(n => n.IsRedZone &&
                            (action.Locations ?? new[] { action.Target }).Contains(n.LocationId)))
                            PlayProgress(action.Actor).RedZoneReached = true;
                    }
                    Append("逐玩家行动计划.jsonl", new { State.GameId, State.Round, State.ActionRound,
                        action.Command.CommandId, action.Kind, action.Target, Progress = PlayProgress(action.Actor),
                        Evidence = "准确成功Applied回执、正式效果结清及实际行动状态均成立" });
                }
                Append("主行动受理与结清.jsonl", new { action.Command.CommandId, action.Kind, action.Actor,
                    action.Key, Accepted = true, Completed = true, action.Roots, action.StateObserved,
                    ActualRevision = State.EffectRuntime.StateRevision, AccurateCommandAndReceipt = action.Receipt });
            }
        }
    }
}
#endif
