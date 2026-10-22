using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Domain.Presentation;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>
    /// 将已受理的特殊行动会话映射为地图交互。所有合法性仍由特殊行动命令处理器复核。
    /// </summary>
    internal sealed class SpecialActionInteractionUiCoordinator : IDisposable, IInteractionRequestRenderer
    {
        private readonly Func<GameState> getState;
        private readonly Func<int> getLocalPlayerId;
        private readonly SpecialActionOptionQueryService optionQuery;
        private readonly SpecialActionChoiceDialog dialog;
        private readonly Action<IReadOnlyList<WorkflowHighlight>> setHighlights;
        private readonly Action clearHighlights;
        private readonly Action<GameCommand> submit;
        private readonly Action<string> setPrompt;
        private readonly List<string> selectedMilitarySlotIds = new List<string>();
        private string sessionId = string.Empty;
        private string renderedStep = string.Empty;
        private int renderedRemainingRepetitions = -1;
        private string renderedRouteId = string.Empty;
        private string inFlightCommandId = string.Empty;
        private string inFlightSessionId = string.Empty;
        private string genericInteractionId = string.Empty;
        private int genericInteractionRevision = -1;
        private readonly PaymentPageDraftBinding paymentDraft = new PaymentPageDraftBinding();
        private readonly List<int> paymentValues = new List<int>();
        private bool buildingPaymentPage;
        public GameplayExpansionUi ExpansionUi { private get; set; }
        public Action PaymentDraftStateChanged { private get; set; }
        public Func<bool> CanSubmitCommand { private get; set; }
        private string PaymentScope => (getState()?.GameId ?? string.Empty) + ":" + (getState()?.ContentPackHash ?? string.Empty) + ":" + getLocalPlayerId();
        public Func<IReadOnlyList<InteractionRequest>> GetVisibleRequests { private get; set; }
        public string Id => "effect.city-style.special-action";
        public int Priority => 300;
        public bool CanRender(InteractionRequestProjection request) => request != null &&
            request.VisibleToViewer && request.Status == "open" && request.AnsweringPlayerId == getLocalPlayerId() &&
            (request.InteractionTypeId.StartsWith("city_style.special_action", StringComparison.Ordinal) ||
             (request.InteractionTypeId == ResourcePaymentChoiceEffectExecutor.InteractionTypeId && request.AnswerSchema == "candidate_id"));
        public void Render(InteractionRequestProjection request)
        {
            if (CanRender(request)) Synchronize();
        }
        public void Clear() => ResetAndHide();
        public bool HasAuthoritativeRequest => TryGetGenericInteraction(out _);

        public string MapConfirmationScope
        {
            get
            {
                if (!string.IsNullOrEmpty(inFlightCommandId)) return string.Empty;
                if (TryGetGenericInteraction(out var request))
                    return request.GetStableInteractionId() + ":" + request.StateRevision + ":" + request.CandidateSetVersion;
                return TryGetPending(out var pending) ? pending.SessionId + ":" + pending.Step + ":" +
                    pending.RemainingRepetitions + ":" + selectedMilitarySlotIds.Count : string.Empty;
            }
        }

        public InteractionPresentation BuildMapPresentation()
        {
            if (string.IsNullOrEmpty(MapConfirmationScope)) return InteractionPresentation.Busy;
            IReadOnlyList<WorkflowHighlight> targets = Array.Empty<WorkflowHighlight>();
            if (TryGetGenericInteraction(out var request))
            {
                if (request.InteractionTypeId != ResourcePaymentChoiceEffectExecutor.InteractionTypeId)
                {
                    var highlights = new List<WorkflowHighlight>();
                    foreach (var id in request.CandidateIds)
                        highlights.Add(new WorkflowHighlight(id.Contains(":") ? WorkflowHighlightTargetKind.InfluenceSlot : WorkflowHighlightTargetKind.Location,
                            id, WorkflowHighlightSemantic.MoveTarget));
                    targets = highlights;
                }
            }
            else if (TryGetPending(out var pending))
            {
                switch (pending.Step)
                {
                    case SpecialActionPendingSteps.AwaitMilitaryTargets:
                        targets = BuildInfluenceHighlights(optionQuery.GetLegalInfluencePlacementSlotIds(getState(), getLocalPlayerId()), WorkflowHighlightSemantic.DeployTarget); break;
                    case SpecialActionPendingSteps.AwaitMobilizationTarget:
                        targets = BuildInfluenceHighlights(optionQuery.GetReplaceableInfluenceSlotIds(getState(), getLocalPlayerId()), WorkflowHighlightSemantic.EventInfluenceTarget); break;
                    case SpecialActionPendingSteps.AwaitFreeMoveTarget:
                        targets = BuildLocationHighlights(optionQuery.GetLegalFreeMoveTargetIds(getState(), getLocalPlayerId())); break;
                    case SpecialActionPendingSteps.AwaitRouteInfluence:
                        targets = BuildInfluenceHighlights(optionQuery.GetLegalRouteInfluenceSlotIds(getState(), getLocalPlayerId(), pending.TraversedRouteId), WorkflowHighlightSemantic.DeployTarget); break;
                }
            }
            return new InteractionPresentation(targets, string.Empty, InteractionMode.Busy);
        }

        public SpecialActionInteractionUiCoordinator(
            Func<GameState> getState,
            Func<int> getLocalPlayerId,
            Func<RectTransform> getCanvas,
            GameplayDialogRegistry dialogRegistry,
            SpecialActionOptionQueryService optionQuery,
            Action<IReadOnlyList<WorkflowHighlight>> setHighlights,
            Action clearHighlights,
            Action<GameCommand> submit,
            Action<string> setPrompt)
        {
            this.getState = getState ?? throw new ArgumentNullException(nameof(getState));
            this.getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
            this.optionQuery = optionQuery ?? throw new ArgumentNullException(nameof(optionQuery));
            this.setHighlights = setHighlights ?? throw new ArgumentNullException(nameof(setHighlights));
            this.clearHighlights = clearHighlights ?? throw new ArgumentNullException(nameof(clearHighlights));
            this.submit = submit ?? throw new ArgumentNullException(nameof(submit));
            this.setPrompt = setPrompt ?? throw new ArgumentNullException(nameof(setPrompt));
            dialog = new SpecialActionChoiceDialog(
                dialogRegistry ?? throw new ArgumentNullException(nameof(dialogRegistry)),
                getCanvas ?? throw new ArgumentNullException(nameof(getCanvas)));
        }

        public bool IsActive
        {
            get
            {
                if (TryGetGenericInteraction(out _)) return true;
                PendingSpecialActionState ignored;
                return TryGetPending(out ignored);
            }
        }

        public bool IsBlockingMapInteraction
        {
            get
            {
                InteractionRequest generic;
                if (TryGetGenericInteraction(out generic)) return true;
                PendingSpecialActionState pending;
                return TryGetPending(out pending) && pending.Step != SpecialActionPendingSteps.AwaitMoveEvent;
            }
        }

        public bool Synchronize()
        {
            InteractionRequest generic;
            if (TryGetGenericInteraction(out generic))
            {
                if (generic.InteractionTypeId == ResourcePaymentChoiceEffectExecutor.InteractionTypeId)
                {
                    if (!string.IsNullOrEmpty(inFlightCommandId)) return true;
                    var paymentIdentityChanged = paymentDraft.Bind(PaymentScope, generic);
                    if (generic.StateRevision != paymentDraft.Revision) return true;
                    if (paymentIdentityChanged)
                    {
                        paymentValues.Clear();
                        paymentValues.AddRange(paymentDraft.InitialCost);
                    }
                    else if (!paymentDraft.CanChange && paymentDraft.AuthoritativeCost != null)
                    { paymentValues.Clear(); paymentValues.AddRange(paymentDraft.AuthoritativeCost); }
                    if (genericInteractionId == generic.GetStableInteractionId() && genericInteractionRevision == generic.StateRevision && dialog.IsShowing)
                    { TrySubmitPaymentDraft(generic); return true; }
                    genericInteractionId = generic.GetStableInteractionId();
                    genericInteractionRevision = generic.StateRevision;
                    clearHighlights();
                    var prompt = ExpansionUi?.ResolveGameplayText(generic.Presentation?.Prompt);
                    if (prompt == null) throw new InvalidOperationException("付款选择请求缺少正式提示引用。");
                    var labels = new List<string>();
                    foreach (var key in PaymentPageDraftBinding.ResourceLabelKeys)
                    {
                        var label = ExpansionUi?.ResolveGameplayText(new GameplayTextRef(key));
                        if (label == null) throw new InvalidOperationException("付款资源行缺少正式文案引用：" + key);
                        labels.Add(label.ToPlainText());
                    }
                    buildingPaymentPage = true;
                    try
                    {
                        dialog.ShowPaymentDraft(prompt.ToPlainText(), labels, paymentDraft.Maximums, paymentValues,
                            values =>
                            {
                                if (!PaymentRequestCurrent(generic)) return;
                                paymentValues.Clear(); paymentValues.AddRange(values);
                                paymentDraft.SetDesired(values);
                                PaymentDraftStateChanged?.Invoke();
                                if (!buildingPaymentPage) TrySubmitPaymentDraft(generic);
                            }, valid =>
                            {
                                if (!PaymentRequestCurrent(generic)) return;
                                if (paymentDraft.SetInputValid(valid)) PaymentDraftStateChanged?.Invoke();
                            }, () => PaymentRequestCurrent(generic), () => paymentDraft.CanConfirm, () => paymentDraft.CanChange,
                            values => paymentDraft.Match(values) != null, values =>
                            {
                                if (!PaymentRequestCurrent(generic) || !paymentDraft.CanConfirm) return;
                                var candidate = paymentDraft.Match(values);
                                if (candidate == paymentDraft.PreparedCandidate) SubmitGenericCandidate(generic, candidate);
                            }, generic.AllowDecline ? (Action)(() => SubmitGenericAnswer(generic, null, true)) : null);
                    }
                    finally { buildingPaymentPage = false; }
                    setPrompt(prompt.ToPlainText());
                    PaymentDraftStateChanged?.Invoke();
                    TrySubmitPaymentDraft(generic);
                    return true;
                }
                genericInteractionId = generic.GetStableInteractionId();
                genericInteractionRevision = generic.StateRevision;
                clearHighlights();
                var highlights = new List<WorkflowHighlight>();
                if (generic.CandidateIds != null)
                {
                    for (int i = 0; i < generic.CandidateIds.Count; i++)
                    {
                        string candidate = generic.CandidateIds[i];
                        highlights.Add(!candidate.Contains(":")
                            ? new WorkflowHighlight(WorkflowHighlightTargetKind.Location, candidate, WorkflowHighlightSemantic.MoveTarget)
                            : new WorkflowHighlight(WorkflowHighlightTargetKind.InfluenceSlot, candidate, WorkflowHighlightSemantic.DeployTarget));
                    }
                }
                setHighlights(highlights.AsReadOnly());
                setPrompt("请选择高亮的特殊行动目标。");
                dialog.Hide();
                return true;
            }
            PendingSpecialActionState pending;
            if (!TryGetPending(out pending))
            {
                ResetAndHide();
                return false;
            }

            if (!string.IsNullOrEmpty(inFlightSessionId) &&
                !string.Equals(inFlightSessionId, pending.SessionId, StringComparison.Ordinal))
            {
                ClearSubmissionInFlight();
            }

            if (pending.Step == SpecialActionPendingSteps.AwaitMoveEvent)
            {
                sessionId = pending.SessionId;
                renderedStep = pending.Step;
                renderedRemainingRepetitions = pending.RemainingRepetitions;
                renderedRouteId = pending.TraversedRouteId ?? string.Empty;
                selectedMilitarySlotIds.Clear();
                dialog.Hide();
                clearHighlights();
                return false;
            }

            var changed = !string.Equals(sessionId, pending.SessionId, StringComparison.Ordinal) ||
                          !string.Equals(renderedStep, pending.Step, StringComparison.Ordinal) ||
                          renderedRemainingRepetitions != pending.RemainingRepetitions ||
                          !string.Equals(
                              renderedRouteId,
                              pending.TraversedRouteId ?? string.Empty,
                              StringComparison.Ordinal);
            if (changed)
            {
                sessionId = pending.SessionId;
                renderedStep = pending.Step;
                renderedRemainingRepetitions = pending.RemainingRepetitions;
                renderedRouteId = pending.TraversedRouteId ?? string.Empty;
                selectedMilitarySlotIds.Clear();
            }

            if (changed || !dialog.IsShowing)
            {
                Render(pending);
            }

            return true;
        }

        public bool TryHandleInfluenceSlotClicked(string slotId)
        {
            InteractionRequest generic;
            if (TryGetGenericInteraction(out generic))
            {
                SubmitGenericCandidate(generic, slotId);
                return true;
            }
            PendingSpecialActionState pending;
            if (!TryGetPending(out pending) || pending.Step == SpecialActionPendingSteps.AwaitMoveEvent)
            {
                return false;
            }

            if (RejectWhileSubmissionInFlight(pending))
            {
                return true;
            }

            switch (pending.Step)
            {
                case SpecialActionPendingSteps.AwaitMilitaryTargets:
                    HandleMilitarySlotClicked(pending, slotId);
                    break;
                case SpecialActionPendingSteps.AwaitMobilizationTarget:
                    HandleMobilizationSlotClicked(pending, slotId);
                    break;
                case SpecialActionPendingSteps.AwaitRouteInfluence:
                    HandleRouteSlotClicked(pending, slotId);
                    break;
                case SpecialActionPendingSteps.AwaitFreeMoveTarget:
                    setPrompt("请点击高亮地点，完成免费城市移动。");
                    break;
                default:
                    setPrompt("当前特殊行动不接受影响力槽位选择。");
                    break;
            }

            return true;
        }

        public bool TryHandleLocationClicked(string locationId)
        {
            InteractionRequest generic;
            if (TryGetGenericInteraction(out generic))
            {
                SubmitGenericCandidate(generic, locationId);
                return true;
            }
            PendingSpecialActionState pending;
            if (!TryGetPending(out pending) || pending.Step == SpecialActionPendingSteps.AwaitMoveEvent)
            {
                return false;
            }

            if (RejectWhileSubmissionInFlight(pending))
            {
                return true;
            }

            if (pending.Step != SpecialActionPendingSteps.AwaitFreeMoveTarget)
            {
                setPrompt("当前特殊行动不接受地点选择。");
                return true;
            }

            var legalTargets = optionQuery.GetLegalFreeMoveTargetIds(getState(), getLocalPlayerId());
            if (string.IsNullOrEmpty(locationId) || !legalTargets.Contains(locationId))
            {
                setPrompt("请选择一个高亮的免费移动目标地点。");
                return true;
            }

            var command = CreateResolveCommand(pending);
            command.TargetId = locationId;
            command.Parameters[UseSpecialActionCommandHandler.TargetLocationIdParameter] = locationId;
            SubmitPendingCommand(pending, command);
            return true;
        }

        public bool TryHandleEscape()
        {
            InteractionRequest generic;
            if (TryGetGenericInteraction(out generic))
            {
                if (generic.InteractionTypeId == ResourcePaymentChoiceEffectExecutor.InteractionTypeId && generic.AllowDecline)
                {
                    SubmitGenericAnswer(generic, null, true);
                    return true;
                }
                setPrompt("特殊行动选择已开放，必须提交一个合法目标。");
                return true;
            }
            PendingSpecialActionState pending;
            if (!TryGetPending(out pending) || pending.Step == SpecialActionPendingSteps.AwaitMoveEvent)
                return false;
            if (RejectWhileSubmissionInFlight(pending)) return true;
            if (selectedMilitarySlotIds.Count > 0)
            {
                selectedMilitarySlotIds.Clear();
                Render(pending);
                setPrompt("已清空尚未提交的槽位选择，请重新选择高亮目标。");
                return true;
            }
            setPrompt("当前特殊行动已生效，必须完成后续选择；不能取消已结算的步骤。");
            return true;
        }

        public void NotifyCommandSettled(string commandId)
        {
            if (string.IsNullOrEmpty(inFlightCommandId) ||
                (!string.IsNullOrEmpty(commandId) &&
                 !string.Equals(inFlightCommandId, commandId, StringComparison.Ordinal)))
            {
                return;
            }

            ClearSubmissionInFlight();
        }

        public void NotifyPaymentDraftSettled(string commandId, bool applied)
        {
            if (paymentDraft.Settle(commandId, applied))
            {
                genericInteractionRevision = -1;
                PaymentDraftStateChanged?.Invoke();
            }
        }

        public bool CanInvokeSettlement(SettlementOptionView option) =>
            option == null || option.OwnerEffectId != paymentDraft.OwnerEffectId || paymentDraft.CanConfirm;

        private bool PaymentRequestCurrent(InteractionRequest request) =>
            !GameplayHudFrame.EffectInputSuspended && paymentDraft.IsScopeCurrent(PaymentScope) && TryGetGenericInteraction(out var current) &&
            current.InteractionTypeId == ResourcePaymentChoiceEffectExecutor.InteractionTypeId &&
            paymentDraft.IsIdentityCurrent(current) && current.StateRevision == paymentDraft.Revision && current.StateRevision == request.StateRevision;

        private void TrySubmitPaymentDraft(InteractionRequest request)
        {
            if (!PaymentRequestCurrent(request) || CanSubmitCommand?.Invoke() == false) return;
            var command = paymentDraft.PrepareCommand(request, getLocalPlayerId());
            if (command == null) return;
            PaymentDraftStateChanged?.Invoke();
            try { submit(command); }
            catch { NotifyPaymentDraftSettled(command.CommandId, false); throw; }
        }

        public void Dispose()
        {
            ResetAndHide();
        }

        private void HandleMilitarySlotClicked(PendingSpecialActionState pending, string slotId)
        {
            var legalSlots = optionQuery.GetLegalInfluencePlacementSlotIds(getState(), getLocalPlayerId());
            if (string.IsNullOrEmpty(slotId) || !legalSlots.Contains(slotId))
            {
                setPrompt("请选择一个高亮的空影响力槽位。");
                return;
            }

            if (selectedMilitarySlotIds.Contains(slotId))
            {
                selectedMilitarySlotIds.Remove(slotId);
            }
            else
            {
                selectedMilitarySlotIds.Add(slotId);
            }

            var requiredCount = optionQuery.GetRequiredMilitaryPlacementCount(getState(), getLocalPlayerId());
            if (selectedMilitarySlotIds.Count < requiredCount)
            {
                setPrompt("军工化区域：已选择 " + selectedMilitarySlotIds.Count + "/" + requiredCount + " 个槽位。");
                return;
            }

            var command = CreateResolveCommand(pending);
            command.OptionIds.AddRange(selectedMilitarySlotIds);
            command.Parameters[UseSpecialActionCommandHandler.InfluenceSlotIdsParameter] =
                string.Join(",", selectedMilitarySlotIds.ToArray());
            SubmitPendingCommand(pending, command);
        }

        private void HandleMobilizationSlotClicked(PendingSpecialActionState pending, string slotId)
        {
            var legalSlots = optionQuery.GetReplaceableInfluenceSlotIds(getState(), getLocalPlayerId());
            if (string.IsNullOrEmpty(slotId) || !legalSlots.Contains(slotId))
            {
                setPrompt("请选择一个高亮的对手影响力。");
                return;
            }

            var command = CreateResolveCommand(pending);
            command.TargetId = slotId;
            command.Parameters[UseSpecialActionCommandHandler.TargetInfluenceSlotIdParameter] = slotId;
            SubmitPendingCommand(pending, command);
        }

        private void HandleRouteSlotClicked(PendingSpecialActionState pending, string slotId)
        {
            var legalSlots = optionQuery.GetLegalRouteInfluenceSlotIds(
                getState(),
                getLocalPlayerId(),
                pending.TraversedRouteId);
            if (string.IsNullOrEmpty(slotId) || !legalSlots.Contains(slotId))
            {
                setPrompt("请选择经过航道上高亮的空影响力槽位。");
                return;
            }

            var command = CreateResolveCommand(pending);
            command.TargetId = slotId;
            command.Parameters[UseSpecialActionCommandHandler.RouteInfluenceSlotIdParameter] = slotId;
            SubmitPendingCommand(pending, command);
        }

        private void Render(PendingSpecialActionState pending)
        {
            clearHighlights();
            var definition = SpecialActionDatabase.Get(pending.SpecialActionId);
            var title = definition == null ? "特殊行动" : definition.Name;
            switch (pending.Step)
            {
                case SpecialActionPendingSteps.AwaitMilitaryTargets:
                    RenderMilitary(pending, title);
                    break;
                case SpecialActionPendingSteps.AwaitMobilizationTarget:
                    RenderMobilization(title);
                    break;
                case SpecialActionPendingSteps.AwaitFreeMoveTarget:
                    RenderFreeMove(pending, title);
                    break;
                case SpecialActionPendingSteps.AwaitRouteInfluence:
                    RenderRouteInfluence(pending, title);
                    break;
                default:
                    dialog.ShowMapSelection(
                        title,
                        "等待特殊行动的下一步结算。",
                        title + "：等待继续结算");
                    break;
            }
        }

        private void RenderMilitary(PendingSpecialActionState pending, string title)
        {
            var legalSlots = optionQuery.GetLegalInfluencePlacementSlotIds(getState(), getLocalPlayerId());
            setHighlights(BuildInfluenceHighlights(legalSlots, WorkflowHighlightSemantic.DeployTarget));
            var requiredCount = optionQuery.GetRequiredMilitaryPlacementCount(getState(), getLocalPlayerId());
            dialog.ShowMapSelection(
                title,
                "点击地图上 " + requiredCount + " 个高亮空槽位；再次点击已选槽位可撤回该选择。",
                title + "：选择 " + requiredCount + " 个影响力槽位");
            setPrompt("军工化区域：请选择 " + requiredCount + " 个高亮空槽位。");
        }

        private void RenderMobilization(string title)
        {
            var legalSlots = optionQuery.GetReplaceableInfluenceSlotIds(getState(), getLocalPlayerId());
            setHighlights(BuildInfluenceHighlights(legalSlots, WorkflowHighlightSemantic.EventInfluenceTarget));
            dialog.ShowMapSelection(
                title,
                "点击一个高亮的对手影响力；结算会先移除它，再尽量放置己方影响力。",
                title + "：选择对手影响力");
            setPrompt("动员配套体系：请选择一个高亮的对手影响力。");
        }

        private void RenderFreeMove(PendingSpecialActionState pending, string title)
        {
            var legalTargets = optionQuery.GetLegalFreeMoveTargetIds(getState(), getLocalPlayerId());
            setHighlights(BuildLocationHighlights(legalTargets));
            var segment = pending.SpecialActionId == SpecialActionDatabase.CompositePowerSystem
                ? "本次"
                : pending.RemainingRepetitions > 1 ? "第一段" : "第二段";
            dialog.ShowMapSelection(
                title,
                "点击一个高亮地点执行" + segment + "免费城市移动；抵达后仍须结算移动事件。",
                title + "：选择" + segment + "免费移动目标");
            setPrompt(title + "：请选择一个高亮的免费移动目标地点。");
        }

        private void RenderRouteInfluence(PendingSpecialActionState pending, string title)
        {
            var legalSlots = optionQuery.GetLegalRouteInfluenceSlotIds(
                getState(),
                getLocalPlayerId(),
                pending.TraversedRouteId);
            setHighlights(BuildInfluenceHighlights(legalSlots, WorkflowHighlightSemantic.DeployTarget));
            dialog.ShowMapSelection(
                title,
                "点击刚才经过航道上的一个高亮空槽位，放置 1 个己方影响力。",
                title + "：在经过航道放置影响力");
            setPrompt("复合动力系统：请在经过航道上选择一个高亮空槽位。");
        }

        private static IReadOnlyList<WorkflowHighlight> BuildInfluenceHighlights(
            IReadOnlyList<string> slotIds,
            WorkflowHighlightSemantic semantic)
        {
            var result = new List<WorkflowHighlight>();
            if (slotIds != null)
            {
                for (var i = 0; i < slotIds.Count; i++)
                {
                    result.Add(new WorkflowHighlight(
                        WorkflowHighlightTargetKind.InfluenceSlot,
                        slotIds[i],
                        semantic));
                }
            }

            return result.AsReadOnly();
        }

        private static IReadOnlyList<WorkflowHighlight> BuildLocationHighlights(IReadOnlyList<string> locationIds)
        {
            var result = new List<WorkflowHighlight>();
            if (locationIds != null)
            {
                for (var i = 0; i < locationIds.Count; i++)
                {
                    result.Add(new WorkflowHighlight(
                        WorkflowHighlightTargetKind.Location,
                        locationIds[i],
                        WorkflowHighlightSemantic.MoveTarget));
                }
            }

            return result.AsReadOnly();
        }

        private GameCommand CreateResolveCommand(PendingSpecialActionState pending)
        {
            var command = new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = getLocalPlayerId(),
                SourceId = pending.SessionId
            };
            command.Parameters[UseSpecialActionCommandHandler.SessionIdParameter] = pending.SessionId;
            return command;
        }

        private void SubmitPendingCommand(PendingSpecialActionState pending, GameCommand command)
        {
            if (pending == null || command == null || RejectWhileSubmissionInFlight(pending))
            {
                return;
            }

            inFlightCommandId = command.CommandId;
            inFlightSessionId = pending.SessionId ?? string.Empty;
            try
            {
                submit(command);
            }
            catch
            {
                ClearSubmissionInFlight();
                throw;
            }
        }

        private bool RejectWhileSubmissionInFlight(PendingSpecialActionState pending)
        {
            if (string.IsNullOrEmpty(inFlightCommandId))
            {
                return false;
            }

            if (pending == null ||
                !string.Equals(inFlightSessionId, pending.SessionId, StringComparison.Ordinal))
            {
                ClearSubmissionInFlight();
                return false;
            }

            setPrompt("特殊行动选择已发送给主机，正在等待确认，请勿重复提交。");
            return true;
        }

        private void ClearSubmissionInFlight()
        {
            inFlightCommandId = string.Empty;
            inFlightSessionId = string.Empty;
        }

        private void SubmitGenericCandidate(InteractionRequest request, string candidateId) => SubmitGenericAnswer(request, candidateId, false);

        private void SubmitGenericAnswer(InteractionRequest request, string candidateId, bool decline)
        {
            if (GameplayHudFrame.EffectInputSuspended) return;
            if (request == null || (decline ? !request.AllowDecline :
                string.IsNullOrEmpty(candidateId) || request.CandidateIds == null || !request.CandidateIds.Contains(candidateId)))
            {
                setPrompt("请选择高亮的特殊行动目标。");
                return;
            }
            if (!string.IsNullOrEmpty(inFlightCommandId))
            {
                setPrompt("特殊行动选择已发送，请等待主机确认。");
                return;
            }
            var command = EffectInteractionCommands.Answer(
                InteractionRequestProjector.ProjectForPlayer(request, getLocalPlayerId()),
                getLocalPlayerId(), decline ? null : new[] { candidateId }, decline);
            dialog.Hide();
            inFlightCommandId = command.CommandId;
            inFlightSessionId = request.GetStableInteractionId();
            try
            {
                submit(command);
            }
            catch
            {
                ClearSubmissionInFlight();
                throw;
            }
        }

        private bool TryGetGenericInteraction(out InteractionRequest request)
        {
            request = null;
            GameState state = getState();
            var requests = GetVisibleRequests == null
                ? VisibleInteractionRequestSource.Read(state, null, getLocalPlayerId()) : GetVisibleRequests();
            if (requests == null) return false;
            for (int i = 0; i < requests.Count; i++)
            {
                InteractionRequest candidate = requests[i];
                if (candidate != null && candidate.Status == "open" && candidate.AnsweringPlayerId == getLocalPlayerId() &&
                    (candidate.InteractionTypeId.StartsWith("city_style.special_action", StringComparison.Ordinal) ||
                     (candidate.InteractionTypeId == ResourcePaymentChoiceEffectExecutor.InteractionTypeId && candidate.AnswerSchema == "candidate_id")))
                {
                    if (!InteractionRequestProjector.ProjectForPlayer(candidate, getLocalPlayerId()).VisibleToViewer) continue;
                    request = candidate;
                    return true;
                }
            }
            return false;
        }

        private bool TryGetPending(out PendingSpecialActionState pending)
        {
            var state = getState();
            pending = state == null ? null : state.PendingSpecialAction;
            return pending != null &&
                   pending.IsValid(state) &&
                   pending.PlayerId == getLocalPlayerId();
        }

        private void ResetAndHide()
        {
            var hadPresentation = !string.IsNullOrEmpty(sessionId) || dialog.IsShowing;
            ClearSubmissionInFlight();
            sessionId = string.Empty;
            renderedStep = string.Empty;
            renderedRemainingRepetitions = -1;
            renderedRouteId = string.Empty;
            genericInteractionId = string.Empty;
            genericInteractionRevision = -1;
            paymentDraft.Clear();
            paymentValues.Clear();
            PaymentDraftStateChanged?.Invoke();
            selectedMilitarySlotIds.Clear();
            dialog.Hide();
            if (hadPresentation) clearHighlights();
        }
    }

    // 只绑定授权请求和主机回执；费用只用于本地完整候选匹配，不作为命令参数。
    internal sealed class PaymentPageDraftBinding
    {
        internal static readonly string[] CostFields = { "originium", "originiumShard", "iron", "pureOriginium", "goldVoucher" };
        internal static readonly string[] ResourceLabelKeys = { "core:resource.orirock.alt", "core:resource.originium_shard.alt",
            "core:resource.iron.alt", "core:resource.pure_originium.alt", "core:resource.gold_voucher.alt" };
        private readonly Dictionary<string, int[]> options = new Dictionary<string, int[]>(StringComparer.Ordinal);
        private string scope, interaction, candidateSet;
        private string paymentId, windowId;
        private int candidateVersion, revision = -1, submittedRevision;
        private string desired, attempted, pendingCommand, pendingCandidate, observedCandidate;
        private bool inputValid = true, receiptApplied, canChange;
        public string OwnerEffectId { get; private set; }
        public string PreparedCandidate { get; private set; }
        public int[] InitialCost { get; private set; } = new int[5];
        public int[] Maximums { get; private set; } = new int[5];
        public bool CanConfirm => inputValid && pendingCommand == null && desired != null &&
            desired == PreparedCandidate && desired == observedCandidate;
        public bool CanChange => canChange;
        public int Revision => revision;
        public bool IsScopeCurrent(string currentScope) => scope == currentScope;
        public IReadOnlyList<int> AuthoritativeCost => observedCandidate != null && options.TryGetValue(observedCandidate, out var cost) ? cost : null;

        internal static NormalizedValue Read(NormalizedValue value, string name) => value?.Kind == NormalizedValueKind.Object
            ? value.Properties.Find(entry => entry.Name == name)?.Value : null;
        private static string Text(NormalizedValue value) => value?.Kind == NormalizedValueKind.String ? value.StringValue : null;

        public bool IsIdentityCurrent(InteractionRequest request) => request != null &&
            request.GetStableInteractionId() == interaction && request.OwnerEffectId == OwnerEffectId &&
            request.CandidateSetId == candidateSet && request.CandidateSetVersion == candidateVersion;

        public bool Bind(string nextScope, InteractionRequest request)
        {
            var sameRequest = nextScope == scope && IsIdentityCurrent(request);
            if (sameRequest && request.StateRevision <= revision) return false;
            var host = Read(request.PromptParameters, "paymentDraft");
            var nextPaymentId = Text(Read(host, "paymentId")); var nextWindowId = Text(Read(host, "windowId"));
            var changed = !sameRequest || (!string.IsNullOrEmpty(paymentId) &&
                (paymentId != nextPaymentId || windowId != nextWindowId));
            if (changed)
            {
                Clear(); scope = nextScope; interaction = request.GetStableInteractionId();
                OwnerEffectId = request.OwnerEffectId; candidateSet = request.CandidateSetId; candidateVersion = request.CandidateSetVersion;
            }
            var projected = Read(request.PromptParameters, "paymentOptions");
            if (projected?.Kind != NormalizedValueKind.Array || projected.Items.Count == 0)
                throw new InvalidOperationException("付款选择投影缺少结构化费用选项。");
            options.Clear(); Maximums = new int[5];
            foreach (var value in projected.Items)
            {
                var id = Text(Read(value, "candidateId")); var cost = Read(value, "cost");
                if (string.IsNullOrEmpty(id) || request.CandidateIds == null || !request.CandidateIds.Contains(id) || options.ContainsKey(id))
                    throw new InvalidOperationException("付款选择投影包含非授权或重复候选。");
                var amounts = new int[5];
                for (var i = 0; i < amounts.Length; i++)
                {
                    var amount = Read(cost, CostFields[i]);
                    if (amount?.Kind != NormalizedValueKind.Integer || amount.IntegerValue < 0 || amount.IntegerValue > int.MaxValue)
                        throw new InvalidOperationException("付款选择投影包含无效完整费用。");
                    amounts[i] = (int)amount.IntegerValue; Maximums[i] = Math.Max(Maximums[i], amounts[i]);
                }
                options.Add(id, amounts);
            }
            if (options.Count != request.CandidateIds.Count) throw new InvalidOperationException("付款候选缺少完整结构化费用。");
            var change = Read(host, "canChange"); observedCandidate = Text(Read(host, "candidateId"));
            if (change?.Kind != NormalizedValueKind.Boolean || observedCandidate == null ||
                (observedCandidate.Length > 0 && (!options.ContainsKey(observedCandidate) ||
                 string.IsNullOrEmpty(Text(Read(host, "paymentId"))) || string.IsNullOrEmpty(Text(Read(host, "windowId"))))))
                throw new InvalidOperationException("付款草稿缺少主机身份或可改状态。");
            canChange = change.BooleanValue;
            paymentId = nextPaymentId; windowId = nextWindowId;
            revision = request.StateRevision;
            if (pendingCommand == null) PreparedCandidate = observedCandidate.Length == 0 ? null : observedCandidate;
            if (changed)
            {
                InitialCost = (int[])options[PreparedCandidate ?? Text(Read(projected.Items[0], "candidateId"))].Clone();
            }
            CompleteAcceptedDraft();
            return changed;
        }

        public string Match(IReadOnlyList<int> values)
        {
            if (values == null || values.Count != 5) return null;
            string result = null;
            foreach (var option in options)
            {
                var equal = true;
                for (var i = 0; i < 5; i++) if (values[i] != option.Value[i]) { equal = false; break; }
                if (!equal) continue;
                if (result != null) return null;
                result = option.Key;
            }
            return result;
        }

        public void SetDesired(IReadOnlyList<int> values)
        {
            var next = Match(values);
            if (next == desired) return;
            desired = next; attempted = null;
        }
        public bool SetInputValid(bool value) { if (inputValid == value) return false; inputValid = value; return true; }

        public GameCommand PrepareCommand(InteractionRequest request, int playerId)
        {
            if (!IsIdentityCurrent(request) || request.StateRevision != revision || !canChange || !inputValid ||
                pendingCommand != null || desired == null || (desired == PreparedCandidate && desired == observedCandidate) || desired == attempted) return null;
            var command = new GameCommand { Kind = GameCommandKind.PreparePaymentDraft, PlayerId = playerId };
            command.Parameters["ownerEffectId"] = OwnerEffectId; command.Parameters["interactionId"] = interaction;
            command.Parameters["candidateId"] = desired; command.Parameters["expectedRevision"] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            pendingCommand = command.CommandId; pendingCandidate = desired; attempted = desired; submittedRevision = revision; receiptApplied = false;
            return command;
        }

        public bool Settle(string commandId, bool applied)
        {
            if (pendingCommand == null || string.IsNullOrEmpty(commandId) || pendingCommand != commandId) return false;
            if (!applied) { pendingCommand = null; pendingCandidate = null; receiptApplied = false; }
            else { receiptApplied = true; CompleteAcceptedDraft(); }
            return true;
        }
        private void CompleteAcceptedDraft()
        {
            if (!receiptApplied || pendingCommand == null || revision <= submittedRevision || observedCandidate != pendingCandidate) return;
            PreparedCandidate = observedCandidate; pendingCommand = null; pendingCandidate = null; receiptApplied = false;
        }
        public void Clear()
        {
            scope = interaction = candidateSet = paymentId = windowId = OwnerEffectId = null; candidateVersion = 0; revision = -1;
            desired = attempted = pendingCommand = pendingCandidate = observedCandidate = PreparedCandidate = null;
            inputValid = true; canChange = receiptApplied = false; options.Clear(); InitialCost = new int[5]; Maximums = new int[5];
        }
    }
}
