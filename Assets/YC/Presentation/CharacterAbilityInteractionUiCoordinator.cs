using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>
    /// 展示 NMC-010 角色能力生成的通用 InteractionRequest。
    /// 这类请求不再回退到 PendingCharacterEffect，也不能借用事件牌提示。
    /// </summary>
    internal sealed class CharacterAbilityInteractionUiCoordinator :
        InteractionBase,
        IInteractionRequestRenderer,
        IMapConfirmationScope,
        IDisposable
    {
        private static readonly Unity.Profiling.ProfilerMarker PlayerProjectionMarker = new Unity.Profiling.ProfilerMarker("YC.PlayerProjection");
        private readonly Func<GameState> getState;
        private readonly Func<int> getLocalPlayerId;
        private readonly CharacterCardEffectChoiceDialog dialog;
        private readonly Action<IReadOnlyList<WorkflowHighlight>> setHighlights;
        private readonly Action clearHighlights;
        private readonly Action<GameCommand> submit;
        private readonly Action<string> setPrompt;
        private string renderedInteractionId = string.Empty;
        private int renderedRevision = -1;
        private bool needsRefresh;
        private string inFlightCommandId = string.Empty;
        private string inFlightGameId;
        private int inFlightPlayerId = -1;
        private string renderedGameId;
        private InteractionRequestProjection renderedRequest;
        private GameStateView presentationView;
        private InteractionRequestProjection presentationInput, presentationProjection;
        private string presentationGameId;
        private int presentationPlayerId = -1;
        private readonly InteractionCommandIntent confirmedIntent = new InteractionCommandIntent();
        private bool ownsPresentation;
        private readonly List<int> saleDraft = new List<int>();
        private string inventoryKey = string.Empty;
        private readonly HashSet<string> selectedCandidates = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> influenceSourceCandidates = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> influenceMoveCandidates =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private string influenceMoveSourceId = string.Empty;
        private string influenceMoveSourceSlotId = string.Empty;
        private int influenceMoveDraftGeneration;
        private int mapSelectionVersion;
        public Func<GameStateView> GetVisibleState { private get; set; }
        public GameplayExpansionUi ExpansionUi { private get; set; }
        public Action<InteractionRequestProjection> RenderUseSelection { private get; set; }
        public Action ClearUseSelection { private get; set; }
        private bool ownsUseSelection;
        public Func<IReadOnlyList<InteractionRequest>> GetVisibleRequests { private get; set; }
        public Func<bool> CanSubmit { private get; set; }
        public Func<bool> RequiresExplicitRefresh { private get; set; }
        public string MapConfirmationScope
        {
            get
            {
                if (!string.IsNullOrEmpty(inFlightCommandId) || !TryGetRequest(out var request)) return string.Empty;
                var scope = request.InteractionId + ":" + request.StateRevision + ":" + request.CandidateSetVersion;
                if (CanSelectInfluenceMapArray(renderedRequest))
                    return IsCurrentPresentedRequest(request) ? renderedGameId + ":" + scope + ":map-selection:" + mapSelectionVersion : string.Empty;
                if (IsLocationTargetInteraction(renderedRequest))
                    return IsCurrentPresentedRequest(request) ? renderedGameId + ":" + scope : string.Empty;
                if (!IsInfluenceMoveChoice(request.InteractionTypeId)) return scope;
                if (GameplayHudFrame.EffectInputSuspended || CanSubmit?.Invoke() == false ||
                    !IsCurrentInfluenceMoveRequest(request)) return string.Empty;
                return renderedGameId + ":" + scope + ":" + influenceMoveDraftGeneration + (string.IsNullOrEmpty(influenceMoveSourceId)
                    ? ":move-source" : ":move-target:" + influenceMoveSourceId + ":" + influenceMoveSourceSlotId);
            }
        }

        public CharacterAbilityInteractionUiCoordinator(
            Func<GameState> getState,
            Func<int> getLocalPlayerId,
            CharacterCardEffectChoiceDialog dialog,
            Action<IReadOnlyList<WorkflowHighlight>> setHighlights,
            Action clearHighlights,
            Action<GameCommand> submit,
            Action<string> setPrompt)
        {
            this.getState = getState ?? throw new ArgumentNullException(nameof(getState));
            this.getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
            this.dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            this.setHighlights = setHighlights ?? throw new ArgumentNullException(nameof(setHighlights));
            this.clearHighlights = clearHighlights ?? throw new ArgumentNullException(nameof(clearHighlights));
            this.submit = submit ?? throw new ArgumentNullException(nameof(submit));
            this.setPrompt = setPrompt ?? throw new ArgumentNullException(nameof(setPrompt));
        }

        public override string Id => "character-ability.interaction";

        int IInteractionRequestRenderer.Priority => 310;

        public override InteractionPriority Priority => InteractionPriority.PendingResolution;

        public override bool IsActive
        {
            get
            {
                InteractionRequest ignored;
                return TryGetRequest(out ignored);
            }
        }

        public bool CanRender(InteractionRequestProjection request)
        {
            return request != null && request.VisibleToViewer &&
                   request.AnsweringPlayerId == getLocalPlayerId() && request.Status == "open" &&
                   (IsCharacterAbilityInteraction(request.InteractionTypeId) || EffectDialogSelectionSpec.IsInfluenceMapArray(request)) && !ActionPaymentPreparation.IsPreparedActionRequest(request) &&
                   (request.InteractionTypeId != ResourceEffectExecutor.PaymentConfirmationInteractionTypeId || request.AnswerSchema == "candidate_id") &&
                   (request.InteractionTypeId != ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId ||
                    request.AnswerSchema == ResourceAllocationAnswer.BoundedAnswerSchema);
        }

        // 仅已实测的初始角色弃牌多选；不将角色模式、地图或数量式草稿并入此许可。
        internal bool CanPreserveUnsubmittedInitialCharacterDraft(InteractionRequestProjection routedRequest)
        {
            if (!ownsPresentation || ownsUseSelection || needsRefresh || confirmedIntent.IsBusy || !string.IsNullOrEmpty(inFlightCommandId) ||
                renderedRequest?.InteractionTypeId != "setup.basic_character.discard" ||
                renderedRequest.AnswerSchema != "candidate_ids" || renderedRequest.MaxSelections <= 1 ||
                !TryGetRequest(out var request)) return false;
            var game = getState()?.GameId;
            var current = ProjectForPresentation(InteractionRequestProjector.ProjectForPlayer(request, getLocalPlayerId()), game);
            return CanRender(current) && current.StateRevision >= renderedRevision &&
                InteractionCommandIntent.SameContract(renderedGameId, renderedRequest, game, current) &&
                InteractionCommandIntent.SameContract(renderedGameId, routedRequest, game, current) &&
                selectedCandidates.Count <= current.MaxSelections && selectedCandidates.All(id => current.CandidateIds.Contains(id));
        }

        public void Render(InteractionRequestProjection projection)
        {
            if (!CanRender(projection))
            {
                Clear();
                return;
            }

            var currentGame = getState()?.GameId;
            projection = ProjectForPresentation(projection, currentGame);
            if (renderedGameId == currentGame && projection.InteractionId == renderedInteractionId && projection.StateRevision < renderedRevision) return;
            if (!string.IsNullOrEmpty(currentGame) && !string.IsNullOrEmpty(inFlightCommandId) &&
                (inFlightGameId != currentGame || inFlightPlayerId != getLocalPlayerId()))
            { inFlightCommandId = string.Empty; confirmedIntent.Reset(); }
            var sameRequest = InteractionCommandIntent.SameContract(renderedGameId, renderedRequest, currentGame, projection);
            if (sameRequest && projection.StateRevision < renderedRevision) return;
            if (CanSubmit?.Invoke() != false && confirmedIntent.TryRebuild(currentGame, projection, string.Empty, out var rebuilt))
            { DispatchCommand(rebuilt); return; }
            if (confirmedIntent.IsBusy || !string.IsNullOrEmpty(inFlightCommandId)) return;
            if (!sameRequest)
            {
                selectedCandidates.Clear();
                mapSelectionVersion++;
                saleDraft.Clear();
                ClearInfluenceMoveDraft();
            }
            if (!string.IsNullOrEmpty(inFlightCommandId)) return;
            renderedGameId = currentGame; renderedRequest = projection;
            if (projection.InteractionTypeId == CharacterLifecycleService.OrderInteractionTypeId && RenderUseSelection == null)
                throw new InvalidOperationException("角色首模式选择尚未接入原卡面，不能使用额外顺序选择页。");
            bool dedicatedUseSelection = RenderUseSelection != null &&
                (CharacterCardPanelPresenter.UsesOriginalModeViewer(projection) ||
                 projection.InteractionTypeId == CharacterLifecycleService.SecondModeInteractionTypeId);
            if (dedicatedUseSelection)
            {
                if (sameRequest && !needsRefresh && projection.StateRevision == renderedRevision && ownsUseSelection) return;
                if (ownsUseSelection && !sameRequest) ClearUseSelection?.Invoke();
                renderedInteractionId = projection.InteractionId;
                renderedRevision = projection.StateRevision;
                needsRefresh = false;
                ownsPresentation = ownsUseSelection = true;
                clearHighlights(); dialog.Hide();
                RenderUseSelection(projection);
                setPrompt(RequestPrompt(projection));
                return;
            }
            if (ownsUseSelection) { ownsUseSelection = false; ClearUseSelection?.Invoke(); }
            var maximums = CurrentSaleInventory();
            var nextInventoryKey = string.Join(",", maximums);
            // 收起保留视图；只有视图已丢失时重建仍有效的选择请求。
            var mapChoice = IsLocationTargetInteraction(projection) || CanPresentInfluenceMapTarget(projection) ||
                projection.InteractionTypeId == "effect.influence.move.source" || IsInfluenceMoveChoice(projection.InteractionTypeId);
            if (sameRequest && !needsRefresh && projection.StateRevision == renderedRevision && inventoryKey == nextInventoryKey &&
                (mapChoice || projection.InteractionTypeId == "character.cleanup.settlement" || dialog.IsShowing))
                return;
            selectedCandidates.RemoveWhere(id => projection.CandidateIds == null || !projection.CandidateIds.Contains(id));
            if (selectedCandidates.Count > projection.MaxSelections)
            {
                var ordered = projection.CandidateIds.FindAll(id => selectedCandidates.Contains(id));
                for (var i = Math.Max(0, projection.MaxSelections); i < ordered.Count; i++) selectedCandidates.Remove(ordered[i]);
            }
            renderedInteractionId = projection.InteractionId;
            renderedRevision = projection.StateRevision;
            inventoryKey = nextInventoryKey;
            needsRefresh = false;
            clearHighlights();
            ownsPresentation = true;

            influenceSourceCandidates.Clear();
            if (projection.InteractionTypeId == "character.cleanup.settlement")
            {
                // 当前窗口仅使用既有底栏来源条目和结束操作。
                dialog.Hide();
            }
            else if (projection.InteractionTypeId == ResourceEffectExecutor.PaymentConfirmationInteractionTypeId)
            {
                if (projection.CandidateIds.Count != 1 ||
                    projection.CandidateIds[0] != ResourceEffectExecutor.PaymentConfirmationCandidateId)
                    throw new InvalidOperationException("原付款确认请求缺少完整正式提示或正常付款候选。");
                if (ExpansionUi != null) dialog.BindGameplayText(ExpansionUi.ResolveGameplayText, ExpansionUi.RenderEnterpriseDetails);
                dialog.ShowPaymentConfirmation(projection, () => IsCurrent(projection),
                    () => SubmitCandidates(projection, new[] { ResourceEffectExecutor.PaymentConfirmationCandidateId }));
            }
            else if (projection.InteractionTypeId == ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId &&
                projection.AnswerSchema == ResourceAllocationAnswer.BoundedAnswerSchema)
            {
                ShowPaymentSubstitution(projection);
            }
            else if (projection.InteractionTypeId == ContentPrimitiveEffectExecutor.QuantityInteractionTypeId)
            {
                var minimum = projection.PromptParameters?.Properties.Find(value => value.Name == "minAmount")?.Value;
                var maximum = projection.PromptParameters?.Properties.Find(value => value.Name == "maxAmount")?.Value;
                var kind = projection.PromptParameters?.Properties.Find(value => value.Name == "amountKind")?.Value?.StringValue;
                if (minimum?.Kind != NormalizedValueKind.Integer || maximum?.Kind != NormalizedValueKind.Integer ||
                    minimum.IntegerValue < 0 || maximum.IntegerValue < minimum.IntegerValue || maximum.IntegerValue > int.MaxValue)
                    throw new InvalidOperationException("数量请求缺少有效上下限。");
                if (ExpansionUi != null) dialog.BindGameplayText(ExpansionUi.ResolveGameplayText, ExpansionUi.RenderEnterpriseDetails);
                dialog.ShowQuantity(projection, kind == "player_marker" ? dialog.Copy.PlayerMarkerLabel : FormatResource(kind),
                    (int)minimum.IntegerValue, (int)maximum.IntegerValue, saleDraft, RememberDraft,
                    () => IsCurrent(projection), amount =>
                    {
                        if (GameplayHudFrame.EffectInputSuspended || !IsCurrent(projection)) return;
                        var answer = amount.ToString(CultureInfo.InvariantCulture);
                        if (!ContentPrimitiveEffectExecutor.TryReadBoundedAmount(projection.PromptParameters,
                            NormalizedValue.CreateString(answer), out _)) return;
                        var command = EffectInteractionCommands.Answer(projection, getLocalPlayerId(), null);
                        command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = answer;
                        SubmitCommand(command);
                    });
            }
            else if (projection.PromptKey == "effect.resource.choose_type")
            {
                dialog.ShowResourceType(projection.CandidateIds.Select(FormatResource).ToArray(), saleDraft, RememberDraft,
                    () => IsCurrent(projection), index => SubmitCandidates(projection, new[] { projection.CandidateIds[index] }));
            }
            else if (IsInfluenceMoveChoice(projection.InteractionTypeId))
            {
                dialog.Hide();
                BindInfluenceMoveCandidates(projection);
                setHighlights(BuildInfluenceMovePresentation().Highlights);
            }
            else if (projection.InteractionTypeId == "effect.influence.move.source")
            {
                dialog.Hide();
                BindInfluenceSlotCandidates(projection);
                setHighlights(BuildTargetHighlights(influenceSourceCandidates.Keys.ToArray(),
                    WorkflowHighlightTargetKind.InfluenceSlot, WorkflowHighlightSemantic.DispatchSource));
            }
            else if (projection.InteractionTypeId == "setup.basic_character.keep")
            {
                // 旧待答存档只解释不支持状态，绝不把保留答案翻译成弃牌答案。
                dialog.ShowOptions(RequestTitle(projection), RequestPrompt(projection), Array.Empty<EffectDialogOption>());
            }
            else if (IsLocationTargetInteraction(projection))
            {
                dialog.Hide();
                setHighlights(BuildTargetHighlights(
                    projection.CandidateIds,
                    WorkflowHighlightTargetKind.Location,
                    WorkflowHighlightSemantic.MoveTarget));
            }
            else if (CanPresentInfluenceMapTarget(projection))
            {
                dialog.Hide();
                BindInfluenceSlotCandidates(projection);
                setHighlights(BuildInfluenceTargetHighlights(projection));
            }
            else if (projection.PromptKey == "character.cannot.strategy.choose_sale" || projection.PromptKey == "effect.resource.sell")
            {
                GameStateView visible;
                if (GetVisibleState != null) visible = GetVisibleState();
                else
                {
                    using (PlayerProjectionMarker.Auto()) visible = GameStateViewProjector.ProjectForPlayer(getState(), getLocalPlayerId());
                }
                var receipt = ResourceSaleReceiptProjection.Latest(visible, getLocalPlayerId());
                if (ExpansionUi != null) dialog.BindGameplayText(ExpansionUi.ResolveGameplayText, ExpansionUi.RenderEnterpriseDetails);
                dialog.ShowResourceSaleCandidates(
                    projection.CandidateIds,
                    selected => SubmitCandidates(projection, selected),
                    null, saleDraft, values => { saleDraft.Clear(); saleDraft.AddRange(values); },
                    () => IsCurrent(projection), maximums,
                    projection.Presentation?.Prompt != null || projection.Presentation?.Progress != null || receipt == null
                        ? null : string.Format(dialog.Copy.SaleReceiptSummaryFormat, receipt.Revenue), projection);
            }
            else
            {
                ShowCandidateOptions(projection, sameRequest);
            }

            setPrompt(IsInfluenceMoveChoice(projection.InteractionTypeId)
                ? InfluenceMovePrompt() : RequestPrompt(projection));
        }

        public override InteractionResult OnLocationClicked(string locationId)
        {
            if (!IsActive) return InteractionResult.Passthrough;
            InteractionRequest request;
            if (TryGetRequest(out request) &&
                IsLocationTargetInteraction(InteractionRequestProjector.ProjectForPlayer(request, getLocalPlayerId())) &&
                request.CandidateIds != null &&
                request.CandidateIds.Contains(locationId))
            {
                if (GameplayHudFrame.EffectInputSuspended || !IsCurrentPresentedRequest(request)) return InteractionResult.Consumed;
                SubmitCandidates(renderedRequest, new[] { locationId });
                return InteractionResult.Consumed;
            }

            setPrompt("请先完成当前角色能力选择。");
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            if (!IsActive) return InteractionResult.Passthrough;
            InteractionRequest request;
            if (TryGetRequest(out request) && CanSelectInfluenceMapArray(renderedRequest))
            {
                if (GameplayHudFrame.EffectInputSuspended || !IsCurrentPresentedRequest(request) ||
                    !influenceSourceCandidates.TryGetValue(slotId, out var candidate)) return InteractionResult.Consumed;
                var changed = selectedCandidates.Remove(candidate);
                if (!changed && selectedCandidates.Count < renderedRequest.MaxSelections)
                { selectedCandidates.Add(candidate); changed = true; }
                if (!changed) return InteractionResult.Consumed;
                mapSelectionVersion++;
                if (selectedCandidates.Count == renderedRequest.MaxSelections)
                    SubmitCandidates(renderedRequest, renderedRequest.CandidateIds.FindAll(selectedCandidates.Contains));
                else setHighlights(BuildInfluenceTargetHighlights(renderedRequest));
                return InteractionResult.Consumed;
            }
            if (TryGetRequest(out request) && IsInfluenceMoveChoice(request.InteractionTypeId))
            {
                if (GameplayHudFrame.EffectInputSuspended || CanSubmit?.Invoke() == false ||
                    !IsCurrentInfluenceMoveRequest(request)) return InteractionResult.Consumed;
                BindInfluenceMoveCandidates(renderedRequest);
                if (string.IsNullOrEmpty(influenceMoveSourceId))
                {
                    if (influenceSourceCandidates.TryGetValue(slotId, out var sourceId))
                    {
                        influenceMoveSourceId = sourceId;
                        influenceMoveSourceSlotId = slotId;
                        setHighlights(BuildInfluenceMovePresentation().Highlights);
                    }
                    setPrompt(InfluenceMovePrompt());
                }
                else if (influenceMoveCandidates.TryGetValue(influenceMoveSourceId, out var targets) &&
                         targets.TryGetValue(slotId, out var candidate))
                {
                    // 来源只是本地草稿；目的地确认仍回答正式完整候选和当前已展示的严格revision。
                    // Dispatch在submit之前清旧草稿，同帧准确回执产生的新请求不会被旧收尾覆盖。
                    SubmitCommand(EffectInteractionCommands.Answer(renderedRequest, getLocalPlayerId(), new[] { candidate }));
                }
                else setPrompt(InfluenceMovePrompt());
                return InteractionResult.Consumed;
            }
            if (TryGetRequest(out request) &&
                (request.InteractionTypeId == "effect.influence.move.source" ||
                 CanPresentInfluenceMapTarget(InteractionRequestProjector.ProjectForPlayer(request, getLocalPlayerId()))) &&
                influenceSourceCandidates.TryGetValue(slotId, out var itemId))
            {
                if (!IsCurrentPresentedRequest(request)) return InteractionResult.Consumed;
                SubmitCandidates(renderedRequest, new[] { itemId });
                return InteractionResult.Consumed;
            }
            setPrompt(request != null && IsInfluenceTargetInteraction(request.PromptKey)
                ? "请点击地图上高亮的合法影响力槽位。"
                : "请在角色能力选择界面中完成当前选择。");
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnMobileCityClicked()
        {
            if (!IsActive) return InteractionResult.Passthrough;
            setPrompt("请先完成当前角色能力选择。");
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnEscape()
        {
            if (!IsActive) return InteractionResult.Passthrough;
            if (GameplayHudFrame.EffectInputSuspended) return InteractionResult.Consumed;
            if (TryGetRequest(out var request) && request.AllowDecline)
            {
                SubmitDecline(InteractionRequestProjector.ProjectForPlayer(request, getLocalPlayerId()));
                return InteractionResult.Consumed;
            }
            setPrompt("当前角色能力选择不能取消，请完成结算。");
            return InteractionResult.Consumed;
        }

        public override InteractionPresentation BuildPresentation()
        {
            if (!string.IsNullOrEmpty(inFlightCommandId)) return InteractionPresentation.Busy;
            InteractionRequest request;
            if (!TryGetRequest(out request)) return InteractionPresentation.Empty;

            if (IsInfluenceMoveChoice(request.InteractionTypeId))
            {
                if (!IsCurrentInfluenceMoveRequest(request))
                {
                    return InteractionPresentation.Busy;
                }
                BindInfluenceMoveCandidates(renderedRequest);
                return BuildInfluenceMovePresentation();
            }

            if (request.InteractionTypeId == "effect.influence.move.source")
            {
                if (!IsCurrentPresentedRequest(request)) return InteractionPresentation.Busy;
                return new InteractionPresentation(BuildTargetHighlights(influenceSourceCandidates.Keys.ToArray(),
                    WorkflowHighlightTargetKind.InfluenceSlot, WorkflowHighlightSemantic.DispatchSource),
                    RequestPrompt(renderedRequest), InteractionMode.Busy, true);
            }

            if (IsLocationTargetInteraction(InteractionRequestProjector.ProjectForPlayer(request, getLocalPlayerId())))
            {
                if (!IsCurrentPresentedRequest(request)) return InteractionPresentation.Busy;
                return new InteractionPresentation(
                    BuildTargetHighlights(
                        request.CandidateIds,
                        WorkflowHighlightTargetKind.Location,
                        WorkflowHighlightSemantic.MoveTarget),
                    RequestPrompt(renderedRequest),
                    InteractionMode.Busy,
                    true);
            }

            if (CanPresentInfluenceMapTarget(InteractionRequestProjector.ProjectForPlayer(request, getLocalPlayerId())))
            {
                if (!IsCurrentPresentedRequest(request)) return InteractionPresentation.Busy;
                return new InteractionPresentation(
                    BuildInfluenceTargetHighlights(renderedRequest),
                    BuildPrompt(request.PromptKey),
                    InteractionMode.Busy,
                    true);
            }

            return new InteractionPresentation(
                new List<WorkflowHighlight>().AsReadOnly(),
                RequestPrompt(InteractionRequestProjector.ProjectForPlayer(request, getLocalPlayerId())),
                InteractionMode.Busy,
                false);
        }

        public override void Cancel()
        {
            Clear();
        }

        public override void NotifyCommandSettled(string commandId)
        {
        }

        public override void NotifyInteractionCommandResolved(string gameId, string commandId, bool accepted, CommandErrorCode errorCode)
        {
            if (string.IsNullOrEmpty(commandId) || commandId != inFlightCommandId || gameId != inFlightGameId) return;
            confirmedIntent.Resolve(gameId, commandId, accepted, errorCode, RequiresExplicitRefresh?.Invoke() == true);
            inFlightCommandId = string.Empty; needsRefresh = true;
        }

        public void Dispose()
        {
            Clear();
        }

        public void Clear()
        {
            if (ownsUseSelection) { ownsUseSelection = false; ClearUseSelection?.Invoke(); }
            mapSelectionVersion++;
            selectedCandidates.Clear();
            influenceSourceCandidates.Clear();
            ClearInfluenceMoveDraft();
            saleDraft.Clear();
            inventoryKey = string.Empty;
            renderedInteractionId = string.Empty;
            renderedRevision = -1;
            needsRefresh = false;
            if (ownsPresentation)
            {
                ownsPresentation = false;
                dialog.Hide();
                clearHighlights();
            }
        }

        public bool TrySubmitUseSelection(InteractionRequestProjection request, string candidate)
        {
            if (GameplayHudFrame.EffectInputSuspended || !string.IsNullOrEmpty(inFlightCommandId) || request == null || !CanRender(request) ||
                !request.CandidateIds.Contains(candidate) || !IsCurrent(request)) return false;
            if (!TryGetRequest(out var current) || current.StateRevision != request.StateRevision) return false;
            var observedRevision = GetVisibleState?.Invoke()?.StateRevision ?? getState()?.EffectRuntime?.StateRevision;
            if (observedRevision.HasValue && observedRevision.Value != request.StateRevision) return false;
            SubmitCandidates(request, new[] { candidate });
            return true;
        }

        private void ShowCandidateOptions(InteractionRequestProjection projection, bool preserveScroll = true)
        {
            var state = getState();
            var requestGameId = state?.GameId;
            var optionalExecution = IsOptionalExecutionChoice(projection);
            bool Current() => optionalExecution || EffectDialogSelectionSpec.IsExecutionChoice(projection)
                ? requestGameId == getState()?.GameId && projection.StateRevision == renderedRevision &&
                  TryGetRequest(out var current) && IsCurrentPresentedRequest(current) &&
                  InteractionCommandIntent.SameContract(requestGameId, projection, renderedGameId, renderedRequest)
                : IsCurrent(projection);
            if (optionalExecution)
            {
                if (ExpansionUi != null) dialog.BindGameplayText(ExpansionUi.ResolveGameplayText, ExpansionUi.RenderEnterpriseDetails);
                var options = projection.CandidateIds.Select(id => new EffectDialogOption(RequestLabel(id, projection), () =>
                {
                    if (Current()) SubmitCandidates(projection, new[] { id });
                })
                {
                    StableId = id, RequestId = projection.InteractionId, Revision = projection.StateRevision,
                    ExecuteImmediately = true, IsCurrent = Current,
                    LabelDocument = dialog.ResolveGameplayText(projection.Presentation?.Options.Find(value => value.CandidateId == id)?.Label),
                    DescriptionDocument = dialog.ResolveGameplayText(projection.Presentation?.Options.Find(value => value.CandidateId == id)?.Description)
                }).ToArray();
                // 沿原执行条目与结束当前请求入口；不伪改0..1合同，也不增加确认或取消页。
                dialog.ShowOptions(projection, RequestTitle(projection), RequestPrompt(projection), options);
                return;
            }
            var visible = GetVisibleState?.Invoke() ?? ExpansionUi?.VisibleView();
            var displayRequest = CharacterCardDisplayOrder.ForDisplay(state, projection, id =>
            {
                return CharacterCardDisplayOrder.ResolveDefinition(state, ResolveCandidateDefinition(projection, id, visible));
            });
            var specification = new EffectDialogSelectionSpec
            {
                Request = displayRequest,
                ExecuteImmediately = EffectDialogSelectionSpec.IsExecutionChoice(projection),
                ConfirmLabel = projection.InteractionTypeId == "setup.basic_character.discard" ? ExpansionUi?.Copy.DiscardConfirmLabel : null,
                SelectedIds = selectedCandidates,
                Players = PlayerSelectionRequestAdapter.Build(projection,getState(),getLocalPlayerId(),selectedCandidates),
                PlayerSelectionChanged = ids =>
                { selectedCandidates.Clear(); foreach (var id in ids) selectedCandidates.Add("player:"+id); },
                Label = candidateId => RequestLabel(candidateId, projection),
                CardSprite = candidateId =>
                {
                    var definitionId = ResolveCandidateDefinition(projection, candidateId, visible);
                    var catalog = ExpansionUi?.GetComponent<GameplayInteractionHudView>()?.DialogRegistry.CardVisualCatalog;
                    return catalog?.GetCharacterFront(definitionId) ?? catalog?.GetFacility(definitionId) ?? catalog?.GetEvent(definitionId);
                },
                OptionNamePrefix = "Character Effect Option ",
                IsCurrent = Current,
                Select = candidateId =>
                {
                    if (!Current()) return;
                    if (EffectDialogSelectionSpec.IsExecutionChoice(projection))
                    {
                        SubmitCandidates(projection, new[] { candidateId });
                        return;
                    }
                    if (projection.MaxSelections <= 1)
                    {
                        if (!selectedCandidates.Remove(candidateId))
                        { selectedCandidates.Clear(); selectedCandidates.Add(candidateId); }
                    }
                    else if (!selectedCandidates.Remove(candidateId) && selectedCandidates.Count < projection.MaxSelections)
                        selectedCandidates.Add(candidateId);
                    ShowCandidateOptions(projection);
                },
                Confirm = () => SubmitCandidates(projection,
                    projection.CandidateIds.FindAll(id => selectedCandidates.Contains(id))),
                Cancel = null
            };
            if (specification.Players == null && ExpansionUi != null)
            {
                specification.ResolveText = ExpansionUi.ResolveGameplayText;
                specification.RenderText = ExpansionUi.RenderEnterpriseDetails;
                specification.CompatibilityPrompt = () => RequestPrompt(projection);
                specification.PromptDocument = specification.ResolvePromptDocument();
            }
            dialog.ShowSelection(RequestTitle(projection), specification.PromptDocument == null ? RequestPrompt(projection) : null,
                specification, preserveScroll);
        }

        private static bool IsOptionalExecutionChoice(InteractionRequestProjection request) =>
            request != null && request.InteractionTypeId == "effect.choice.execute" && request.AnswerSchema == "candidate_id" &&
            request.MinSelections == 0 && request.MaxSelections == 1 && request.AllowDecline;

        private static string ResolveCandidateDefinition(InteractionRequestProjection request, string id, GameStateView visible)
        {
            var declared = GameplayExpansionUi.CandidateDefinition(request, id);
            return !string.IsNullOrEmpty(declared) ? declared : visible?.ItemZones?.Items.Find(item => item.ItemId == id)?.DefinitionId ?? id;
        }

        private void RememberDraft(IReadOnlyList<int> values)
        { saleDraft.Clear(); saleDraft.AddRange(values); }

        private void ShowPaymentSubstitution(InteractionRequestProjection request)
        {
            var parameters = request.PromptParameters;
            NormalizedValue Read(string key) => parameters?.Kind == NormalizedValueKind.Object
                ? parameters.Properties.Find(value => value.Name == key)?.Value : null;
            var minimum = Read("minAmount"); var maximum = Read("maxAmount");
            var rowIds = Read("rowIds"); var rowMaximums = Read("rowMaximums");
            if (minimum?.Kind != NormalizedValueKind.Integer || maximum?.Kind != NormalizedValueKind.Integer ||
                minimum.IntegerValue < 0 || maximum.IntegerValue < minimum.IntegerValue || maximum.IntegerValue > 99 ||
                rowIds?.Kind != NormalizedValueKind.Array || rowMaximums?.Kind != NormalizedValueKind.Object)
                throw new InvalidOperationException("付款分配投影缺少有效范围或行约束。");
            var ids = new List<string>(); var labels = new List<string>(); var maximums = new List<int>();
            var limits = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var value in rowIds.Items)
            {
                var id = value.Kind == NormalizedValueKind.String ? value.StringValue : null;
                var limit = rowMaximums.Properties.Find(row => row.Name == id)?.Value;
                var label = request.Presentation?.QuantityRows.Find(row => row.RowId == id)?.Label;
                var document = ExpansionUi?.ResolveGameplayText(label);
                if (string.IsNullOrEmpty(id) || !request.CandidateIds.Contains(id) || limits.ContainsKey(id) ||
                    limit?.Kind != NormalizedValueKind.Integer || limit.IntegerValue < 0 || limit.IntegerValue > 99 || document == null)
                    throw new InvalidOperationException("付款分配投影的授权行或统一文案不完整。");
                ids.Add(id); labels.Add(FormatResource(id == nameof(ResourceType.OriginiumShard) ? "originium-shard" : id.ToLowerInvariant()));
                maximums.Add((int)limit.IntegerValue); limits.Add(id, (int)limit.IntegerValue);
            }
            if (ids.Count != request.CandidateIds.Count) throw new InvalidOperationException("付款分配候选与授权行不一致。");
            if (ExpansionUi != null) dialog.BindGameplayText(ExpansionUi.ResolveGameplayText, ExpansionUi.RenderEnterpriseDetails);
            var description = request.Presentation?.Prompt == null && request.Presentation?.Progress == null ? RequestPrompt(request) : null;
            dialog.ShowBoundedResourceAllocation(request, ids, description, labels, maximums,
                (int)minimum.IntegerValue, (int)maximum.IntegerValue, saleDraft, RememberDraft,
                () => IsCurrent(request), values =>
                {
                    if (GameplayHudFrame.EffectInputSuspended || !IsCurrent(request) || values.Count != ids.Count) return;
                    var encoded = string.Join(",", ids.Select((id, index) => id + "=" + values[index].ToString(CultureInfo.InvariantCulture)));
                    if (!ResourceAllocationAnswer.TryParseBounded(encoded, ids, (int)minimum.IntegerValue,
                        (int)maximum.IntegerValue, limits, out _, out _)) return;
                    var command = EffectInteractionCommands.Answer(request, getLocalPlayerId(), null);
                    command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = encoded;
                    SubmitCommand(command);
                }, request.AllowDecline ? (Action)(() => SubmitDecline(request)) : null);
        }

        private int[] CurrentSaleInventory()
        {
            var resources = getState()?.FindPlayer(getLocalPlayerId())?.Resources;
            return resources == null ? new int[4] : new[]
                { resources.Originium, resources.OriginiumShard, resources.Iron, resources.PureOriginium };
        }

        private bool IsCurrent(InteractionRequestProjection projection)
        {
            if (projection == null || !string.IsNullOrEmpty(inFlightCommandId))
                return false;
            if (!TryGetRequest(out var request)) return false;
            var fresh = InteractionRequestProjector.ProjectForPlayer(request, getLocalPlayerId());
            return CanSubmit?.Invoke() != false && !confirmedIntent.IsBusy && CanRender(fresh) &&
                InteractionCommandIntent.SameContract(renderedGameId, projection, getState()?.GameId, fresh) &&
                fresh.StateRevision >= projection.StateRevision;
        }

        private void SubmitDecline(InteractionRequestProjection projection)
        {
            if (!IsCurrent(projection) || !projection.AllowDecline) return;
            if (TryGetRequest(out var fresh)) SubmitCommand(EffectInteractionCommands.Answer(
                InteractionRequestProjector.ProjectForPlayer(fresh, getLocalPlayerId()), getLocalPlayerId(), null, true));
        }

        private void SubmitCandidates(
            InteractionRequestProjection projection,
            IReadOnlyList<string> selected)
        {
            if (projection?.InteractionTypeId == "setup.basic_character.keep" || GameplayHudFrame.EffectInputSuspended || !IsCurrent(projection) || selected == null || selected.Count < projection.MinSelections ||
                selected.Count > projection.MaxSelections) return;
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in selected)
                if (!distinct.Add(id) || projection.CandidateIds == null || !projection.CandidateIds.Contains(id)) return;
            var resourceIds = new[] { "originium", "originium-shard", "iron", "pure-originium" };
            var inventory = CurrentSaleInventory();
            foreach (var id in selected)
            {
                var parts = id.Split('|');
                if (parts.Length != 3 || parts[0] != "sale") continue;
                var index = Array.IndexOf(resourceIds, parts[1]);
                if (index < 0 || !int.TryParse(parts[2], out var amount) || amount < 0 || amount > inventory[index])
                {
                    needsRefresh = true;
                    Render(projection);
                    return;
                }
            }
            if (TryGetRequest(out var fresh)) SubmitCommand(EffectInteractionCommands.Answer(
                InteractionRequestProjector.ProjectForPlayer(fresh, getLocalPlayerId()), getLocalPlayerId(), selected));
        }

        private void SubmitCommand(GameCommand command)
        {
            if (!string.IsNullOrEmpty(inFlightCommandId) || confirmedIntent.IsBusy) return;
            if (command.Kind == GameCommandKind.AnswerInteraction && TryGetRequest(out var request))
                confirmedIntent.Begin(getState()?.GameId, InteractionRequestProjector.ProjectForPlayer(request, getLocalPlayerId()), command,
                    request.InteractionTypeId == "setup.basic_character.discard");
            DispatchCommand(command);
        }

        private void DispatchCommand(GameCommand command)
        {
            inFlightGameId = getState()?.GameId;
            inFlightPlayerId = command.PlayerId;
            inFlightCommandId = command.CommandId;
            ClearInfluenceMoveDraft();
            ownsPresentation = false;
            dialog.Hide();
            clearHighlights();
            try
            {
                submit(command);
            }
            catch
            {
                needsRefresh = true;
                throw;
            }
        }

        private void SubmitCandidates(
            InteractionRequest request,
            IReadOnlyList<string> selected)
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
                    !ActionPaymentPreparation.IsPreparedActionRequest(candidate) &&
                    (IsCharacterAbilityInteraction(candidate.InteractionTypeId) ||
                     candidate.AnswerSchema == "candidate_id_array" &&
                     EffectDialogSelectionSpec.IsInfluenceMapTarget(InteractionRequestProjector.ProjectForPlayer(candidate, localPlayerId))))
                {
                    request = candidate;
                    return true;
                }
            }

            return false;
        }

        private InteractionRequestProjection ProjectForPresentation(InteractionRequestProjection projection, string gameId)
        {
            var playerId = getLocalPlayerId();
            var visible = GetVisibleState?.Invoke() ?? ExpansionUi?.VisibleView();
            if (presentationGameId == gameId && presentationPlayerId == playerId && ReferenceEquals(presentationView, visible) &&
                presentationInput?.StateRevision == projection.StateRevision &&
                InteractionCommandIntent.SameContract(gameId, presentationInput, gameId, projection))
                return presentationProjection;
            var requests = GetVisibleRequests == null ? VisibleInteractionRequestSource.Read(getState(), visible, playerId) : GetVisibleRequests();
            var request = requests?.FirstOrDefault(value => value != null &&
                value.GetStableInteractionId() == projection.InteractionId && value.SourceNodeId == projection.SourceNodeId &&
                value.CandidateSetId == projection.CandidateSetId && value.CandidateSetVersion == projection.CandidateSetVersion &&
                value.StateRevision == projection.StateRevision);
            var safe = request == null ? GameStateCloneService.DeepClone(projection) :
                InteractionRequestProjector.ProjectForPlayer(request, playerId, visible, gameId);
            if (request == null) safe.Presentation = null;
            presentationView = visible;
            presentationInput = GameStateCloneService.DeepClone(projection);
            presentationProjection = safe;
            presentationGameId = gameId;
            presentationPlayerId = playerId;
            return safe;
        }

        private static bool IsCharacterAbilityInteraction(string interactionTypeId)
        {
            return interactionTypeId == "lua.choice" ||
                   interactionTypeId == "effect.choice.execute" ||
                   interactionTypeId == "effect.map.location.target" ||
                   interactionTypeId == ResourceEffectExecutor.PaymentConfirmationInteractionTypeId ||
                   interactionTypeId == ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId ||
                   interactionTypeId == ContentPrimitiveEffectExecutor.QuantityInteractionTypeId ||
                   interactionTypeId == "character.cleanup.settlement" ||
                   interactionTypeId == "effect.influence.move.source" || interactionTypeId == "effect.influence.move.target" ||
                   interactionTypeId == CharacterLifecycleService.ModeInteractionTypeId || interactionTypeId == CharacterLifecycleService.OrderInteractionTypeId ||
                   interactionTypeId == CharacterLifecycleService.SecondModeInteractionTypeId ||
                   interactionTypeId == "operator.dispatch.choose" || interactionTypeId == "operator.dispatch.return" ||
                   interactionTypeId == "operator.replace.choose" || interactionTypeId == "operator.replace.hand" ||
                   interactionTypeId == "setup.basic_character.discard" || interactionTypeId == "setup.basic_character.keep" || interactionTypeId == "round.supply.retire" ||
                   interactionTypeId == "reward.choose" ||
                   (!string.IsNullOrEmpty(interactionTypeId) &&
                    interactionTypeId.StartsWith("character.ability.", StringComparison.Ordinal));
        }

        private static bool IsLocationTargetInteraction(InteractionRequestProjection request)
        {
            return request != null && (request.PromptKey == "character.elysium.tactic.choose_raid" ||
                request.InteractionTypeId == "effect.map.location.target" && request.AnswerSchema == "candidate_id" &&
                request.MinSelections == 1 && request.MaxSelections == 1 &&
                (request.PromptKey == "core:interaction.token.initial.choose" || request.PromptKey == "core:interaction.token.adjacent.choose"));
        }

        private string RequestTitle(InteractionRequestProjection request)
        {
            if (request.PromptKey == "effect.player.choose_target") return dialog.Copy.PlayerTargetSelectionTitle;
            if (ExpansionUi == null) return dialog.Copy.CharacterSelectionTitle;
            if (request.InteractionTypeId.StartsWith("operator.", StringComparison.Ordinal)) return ExpansionUi.Copy.DispatchTitle;
            if (request.InteractionTypeId == "setup.basic_character.keep") return ExpansionUi.Copy.RetainTitle;
            if (request.InteractionTypeId == "setup.basic_character.discard") return ExpansionUi.Copy.DiscardTitle;
            if (request.InteractionTypeId == "round.supply.retire") return ExpansionUi.Copy.SupplyTitle;
            if (request.InteractionTypeId == "reward.choose") return ExpansionUi.Copy.RewardTitle;
            return request.InteractionTypeId.StartsWith("character.use.", StringComparison.Ordinal) ? ExpansionUi.Copy.CharacterTitle : dialog.Copy.CharacterSelectionTitle;
        }
        private string RequestPrompt(InteractionRequestProjection request)
        {
            var document = ExpansionUi == null ? null : EffectDialogTextDocuments.ResolvePrompt(request, ExpansionUi.ResolveGameplayText);
            if (document != null) return document.ToPlainText();
            if (request.PromptKey == "effect.player.choose_target") return dialog.Copy.PlayerTargetSelectionDescription;
            if (request.InteractionTypeId == "character.cleanup.settlement") return dialog.Copy.CleanupSettlementPrompt;
            if (request.InteractionTypeId == ContentPrimitiveEffectExecutor.QuantityInteractionTypeId) return dialog.Copy.QuantitySelectionTitle;
            if (request.PromptKey == "effect.resource.choose_type") return dialog.Copy.ResourceTypeSelectionDescription;
            if (ExpansionUi == null) return BuildPrompt(request.PromptKey);
            var copy = ExpansionUi.Copy;
            switch (request.InteractionTypeId)
            {
                case "character.use.mode":
                case "character.use.order":
                case "character.use.second_mode":
                    var view = ExpansionUi.VisibleView();
                    var use = view?.CharacterUses.LastOrDefault(value => value.OwnerEffectId == request.OwnerEffectId);
                    var item = view?.ItemZones.Items.Find(value => value.ItemId == use?.CardInstanceId);
                    return (request.InteractionTypeId == "character.use.mode" ? copy.CharacterPrompt :
                        request.InteractionTypeId == "character.use.second_mode" ? ExpansionUi.Description(CharacterLifecycleService.SecondModeInteractionTypeId) : copy.OrderPrompt) +
                        (item == null ? string.Empty : "\n" + string.Format(copy.CurrentCharacterFormat, ExpansionUi.CardName(item.DefinitionId)));
                case "operator.dispatch.choose": return copy.DispatchPrompt;
                case "operator.dispatch.return": return copy.DispatchReturnPrompt;
                case "operator.replace.choose":
                case "operator.replace.hand": return copy.ReplacePrompt;
                case "setup.basic_character.keep": return copy.LegacyKeepUnsupported;
                case "setup.basic_character.discard": return string.Format(copy.DiscardPrompt, request.MinSelections);
                case "round.supply.retire": return copy.SupplyPrompt;
                case "reward.choose": return copy.RewardPrompt;
                default:
                    var configured = ExpansionUi.Description(request.PromptKey);
                    return (string.IsNullOrEmpty(configured) ? BuildPrompt(request.PromptKey) : configured) + ExpansionUi.RequestProgress(request);
            }
        }
        private string RequestLabel(string id, InteractionRequestProjection request)
        {
            var label = request.Presentation?.Options.Find(value => value.CandidateId == id)?.Label;
            var document = ExpansionUi?.ResolveGameplayText(label);
            if (document != null) return document.ToPlainText();
            if (ExpansionUi != null && (request.InteractionTypeId == CharacterLifecycleService.ModeInteractionTypeId ||
                request.InteractionTypeId == CharacterLifecycleService.OrderInteractionTypeId || request.InteractionTypeId == CharacterLifecycleService.SecondModeInteractionTypeId))
                return id == CharacterLifecycleService.FinishChoiceId || id == CharacterLifecycleService.CancelChoiceId
                    ? ExpansionUi.ResolveText("character.use." + id) : ExpansionUi.ModeName(id);
            var definitionId = GameplayExpansionUi.CandidateDefinition(request, id);
            if (!string.IsNullOrEmpty(definitionId))
                return ExpansionUi?.ItemName(definitionId) ?? CharacterCardDatabase.Get(getState(), definitionId)?.Name ?? definitionId;
            if (ExpansionUi != null)
            {
                var visible = ExpansionUi.VisibleView();
                var item = visible?.ItemZones.Items.Find(value => value.ItemId == id);
                if (item != null) return ExpansionUi.ItemName(item.DefinitionId) + "\n" +
                    string.Format(ExpansionUi.Copy.OwnerFormat, ExpansionUi.PlayerName(visible, item.OwnerSubject), ExpansionUi.PlayerName(visible, item.ControllerSubject));
                var configured = ExpansionUi.ResolveText(id);
                if (configured != id) return configured;
                var mapCandidate = ExpansionUi.MapCandidateLabel(id);
                if (!string.IsNullOrEmpty(mapCandidate)) return mapCandidate;
            }
            return FormatCandidateLabel(id, request);
        }

        private void BindInfluenceSlotCandidates(InteractionRequestProjection request)
        {
            influenceSourceCandidates.Clear();
            if (request.CandidateIds == null) return;
            // 放置与移动目标的正式候选本来就是空槽位；占用影响力的选择则保留其原物品身份。
            if (request.PromptKey == "effect.influence.place.choose_target" || request.PromptKey == "effect.influence.move.choose_target" ||
                request.InteractionTypeId == "effect.influence.place.target" || request.InteractionTypeId == "effect.influence.move.target")
            {
                foreach (var candidate in request.CandidateIds) influenceSourceCandidates[candidate] = candidate;
                return;
            }
            GameStateView visible;
            if (GetVisibleState != null) visible = GetVisibleState();
            else
            {
                using (PlayerProjectionMarker.Auto()) visible = GameStateViewProjector.ProjectForPlayer(getState(), getLocalPlayerId());
            }
            if (visible?.Map?.Influences == null) return;
            foreach (var influence in visible.Map.Influences)
            {
                if (string.IsNullOrEmpty(influence.SlotId)) continue;
                var candidate = request.CandidateIds.Contains(influence.InfluenceId) ? influence.InfluenceId :
                    request.CandidateIds.Contains(influence.SlotId) ? influence.SlotId : null;
                if (candidate != null) influenceSourceCandidates[influence.SlotId] = candidate;
            }
        }

        private static bool IsInfluenceMoveChoice(string interactionTypeId) =>
            interactionTypeId == "character.ability.choice.move";

        private bool IsCurrentInfluenceMoveRequest(InteractionRequest request)
        {
            return IsInfluenceMoveChoice(renderedRequest?.InteractionTypeId) && IsCurrentPresentedRequest(request);
        }

        private bool IsCurrentPresentedRequest(InteractionRequest request)
        {
            if (!string.IsNullOrEmpty(inFlightCommandId) || confirmedIntent.IsBusy || CanSubmit?.Invoke() == false || request == null || renderedRequest == null ||
                request.StateRevision != renderedRevision || renderedRequest.StateRevision != renderedRevision) return false;
            var fresh = InteractionRequestProjector.ProjectForPlayer(request, getLocalPlayerId());
            var observedRevision = GetVisibleState?.Invoke()?.StateRevision ?? getState()?.EffectRuntime?.StateRevision;
            return (!observedRevision.HasValue || observedRevision.Value == renderedRevision) && CanRender(fresh) &&
                InteractionCommandIntent.SameContract(renderedGameId, renderedRequest, getState()?.GameId, fresh);
        }

        private static bool CanSelectInfluenceMapArray(InteractionRequestProjection request) =>
            EffectDialogSelectionSpec.IsInfluenceMapArray(request) && request.MinSelections > 0 && request.MinSelections == request.MaxSelections;

        private static bool CanPresentInfluenceMapTarget(InteractionRequestProjection request) =>
            EffectDialogSelectionSpec.IsInfluenceMapTarget(request) &&
            (request.AnswerSchema == "candidate_id" || CanSelectInfluenceMapArray(request));

        private List<WorkflowHighlight> BuildInfluenceTargetHighlights(InteractionRequestProjection request)
        {
            var result = new List<WorkflowHighlight>();
            foreach (var source in influenceSourceCandidates)
                result.Add(new WorkflowHighlight(WorkflowHighlightTargetKind.InfluenceSlot, source.Key,
                    request.InteractionTypeId == "effect.influence.place.target" ? WorkflowHighlightSemantic.DeployTarget :
                        request.InteractionTypeId == "effect.influence.move.target" ? WorkflowHighlightSemantic.DispatchTarget :
                        InfluenceTargetSemantic(request.PromptKey), selectedCandidates.Contains(source.Value)
                        ? WorkflowHighlightState.Selected : WorkflowHighlightState.Available));
            return result;
        }

        private void BindInfluenceMoveCandidates(InteractionRequestProjection request)
        {
            influenceMoveCandidates.Clear();
            influenceSourceCandidates.Clear();
            if (request.AnswerSchema != "candidate_id" || request.MinSelections != 1 || request.MaxSelections != 1 || request.CandidateIds == null)
                throw new InvalidOperationException("地图移动请求缺少单个完整候选合同。");
            foreach (var candidate in request.CandidateIds)
            {
                var parts = candidate?.Split('|');
                if (parts == null || parts.Length != 3 || parts[0] != "move" ||
                    string.IsNullOrEmpty(parts[1]) || string.IsNullOrEmpty(parts[2]))
                    throw new InvalidOperationException("地图移动请求包含无效的完整移动候选。");
                if (!influenceMoveCandidates.TryGetValue(parts[1], out var targets))
                    influenceMoveCandidates.Add(parts[1], targets = new Dictionary<string, string>(StringComparer.Ordinal));
                targets[parts[2]] = candidate;
            }
            GameStateView visible;
            if (GetVisibleState != null) visible = GetVisibleState();
            else
            {
                using (PlayerProjectionMarker.Auto()) visible = GameStateViewProjector.ProjectForPlayer(getState(), getLocalPlayerId());
            }
            if (visible?.Map?.Influences != null)
                foreach (var influence in visible.Map.Influences)
                    if (influence != null && !string.IsNullOrEmpty(influence.InfluenceId) && !string.IsNullOrEmpty(influence.SlotId) &&
                        influenceMoveCandidates.ContainsKey(influence.InfluenceId))
                        influenceSourceCandidates[influence.SlotId] = influence.InfluenceId;
            if (!string.IsNullOrEmpty(influenceMoveSourceId) &&
                (!influenceSourceCandidates.TryGetValue(influenceMoveSourceSlotId, out var currentSource) ||
                 currentSource != influenceMoveSourceId))
            {
                influenceMoveSourceId = influenceMoveSourceSlotId = string.Empty;
            }
        }

        private string InfluenceMovePrompt() => string.IsNullOrEmpty(influenceMoveSourceId)
            ? dialog.Copy.InfluenceMoveSourcePrompt : dialog.Copy.InfluenceMoveTargetPrompt;

        private InteractionPresentation BuildInfluenceMovePresentation()
        {
            var selectingSource = string.IsNullOrEmpty(influenceMoveSourceId);
            var slots = selectingSource ? influenceSourceCandidates.Keys.ToArray() :
                influenceMoveCandidates[influenceMoveSourceId].Keys.ToArray();
            return new InteractionPresentation(BuildTargetHighlights(slots, WorkflowHighlightTargetKind.InfluenceSlot,
                selectingSource ? WorkflowHighlightSemantic.DispatchSource : WorkflowHighlightSemantic.DispatchTarget),
                InfluenceMovePrompt(), InteractionMode.Busy, true);
        }

        private void ClearInfluenceMoveDraft()
        {
            // 仅隔离本地地图首击；不写入请求、命令或权威revision。
            influenceMoveDraftGeneration++;
            influenceMoveSourceId = influenceMoveSourceSlotId = string.Empty;
            influenceMoveCandidates.Clear();
        }

        private static bool IsInfluenceTargetInteraction(string promptKey)
        {
            return promptKey == "effect.influence.move.choose_target" || promptKey == "effect.influence.remove.choose_target" || promptKey == "effect.influence.replace.choose_target" || promptKey == "effect.influence.place.choose_target" ||
                   promptKey == "character.liskarm.tactic.choose_target" ||
                   promptKey == "character.texas.tactic.choose_removal";
        }

        private static WorkflowHighlightSemantic InfluenceTargetSemantic(string promptKey)
        {
            if (promptKey == "effect.influence.move.choose_target") return WorkflowHighlightSemantic.DispatchTarget;
            return promptKey == "effect.influence.place.choose_target"
                ? WorkflowHighlightSemantic.DeployTarget
                : WorkflowHighlightSemantic.EventInfluenceTarget;
        }

        private static List<WorkflowHighlight> BuildTargetHighlights(
            IReadOnlyList<string> candidateIds,
            WorkflowHighlightTargetKind targetKind,
            WorkflowHighlightSemantic semantic)
        {
            var highlights = new List<WorkflowHighlight>();
            if (candidateIds == null) return highlights;

            for (var i = 0; i < candidateIds.Count; i++)
            {
                if (!string.IsNullOrEmpty(candidateIds[i]))
                {
                    highlights.Add(new WorkflowHighlight(targetKind, candidateIds[i], semantic));
                }
            }

            return highlights;
        }

        private string BuildPrompt(string promptKey)
        {
            switch (promptKey ?? string.Empty)
            {
                case "effect.repeat.continue": return dialog.Copy.RepeatContinuePrompt;
                case "effect.resource.sell": return "选择要出售的资源数量，或结束出售。";
                case "effect.influence.replace.choose_target": return "请选择要替换的对手影响力。";
                case "facility.entry.choose_influence_branch": return "选择替换或放置 1 个影响力。";
                case "effect.influence.place.choose_target":
                    return "请选择地图上高亮的空格放置影响力。";
                case "character.elysium.strategy.choose_resource":
                    return "极境策略：选择一种基础资源。";
                case "character.cannot.tactic.choose_resource":
                    return "坎诺特计谋：选择一种基础资源进行征收。";
                case "character.cannot.strategy.choose_sale":
                    return "坎诺特策略：选择要出售的资源数量。";
                case "character.liskarm.tactic.choose_target":
                    return "雷蛇战术：选择要控制的影响力目标。";
                case "character.elysium.tactic.choose_raid":
                    return "极境计谋：选择相邻、已探索且有己方影响力的资源点移动城市。";
                case "character.texas.strategy.choose_facility":
                    return "德克萨斯策略：选择一张设施牌。";
                case "effect.influence.remove.choose_target":
                    return "选择要移除的影响力。";
                case "character.texas.tactic.choose_removal":
                    return "德克萨斯战术：选择要移除的影响力。";
                case "effect.influence.move.choose_source": return dialog.Copy.InfluenceMoveSourcePrompt;
                case "effect.influence.move.choose_target": return dialog.Copy.InfluenceMoveTargetPrompt;
                case "character.texas.tactic.choose_first_move":
                    return "德克萨斯战术：选择第一次移动。";
                case "character.texas.tactic.choose_second_move":
                    return "德克萨斯战术：选择第二次移动。";
                case "character.tin_man.strategy.choose_purchase":
                    return "锡人策略：选择是否购买至纯源石。";
                case "character.tin_man.strategy.choose_second_purchase":
                    return "锡人策略：选择是否进行第二次购买。";
                case "character.tin_man.tactic.choose_discard_resolution":
                    return "锡人战术：选择弃牌带来的奖励。";
                default:
                    return "请完成当前角色能力选择。";
            }
        }

        private string FormatCandidateLabel(string candidateId, InteractionRequestProjection request)
        {
            if (string.IsNullOrEmpty(candidateId)) return "未命名选项";
            if (request.PromptKey == "effect.repeat.continue")
            {
                if (candidateId == "continue") return dialog.Copy.RepeatContinueLabel;
                if (candidateId == "stop") return dialog.Copy.RepeatStopLabel;
            }
            int candidateIndex = request.CandidateIds.IndexOf(candidateId);
            var definitions = YC.Domain.Effects.ItemTransferEffectExecutor.Value(request.PromptParameters, "candidateDefinitionIds");
            if (definitions?.Kind == NormalizedValueKind.Array && candidateIndex >= 0 && candidateIndex < definitions.Items.Count)
            {
                var definition = YC.Domain.Facilities.FacilityCardDatabase.Get(definitions.Items[candidateIndex].StringValue);
                if (definition != null) return definition.Name;
            }

            var parts = candidateId.Split('|');
            if (parts.Length == 3 && parts[0] == "sale")
            {
                return FormatResource(parts[1]) + " × " + parts[2];
            }

            if (parts.Length == 3 && parts[0] == "place")
            {
                return "放置到 " + parts[1] + " 和 " + parts[2];
            }

            if (parts.Length == 3 && parts[0] == "move")
            {
                return "从 " + parts[1] + " 移动到 " + parts[2];
            }

            return FormatResource(candidateId);
        }

        private static string FormatResource(string resourceId)
        {
            switch (resourceId ?? string.Empty)
            {
                case "originium": return "源岩";
                case "originium-shard": return "源石碎片";
                case "iron": return "异铁";
                case "pure-originium": return "至纯源石";
                case "choice.finish_character_use": return "结束角色牌使用";
                case "choice.purchase_first_pure_originium": return "支付 12 金券购买至纯源石";
                case "choice.purchase_second_pure_originium": return "支付 15 金券购买至纯源石";
                case "choice.gain_gold":
                case CharacterEffectChoiceIds.GainGold: return "获得 5 金券";
                case "replace-influence": return "替换 1 个对手影响力";
                case "place-influence": return "放置 1 个己方影响力";
                case "move-influence": return "移动 1 枚己方影响力";
                default: return resourceId;
            }
        }
    }
}
