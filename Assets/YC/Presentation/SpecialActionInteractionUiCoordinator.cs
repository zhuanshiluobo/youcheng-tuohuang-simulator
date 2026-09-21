using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Domain.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>
    /// 将已受理的特殊行动会话映射为地图交互。所有合法性仍由特殊行动命令处理器复核。
    /// </summary>
    internal sealed class SpecialActionInteractionUiCoordinator : IDisposable
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
                    if (genericInteractionId == generic.GetStableInteractionId() && genericInteractionRevision == generic.StateRevision && dialog.IsShowing) return true;
                    genericInteractionId = generic.GetStableInteractionId();
                    genericInteractionRevision = generic.StateRevision;
                    clearHighlights();
                    var resources = getState().FindPlayer(getLocalPlayerId()).Resources;
                    dialog.ShowCompositePayment(resources.Originium, resources.Iron, values =>
                    {
                        string prefix = "pay|" + values[0] + "|" + values[1] + "|";
                        string candidate = generic.CandidateIds.Find(id => id.StartsWith(prefix, StringComparison.Ordinal));
                        SubmitGenericCandidate(generic, candidate);
                    }, () => SubmitGenericAnswer(generic, null, true));
                    setPrompt("先选择并支付材料，支付成功后再选择城市移动目标。");
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
                        highlights.Add(candidate.IndexOf("location", StringComparison.OrdinalIgnoreCase) >= 0
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
                    dialog.ShowCollapsibleMapPrompt(
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
            dialog.ShowCollapsibleMapPrompt(
                title,
                "点击地图上 " + requiredCount + " 个高亮空槽位；再次点击已选槽位可撤回该选择。",
                title + "：选择 " + requiredCount + " 个影响力槽位");
            setPrompt("军工化区域：请选择 " + requiredCount + " 个高亮空槽位。");
        }

        private void RenderMobilization(string title)
        {
            var legalSlots = optionQuery.GetReplaceableInfluenceSlotIds(getState(), getLocalPlayerId());
            setHighlights(BuildInfluenceHighlights(legalSlots, WorkflowHighlightSemantic.EventInfluenceTarget));
            dialog.ShowCollapsibleMapPrompt(
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
            dialog.ShowCollapsibleMapPrompt(
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
            dialog.ShowCollapsibleMapPrompt(
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
            if (state == null || state.EffectRuntime == null || state.EffectRuntime.InteractionRequests == null) return false;
            for (int i = 0; i < state.EffectRuntime.InteractionRequests.Count; i++)
            {
                InteractionRequest candidate = state.EffectRuntime.InteractionRequests[i];
                if (candidate != null && candidate.Status == "open" && candidate.AnsweringPlayerId == getLocalPlayerId() &&
                    (candidate.InteractionTypeId.StartsWith("city_style.special_action", StringComparison.Ordinal) || candidate.InteractionTypeId == ResourcePaymentChoiceEffectExecutor.InteractionTypeId))
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
            selectedMilitarySlotIds.Clear();
            dialog.Hide();
            if (hadPresentation) clearHighlights();
        }
    }
}
