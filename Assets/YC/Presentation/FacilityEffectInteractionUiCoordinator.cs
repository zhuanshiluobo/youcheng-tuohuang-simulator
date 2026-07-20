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
            AdditionalPayment,
            ExtensionHub,
            ReplaceInfluence,
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
        private string sessionId = string.Empty;
        private SelectionStage stage;
        private string selectedOptionId = string.Empty;
        private int selectedCityBoardSlotIndex = -1;
        private string removeInfluenceSlotId = string.Empty;
        private string sourceInfluenceSlotId = string.Empty;
        private readonly List<string> deployInfluenceSlotIds = new List<string>();
        private bool extensionHubDragging;

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

        public bool Synchronize()
        {
            PendingCardSessionState pending;
            if (!presenter.TryGetPending(getState(), getLocalPlayerId(), out pending))
            {
                ResetAndHide();
                return false;
            }

            var sessionChanged = !string.Equals(sessionId, pending.SessionId, StringComparison.Ordinal);
            if (sessionChanged)
            {
                ResetForPending(pending);
            }

            if (sessionChanged || !dialog.IsShowing)
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
                case SelectionStage.ReplaceInfluence:
                    if (pending.OptionIds.Contains(slotId))
                    {
                        Submit(pending, slotId, null);
                    }
                    else
                    {
                        setPrompt("请选择高亮的影响力进行替换。");
                    }
                    break;
                case SelectionStage.DeployInfluences:
                    if (string.IsNullOrEmpty(slotId) || deployInfluenceSlotIds.Contains(slotId))
                    {
                        setPrompt("该槽位已经选择，请选择另一个槽位或提交当前选择。");
                        break;
                    }

                    if (deployInfluenceSlotIds.Count >= 2)
                    {
                        deployInfluenceSlotIds.RemoveAt(0);
                    }

                    deployInfluenceSlotIds.Add(slotId);
                    Render(pending);
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

            dialog.Hide();
            return true;
        }

        public bool TryHandleAdditionalBuildDrop(string facilityId, int cityBoardSlotIndex)
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

            if (cityBoardSlotIndex < 0 || cityBoardSlotIndex >= BuildFacilityService.CityBoardSlotCount)
            {
                setPrompt("请把建设牌拖到城市面板的空槽位。");
                Render(pending);
                return true;
            }

            var occupied = getState().Map.Facilities.Exists(placement =>
                placement.PlayerId == getLocalPlayerId() &&
                placement.CityBoardSlotIndex == cityBoardSlotIndex);
            if (occupied)
            {
                setPrompt("该城市面板槽位已经被占用。");
                Render(pending);
                return true;
            }

            selectedOptionId = facilityId;
            selectedCityBoardSlotIndex = cityBoardSlotIndex;
            stage = SelectionStage.AdditionalPayment;
            Render(pending);
            return true;
        }

        public void Dispose()
        {
            ResetAndHide();
        }

        private void ResetForPending(PendingCardSessionState pending)
        {
            sessionId = pending.SessionId;
            selectedOptionId = string.Empty;
            selectedCityBoardSlotIndex = -1;
            removeInfluenceSlotId = string.Empty;
            sourceInfluenceSlotId = string.Empty;
            deployInfluenceSlotIds.Clear();
            extensionHubDragging = false;
            stage = InitialStage(pending.ChoiceType);
        }

        private void ResetAndHide()
        {
            sessionId = string.Empty;
            stage = SelectionStage.None;
            selectedOptionId = string.Empty;
            selectedCityBoardSlotIndex = -1;
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
                    ShowReplaceInfluence(pending);
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
            if (stage == SelectionStage.AdditionalFacility)
            {
                stage = SelectionStage.AdditionalFacility;
                dialog.ShowMapPrompt(
                    getCanvas(),
                    "简陋工程营",
                    "拖动供应区中亮起的建设牌到城市面板空槽位，选择位置后再决定支付方式。",
                    string.Empty,
                    null);
                return;
            }

            var payments = new List<EffectDialogOption>
            {
                new EffectDialogOption("支付资源", () => SubmitAdditionalBuild(pending, BuildFacilityService.PaymentModeResources)),
                new EffectDialogOption("支付金券", () => SubmitAdditionalBuild(pending, BuildFacilityService.PaymentModeGold))
            };
            dialog.ShowOptions(
                getCanvas(),
                "简陋工程营",
                "选择支付方式。实际费用和可支付性由规则层确认。",
                payments,
                () =>
                {
                    stage = SelectionStage.AdditionalFacility;
                    Render(pending);
                });
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

        private void ShowReplaceInfluence(PendingCardSessionState pending)
        {
            var highlights = new List<WorkflowHighlight>();
            for (var i = 0; i < pending.OptionIds.Count; i++)
            {
                highlights.Add(new WorkflowHighlight(
                    WorkflowHighlightTargetKind.InfluenceSlot,
                    pending.OptionIds[i],
                    WorkflowHighlightSemantic.EventInfluenceTarget));
            }

            setHighlights(highlights);
            dialog.ShowMapPrompt(getCanvas(), "佣兵指挥部", "点击地图上的高亮影响力，将它替换为你的影响力。", string.Empty, null);
        }

        private void ShowDeployInfluences(PendingCardSessionState pending)
        {
            setHighlights(BuildAllInfluenceSlotHighlights(WorkflowHighlightSemantic.DeployTarget));
            var selectedText = deployInfluenceSlotIds.Count == 0
                ? "尚未选择。请依次点击一至两个影响力槽位。"
                : "已选：" + string.Join("、", deployInfluenceSlotIds.ToArray());
            dialog.ShowMapPrompt(
                getCanvas(),
                "护航调度中心",
                selectedText,
                "提交已选槽位",
                deployInfluenceSlotIds.Count == 0 ? (Action)null : () => Submit(
                    pending,
                    FacilityPendingChoiceTypes.ConfirmOption,
                    Parameters(
                        ResolveFacilityEffectCommandHandler.InfluenceSlotIdsParameter,
                        string.Join(",", deployInfluenceSlotIds.ToArray()))));
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
                var options = new List<EffectDialogOption>();
                if (pending.OptionIds.Contains(FacilityPendingChoiceTypes.RemoveDispatchOption))
                {
                    options.Add(new EffectDialogOption("移除一个影响力，然后调度", () =>
                    {
                        selectedOptionId = FacilityPendingChoiceTypes.RemoveDispatchOption;
                        stage = SelectionStage.WarehouseRemove;
                        Render(pending);
                    }));
                }

                if (pending.OptionIds.Contains(FacilityPendingChoiceTypes.ExploreOption))
                {
                    options.Add(new EffectDialogOption("执行一次正常探索", () =>
                    {
                        selectedOptionId = FacilityPendingChoiceTypes.ExploreOption;
                        stage = SelectionStage.WarehouseExplore;
                        Render(pending);
                        beginAdditionalExplore(pending, FacilityPendingChoiceTypes.ExploreOption);
                    }));
                }

                dialog.ShowCollapsibleOptions(
                    getCanvas(),
                    "载具仓库",
                    "选择本次入场效果的执行分支。",
                    "载具仓库 · 选择分支",
                    options);
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

        private void SubmitAdditionalBuild(PendingCardSessionState pending, string paymentMode)
        {
            var parameters = new Dictionary<string, string>
            {
                [BuildFacilityCommandHandler.CityBoardSlotIndexParameter] = selectedCityBoardSlotIndex.ToString(),
                [BuildFacilityCommandHandler.PaymentModeParameter] = paymentMode
            };
            Submit(pending, selectedOptionId, parameters);
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
            submit(presenter.CreateResolveCommand(pending, getLocalPlayerId(), optionId, parameters));
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
                    return SelectionStage.ReplaceInfluence;
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
