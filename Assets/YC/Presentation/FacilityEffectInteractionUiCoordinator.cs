using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Economy;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>
    /// 展示设施入场待选会话并收集玩家输入。合法性始终由 ResolveFacilityEffectCommandHandler 判断。
    /// </summary>
    internal sealed class FacilityEffectInteractionUiCoordinator : IDisposable
    {
        private enum SelectionStage
        {
            None,
            AdditionalFacility,
            ExtensionHub,
            MercenaryBranch,
            MercenaryReplace,
            MercenaryDeploy,
            DeployInfluences,
            WarehouseBranch,
            WarehouseRemove,
            WarehouseSource,
            WarehouseTarget,
            WarehouseExplore,
            FreeCityMove
        }

        private readonly Func<GameState> getState;
        private readonly Func<int> getLocalPlayerId;
        private readonly Func<RectTransform> getCanvas;
        private readonly IMapQueryService mapQuery;
        private readonly FacilityEffectChoiceDialog dialog;
        private readonly FacilityEffectPendingChoicePresenter presenter;
        private readonly Action<IReadOnlyList<WorkflowHighlight>> setHighlights;
        private readonly Action clearHighlights;
        private readonly Action<PendingCardSessionState, string> beginAdditionalExplore;
        private readonly Action cancelAdditionalExplore;
        private readonly Action<GameCommand> submit;
        private readonly Action<string> setPrompt;
        private readonly BuildFacilityService buildFacilityService = new BuildFacilityService();
        private readonly BuildFacilitySelectionController additionalBuildSelection =
            new BuildFacilitySelectionController();
        private readonly InfluenceService influenceService;
        private Action<BuildFacilityDraftViewModel> showAdditionalBuildDraft;
        private Action hideAdditionalBuildDraft;
        private string sessionId = string.Empty;
        private string inFlightCommandId = string.Empty;
        private string inFlightSessionId = string.Empty;
        private SelectionStage stage;
        private string selectedOptionId = string.Empty;
        private string removeInfluenceSlotId = string.Empty;
        private string sourceInfluenceSlotId = string.Empty;
        private readonly List<string> deployInfluenceSlotIds = new List<string>();
        private bool extensionHubDragging;
        private bool additionalBuildDraftVisible;

        public FacilityEffectInteractionUiCoordinator(
            Func<GameState> getState,
            Func<int> getLocalPlayerId,
            Func<RectTransform> getCanvas,
            IMapQueryService mapQuery,
            FacilityEffectChoiceDialog dialog,
            Action<IReadOnlyList<WorkflowHighlight>> setHighlights,
            Action clearHighlights,
            Action<PendingCardSessionState, string> beginAdditionalExplore,
            Action cancelAdditionalExplore,
            Action<GameCommand> submit,
            Action<string> setPrompt)
        {
            this.getState = getState ?? throw new ArgumentNullException(nameof(getState));
            this.getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
            this.getCanvas = getCanvas ?? throw new ArgumentNullException(nameof(getCanvas));
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            this.setHighlights = setHighlights ?? throw new ArgumentNullException(nameof(setHighlights));
            this.clearHighlights = clearHighlights ?? throw new ArgumentNullException(nameof(clearHighlights));
            this.beginAdditionalExplore = beginAdditionalExplore ??
                                          throw new ArgumentNullException(nameof(beginAdditionalExplore));
            this.cancelAdditionalExplore = cancelAdditionalExplore ??
                                           throw new ArgumentNullException(nameof(cancelAdditionalExplore));
            this.submit = submit ?? throw new ArgumentNullException(nameof(submit));
            this.setPrompt = setPrompt ?? throw new ArgumentNullException(nameof(setPrompt));
            influenceService = new InfluenceService(mapQuery);
            presenter = new FacilityEffectPendingChoicePresenter();
        }

        public bool IsActive
        {
            get
            {
                PendingCardSessionState ignored;
                return presenter.TryGetPending(getState(), getLocalPlayerId(), out ignored);
            }
        }

        public bool CanDragAdditionalBuild
        {
            get
            {
                PendingCardSessionState pending;
                return stage == SelectionStage.AdditionalFacility &&
                       presenter.TryGetPending(getState(), getLocalPlayerId(), out pending) &&
                       pending.ChoiceType == FacilityPendingChoiceTypes.BuildAdditionalFacility;
            }
        }

        public bool IsExtensionHubDragging
        {
            get { return extensionHubDragging && stage == SelectionStage.ExtensionHub; }
        }

        public void ConfigureAdditionalBuildDraftView(
            Action<BuildFacilityDraftViewModel> show,
            Action hide)
        {
            showAdditionalBuildDraft = show ?? throw new ArgumentNullException(nameof(show));
            hideAdditionalBuildDraft = hide ?? throw new ArgumentNullException(nameof(hide));
        }

        public bool Synchronize()
        {
            PendingCardSessionState pending;
            if (!presenter.TryGetPending(getState(), getLocalPlayerId(), out pending))
            {
                ResetAndHide();
                return false;
            }

            if (!string.IsNullOrEmpty(inFlightSessionId) &&
                !string.Equals(inFlightSessionId, pending.SessionId, StringComparison.Ordinal))
            {
                ClearSubmissionInFlight();
            }

            var sessionChanged = !string.Equals(sessionId, pending.SessionId, StringComparison.Ordinal);
            if (sessionChanged)
            {
                ResetForPending(pending);
            }

            if (sessionChanged || (!dialog.IsShowing && !additionalBuildDraftVisible))
            {
                Render(pending);
            }

            return true;
        }

        public bool TryHandleInfluenceSlotClicked(string slotId)
        {
            PendingCardSessionState pending;
            if (!presenter.TryGetPending(getState(), getLocalPlayerId(), out pending))
            {
                return false;
            }

            switch (stage)
            {
                case SelectionStage.MercenaryReplace:
                    if (IsOpponentInfluenceSlot(slotId))
                    {
                        Submit(
                            pending,
                            FacilityPendingChoiceTypes.ReplaceInfluenceOption,
                            Parameters(
                                ResolveFacilityEffectCommandHandler.TargetInfluenceSlotIdParameter,
                                slotId));
                    }
                    else
                    {
                        setPrompt("请选择高亮的影响力进行替换。");
                    }
                    break;
                case SelectionStage.MercenaryDeploy:
                    if (string.IsNullOrEmpty(slotId) ||
                        !influenceService.CanPlace(getState(), getLocalPlayerId(), slotId).IsValid)
                    {
                        setPrompt("请选择高亮的空槽位放置影响力。");
                        break;
                    }

                    Submit(
                        pending,
                        FacilityPendingChoiceTypes.DeployInfluenceOption,
                        Parameters(
                            ResolveFacilityEffectCommandHandler.TargetInfluenceSlotIdParameter,
                            slotId));
                    break;
                case SelectionStage.DeployInfluences:
                    if (string.IsNullOrEmpty(slotId))
                    {
                        setPrompt("请选择高亮的空槽位。");
                        break;
                    }

                    if (deployInfluenceSlotIds.Contains(slotId))
                    {
                        setPrompt("请选择另一个高亮空槽位。");
                        break;
                    }

                    var deploymentValidation = influenceService.CanPlace(
                        getState(),
                        getLocalPlayerId(),
                        slotId);
                    if (!deploymentValidation.IsValid)
                    {
                        setPrompt("请选择高亮的空槽位。");
                        break;
                    }

                    deployInfluenceSlotIds.Add(slotId);
                    if (deployInfluenceSlotIds.Count < 2)
                    {
                        ShowDeployInfluences(pending);
                        break;
                    }

                    Submit(
                        pending,
                        FacilityPendingChoiceTypes.ConfirmOption,
                        Parameters(
                            ResolveFacilityEffectCommandHandler.InfluenceSlotIdsParameter,
                            string.Join(",", deployInfluenceSlotIds.ToArray())));
                    break;
                case SelectionStage.WarehouseRemove:
                    removeInfluenceSlotId = slotId ?? string.Empty;
                    stage = SelectionStage.WarehouseSource;
                    Render(pending);
                    break;
                case SelectionStage.WarehouseSource:
                    sourceInfluenceSlotId = slotId ?? string.Empty;
                    stage = SelectionStage.WarehouseTarget;
                    Render(pending);
                    break;
                case SelectionStage.WarehouseTarget:
                    SubmitWarehouseRemoveDispatch(pending, slotId);
                    break;
                default:
                    setPrompt("当前设施效果不接受地图影响力槽位选择。");
                    break;
            }

            return true;
        }

        public bool TryHandleLocationClicked(string locationId)
        {
            PendingCardSessionState pending;
            if (!presenter.TryGetPending(getState(), getLocalPlayerId(), out pending))
            {
                return false;
            }

            if (stage == SelectionStage.WarehouseExplore)
            {
                return false;
            }

            if (stage == SelectionStage.FreeCityMove)
            {
                Submit(
                    pending,
                    FacilityPendingChoiceTypes.ConfirmOption,
                    Parameters(ResolveFacilityEffectCommandHandler.TargetLocationIdParameter, locationId));
            }
            else
            {
                setPrompt("当前设施效果不接受地图地点选择。");
            }

            return true;
        }

        public bool TryBeginAdditionalBuildDrag(string facilityId)
        {
            PendingCardSessionState pending;
            if (!presenter.TryGetPending(getState(), getLocalPlayerId(), out pending) ||
                pending.ChoiceType != FacilityPendingChoiceTypes.BuildAdditionalFacility ||
                stage != SelectionStage.AdditionalFacility ||
                string.IsNullOrEmpty(facilityId) ||
                !pending.OptionIds.Contains(facilityId))
            {
                return false;
            }

            EnsureAdditionalBuildSelection(pending);
            string reason;
            if (!additionalBuildSelection.TryBeginDrag(getState(), facilityId, out reason))
            {
                setPrompt(reason);
                Render(pending);
                return true;
            }

            dialog.Hide();
            HideAdditionalBuildDraft();
            return true;
        }

        public bool TryHandleAdditionalBuildDrop(string facilityId, int cityBoardSlotIndex)
        {
            PendingCardSessionState pending;
            if (!presenter.TryGetPending(getState(), getLocalPlayerId(), out pending) ||
                pending.ChoiceType != FacilityPendingChoiceTypes.BuildAdditionalFacility ||
                stage != SelectionStage.AdditionalFacility ||
                string.IsNullOrEmpty(facilityId) ||
                !pending.OptionIds.Contains(facilityId) ||
                !string.Equals(
                    additionalBuildSelection.FacilityId,
                    facilityId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            string reason;
            if (!additionalBuildSelection.TryDrop(getState(), cityBoardSlotIndex, out reason))
            {
                setPrompt(reason);
                EnsureAdditionalBuildSelection(pending);
                Render(pending);
                return true;
            }

            Render(pending);
            return true;
        }

        public bool TryHandleAdditionalBuildEscape()
        {
            PendingCardSessionState pending;
            if (!presenter.TryGetPending(getState(), getLocalPlayerId(), out pending) ||
                pending.ChoiceType != FacilityPendingChoiceTypes.BuildAdditionalFacility ||
                !additionalBuildSelection.IsActive)
            {
                return false;
            }

            CollapseAdditionalBuildToGhost(pending);
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

        private void ResetForPending(PendingCardSessionState pending)
        {
            ClearSubmissionInFlight();
            HideAdditionalBuildDraft();
            additionalBuildSelection.Cancel();
            sessionId = pending.SessionId;
            selectedOptionId = string.Empty;
            removeInfluenceSlotId = string.Empty;
            sourceInfluenceSlotId = string.Empty;
            deployInfluenceSlotIds.Clear();
            extensionHubDragging = false;
            stage = InitialStage(pending.ChoiceType);
            if (pending.ChoiceType == FacilityPendingChoiceTypes.BuildAdditionalFacility)
            {
                EnsureAdditionalBuildSelection(pending);
            }
        }

        private void ResetAndHide()
        {
            ClearSubmissionInFlight();
            HideAdditionalBuildDraft();
            additionalBuildSelection.Cancel();
            sessionId = string.Empty;
            stage = SelectionStage.None;
            selectedOptionId = string.Empty;
            removeInfluenceSlotId = string.Empty;
            sourceInfluenceSlotId = string.Empty;
            deployInfluenceSlotIds.Clear();
            extensionHubDragging = false;
            dialog.Hide();
        }

        private void Render(PendingCardSessionState pending)
        {
            clearHighlights();
            switch (pending.ChoiceType)
            {
                case FacilityPendingChoiceTypes.CopyAdjacentEntryEffect:
                    ShowCopyAdjacent(pending);
                    return;
                case FacilityPendingChoiceTypes.BuildAdditionalFacility:
                    ShowAdditionalBuild(pending);
                    return;
                case FacilityPendingChoiceTypes.BuildExtensionHub:
                    ShowExtensionHub(pending);
                    return;
                case FacilityPendingChoiceTypes.SellResources:
                    ShowTrade(pending);
                    return;
                case FacilityPendingChoiceTypes.FreeCityMove:
                    ShowFreeCityMove();
                    return;
                case FacilityPendingChoiceTypes.ChooseFiveBasicResources:
                    ShowFiveResources(pending);
                    return;
                case FacilityPendingChoiceTypes.ReplaceOneInfluence:
                    ShowMercenary(pending);
                    return;
                case FacilityPendingChoiceTypes.DeployTwoInfluences:
                    ShowDeployInfluences(pending);
                    return;
                case FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore:
                    ShowWarehouse(pending);
                    return;
                default:
                    dialog.ShowMapPrompt(
                        getCanvas(),
                        "设施入场效果",
                        "暂不支持显示该设施待选类型：" + pending.ChoiceType,
                        string.Empty,
                        null);
                    return;
            }
        }

        private void ShowCopyAdjacent(PendingCardSessionState pending)
        {
            var options = new List<EffectDialogOption>();
            for (var i = 0; i < pending.OptionIds.Count; i++)
            {
                var optionId = pending.OptionIds[i];
                var captured = optionId;
                options.Add(new EffectDialogOption(
                    "城市面板槽位 " + optionId + " · " + GetFacilityNameAtSlot(optionId),
                    () => Submit(pending, captured, null)));
            }

            dialog.ShowOptions(getCanvas(), "附属能源设施", "选择一张十字相邻设施，重新结算它的入场效果。", options);
        }

        private void ShowAdditionalBuild(PendingCardSessionState pending)
        {
            EnsureAdditionalBuildSelection(pending);
            if (additionalBuildSelection.Phase == BuildFacilityDraftPhase.Focused ||
                additionalBuildSelection.Phase == BuildFacilityDraftPhase.Confirming ||
                additionalBuildSelection.Phase == BuildFacilityDraftPhase.Ghosted ||
                additionalBuildSelection.Phase == BuildFacilityDraftPhase.Dragging)
            {
                ShowAdditionalBuildDraft(pending);
                return;
            }

            HideAdditionalBuildDraft();
            dialog.ShowMapPrompt(
                getCanvas(),
                "简陋工程营",
                "拖动供应区中亮起的建设牌到城市面板空槽位，选择位置后进入标准建设确认流程。",
                string.Empty,
                null);
        }

        private void ShowAdditionalBuildDraft(PendingCardSessionState pending)
        {
            if (showAdditionalBuildDraft == null)
            {
                setPrompt("标准建设界面尚未就绪，请稍后重试。");
                ReturnToAdditionalBuildSelection(pending);
                return;
            }

            dialog.Hide();
            var state = getState();
            var facility = FacilityCardDatabase.Get(additionalBuildSelection.FacilityId);
            additionalBuildDraftVisible = true;
            showAdditionalBuildDraft(new BuildFacilityDraftViewModel(
                additionalBuildSelection.Phase,
                additionalBuildSelection.QueryOptions(state),
                additionalBuildSelection.QuerySelectedOption(state),
                facility,
                additionalBuildSelection.CityBoardSlotIndex,
                additionalBuildSelection.PaymentMode,
                additionalBuildSelection.ErrorMessage,
                additionalBuildSelection.QueryLegalSlotIndexes(state),
                intent => DispatchAdditionalBuildIntent(pending, intent)));
        }

        private void DispatchAdditionalBuildIntent(
            PendingCardSessionState pending,
            BuildFacilityIntent intent)
        {
            if (intent == null)
            {
                throw new ArgumentNullException(nameof(intent));
            }

            var beginDrag = intent as BuildFacilityIntent.BeginDrag;
            if (beginDrag != null)
            {
                TryBeginAdditionalBuildDrag(beginDrag.FacilityId);
                return;
            }

            if (intent is BuildFacilityIntent.BeginGhostDrag)
            {
                string reason;
                if (!additionalBuildSelection.TryBeginGhostDrag(out reason))
                {
                    setPrompt(reason);
                }

                Render(pending);
                return;
            }

            var drop = intent as BuildFacilityIntent.Drop;
            if (drop != null)
            {
                string reason;
                if (!additionalBuildSelection.TryDrop(
                        getState(),
                        drop.CityBoardSlotIndex,
                        out reason))
                {
                    setPrompt(reason);
                }

                Render(pending);
                return;
            }

            if (intent is BuildFacilityIntent.RejectDrop)
            {
                additionalBuildSelection.RejectDrop();
                Render(pending);
                return;
            }

            if (intent is BuildFacilityIntent.Escape)
            {
                CollapseAdditionalBuildToGhost(pending);
                return;
            }

            var selectPayment = intent as BuildFacilityIntent.SelectPayment;
            if (selectPayment != null)
            {
                SelectAdditionalBuildPayment(pending, selectPayment.PaymentMode);
                return;
            }

            if (intent is BuildFacilityIntent.Back)
            {
                BackToAdditionalBuildPayment(pending);
                return;
            }

            if (intent is BuildFacilityIntent.Confirm)
            {
                ConfirmAdditionalBuild(pending);
                return;
            }

            if (intent is BuildFacilityIntent.Cancel)
            {
                ReturnToAdditionalBuildSelection(pending);
                return;
            }

            throw new ArgumentException("不支持的额外建设交互意图。", nameof(intent));
        }

        private void CollapseAdditionalBuildToGhost(PendingCardSessionState pending)
        {
            if (additionalBuildSelection.Phase == BuildFacilityDraftPhase.Confirming)
            {
                additionalBuildSelection.BackToPayment();
            }

            if (additionalBuildSelection.Phase == BuildFacilityDraftPhase.Focused)
            {
                additionalBuildSelection.CollapseFocusToGhost();
                setPrompt("额外建设草稿已缩回本地虚影；拖动建设牌可继续，点击 × 可取消当前选择。");
                Render(pending);
            }
        }

        private void SelectAdditionalBuildPayment(PendingCardSessionState pending, string paymentMode)
        {
            string reason;
            if (!additionalBuildSelection.TrySelectPayment(getState(), paymentMode, out reason))
            {
                setPrompt(reason);
            }

            Render(pending);
        }

        private void BackToAdditionalBuildPayment(PendingCardSessionState pending)
        {
            additionalBuildSelection.BackToPayment();
            Render(pending);
        }

        private void ConfirmAdditionalBuild(PendingCardSessionState pending)
        {
            if (additionalBuildSelection.Phase != BuildFacilityDraftPhase.Confirming)
            {
                return;
            }

            SubmitAdditionalBuild(pending, additionalBuildSelection.PaymentMode);
        }

        private void ReturnToAdditionalBuildSelection(PendingCardSessionState pending)
        {
            HideAdditionalBuildDraft();
            additionalBuildSelection.BeginAdditionalBuild(getLocalPlayerId(), pending.OptionIds);
            stage = SelectionStage.AdditionalFacility;
            Render(pending);
        }

        private void EnsureAdditionalBuildSelection(PendingCardSessionState pending)
        {
            if (!additionalBuildSelection.IsActive)
            {
                additionalBuildSelection.BeginAdditionalBuild(getLocalPlayerId(), pending.OptionIds);
            }
        }

        private void HideAdditionalBuildDraft()
        {
            if (!additionalBuildDraftVisible)
            {
                return;
            }

            additionalBuildDraftVisible = false;
            if (hideAdditionalBuildDraft != null)
            {
                hideAdditionalBuildDraft();
            }
        }

        private void ShowExtensionHub(PendingCardSessionState pending)
        {
            var hubIds = new[]
            {
                FacilityCardDatabase.ExtensionHubBlue,
                FacilityCardDatabase.ExtensionHubYellow,
                FacilityCardDatabase.ExtensionHubRed
            };
            var hubs = new List<FacilityEffectCardOption>(hubIds.Length);
            for (var i = 0; i < hubIds.Length; i++)
            {
                var hubId = hubIds[i];
                hubs.Add(new FacilityEffectCardOption(
                    hubId,
                    "延伸枢纽",
                    pending.OptionIds.Contains(hubId)));
            }

            dialog.ShowExtensionHubOptions(
                getCanvas(),
                hubs,
                () => extensionHubDragging = true,
                (facilityId, slotIndex) =>
                {
                    extensionHubDragging = false;
                    if (slotIndex < 0)
                    {
                        setPrompt("请把延伸枢纽拖到城市面板的空槽位。");
                        return;
                    }

                    var state = getState();
                    if (state == null)
                    {
                        setPrompt("当前游戏状态不可用，请稍后重试。");
                        return;
                    }

                    var validation = buildFacilityService.ValidateReserveBuild(
                        state,
                        getLocalPlayerId(),
                        facilityId,
                        slotIndex);
                    if (!validation.IsValid)
                    {
                        setPrompt(validation.Reason);
                        return;
                    }

                    Submit(
                        pending,
                        facilityId,
                        Parameters(
                            BuildFacilityCommandHandler.CityBoardSlotIndexParameter,
                            slotIndex.ToString()));
                },
                () => extensionHubDragging = false,
                () =>
                {
                    extensionHubDragging = false;
                    Submit(pending, FacilityPendingChoiceTypes.SkipOption, null);
                });
        }

        private void ShowTrade(PendingCardSessionState pending)
        {
            var player = getState().FindPlayer(getLocalPlayerId());
            var maximums = player == null
                ? new[] { 0, 0, 0, 0 }
                : new[]
                {
                    player.Resources.Originium,
                    player.Resources.OriginiumShard,
                    player.Resources.Iron,
                    player.Resources.PureOriginium
                };
            dialog.ShowResourceAllocation(
                getCanvas(),
                "贸易街区",
                "选择出售数量：源岩每个 " + ResourceSaleService.OriginiumUnitPrice +
                " 金券，源石碎片每个 " + ResourceSaleService.OriginiumShardUnitPrice +
                " 金券，异铁每个 " + ResourceSaleService.IronUnitPrice +
                " 金券，至纯源石每个 " + ResourceSaleService.PureOriginiumUnitPrice + " 金券。",
                new[] { "源岩", "源石碎片", "异铁", "至纯源石" },
                maximums,
                -1,
                values => Submit(
                    pending,
                    FacilityPendingChoiceTypes.ConfirmOption,
                    ResourceParameters(values, true)),
                () => Submit(pending, FacilityPendingChoiceTypes.SkipOption, null));
        }

        private void ShowFiveResources(PendingCardSessionState pending)
        {
            dialog.ShowResourceAllocation(
                getCanvas(),
                "开采电铲",
                "在源岩、源石碎片和异铁之间恰好分配 5 个资源。",
                new[] { "源岩", "源石碎片", "异铁" },
                new[] { 5, 5, 5 },
                5,
                values => Submit(
                    pending,
                    FacilityPendingChoiceTypes.ConfirmOption,
                    ResourceParameters(values, false)),
                null);
        }

        private void ShowMercenary(PendingCardSessionState pending)
        {
            if (stage == SelectionStage.MercenaryBranch)
            {
                var canReplace = pending.OptionIds.Contains(FacilityPendingChoiceTypes.ReplaceInfluenceOption);
                var canDeploy = pending.OptionIds.Contains(FacilityPendingChoiceTypes.DeployInfluenceOption);
                var canClose = pending.OptionIds.Contains(FacilityPendingChoiceTypes.SkipOption);
                var options = new List<EffectDialogOption>
                {
                    new EffectDialogOption("替换 1 个影响力", () =>
                    {
                        selectedOptionId = FacilityPendingChoiceTypes.ReplaceInfluenceOption;
                        stage = SelectionStage.MercenaryReplace;
                        Render(pending);
                    }, canReplace),
                    new EffectDialogOption("放置 1 个影响力", () =>
                    {
                        selectedOptionId = FacilityPendingChoiceTypes.DeployInfluenceOption;
                        stage = SelectionStage.MercenaryDeploy;
                        Render(pending);
                    }, canDeploy)
                };

                ShowOrBranchOptions(
                    pending,
                    "佣兵指挥部",
                    "佣兵指挥部 · 选择分支",
                    options,
                    canClose);
                return;
            }

            if (stage == SelectionStage.MercenaryReplace)
            {
                setHighlights(BuildOpponentInfluenceHighlights());
                dialog.ShowCollapsibleMapPrompt(
                    getCanvas(),
                    "佣兵指挥部 · 替换",
                    "点击地图上的高亮影响力，将它替换为你的影响力。",
                    "佣兵指挥部 · 替换影响力",
                    string.Empty,
                    null,
                    () =>
                    {
                        selectedOptionId = string.Empty;
                        stage = SelectionStage.MercenaryBranch;
                        Render(pending);
                    });
                return;
            }

            setHighlights(BuildDeployInfluenceSlotHighlights());
            dialog.ShowCollapsibleMapPrompt(
                getCanvas(),
                "佣兵指挥部 · 放置",
                "点击地图上的一个高亮空槽位，放置你的影响力。",
                "佣兵指挥部 · 放置影响力",
                string.Empty,
                null,
                () =>
                {
                    selectedOptionId = string.Empty;
                    stage = SelectionStage.MercenaryBranch;
                    Render(pending);
                });
        }

        private void ShowDeployInfluences(PendingCardSessionState pending)
        {
            setHighlights(BuildDeployInfluenceSlotHighlights());
            dialog.ShowCollapsibleMapPrompt(
                getCanvas(),
                "护航调度中心",
                "请依次点击地图上的两个高亮空槽位。",
                "护航调度中心 · 放置两个影响力",
                string.Empty,
                null,
                null,
                true);
        }

        private void ShowFreeCityMove()
        {
            setHighlights(BuildAllLocationHighlights(WorkflowHighlightSemantic.MoveTarget));
            dialog.ShowCollapsibleMapPrompt(
                getCanvas(),
                "高性能动力设施",
                "点击地图地点，尝试执行一次免费城市移动。",
                "高性能动力设施 · 免费城市移动",
                string.Empty,
                null,
                null,
                false);
        }

        private void ShowWarehouse(PendingCardSessionState pending)
        {
            if (stage == SelectionStage.WarehouseBranch)
            {
                var canRemoveDispatch = pending.OptionIds.Contains(FacilityPendingChoiceTypes.RemoveDispatchOption);
                var canExplore = pending.OptionIds.Contains(FacilityPendingChoiceTypes.ExploreOption);
                var canClose = pending.OptionIds.Contains(FacilityPendingChoiceTypes.SkipOption);
                var options = new List<EffectDialogOption>
                {
                    new EffectDialogOption("移除一个影响力，然后调度", () =>
                    {
                        selectedOptionId = FacilityPendingChoiceTypes.RemoveDispatchOption;
                        stage = SelectionStage.WarehouseRemove;
                        Render(pending);
                    }, canRemoveDispatch),
                    new EffectDialogOption("执行一次正常探索", () =>
                    {
                        selectedOptionId = FacilityPendingChoiceTypes.ExploreOption;
                        stage = SelectionStage.WarehouseExplore;
                        Render(pending);
                        beginAdditionalExplore(pending, FacilityPendingChoiceTypes.ExploreOption);
                    }, canExplore)
                };

                ShowOrBranchOptions(
                    pending,
                    "载具仓库",
                    "载具仓库 · 选择分支",
                    options,
                    canClose);
                return;
            }

            if (stage == SelectionStage.WarehouseExplore)
            {
                clearHighlights();
                dialog.ShowCollapsibleMapPrompt(
                    getCanvas(),
                    "载具仓库 · 探索",
                    "请按正常探索流程选择目标、同优路线与路费接收者。",
                    "载具仓库 · 探索",
                    string.Empty,
                    null,
                    () =>
                    {
                        cancelAdditionalExplore();
                        stage = SelectionStage.WarehouseBranch;
                        Render(pending);
                    });
                return;
            }

            if (stage == SelectionStage.WarehouseRemove)
            {
                setHighlights(BuildPlacedInfluenceHighlights(null, WorkflowHighlightSemantic.EventInfluenceTarget));
                dialog.ShowCollapsibleMapPrompt(
                    getCanvas(),
                    "载具仓库 · 移除",
                    "点击一个已有影响力。完成移除选择后还必须执行一次调度。",
                    "载具仓库 · 移除影响力",
                    string.Empty,
                    null,
                    () =>
                    {
                        stage = SelectionStage.WarehouseBranch;
                        Render(pending);
                    });
                return;
            }

            if (stage == SelectionStage.WarehouseSource)
            {
                setHighlights(BuildPlacedInfluenceHighlights(getLocalPlayerId(), WorkflowHighlightSemantic.DispatchSource));
                dialog.ShowCollapsibleMapPrompt(
                    getCanvas(),
                    "载具仓库 · 调度来源",
                    "点击自己的一个影响力作为调度来源。",
                    "载具仓库 · 选择调度来源",
                    string.Empty,
                    null,
                    () =>
                    {
                        stage = SelectionStage.WarehouseRemove;
                        Render(pending);
                    });
                return;
            }

            setHighlights(BuildAllInfluenceSlotHighlights(WorkflowHighlightSemantic.DispatchTarget));
            dialog.ShowCollapsibleMapPrompt(
                getCanvas(),
                "载具仓库 · 调度目标",
                "点击调度目标槽位。",
                "载具仓库 · 选择调度目标",
                string.Empty,
                null,
                () =>
                {
                    stage = SelectionStage.WarehouseSource;
                    Render(pending);
                });
        }

        private void ShowOrBranchOptions(
            PendingCardSessionState pending,
            string title,
            string summary,
            IReadOnlyList<EffectDialogOption> options,
            bool canClose)
        {
            dialog.ShowCollapsibleOptions(
                getCanvas(),
                title,
                canClose ? "当前没有合法目标，请关闭以完成入场结算。" : "选择本次入场效果的执行分支。",
                summary,
                options,
                canClose ? (Action)(() => Submit(pending, FacilityPendingChoiceTypes.SkipOption, null)) : null,
                canClose ? "关闭" : null);
        }

        private void SubmitAdditionalBuild(PendingCardSessionState pending, string paymentMode)
        {
            var parameters = new Dictionary<string, string>
            {
                [BuildFacilityCommandHandler.CityBoardSlotIndexParameter] =
                    additionalBuildSelection.CityBoardSlotIndex.ToString(),
                [BuildFacilityCommandHandler.PaymentModeParameter] = paymentMode
            };
            Submit(pending, additionalBuildSelection.FacilityId, parameters);
        }

        private void SubmitWarehouseRemoveDispatch(PendingCardSessionState pending, string targetSlotId)
        {
            var parameters = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(removeInfluenceSlotId))
            {
                parameters[ResolveFacilityEffectCommandHandler.RemoveInfluenceSlotIdParameter] = removeInfluenceSlotId;
            }

            if (!string.IsNullOrEmpty(sourceInfluenceSlotId) && !string.IsNullOrEmpty(targetSlotId))
            {
                parameters[ResolveFacilityEffectCommandHandler.SourceInfluenceSlotIdParameter] = sourceInfluenceSlotId;
                parameters[ResolveFacilityEffectCommandHandler.TargetInfluenceSlotIdParameter] = targetSlotId;
            }

            Submit(pending, FacilityPendingChoiceTypes.RemoveDispatchOption, parameters);
        }

        private void Submit(
            PendingCardSessionState pending,
            string optionId,
            IReadOnlyDictionary<string, string> parameters)
        {
            if (pending == null || RejectWhileSubmissionInFlight(pending))
            {
                return;
            }

            var command = presenter.CreateResolveCommand(
                pending,
                getLocalPlayerId(),
                optionId,
                parameters);
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

        private bool RejectWhileSubmissionInFlight(PendingCardSessionState pending)
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

            setPrompt("设施待选命令已发送，正在等待结算，请勿重复提交。");
            return true;
        }

        private void ClearSubmissionInFlight()
        {
            inFlightCommandId = string.Empty;
            inFlightSessionId = string.Empty;
        }

        private IReadOnlyDictionary<string, string> ResourceParameters(IReadOnlyList<int> values, bool includePure)
        {
            var result = new Dictionary<string, string>
            {
                [ResolveFacilityEffectCommandHandler.OriginiumAmountParameter] = ValueAt(values, 0).ToString(),
                [ResolveFacilityEffectCommandHandler.OriginiumShardAmountParameter] = ValueAt(values, 1).ToString(),
                [ResolveFacilityEffectCommandHandler.IronAmountParameter] = ValueAt(values, 2).ToString()
            };
            if (includePure)
            {
                result[ResolveFacilityEffectCommandHandler.PureOriginiumAmountParameter] = ValueAt(values, 3).ToString();
            }

            return result;
        }

        private List<WorkflowHighlight> BuildAllInfluenceSlotHighlights(WorkflowHighlightSemantic semantic)
        {
            var result = new List<WorkflowHighlight>();
            if (mapQuery.Map == null)
            {
                return result;
            }

            for (var locationIndex = 0; locationIndex < mapQuery.Map.Locations.Count; locationIndex++)
            {
                var location = mapQuery.Map.Locations[locationIndex];
                for (var slotIndex = 0; slotIndex < location.InfluenceSlotCount; slotIndex++)
                {
                    result.Add(new WorkflowHighlight(
                        WorkflowHighlightTargetKind.InfluenceSlot,
                        InfluenceService.GetLocationSlotId(location.LocationId, slotIndex),
                        semantic));
                }
            }

            for (var routeIndex = 0; routeIndex < mapQuery.Map.Routes.Count; routeIndex++)
            {
                var route = mapQuery.Map.Routes[routeIndex];
                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    result.Add(new WorkflowHighlight(
                        WorkflowHighlightTargetKind.InfluenceSlot,
                        InfluenceService.GetRouteSlotId(route.RouteId, slotIndex),
                        semantic));
                }
            }

            return result;
        }

        private List<WorkflowHighlight> BuildDeployInfluenceSlotHighlights()
        {
            var result = BuildAllInfluenceSlotHighlights(WorkflowHighlightSemantic.DeployTarget);
            var state = getState();
            var playerId = getLocalPlayerId();
            for (var i = result.Count - 1; i >= 0; i--)
            {
                var slotId = result[i].TargetId;
                if (deployInfluenceSlotIds.Contains(slotId) ||
                    !influenceService.CanPlace(state, playerId, slotId).IsValid)
                {
                    result.RemoveAt(i);
                }
            }

            return result;
        }

        private List<WorkflowHighlight> BuildPlacedInfluenceHighlights(
            int? playerId,
            WorkflowHighlightSemantic semantic)
        {
            var result = new List<WorkflowHighlight>();
            var state = getState();
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if ((!playerId.HasValue || influence.PlayerId == playerId.Value) &&
                    !string.IsNullOrEmpty(influence.SlotId))
                {
                    result.Add(new WorkflowHighlight(WorkflowHighlightTargetKind.InfluenceSlot, influence.SlotId, semantic));
                }
            }

            return result;
        }

        private List<WorkflowHighlight> BuildOpponentInfluenceHighlights()
        {
            var result = new List<WorkflowHighlight>();
            var state = getState();
            var localPlayerId = getLocalPlayerId();
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId != localPlayerId && !string.IsNullOrEmpty(influence.SlotId))
                {
                    result.Add(new WorkflowHighlight(
                        WorkflowHighlightTargetKind.InfluenceSlot,
                        influence.SlotId,
                        WorkflowHighlightSemantic.EventInfluenceTarget));
                }
            }

            return result;
        }

        private bool IsOpponentInfluenceSlot(string slotId)
        {
            if (string.IsNullOrEmpty(slotId))
            {
                return false;
            }

            var state = getState();
            var localPlayerId = getLocalPlayerId();
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId != localPlayerId &&
                    string.Equals(influence.SlotId, slotId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private List<WorkflowHighlight> BuildAllLocationHighlights(WorkflowHighlightSemantic semantic)
        {
            var result = new List<WorkflowHighlight>();
            if (mapQuery.Map == null)
            {
                return result;
            }

            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                result.Add(new WorkflowHighlight(
                    WorkflowHighlightTargetKind.Location,
                    mapQuery.Map.Locations[i].LocationId,
                    semantic));
            }

            return result;
        }

        private string GetFacilityNameAtSlot(string slotIndexText)
        {
            int slotIndex;
            if (!int.TryParse(slotIndexText, out slotIndex))
            {
                return "未知设施";
            }

            var state = getState();
            var placement = state.Map.Facilities.Find(item =>
                item.PlayerId == getLocalPlayerId() &&
                item.CityBoardSlotIndex == slotIndex);
            return placement == null ? "未知设施" : GetFacilityName(placement.FacilityCardId);
        }

        private static string GetFacilityName(string facilityId)
        {
            FacilityCardDefinition facility;
            return FacilityCardDatabase.TryGet(facilityId, out facility) && !string.IsNullOrEmpty(facility.Name)
                ? facility.Name
                : facilityId;
        }

        private static SelectionStage InitialStage(string choiceType)
        {
            switch (choiceType)
            {
                case FacilityPendingChoiceTypes.BuildAdditionalFacility:
                    return SelectionStage.AdditionalFacility;
                case FacilityPendingChoiceTypes.BuildExtensionHub:
                    return SelectionStage.ExtensionHub;
                case FacilityPendingChoiceTypes.ReplaceOneInfluence:
                    return SelectionStage.MercenaryBranch;
                case FacilityPendingChoiceTypes.DeployTwoInfluences:
                    return SelectionStage.DeployInfluences;
                case FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore:
                    return SelectionStage.WarehouseBranch;
                case FacilityPendingChoiceTypes.FreeCityMove:
                    return SelectionStage.FreeCityMove;
                default:
                    return SelectionStage.None;
            }
        }

        private static IReadOnlyDictionary<string, string> Parameters(string key, string value)
        {
            return new Dictionary<string, string> { [key] = value ?? string.Empty };
        }

        private static int ValueAt(IReadOnlyList<int> values, int index)
        {
            return values != null && index >= 0 && index < values.Count ? values[index] : 0;
        }
    }
}
