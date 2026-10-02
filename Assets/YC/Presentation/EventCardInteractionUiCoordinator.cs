using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>
    /// 将迁移后的事件牌 InteractionRequest 接入真实移动城市 UI。
    /// 事件牌选项使用现有事件牌弹窗，影响力目标使用地图槽位高亮；两者都只提交
    /// 通用 AnswerInteraction，不再回退到旧 PendingCardSession 流程。
    /// </summary>
    internal sealed class EventCardInteractionUiCoordinator : InteractionBase, IInteractionRequestRenderer, IMapConfirmationScope, IDisposable
    {
        private readonly Func<GameState> getState;
        private readonly Func<int> getLocalPlayerId;
        private readonly Func<int, string> getPlayerDisplayName;
        private readonly EventChoiceDialog dialog;
        private readonly Action<IReadOnlyList<WorkflowHighlight>> setHighlights;
        private readonly Action clearHighlights;
        private readonly Action<GameCommand> submit;
        private readonly Action<string> setPrompt;
        private readonly List<string> selectedInfluenceSlots = new List<string>();

        private string renderedInteractionId = string.Empty;
        private int renderedRevision = -1;
        private bool needsRefresh;
        private string inFlightCommandId = string.Empty;
        public Func<IReadOnlyList<InteractionRequest>> GetVisibleRequests { private get; set; }

        public EventCardInteractionUiCoordinator(
            Func<GameState> getState,
            Func<int> getLocalPlayerId,
            Func<int, string> getPlayerDisplayName,
            EventChoiceDialog dialog,
            Action<IReadOnlyList<WorkflowHighlight>> setHighlights,
            Action clearHighlights,
            Action<GameCommand> submit,
            Action<string> setPrompt)
        {
            this.getState = getState ?? throw new ArgumentNullException(nameof(getState));
            this.getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
            this.getPlayerDisplayName = getPlayerDisplayName ?? throw new ArgumentNullException(nameof(getPlayerDisplayName));
            this.dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            this.setHighlights = setHighlights ?? throw new ArgumentNullException(nameof(setHighlights));
            this.clearHighlights = clearHighlights ?? throw new ArgumentNullException(nameof(clearHighlights));
            this.submit = submit ?? throw new ArgumentNullException(nameof(submit));
            this.setPrompt = setPrompt ?? throw new ArgumentNullException(nameof(setPrompt));
        }

        public override string Id => "event-card.interaction";

        public int RendererPriority => 300;

        int IInteractionRequestRenderer.Priority => RendererPriority;

        public override InteractionPriority Priority => InteractionPriority.PendingResolution;

        public override bool IsActive
        {
            get
            {
                InteractionRequest request;
                return TryGetRequest(out request);
            }
        }

        public bool CanRender(InteractionRequestProjection request)
        {
            return request != null && request.VisibleToViewer &&
                   request.AnsweringPlayerId == getLocalPlayerId() && request.Status == "open" &&
                   IsEventCardInteraction(request.InteractionTypeId);
        }

        public void Render(InteractionRequestProjection projection)
        {
            if (!CanRender(projection))
            {
                Clear();
                return;
            }

            var sameRequest = projection.InteractionId == renderedInteractionId;
            if (sameRequest && projection.StateRevision < renderedRevision) return;
            if (!sameRequest) { inFlightCommandId = string.Empty; selectedInfluenceSlots.Clear(); }
            if (!string.IsNullOrEmpty(inFlightCommandId)) return;
            if (sameRequest && !needsRefresh && projection.StateRevision == renderedRevision) return;

            renderedInteractionId = projection.InteractionId;
            renderedRevision = projection.StateRevision;
            needsRefresh = false;
            selectedInfluenceSlots.RemoveAll(id => projection.CandidateIds == null || !projection.CandidateIds.Contains(id));
            clearHighlights();

            if (projection.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId)
            {
                RenderOptionInteraction(projection);
            }
            else
            {
                dialog.Hide();
                setHighlights(BuildInfluenceHighlights(projection.CandidateIds));
                setPrompt("请选择事件牌要求的影响力目标。" +
                          (projection.MinSelections > 1
                              ? "还需选择 " + projection.MinSelections + " 个槽位。"
                              : string.Empty));
            }
        }

        public override InteractionResult OnLocationClicked(string locationId)
        {
            if (!IsActive) return InteractionResult.Passthrough;
            setPrompt("当前事件牌需要选择影响力槽位，请点击高亮槽位或事件牌选项。");
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            if (!IsActive) return InteractionResult.Passthrough;
            InteractionRequest request;
            if (!TryGetRequest(out request)) return InteractionResult.Passthrough;
            if (request.InteractionTypeId != EventCardEffectExecutor.InfluenceInteractionTypeId)
            {
                setPrompt("请先选择事件牌选项。");
                return InteractionResult.Consumed;
            }

            TryHandleInfluenceSlotClicked(request, slotId);
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnMobileCityClicked()
        {
            if (!IsActive) return InteractionResult.Passthrough;
            setPrompt("请先完成当前事件牌交互。");
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnEscape()
        {
            if (!IsActive) return InteractionResult.Passthrough;
            setPrompt("当前事件牌交互不能取消，请完成选择。");
            return InteractionResult.Consumed;
        }

        public override InteractionPresentation BuildPresentation()
        {
            if (!string.IsNullOrEmpty(inFlightCommandId)) return InteractionPresentation.Busy;
            InteractionRequest request;
            if (!TryGetRequest(out request)) return InteractionPresentation.Empty;

            if (request.InteractionTypeId == EventCardEffectExecutor.InfluenceInteractionTypeId)
            {
                return new InteractionPresentation(
                    BuildInfluenceHighlights(request.CandidateIds),
                    "请选择事件牌要求的影响力目标。",
                    InteractionMode.Busy,
                    true);
            }

            return new InteractionPresentation(
                new List<WorkflowHighlight>().AsReadOnly(),
                "请选择事件牌选项。",
                InteractionMode.Busy,
                false);
        }

        public override void Cancel()
        {
            Clear();
        }

        public override void NotifyCommandSettled(string commandId)
        {
            if (string.IsNullOrEmpty(inFlightCommandId) || string.IsNullOrEmpty(commandId) || commandId != inFlightCommandId)
            {
                return;
            }

            inFlightCommandId = string.Empty;
            needsRefresh = true;
        }

        public void Dispose()
        {
            Clear();
        }

        public void Clear()
        {
            renderedInteractionId = string.Empty;
            renderedRevision = -1;
            needsRefresh = false;
            inFlightCommandId = string.Empty;
            selectedInfluenceSlots.Clear();
            dialog.Hide();
            clearHighlights();
        }

        private void RenderOptionInteraction(InteractionRequestProjection projection)
        {
            string cardId = ReadString(projection.PromptParameters, "cardId");
            var card = EventCardDatabase.Get(cardId);
            if (card == null)
            {
                dialog.Hide();
                setPrompt("待处理事件牌不存在：" + cardId);
                return;
            }

            dialog.SetChoiceHighlightColor(UiTheme.CyanAccent);
            dialog.ShowEventCardOptions(
                card,
                string.Empty,
                new List<ExplorePaymentChoice>(),
                new Dictionary<string, int>(),
                getPlayerDisplayName,
                choiceIndex =>
                {
                    if (choiceIndex < 0 || choiceIndex >= card.ChoiceDescriptions.Count) return;
                    SubmitCandidates(
                        projection,
                        new List<string> { EventCardOptionIdFactory.Create(card.CardId, choiceIndex) });
                },
                null);
            setPrompt("请选择事件牌选项。");
        }

        public string MapConfirmationScope => !string.IsNullOrEmpty(inFlightCommandId) || !TryGetRequest(out var request)
            ? string.Empty : request.InteractionId + ":" + request.StateRevision + ":" + request.CandidateSetVersion + ":" + selectedInfluenceSlots.Count;

        private bool TryHandleInfluenceSlotClicked(InteractionRequest request, string slotId)
        {
            if (GameplayHudFrame.EffectInputSuspended) return false;
            if (request == null || string.IsNullOrEmpty(slotId) ||
                request.CandidateIds == null || !request.CandidateIds.Contains(slotId))
            {
                setPrompt("请选择高亮的事件牌影响力槽位。");
                return false;
            }

            if (!string.IsNullOrEmpty(inFlightCommandId))
            {
                setPrompt("事件牌回答正在提交，请等待主机确认。");
                return true;
            }

            if (selectedInfluenceSlots.Contains(slotId))
            {
                setPrompt("不能重复选择同一个影响力槽位。");
                return true;
            }

            selectedInfluenceSlots.Add(slotId);
            var required = request.MinSelections > 0 ? request.MinSelections : 1;
            if (selectedInfluenceSlots.Count < required)
            {
                setHighlights(BuildInfluenceHighlights(request.CandidateIds));
                setPrompt("还需选择 " +
                          (required - selectedInfluenceSlots.Count) + " 个事件牌影响力槽位。");
                return true;
            }

            SubmitCandidates(request, new List<string>(selectedInfluenceSlots));
            return true;
        }

        private void SubmitCandidates(InteractionRequestProjection projection, IList<string> selected)
        {
            if (GameplayHudFrame.EffectInputSuspended || projection == null || selected == null ||
                selected.Count < projection.MinSelections || selected.Count > projection.MaxSelections ||
                !string.IsNullOrEmpty(inFlightCommandId) || !TryGetRequest(out var current))
            {
                return;
            }
            var visible = InteractionRequestProjector.ProjectForPlayer(current, getLocalPlayerId());
            if (!CanRender(visible) || visible.InteractionId != projection.InteractionId ||
                visible.StateRevision != projection.StateRevision || visible.CandidateSetId != projection.CandidateSetId ||
                visible.CandidateSetVersion != projection.CandidateSetVersion) return;
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in selected)
                if (!distinct.Add(id) || visible.CandidateIds == null || !visible.CandidateIds.Contains(id)) return;

            var command = EffectInteractionCommands.Answer(projection, getLocalPlayerId(), selected);

            inFlightCommandId = command.CommandId;
            dialog.Hide();
            clearHighlights();
            try
            {
                submit(command);
            }
            catch
            {
                inFlightCommandId = string.Empty;
                needsRefresh = true;
                throw;
            }
        }

        private void SubmitCandidates(InteractionRequest request, IList<string> selected)
        {
            if (request == null) return;
            SubmitCandidates(
                InteractionRequestProjector.ProjectForPlayer(request, getLocalPlayerId()),
                selected);
        }

        private bool TryGetRequest(out InteractionRequest request)
        {
            request = null;
            var localPlayerId = getLocalPlayerId();
            var requests = GetVisibleRequests == null ? VisibleInteractionRequestSource.Read(getState(), null, localPlayerId) : GetVisibleRequests();
            for (var i = 0; requests != null && i < requests.Count; i++)
            {
                var candidate = requests[i];
                if (candidate != null && candidate.Status == "open" &&
                    candidate.AnsweringPlayerId == localPlayerId &&
                    IsEventCardInteraction(candidate.InteractionTypeId))
                {
                    request = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool IsEventCardInteraction(string interactionTypeId)
        {
            return interactionTypeId == EventCardEffectExecutor.OptionInteractionTypeId ||
                   interactionTypeId == EventCardEffectExecutor.InfluenceInteractionTypeId;
        }

        private List<WorkflowHighlight> BuildInfluenceHighlights(IList<string> candidateIds)
        {
            var highlights = new List<WorkflowHighlight>();
            if (candidateIds == null) return highlights;
            for (var i = 0; i < candidateIds.Count; i++)
            {
                var candidateId = candidateIds[i];
                if (string.IsNullOrEmpty(candidateId) || selectedInfluenceSlots.Contains(candidateId))
                {
                    continue;
                }

                highlights.Add(new WorkflowHighlight(
                    WorkflowHighlightTargetKind.InfluenceSlot,
                    candidateId,
                    WorkflowHighlightSemantic.EventInfluenceTarget));
            }

            return highlights;
        }

        private static string ReadString(NormalizedValue value, string name)
        {
            if (value == null || value.Kind != NormalizedValueKind.Object || value.Properties == null)
            {
                return string.Empty;
            }

            for (var i = 0; i < value.Properties.Count; i++)
            {
                var entry = value.Properties[i];
                if (entry != null && entry.Name == name && entry.Value != null &&
                    entry.Value.Kind == NormalizedValueKind.String)
                {
                    return entry.Value.StringValue ?? string.Empty;
                }
            }

            return string.Empty;
        }
    }
}
