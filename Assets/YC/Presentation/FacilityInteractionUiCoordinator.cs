using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Effects;
using YC.Domain.Economy;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>
    /// 展示设施通用 InteractionRequest。它只消费玩家投影并提交 AnswerInteraction，
    /// 不读取或修改设施旧 PendingCardSession，也不持有设施规则分支。
    /// </summary>
    internal sealed class FacilityInteractionUiCoordinator : IDisposable, IInteractionRequestRenderer
    {
        private readonly Func<GameState> getState;
        private readonly Func<int> getLocalPlayerId;
        private readonly FacilityEffectChoiceDialog dialog;
        private readonly Action<IReadOnlyList<WorkflowHighlight>> setHighlights;
        private readonly Action clearHighlights;
        private readonly Action<GameCommand> submit;
        private readonly Action<string> setPrompt;
        private string renderedInteractionId = string.Empty;
        private int renderedRevision = -1;
        private bool needsRefresh;
        private string inFlightCommandId = string.Empty;
        private readonly List<int> resourceDraft = new List<int>();
        private string inventoryKey = string.Empty;
        private readonly List<string> selectedCandidates = new List<string>();
        public Func<IReadOnlyList<InteractionRequest>> GetVisibleRequests { private get; set; }
        public Func<GameStateView> GetVisibleState { private get; set; }
        public GameplayExpansionUi ExpansionUi { private get; set; }
        private GameStateView presentationView;
        private InteractionRequestProjection presentationInput, presentationProjection;
        private string presentationGameId;
        private int presentationPlayerId = -1;

        public FacilityInteractionUiCoordinator(
            Func<GameState> getState,
            Func<int> getLocalPlayerId,
            Func<RectTransform> getCanvas,
            GameplayDialogRegistry dialogRegistry,
            Action<IReadOnlyList<WorkflowHighlight>> setHighlights,
            Action clearHighlights,
            Action<GameCommand> submit,
            Action<string> setPrompt)
        {
            this.getState = getState ?? throw new ArgumentNullException(nameof(getState));
            this.getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
            this.setHighlights = setHighlights ?? throw new ArgumentNullException(nameof(setHighlights));
            this.clearHighlights = clearHighlights ?? throw new ArgumentNullException(nameof(clearHighlights));
            this.submit = submit ?? throw new ArgumentNullException(nameof(submit));
            this.setPrompt = setPrompt ?? throw new ArgumentNullException(nameof(setPrompt));
            dialog = new FacilityEffectChoiceDialog(
                dialogRegistry ?? throw new ArgumentNullException(nameof(dialogRegistry)),
                (getCanvas ?? throw new ArgumentNullException(nameof(getCanvas)))());
        }

        public bool IsActive
        {
            get
            {
                InteractionRequestProjection ignored;
                return TryGetRequest(out ignored);
            }
        }

        public string Id => "effect.facility.renderer";
        public string MapConfirmationScope => string.IsNullOrEmpty(inFlightCommandId) && TryGetRequest(out var request)
            ? request.InteractionId + ":" + request.StateRevision + ":" + request.CandidateSetVersion + ":" + selectedCandidates.Count
            : string.Empty;
        public InteractionPresentation BuildMapPresentation() => string.IsNullOrEmpty(MapConfirmationScope) || !TryGetRequest(out var request)
            ? InteractionPresentation.Busy
            : new InteractionPresentation(BuildHighlights(request), string.Empty, InteractionMode.Busy);
        public int Priority => 305;
        public bool CanRender(InteractionRequestProjection request) => request != null && request.VisibleToViewer && request.Status == "open" &&
            request.AnsweringPlayerId == getLocalPlayerId() && request.InteractionTypeId == FacilityEntryEffectTypeIds.InteractionType;
        public void Clear() => ResetAndHide();
        public void Render(InteractionRequestProjection request)
        {
            if (!CanRender(request)) { Clear(); return; }
            RenderRequest(request);
        }

        public bool Synchronize()
        {
            InteractionRequestProjection request;
            if (!TryGetRequest(out request))
            {
                ResetAndHide();
                return false;
            }

            return RenderRequest(request);
        }

        private bool RenderRequest(InteractionRequestProjection request)
        {
            request = ProjectForPresentation(request);
            var interactionId = request.InteractionId;
            var sameRequest = interactionId == renderedInteractionId;
            if (sameRequest && request.StateRevision < renderedRevision) return true;
            if (!sameRequest)
            {
                selectedCandidates.Clear();
                resourceDraft.Clear();
                inFlightCommandId = string.Empty;
            }
            if (!string.IsNullOrEmpty(inFlightCommandId)) return true;
            var resources = getState()?.FindPlayer(getLocalPlayerId())?.Resources;
            var nextInventoryKey = resources == null ? string.Empty :
                string.Join(",", new[] { resources.Originium, resources.OriginiumShard, resources.Iron, resources.PureOriginium });
            if (sameRequest && !needsRefresh && request.StateRevision == renderedRevision && inventoryKey == nextInventoryKey) return true;
            selectedCandidates.RemoveAll(id => request.CandidateIds == null || !request.CandidateIds.Contains(id));
            if (selectedCandidates.Count > request.MaxSelections)
                selectedCandidates.RemoveRange(Math.Max(0, request.MaxSelections), selectedCandidates.Count - Math.Max(0, request.MaxSelections));
            renderedInteractionId = interactionId;
            renderedRevision = request.StateRevision;
            inventoryKey = nextInventoryKey;
            needsRefresh = false;
            clearHighlights();
            setHighlights(BuildHighlights(request));

            if (request.AnswerSchema == "resource_allocation")
            {
                ShowResourceAllocation(request);
            }
            else
            {
                ShowCandidateOptions(request, sameRequest);
            }

            setPrompt(RequestPrompt(request));
            return true;
        }

        public bool TryHandleLocationClicked(string locationId)
        {
            return TryHandleCandidateClicked(locationId);
        }

        public bool TryHandleInfluenceSlotClicked(string slotId)
        {
            return TryHandleCandidateClicked(slotId);
        }

        public bool TryHandleEscape()
        {
            if (!IsActive) return false;
            if (GameplayHudFrame.EffectInputSuspended) return true;
            if (TryGetRequest(out var request) && request.AllowDecline && IsCurrent(request))
            {
                SubmitAnswer(EffectInteractionCommands.Answer(request, getLocalPlayerId(), null, true));
                return true;
            }
            setPrompt("当前设施效果需要完成选择后才能继续。");
            return true;
        }

        public void NotifyCommandSettled(string commandId)
        {
            if (!string.IsNullOrEmpty(commandId) && commandId == inFlightCommandId)
            {
                inFlightCommandId = string.Empty;
                needsRefresh = true;
            }
        }

        public void Dispose()
        {
            ResetAndHide();
        }

        private bool TryHandleCandidateClicked(string candidateId)
        {
            InteractionRequestProjection request;
            if (!TryGetRequest(out request) || string.IsNullOrEmpty(candidateId) || request.CandidateIds == null)
            {
                return false;
            }

            if (!request.CandidateIds.Contains(candidateId))
            {
                var match = request.CandidateIds.Find(id => id == "explore:" + candidateId ||
                    id == "replace:" + candidateId || id == "deploy:" + candidateId);
                if (match == null) return false;
                candidateId = match;
            }

            SelectCandidate(request, candidateId);
            return true;
        }

        private void ShowCandidateOptions(InteractionRequestProjection request, bool preserveScroll = true)
        {
            var requestGameId = getState()?.GameId;
            var executeImmediately = EffectDialogSelectionSpec.IsExecutionChoice(request);
            bool Current()
            {
                if (requestGameId != getState()?.GameId || !IsCurrent(request)) return false;
                if (!executeImmediately) return true;
                var observedRevision = GetVisibleState?.Invoke()?.StateRevision ?? getState()?.EffectRuntime?.StateRevision;
                return request.StateRevision == renderedRevision &&
                    (!observedRevision.HasValue || observedRevision.Value == renderedRevision) &&
                    TryGetRequest(out var current) &&
                    InteractionCommandIntent.SameContract(requestGameId, request, getState()?.GameId, current);
            }
            var isBuild = request.PromptKey == "facility.entry.choose_additional_build";
            var supply = request.PromptParameters?.Properties.Find(value => value.Name == "isDefaultSupplySet")?.Value;
            var title = !isBuild ? dialog.Copy.FacilitySelectionTitle : supply?.BooleanValue == true
                ? dialog.Copy.BuildCandidateSupplyTitle : dialog.Copy.BuildCandidateAlternateTitle;
            var specification = new EffectDialogSelectionSpec
            {
                Request = request,
                ExecuteImmediately = executeImmediately,
                SelectedIds = selectedCandidates,
                Players = PlayerSelectionRequestAdapter.Build(request,getState(),getLocalPlayerId(),selectedCandidates),
                PlayerSelectionChanged = ids =>
                { selectedCandidates.Clear(); foreach (var id in PlayerSelectionRequestAdapter.CandidateIds(request,ids)) selectedCandidates.Add(id); },
                Label = candidateId => FormatCandidateLabel(candidateId, request),
                OptionNamePrefix = "Option ",
                IsCurrent = Current,
                Select = candidateId =>
                {
                    if (!Current()) return;
                    if (executeImmediately) SubmitCandidateAnswer(request, new List<string> { candidateId });
                    else SelectCandidate(request, candidateId);
                },
                Confirm = () => SubmitCandidateAnswer(request, new List<string>(selectedCandidates)),
                Cancel = null
            };
            if (UsesSelectionDocument(request) && ExpansionUi != null)
            {
                specification.ResolveText = ExpansionUi.ResolveGameplayText;
                specification.RenderText = ExpansionUi.RenderEnterpriseDetails;
                specification.PromptDocument = specification.ResolvePromptDocument();
                specification.CompatibilityPrompt = () => FormatPrompt(request.PromptKey);
            }
            dialog.ShowSelection(title, specification.PromptDocument == null ? FormatPrompt(request.PromptKey) : null,
                specification, preserveScroll);
        }

        private bool IsCurrent(InteractionRequestProjection request)
        {
            return request != null && string.IsNullOrEmpty(inFlightCommandId) && TryGetRequest(out var current) &&
                current.InteractionId == request.InteractionId && current.StateRevision == request.StateRevision &&
                current.CandidateSetId == request.CandidateSetId &&
                current.CandidateSetVersion == request.CandidateSetVersion;
        }

        private void ShowResourceAllocation(InteractionRequestProjection request)
        {
            var player = getState()?.FindPlayer(getLocalPlayerId());
            var resources = player == null ? new ResourceSet() : player.Resources;
            var isSale = request.CandidateIds != null && request.CandidateIds.Contains("choice.confirm");
            var labels = isSale
                ? new List<string> { "源岩", "源石碎片", "异铁", "至纯源石" }
                : new List<string> { "源岩", "源石碎片", "异铁" };
            int total = 5;
            if (request.CandidateIds != null) foreach (var candidate in request.CandidateIds)
                if (candidate.StartsWith("total:", StringComparison.Ordinal) && int.TryParse(candidate.Substring(6), out int requestedTotal)) total = requestedTotal;
            var declaredTotal = ItemTransferEffectExecutor.Value(request.PromptParameters, "totalAmount");
            if (!isSale && declaredTotal?.Kind == NormalizedValueKind.Integer) total = checked((int)declaredTotal.IntegerValue);
            var maximums = isSale
                ? new List<int> { resources.Originium, resources.OriginiumShard, resources.Iron, resources.PureOriginium }
                : new List<int> { total, total, total };
            var exactTotal = isSale ? -1 : total;
            var formalRequest = !isSale && request.PromptKey == "effect.resource.choose_amounts" ? request : null;
            var declaredRows = ItemTransferEffectExecutor.Value(formalRequest?.PromptParameters, "rowIds");
            var rowIds = declaredRows?.Kind == NormalizedValueKind.Array
                ? declaredRows.Items.Select(value => value.Kind == NormalizedValueKind.String ? value.StringValue : null).ToArray() : null;
            if (rowIds == null || !rowIds.SequenceEqual(new[] { "Originium", "OriginiumShard", "Iron" })) formalRequest = null;
            var declaredMaximums = ItemTransferEffectExecutor.Value(formalRequest?.PromptParameters, "rowMaximums");
            if (declaredMaximums?.Kind == NormalizedValueKind.Array && declaredMaximums.Items.Count == maximums.Count)
                maximums = declaredMaximums.Items.Select(value => checked((int)value.IntegerValue)).ToList();
            if (formalRequest != null && ExpansionUi != null)
                dialog.BindGameplayText(ExpansionUi.ResolveGameplayText, ExpansionUi.RenderEnterpriseDetails);
            var description = formalRequest?.Presentation?.Prompt != null || formalRequest?.Presentation?.Progress != null
                ? null : isSale ? dialog.Copy.SaleDescription : string.Format(dialog.Copy.ResourceAllocationDescriptionFormat, total);
            dialog.ShowResourceAllocation(formalRequest, formalRequest == null ? null : rowIds,
                dialog.Copy.ResourceAllocationTitle,
                description,
                labels,
                maximums,
                exactTotal,
                values => SubmitResourceAnswer(request, values, labels),
                isSale && request.CandidateIds.Contains("choice.skip")
                    ? (Action)(() => SubmitCandidateAnswer(request, new List<string> { "choice.skip" })) : null,
                resourceDraft.Count == 0 ? null : resourceDraft,
                values => { resourceDraft.Clear(); resourceDraft.AddRange(values); },
                () => IsCurrent(request),
                isSale ? new[] { ResourceSaleService.OriginiumUnitPrice, ResourceSaleService.OriginiumShardUnitPrice,
                    ResourceSaleService.IronUnitPrice, ResourceSaleService.PureOriginiumUnitPrice } : null);
        }

        private void SubmitCandidateAnswer(InteractionRequestProjection request, IList<string> selected)
        {
            if (GameplayHudFrame.EffectInputSuspended || !IsCurrent(request) || selected == null ||
                selected.Count < request.MinSelections || selected.Count > request.MaxSelections) return;
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in selected)
                if (!unique.Add(id) || request.CandidateIds == null || !request.CandidateIds.Contains(id)) return;
            var command = CreateAnswerCommand(request);
            for (var i = 0; i < selected.Count; i++) command.OptionIds.Add(selected[i]);
            SubmitAnswer(command);
        }

        private void SubmitResourceAnswer(
            InteractionRequestProjection request,
            IReadOnlyList<int> values,
            IReadOnlyList<string> labels)
        {
            if (GameplayHudFrame.EffectInputSuspended || !IsCurrent(request) || values == null) return;
            var resources = getState()?.FindPlayer(getLocalPlayerId())?.Resources;
            var isSale = request.CandidateIds != null && request.CandidateIds.Contains("choice.confirm");
            if (isSale)
            {
                var maximums = resources == null ? new int[4] : new[]
                    { resources.Originium, resources.OriginiumShard, resources.Iron, resources.PureOriginium };
                var total = 0;
                for (var i = 0; i < values.Count; i++)
                {
                    if (i >= maximums.Length || values[i] < 0 || values[i] > maximums[i])
                    { needsRefresh = true; RenderRequest(request); return; }
                    total += values[i];
                }
                if (total == 0) return;
            }
            var parts = new List<string>();
            var resourceKeys = new[] { "Originium", "OriginiumShard", "Iron", "PureOriginium" };
            for (var i = 0; i < values.Count && i < labels.Count && i < resourceKeys.Length; i++)
            {
                if (values[i] > 0)
                {
                    parts.Add(resourceKeys[i] + "=" + values[i].ToString(CultureInfo.InvariantCulture));
                }
            }

            var command = CreateAnswerCommand(request);
            command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] =
                string.Join(",", parts.ToArray());
            SubmitAnswer(command);
        }

        private GameCommand CreateAnswerCommand(InteractionRequestProjection request)
        {
            return EffectInteractionCommands.Answer(request, getLocalPlayerId(), null);
        }

        private void SubmitAnswer(GameCommand command)
        {
            if (!string.IsNullOrEmpty(inFlightCommandId)) return;
            inFlightCommandId = command.CommandId;
            dialog.Hide();
            clearHighlights();
            try { submit(command); }
            catch { inFlightCommandId = string.Empty; needsRefresh = true; throw; }
        }

        private bool TryGetRequest(out InteractionRequestProjection request)
        {
            request = null;
            var localPlayerId = getLocalPlayerId();
            var requests = GetVisibleRequests == null ? VisibleInteractionRequestSource.Read(getState(), null, localPlayerId) : GetVisibleRequests();
            for (var i = 0; requests != null && i < requests.Count; i++)
            {
                var candidate = requests[i];
                if (candidate != null && candidate.Status == "open" &&
                    candidate.InteractionTypeId == FacilityEntryEffectTypeIds.InteractionType &&
                    candidate.AnsweringPlayerId == localPlayerId)
                {
                    var projection = InteractionRequestProjector.ProjectForPlayer(candidate, localPlayerId);
                    if (!projection.VisibleToViewer) continue;
                    request = projection;
                    return true;
                }
            }

            return false;
        }

        private List<WorkflowHighlight> BuildHighlights(InteractionRequestProjection request)
        {
            var highlights = new List<WorkflowHighlight>();
            if (request.CandidateIds == null) return highlights;
            for (var i = 0; i < request.CandidateIds.Count; i++)
            {
                var candidate = request.CandidateIds[i] ?? string.Empty;
                var target = candidate;
                var semantic = WorkflowHighlightSemantic.EventInfluenceTarget;
                var targetKind = WorkflowHighlightTargetKind.InfluenceSlot;
                if (candidate.StartsWith("explore:", StringComparison.Ordinal))
                {
                    target = candidate.Substring("explore:".Length);
                    targetKind = WorkflowHighlightTargetKind.Location;
                    semantic = WorkflowHighlightSemantic.ExploreTarget;
                }
                else if (candidate.StartsWith("replace:", StringComparison.Ordinal) ||
                         candidate.StartsWith("deploy:", StringComparison.Ordinal))
                {
                    target = candidate.Substring(candidate.IndexOf(':') + 1);
                    semantic = WorkflowHighlightSemantic.DeployTarget;
                }
                else if (!candidate.Contains(":") && candidate.IndexOf('-', StringComparison.Ordinal) > 0)
                {
                    targetKind = WorkflowHighlightTargetKind.Location;
                    semantic = WorkflowHighlightSemantic.MoveTarget;
                }
                else if (!candidate.Contains(":"))
                {
                    continue;
                }

                highlights.Add(new WorkflowHighlight(targetKind, target, semantic));
            }

            return highlights;
        }

        private void SelectCandidate(InteractionRequestProjection request, string candidateId)
        {
            if (GameplayHudFrame.EffectInputSuspended || !IsCurrent(request) ||
                request.CandidateIds == null || !request.CandidateIds.Contains(candidateId)) return;
            if (request.MaxSelections <= 1)
            {
                if (!selectedCandidates.Remove(candidateId))
                { selectedCandidates.Clear(); selectedCandidates.Add(candidateId); }
            }
            else if (!selectedCandidates.Remove(candidateId))
            {
                if (selectedCandidates.Count >= request.MaxSelections) return;
                selectedCandidates.Add(candidateId);
            }
            ShowCandidateOptions(request);
        }

        private InteractionRequestProjection ProjectForPresentation(InteractionRequestProjection request)
        {
            var gameId = getState()?.GameId;
            var playerId = getLocalPlayerId();
            var visible = GetVisibleState?.Invoke() ?? ExpansionUi?.VisibleView();
            if (presentationGameId == gameId && presentationPlayerId == playerId && ReferenceEquals(presentationView, visible) &&
                presentationInput?.StateRevision == request.StateRevision &&
                InteractionCommandIntent.SameContract(gameId, presentationInput, gameId, request)) return presentationProjection;
            var requests = GetVisibleRequests == null ? VisibleInteractionRequestSource.Read(getState(), visible, playerId) : GetVisibleRequests();
            var original = requests?.FirstOrDefault(value => value != null && value.GetStableInteractionId() == request.InteractionId &&
                value.SourceNodeId == request.SourceNodeId && value.CandidateSetId == request.CandidateSetId &&
                value.CandidateSetVersion == request.CandidateSetVersion && value.StateRevision == request.StateRevision);
            var safe = original == null ? GameStateCloneService.DeepClone(request) :
                InteractionRequestProjector.ProjectForPlayer(original, playerId, visible, gameId);
            if (original == null) safe.Presentation = null;
            presentationView = visible;
            presentationInput = GameStateCloneService.DeepClone(request);
            presentationProjection = safe;
            presentationGameId = gameId;
            presentationPlayerId = playerId;
            return safe;
        }

        private static bool UsesSelectionDocument(InteractionRequestProjection request) =>
            request.Presentation?.Prompt != null || request.Presentation?.Options?.Count > 0 ||
            request.PromptKey == "facility.entry.choose_vehicle_action" || request.PromptKey == "facility.entry.choose_adjacent";

        private string RequestPrompt(InteractionRequestProjection request)
        {
            var document = ExpansionUi == null || !(UsesSelectionDocument(request) || request.PromptKey == "effect.resource.choose_amounts")
                ? null : EffectDialogTextDocuments.ResolvePrompt(request, ExpansionUi.ResolveGameplayText);
            return document == null ? FormatPrompt(request.PromptKey) : document.ToPlainText();
        }

        private static string FormatPrompt(string key)
        {
            switch (key)
            {
                case "effect.build.choose_payment": return "选择本次建设的支付方式。";
                case "effect.build.choose_slot": return "选择建设位置。确认后支付费用并建设。";
                case "effect.explore.choose_path": return "逐段选择探索路线；到达合法目标后选择落点并结算，可退回上一步。";
                case "effect.explore.choose_recipient": return "选择此段探索路费的接收玩家。";
                case "facility.entry.choose_vehicle_action": return "选择移除后调度，或执行探索。";
                case "facility.entry.choose_adjacent": return "选择相邻设施，触发其入场效果。";
                case "facility.entry.choose_additional_build": return "选择要额外建设的设施。";
                case "facility.entry.choose_extension_hub": return "选择延伸枢纽或跳过。";
                case "facility.entry.sell_resources": return "选择要出售的资源数量。";
                case "facility.entry.choose_free_move": return "选择城市移动目的地。";
                case "facility.entry.choose_resources": return "分配总计 5 点资源。";
                case "facility.entry.choose_influence_branch": return "选择替换或部署影响力。";
                case "facility.entry.choose_two_influences": return "选择影响力位置，再确认选择。";
                default: return "请完成设施效果选择。";
            }
        }

        private string FormatCandidateLabel(string candidateId, InteractionRequestProjection request)
        {
            if (string.IsNullOrEmpty(candidateId)) return "未命名选项";
            int candidateIndex = request.CandidateIds.IndexOf(candidateId);
            var definitions = ItemTransferEffectExecutor.Value(request.PromptParameters, "candidateDefinitionIds");
            if (definitions?.Kind == NormalizedValueKind.Array && candidateIndex >= 0 && candidateIndex < definitions.Items.Count)
            {
                var definition = FacilityCardDatabase.Get(definitions.Items[candidateIndex].StringValue);
                if (definition != null) return definition.Name;
            }
            if (candidateId == "vehicle.remove_move") return "移除 1 个影响力，然后调度 1 次";
            if (candidateId == "vehicle.explore") return "执行 1 次探索";
            if (candidateId == "resources") return "支付资源";
            if (candidateId == "gold") return "支付金券";
            if (candidateId == "free") return "免费建设";
            if (candidateId == "explore.back") return "退回上一段路线";
            if (candidateId.StartsWith("build-slot:", StringComparison.Ordinal) && int.TryParse(candidateId.Substring(11), out int slot)) return "建设到城市面板位置 " + (slot + 1);
            if (candidateId.StartsWith("explore.step:", StringComparison.Ordinal))
            { var parts = candidateId.Substring(13).Split(':'); if (parts.Length == 2) return "经航道 " + Uri.UnescapeDataString(parts[0]) + " 前往 " + Uri.UnescapeDataString(parts[1]); }
            if (candidateId.StartsWith("explore.finish:", StringComparison.Ordinal)) return "确认探索并放置到 " + candidateId.Substring(15);
            if (candidateId.StartsWith("explore.pay:", StringComparison.Ordinal)) return "支付给玩家 " + candidateId.Substring(12);
            var card = FacilityCardDatabase.Get(candidateId);
            if (card != null) return card.Name;
            var placements = getState()?.Map?.Facilities;
            if (placements != null) foreach (var placement in placements)
                if (placement != null && placement.ContentInstanceId == candidateId)
                    return (FacilityCardDatabase.Get(placement.FacilityCardId)?.Name ?? placement.FacilityCardId) + "（位置 " + (placement.CityBoardSlotIndex + 1) + "）";

            if (candidateId.StartsWith("explore:", StringComparison.Ordinal)) return "探索 " + candidateId.Substring(8);
            if (candidateId.StartsWith("replace:", StringComparison.Ordinal)) return "替换影响力 " + candidateId.Substring(8);
            if (candidateId.StartsWith("deploy:", StringComparison.Ordinal)) return "部署影响力 " + candidateId.Substring(7);
            if (candidateId == "choice.skip") return "跳过";
            if (candidateId == "choice.confirm") return "确认出售";
            return candidateId;
        }

        private void ResetAndHide()
        {
            selectedCandidates.Clear();
            resourceDraft.Clear();
            inventoryKey = string.Empty;
            renderedInteractionId = string.Empty;
            renderedRevision = -1;
            needsRefresh = false;
            inFlightCommandId = string.Empty;
            dialog.Hide();
            clearHighlights();
        }
    }
}
