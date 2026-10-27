#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        private Button CurrentAllocationConfirm(InteractionRequestProjection request)
        {
            if (request == null || request.Status != "open" || !request.VisibleToViewer ||
                request.AnsweringPlayerId != LocalPlayer || Request?.InteractionId != request.InteractionId ||
                Request.StateRevision != request.StateRevision || CurrentPaymentView() == null) return null;
            var matches = FindObjectsOfType<EffectDialogShellView>().Select(view =>
                view.TryGetResourceAllocation(request.InteractionId, out var button, out _) ? button : null)
                .Where(button => button != null && button.IsActive()).ToArray();
            Require(matches.Length <= 1, "同一请求出现多个活动数量视图，保留现场");
            return matches.SingleOrDefault();
        }

        private IEnumerator RequiredResourcePayment(InteractionRequestProjection initial)
        {
            Require(initial != null && initial.InteractionTypeId == ResourcePaymentChoiceEffectExecutor.InteractionTypeId,
                "付款驱动只能消费正式资源付款请求");
            var payments = Read(mobile, "specialActionInteraction") as SpecialActionInteractionUiCoordinator;
            Require(payments != null, "原通用付款协调器尚未装配");
            float deadline = Time.realtimeSinceStartup + 20f;
            Button confirm = null;
            var request = initial;
            int observations = 0;
            while (true)
            {
                request = Request;
                Require(request?.InteractionId == initial.InteractionId && request.OwnerEffectId == initial.OwnerEffectId &&
                    request.AnsweringPlayerId == initial.AnsweringPlayerId && request.AnsweringPlayerId == LocalPlayer,
                    "等待付款期间请求或玩家已变化，未点击旧视图");
                confirm = CurrentAllocationConfirm(request);
                var sourceReader = Read(mobile, "commandSubmission") as CommandSubmissionController;
                long sourceReadsBefore = sourceReader?.PaymentSourcePageReadSequence ?? 0;
                bool ready = payments.TryGetPaymentSources(request.InteractionId, out var sources);
                bool sourceReaderCalled = (sourceReader?.PaymentSourcePageReadSequence ?? 0) > sourceReadsBefore;
                Append("付款视图就绪观察.jsonl", new { Request = request.InteractionId, request.OwnerEffectId,
                    Player = LocalPlayer, Revision = request.StateRevision, Observation = ++observations,
                    OriginalViewBound = confirm != null, SourceQueryReady = ready, sources?.SnapshotId,
                    sources?.SourceCount, ConfirmAvailable = confirm != null && confirm.IsInteractable(),
                    SourceQueryFailure = payments.LastPaymentSourceQueryFailure,
                    SourcePageReadCalled = sourceReaderCalled,
                    SourcePageFailure = sourceReaderCalled ? sourceReader.LastPaymentSourcePageFailure : null,
                    SourcePageAuthenticatedViewer = sourceReaderCalled ? (int?)sourceReader.LastPaymentSourcePageViewer : null,
                    SourcePageQuery = sourceReaderCalled ? sourceReader.LastPaymentSourcePageQuery : null });
                if (ready && confirm != null && confirm.IsInteractable()) break;
                Require(Time.realtimeSinceStartup < deadline, "原付款视图、来源查询或原确认未就绪，保留现场");
                yield return Settle();
            }
            ObserveRequestStage(request);
            int before = commands.Count;
            var beforeVisible = CurrentPaymentView();
            yield return Press(confirm, "正常资源付款原确认");
            Require(commands.Count == before + 1, "原资源付款确认未准确提交一次命令");
            var command = Read(commands[before], "Command") as GameCommand;
            var receipt = Read(commands[before], "Result") as WorkflowSubmissionResult;
            Require(command?.Kind == GameCommandKind.AnswerInteraction && receipt?.CommandResult?.Succeeded == true,
                "原付款确认没有成功的正式回答回执");
            RecordRequestSelection(request, command.OptionIds.ToArray());
            RecordRequestConfirmation(request, before);
            var owner = State.EffectRuntime.EffectNodes.SingleOrDefault(node => node.EffectId == request.OwnerEffectId);
            Write("付款原确认-" + request.InteractionId + ".json", new { State.GameId, State.ContentPackHash,
                Request = request, Player = LocalPlayer, BeforeRevision = request.StateRevision,
                BeforeResources = beforeVisible?.Players.FirstOrDefault(p => p.PlayerId == LocalPlayer)?.Resources,
                CommandAndReceipt = commands[before], AfterRevision = State.EffectRuntime.StateRevision,
                AfterResources = CurrentPaymentView()?.Players.FirstOrDefault(p => p.PlayerId == LocalPlayer)?.Resources,
                PaymentOwnerStatus = owner?.Status, Payment = owner?.Payment,
                NextRequest = Request, OriginalPointerPath = true, Observations = observations,
                PaymentSettled = owner?.Status == EffectNodeStatus.Completed,
                EntireSourceActionSettled = continuationStages.Values.Where(row => row.Kind == "来源行动调用").ToArray() });
        }
    }
}
#endif
