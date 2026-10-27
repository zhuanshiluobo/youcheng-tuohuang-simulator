#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Interactions;
using YC.Presentation.Workflows;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        private sealed class MapInputContext
        {
            internal string Game;
            internal int Player, Revision;
            internal InteractionRequestProjection Request;
        }
        private sealed class DriverMapInputReadinessException : DriverContinuationPendingException
        { internal DriverMapInputReadinessException(string message) : base(message) { } }

        // 只判定是否仍能消费原地图草稿；真正输入仍经原MapButton指针、路由和命令回执。
        internal static string MapFollowupDecision(string game, int player, int revision, InteractionRequestProjection expected,
            string currentGame, int currentPlayer, int currentRevision, InteractionRequestProjection current,
            bool originalControlSubmitted, bool actualInputReady)
        {
            if (originalControlSubmitted) return "submitted";
            bool same = game == currentGame && player == currentPlayer && revision == currentRevision &&
                (expected == null ? current == null : current != null && expected.AnsweringPlayerId == player &&
                    current.AnsweringPlayerId == player && expected.StateRevision == current.StateRevision &&
                    InteractionCommandIntent.SameContract(game, expected, currentGame, current));
            return !same ? "changed" : actualInputReady ? "ready" : "pending";
        }
        private string MapDecision(MapInputContext expected, bool submitted, bool ready) =>
            MapFollowupDecision(expected.Game, expected.Player, expected.Revision, expected.Request,
                State.GameId, LocalPlayer, State.EffectRuntime.StateRevision, Request, submitted, ready);
        private bool MapContextStillCurrent(MapInputContext expected) => MapDecision(expected, false, true) == "ready";

        private sealed class MapDraftObservation
        {
            internal IInteraction Owner;
            internal string Scope;
            internal string[] Targets;
        }
        private sealed class DriverMapDraftProgressException : Exception { }

        internal static bool MapDraftProgressed(bool sameContext, object beforeOwner, object afterOwner,
            string beforeScope, string afterScope, string[] beforeTargets, string[] afterTargets) =>
            sameContext && beforeOwner != null && ReferenceEquals(beforeOwner, afterOwner) &&
            !string.IsNullOrEmpty(beforeScope) && !string.IsNullOrEmpty(afterScope) && beforeScope != afterScope &&
            beforeTargets != null && beforeTargets.Length > 0 && afterTargets != null && afterTargets.Length > 0 &&
            !beforeTargets.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(afterTargets.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));

        private MapDraftObservation ObserveMapDraft(MapInputContext expected)
        {
            if (expected?.Request == null || expected.Request.StateRevision != expected.Revision ||
                !MapContextStillCurrent(expected) || State.EffectRuntime.Status == EffectRuntimeStatus.PausedFault ||
                GameplayHudFrame.EffectInputSuspended || MapDisplayController.SuppressMapClick) return null;
            var router = Read(mobile, "interactionRouter") as InteractionRouter;
            var owner = (Read(router, "registrations") as IEnumerable)?.Cast<object>()
                .Select(value => Read(value, "Interaction") as IInteraction).Where(value => value != null && value.IsActive)
                .OrderByDescending(value => value.Priority).FirstOrDefault();
            // 不跳过更高优先级的非地图宿主；也不调用BuildActivePresentation/BuildPresentation刷新草稿。
            if (!(owner is IMapConfirmationScope scope) || (Read(owner, "CanSubmit") as Func<bool>)?.Invoke() == false)
                return null;
            string key = scope.MapConfirmationScope;
            if (string.IsNullOrEmpty(key)) return null;
            var pending = Read(router, "mapConfirmation");
            string pendingTarget = router.HasPendingMapConfirmation && (string)Read(pending, "ActionKey") == key
                ? (string)Read(pending, "TargetId") : null;
            var targets = FindObjectsOfType<MapButtonView>().Where(value => value.IsInteractive &&
                value.Image != null && value.Image.enabled && !string.IsNullOrEmpty(value.TargetId))
                .Select(value => value.TargetKind + ":" + value.TargetId +
                    (value.IsSelected && pendingTarget != value.TargetKind + ":" + value.TargetId ? "|selected" : "|available"))
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            // 同一点首击的PendingConfirmation不算已提交的多选草稿；只观察当前原控件资格和实际选中。
            return new MapDraftObservation { Owner = owner, Scope = key, Targets = targets };
        }
        private static object MapDraftEvidence(MapDraftObservation value) => value == null ? null : new {
            Owner = value.Owner.Id, OwnerType = value.Owner.GetType().FullName,
            OwnerInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Owner),
            value.Scope, value.Targets };

        private bool TryResumeMapDraftStep(Exception error, Stack<IEnumerator> stack)
        {
            if (!(error is DriverMapDraftProgressException)) return false;
            string oldStep = networkActiveStepId;
            if (PerformanceMode)
            {
                if (!stack.Any(value => value is PerformanceStepBoundary)) return false;
                while (stack.Count > 0)
                {
                    var current = stack.Pop(); (current as IDisposable)?.Dispose();
                    if (current is PerformanceStepBoundary) break;
                }
            }
            else
            {
                if (!NetworkMode || oldStep == null || !stack.Any(value => value is NetworkStepBoundary b && b.Id == oldStep))
                    return false;
                NetworkDiscardStep(stack, oldStep);
            }
            stack.Push(RereadAfterMapDraftProgress(oldStep));
            return true;
        }
        private IEnumerator RereadAfterMapDraftProgress(string oldStep)
        {
            // 丢弃整个旧选择步骤，避免外层把同request/revision的合法本地草稿推进再判成失败。
            // 不算命令或覆盖完成；下一次原RunActionStep/PerformanceStep自行重读本人正式候选。
            yield return null;
            yield return new WaitForEndOfFrame();
            Append("地图草稿推进后重新读取.jsonl", new { OldStep = oldStep, Current = ObserveSafely(),
                CommandResubmitted = false, CommandCompletionClaimed = false });
        }

        private bool MapConfirmationOwnerMatches(WorkflowHighlightTargetKind kind, string id)
        {
            var router = Read(mobile, "interactionRouter") as InteractionRouter;
            if (router == null || !router.HasPendingMapConfirmation || GameplayHudFrame.EffectInputSuspended || MapDisplayController.SuppressMapClick) return false;
            var pending = Read(router, "mapConfirmation");
            if ((string)Read(pending, "TargetId") != kind + ":" + id) return false;
            var host = (Read(router, "registrations") as IEnumerable)?.Cast<object>()
                .Select(r => Read(r, "Interaction") as IInteraction).Where(i => i != null && i.IsActive)
                .OrderByDescending(i => i.Priority).FirstOrDefault(i => i is IMapConfirmationScope);
            return (Read(host, "CanSubmit") as Func<bool>)?.Invoke() != false &&
                host is IMapConfirmationScope scope && !string.IsNullOrEmpty(scope.MapConfirmationScope) &&
                (string)Read(pending, "ActionKey") == scope.MapConfirmationScope;
        }
        private object MapOwnerObservation()
        {
            var router = Read(mobile, "interactionRouter");
            return (Read(router, "registrations") as IEnumerable)?.Cast<object>()
                .Select(r => Read(r, "Interaction") as IInteraction).Where(i => i != null && i.IsActive)
                .Select(i => new { i.Id, i.Priority, ConfirmationScope = (i as IMapConfirmationScope)?.MapConfirmationScope }).ToArray();
        }
        private void RequireMapReadiness(bool ready, MapInputContext expected, MapButtonView target, string reason)
        {
            if (ready) return;
            // 只把已核同一必答请求的实际输入失效交给现有三次有界重读；不吞其他InvalidOperationException。
            if (expected?.Request != null && !expected.Request.AllowDecline && expected.Request.MinSelections > 0 && MapContextStillCurrent(expected))
            {
                Append("地图输入就绪首次与重读.jsonl", new { Reason = reason, Expected = expected.Request,
                    Actual = Request, Target = target == null ? null : MapInputDiagnostics(target),
                    InputOwners = MapOwnerObservation(), ActualState = ObserveSafely(), CommandResubmitted = false });
                throw new DriverMapInputReadinessException(reason);
            }
            Require(false, reason);
        }
        private IEnumerator MapPressWithFreshConfirmation(InteractionRequestProjection request, WorkflowHighlightTargetKind kind, string id)
        {
            var expected = new MapInputContext { Game = State.GameId, Player = LocalPlayer,
                Revision = State.EffectRuntime.StateRevision, Request = GameStateCloneService.DeepClone(request) };
            int before = commands.Count;
            var first = FindObjectsOfType<MapButtonView>().SingleOrDefault(b => b.TargetKind == kind && b.TargetId == id);
            bool preselected = first != null && first.IsSelected && MapConfirmationOwnerMatches(kind, id);
            if (!preselected) yield return MapPress(kind, id, "正常入场/地图选择", expected);
            var target = FindObjectsOfType<MapButtonView>().SingleOrDefault(b => b.TargetKind == kind && b.TargetId == id);
            bool ready = target != null && target.IsInteractive && target.IsSelected && target.Image != null && target.Image.enabled &&
                MapConfirmationOwnerMatches(kind, id);
            var decision = MapDecision(expected, commands.Count > before, ready);
            Append("地图首击后重读.jsonl", new { Expected = expected.Request, Actual = Request, Decision = decision,
                OriginalCommandsBefore = before, OriginalCommandsAfter = commands.Count,
                NewOriginalCommands = commands.Skip(before).ToArray(), Target = target == null ? null : MapInputDiagnostics(target),
                InputOwners = MapOwnerObservation(), FirstClickSkippedForActualSelection = preselected,
                Current = ObserveSafely(), BlindSecondClick = false });
            // MapPress已等该控件准确原回执；已提交或正式上下文变化时不再点击旧目标。
            if (decision == "submitted" || decision == "changed") yield break;
            RequireMapReadiness(decision == "ready", expected, target, "同一必答地图请求首击后缺少当前合法预选/确认输入宿主");
            var draftBefore = ObserveMapDraft(expected);
            int beforeConfirm = commands.Count;
            yield return MapPress(kind, id, "正常地图二次确认", expected, true);
            var confirmation = MapDecision(expected, commands.Count > beforeConfirm, false);
            var draftAfter = confirmation == "pending" ? ObserveMapDraft(expected) : null;
            bool draftProgressed = confirmation == "pending" && MapDraftProgressed(MapContextStillCurrent(expected),
                draftBefore?.Owner, draftAfter?.Owner, draftBefore?.Scope, draftAfter?.Scope, draftBefore?.Targets, draftAfter?.Targets);
            Append("地图确认后原命令核对.jsonl", new { Expected = expected.Request, Actual = Request,
                Decision = draftProgressed ? "draft_progressed" : confirmation, Before = beforeConfirm, After = commands.Count,
                DraftBefore = MapDraftEvidence(draftBefore), DraftAfter = MapDraftEvidence(draftAfter),
                NewOriginalCommands = commands.Skip(beforeConfirm).ToArray(), CommandResubmitted = false });
            if (draftProgressed) throw new DriverMapDraftProgressException();
            if (confirmation == "pending") RequireMapReadiness(false, expected, target,
                "同一必答地图请求正常确认没有产生原命令，待有界重读实际入口");
        }
        private sealed class DriverContentDifferenceException : InvalidOperationException
        { internal DriverContentDifferenceException(string message) : base(message) { } }
        private sealed class DriverDeferredTerminalIssuesException : InvalidOperationException
        { internal DriverDeferredTerminalIssuesException(string message) : base(message) { } }
        private readonly HotseatUiValidationContinuationPolicy continuationPolicy = new HotseatUiValidationContinuationPolicy();
        private readonly Queue<int> deferredScreenshots = new Queue<int>();
        private readonly HashSet<string> submittedControlKeys = new HashSet<string>(StringComparer.Ordinal);
        private int deferredDifferenceCount, runtimeErrorsCaptured;
        private bool deferContentDifferencesRequested;
        private bool DeferContentDifferences => (NetworkMode || PerformanceMode) && deferContentDifferencesRequested;

        private void CheckContent(bool condition, string message)
        {
            if (condition) return;
            if (!DeferContentDifferences) { Require(false, message); return; }
            RecordDeferredDifference("内容或夹具预期差异", message, null);
        }
        private void RecordDeferredDifference(string category, string message, Exception original)
        {
            string key = category + ":" + message + ":" + (mobile == null ? null : Request?.InteractionTypeId);
            bool first = continuationPolicy.FirstIssue(key);
            if (first)
            {
                int id = ++deferredDifferenceCount;
                nonfatalUiFailures.Add(category + ":" + message);
                Write("延后差异首次-" + id.ToString("D4") + ".json", new {
                    AtUtc = DateTime.UtcNow.ToString("o"), Stage = stage, Category = category, Message = message,
                    OriginalException = original?.ToString(), Current = ObserveSafely(),
                    LastOriginalCommands = commands.Skip(Math.Max(0, commands.Count - 2)).ToArray(),
                    Passed = false, DeferredUntilTerminalOrNetworkBug = true, DirectSubmission = false });
                deferredScreenshots.Enqueue(id);
            }
            Append("延后合并差异.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Stage = stage,
                Category = category, Message = message, First = first, Passed = false, State = ObserveSafely() });
        }
        private IEnumerator CaptureDeferredDifferences()
        {
            while (deferredScreenshots.Count > 0)
            {
                int id = deferredScreenshots.Dequeue();
                yield return new WaitForEndOfFrame();
                SaveCapture("延后差异首次-" + id.ToString("D4"));
            }
        }
        private bool TryDeferDriverStep(Exception error, Stack<IEnumerator> stack)
        {
            if (TryResumeMapDraftStep(error, stack)) return true;
            // 只重读明确标记的夹具预期/未接页面；其他异常保持原失败路径，绝不全局吞异常。
            if (!DeferContentDifferences || !(error is DriverContentDifferenceException || error is DriverContinuationPendingException) ||
                networkActiveStepId == null || !stack.Any(value => value is NetworkStepBoundary b && b.Id == networkActiveStepId)) return false;
            var coordinator = networkTransport?.SimulationCoordinator;
            bool readable = NetworkTryReadPublication(out var publication);
            bool exact = readable && coordinator != null && NetworkSameBoundSession(coordinator.SessionOptions) &&
                publication.LocalPlayerId == networkBoundPlayerId && LocalPlayer == networkBoundPlayerId;
            bool protocolHealthy = string.IsNullOrEmpty(networkFault) && coordinator != null &&
                !coordinator.IsStopped && !coordinator.RequiresRecovery &&
                string.IsNullOrEmpty(networkTransport?.LocalSimulationConnectionRecoveryFailureCode);
            bool runtimeHealthy = readable && State.EffectRuntime.Status != EffectRuntimeStatus.PausedFault;
            string progress = !readable ? null : publication.GameId + ":" + publication.SimulationEpoch + ":" +
                publication.StateRevision + ":" + publication.LastAppliedSequence + ":" + Request?.InteractionId;
            RecordDeferredDifference(error is DriverMapInputReadinessException ? "地图输入就绪失效待有界重读" : "驱动预期过时或入口待识别", error.Message, error);
            if (!continuationPolicy.TryReread(progress, exact, protocolHealthy, runtimeHealthy))
            {
                Write("有限重读仍不可推进.json", new { Stage = stage, OriginalException = error.ToString(),
                    Current = ObserveSafely(), ExactIdentity = exact, ProtocolHealthy = protocolHealthy,
                    RuntimeHealthy = runtimeHealthy, Classification = error is DriverMapInputReadinessException
                        ? "同一必答地图请求有界重读后仍无合法输入入口" : "驱动未找到可操作原入口；不冒联机根因或内容通过", Passed = false });
                return false;
            }
            string oldStep = networkActiveStepId;
            NetworkDiscardStep(stack, oldStep);
            stack.Push(RereadAfterDeferredStep(oldStep));
            return true;
        }
        private IEnumerator RereadAfterDeferredStep(string oldStep)
        {
            // 若原控件已发命令，只等该原件结果，绝不重发；正常渲染后交回原RunActionStep重新读取。
            yield return CaptureDeferredDifferences();
            yield return NetworkUntil(() => networkCommands.Values.All(record => record.StepId != oldStep ||
                record.Result != null || record.RetiredByRecovery), "差异后等待已有原命令准确结果，不重发", 120f,
                settleAfter: false, resetOnFormalProgress: true);
            yield return null;
            yield return new WaitForEndOfFrame();
            Append("差异后重新读取原页面.jsonl", new { Stage = stage, OldStep = oldStep,
                Current = ObserveSafely(), CommandResubmitted = false, OriginalErrorRetained = true });
        }
        private string OriginalControlKey(string identity) => !DeferContentDifferences || mobile == null || session == null ? null :
            State.GameId + ":" + LocalPlayer + ":" + State.EffectRuntime.StateRevision + ":" + Request?.InteractionId + ":" + identity + ":" +
            JsonConvert.SerializeObject(new {
                CandidateSetVersion = Request?.CandidateSetVersion,
                Candidates = Request?.CandidateIds,
                Choices = new[] { "characterAbilityInteraction", "facilityInteraction", "specialActionInteraction" }
                    .Select(field => (Read(Read(mobile, field), "selectedCandidates") as IEnumerable)?.Cast<object>().Select(value => Convert.ToString(value)).ToArray()).ToArray(),
                Players = FindObjectsOfType<PlayerSelectionPageView>().Where(page => page.isActiveAndEnabled)
                    .Select(page => page.Draft.SelectedIds.ToArray()).ToArray(),
                Enterprises = FindObjectsOfType<EnterpriseSelectionDialogView>().Where(page => page.IsShowing)
                    .Select(page => page.Draft.SelectedIds.ToArray()).ToArray(),
                RouteLocations = Route?.Draft?.SelectedLocationIds, RouteIds = Route?.Draft?.ConfirmedRouteIds });
        private void CheckOriginalControlNotRepeated(string key)
        {
            if (key != null && submittedControlKeys.Contains(key))
                throw new DriverContinuationPendingException("同一规则版本与请求的原控件已提交，重新读取后仍无后继；禁止重复点击：" + key);
        }
        private void RememberSubmittedControl(string key, int commandsBefore)
        { if (key != null && commands.Count > commandsBefore) submittedControlKeys.Add(key); }
        private void RequireOriginalReceipt(int before, string description)
        {
            if (commands.Count <= before) throw new DriverContinuationPendingException(description + "没有产生原命令，重新读取原页，不代造回执");
            CheckContent(commands.Count == before + 1, description + "产生的原命令数量与夹具单条预期不同，保全部原件后继续核实际回执");
        }
        private bool TryDeferBusinessRejection(NetworkCommandRecord record, string gameId, string commandId,
            CommandErrorCode error, string reason)
        {
            var coordinator = networkTransport?.SimulationCoordinator;
            bool exact = record?.Command != null && record.Command.CommandId == commandId && gameId == networkBoundGameId &&
                record.Command.PlayerId == networkBoundPlayerId && coordinator != null &&
                NetworkSameBoundSession(coordinator.SessionOptions) && coordinator.SessionOptions.SimulationEpoch == record.SimulationEpoch &&
                !record.RetiredByRecovery && !record.RejectedByCompetition && !networkApplied.ContainsKey(commandId) &&
                record.Result?.AppliedLocally != true && (record.ContentRejected || record.StepId == networkActiveStepId) &&
                (!record.ContentRejected || record.ContentRejectionCode == error);
            bool healthy = coordinator != null && !coordinator.IsStopped && !coordinator.RequiresRecovery && string.IsNullOrEmpty(networkFault);
            if (!DeferContentDifferences || !HotseatUiValidationContinuationPolicy.CanDeferBusinessRejection(error.ToString(), exact, healthy)) return false;
            record.ContentRejected = true;
            record.ContentRejectionCode = error;
            RecordDeferredDifference("原命令业务拒绝待合并", error + ":" + reason, null);
            Append("业务拒绝原命令.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Original = record.Command,
                record.StepId, record.SimulationEpoch, ErrorCode = error, Reason = reason, Passed = false, Resubmitted = false });
            return true;
        }
    }
}
#endif
